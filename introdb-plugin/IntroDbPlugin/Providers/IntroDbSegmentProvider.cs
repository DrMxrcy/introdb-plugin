// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Configuration;
using IntroDbPlugin.Core;
using IntroDbPlugin.Core.Models;
using IntroDbPlugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Providers;

public class IntroDbSegmentProvider : IMediaSegmentProvider
{
    private const long TicksPerMs = TimeSpan.TicksPerMillisecond;
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";

    private static readonly Regex ImdbIdRegex = new(ImdbIdPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpisodeRegex = new(SeasonEpisodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly IntroDbClient _client;
    private readonly SegmentStore _store;
    private readonly IntroDbSubmissionService _submissionService;
    private readonly ILogger<IntroDbSegmentProvider> _logger;

    public IntroDbSegmentProvider(
        ILibraryManager libraryManager, IntroDbClient client, SegmentStore store,
        IntroDbSubmissionService submissionService, ILogger<IntroDbSegmentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(submissionService);
        ArgumentNullException.ThrowIfNull(logger);
        _libraryManager = libraryManager;
        _client = client;
        _store = store;
        _submissionService = submissionService;
        _logger = logger;
    }

    public string Name => "IntroDB";

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is not Episode episode) return Array.Empty<MediaSegmentDto>();

        if (!TryGetImdbId(episode, out var imdbId))
        {
            _logger.LogDebug("No IMDb id for {ItemId}", request.ItemId);
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetSeasonEpisodeNumbers(episode, out var season, out var ep))
        {
            _logger.LogDebug("No valid season/episode for {ItemId}", request.ItemId);
            return Array.Empty<MediaSegmentDto>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var stored = _store.GetSegments(imdbId, season, ep, config.CacheTtlDays)
                     ?? await FetchAndStoreAsync(imdbId, season, ep, cancellationToken).ConfigureAwait(false);

        if (stored is null || stored.Count == 0)
        {
            _submissionService.RegisterMissing(imdbId, season, ep,
                episode.SeriesName ?? string.Empty, episode.Name ?? string.Empty);
            return Array.Empty<MediaSegmentDto>();
        }

        var segments = new List<MediaSegmentDto>();
        foreach (var seg in stored)
        {
            if (!IsEnabled(seg.Type, config)) continue;
            if (seg.Confidence < config.MinConfidence) continue;
            if (!TryMapType(seg.Type, out var segType)) continue;

            var start = seg.StartMs * TicksPerMs;
            var end = seg.EndMs * TicksPerMs;
            if (end <= start) continue;
            if (episode.RunTimeTicks.HasValue && end > episode.RunTimeTicks.Value) continue;

            segments.Add(new MediaSegmentDto { ItemId = request.ItemId, StartTicks = start, EndTicks = end, Type = segType });
        }

        if (segments.Count == 0)
            _submissionService.RegisterMissing(imdbId, season, ep,
                episode.SeriesName ?? string.Empty, episode.Name ?? string.Empty);

        return segments;
    }

    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode);

    private async Task<IReadOnlyList<StoredSegment>?> FetchAndStoreAsync(
        string imdbId, int season, int episode, CancellationToken cancellationToken)
    {
        IntroDbSegmentsResult? result;
        try
        {
            result = await _client.GetSegmentsAsync(imdbId, season, episode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IntroDB fetch failed for {ImdbId} S{Season}E{Episode}", imdbId, season, episode);
            return null;
        }

        if (result is null) return null;

        var toStore = new List<StoredSegment>();
        if (result.Intro is not null) toStore.Add(new StoredSegment("intro", result.Intro.StartMs, result.Intro.EndMs, result.Intro.Confidence));
        if (result.Recap is not null) toStore.Add(new StoredSegment("recap", result.Recap.StartMs, result.Recap.EndMs, result.Recap.Confidence));
        if (result.Outro is not null) toStore.Add(new StoredSegment("outro", result.Outro.StartMs, result.Outro.EndMs, result.Outro.Confidence));

        await _store.UpsertSegmentsAsync(imdbId, season, episode, toStore).ConfigureAwait(false);
        return toStore;
    }

    private static bool IsEnabled(string type, PluginConfiguration config) => type switch
    {
        "intro" => config.EnableIntro,
        "recap" => config.EnableRecap,
        "outro" => config.EnableOutro,
        _ => false
    };

    private static bool TryMapType(string type, out MediaSegmentType segType)
    {
        // Note: if Jellyfin 10.10/10.11 does not define MediaSegmentType.Recap,
        // map "recap" to MediaSegmentType.Intro as a fallback.
        switch (type)
        {
            case "intro": segType = MediaSegmentType.Intro; return true;
            case "recap": segType = MediaSegmentType.Intro; return true;  // fallback -- update if Jellyfin adds Recap
            case "outro": segType = MediaSegmentType.Outro; return true;
            default: segType = default; return false;
        }
    }

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        if (episode.SeriesId != Guid.Empty &&
            _libraryManager.GetItemById(episode.SeriesId) is Series series &&
            series.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var sid) &&
            !string.IsNullOrWhiteSpace(sid))
        { imdbId = sid; return true; }

        if (episode.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var eid) &&
            !string.IsNullOrWhiteSpace(eid))
        { imdbId = eid; return true; }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var m = ImdbIdRegex.Match(episode.Path);
            if (m.Success) { imdbId = m.Value; return true; }
        }

        imdbId = string.Empty;
        return false;
    }

    private static bool TryGetSeasonEpisodeNumbers(Episode episode, out int season, out int ep)
    {
        season = episode.AiredSeasonNumber ?? episode.ParentIndexNumber ?? 0;
        ep = episode.IndexNumber ?? 0;
        if (season > 0 && ep > 0) return true;

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var m = SeasonEpisodeRegex.Match(episode.Path);
            if (m.Success &&
                int.TryParse(m.Groups["season"].Value, out var s) &&
                int.TryParse(m.Groups["episode"].Value, out var e))
            { season = s; ep = e; return season > 0 && ep > 0; }
        }

        return season > 0 && ep > 0;
    }
}
