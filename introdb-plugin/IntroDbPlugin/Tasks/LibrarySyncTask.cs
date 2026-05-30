// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Configuration;
using IntroDbPlugin.Core;
using IntroDbPlugin.Core.Models;
using IntroDbPlugin.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Tasks;

public class LibrarySyncTask : IScheduledTask
{
    public const string TaskKey = "IntroDbLibrarySync";

    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";
    private static readonly Regex ImdbIdRegex = new(ImdbIdPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpisodeRegex = new(SeasonEpisodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan InterRequestDelay = TimeSpan.FromMilliseconds(100);

    private readonly ILibraryManager _libraryManager;
    private readonly IntroDbClient _client;
    private readonly SegmentStore _store;
    private readonly IntroDbSubmissionService _submissionService;
    private readonly ILogger<LibrarySyncTask> _logger;

    public LibrarySyncTask(
        ILibraryManager libraryManager, IntroDbClient client, SegmentStore store,
        IntroDbSubmissionService submissionService, ILogger<LibrarySyncTask> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _store = store;
        _submissionService = submissionService;
        _logger = logger;
    }

    public string Name => "IntroDB Library Sync";
    public string Key => TaskKey;
    public string Description => "Fetches intro, recap, and outro timestamps from IntroDB for all TV episodes.";
    public string Category => "IntroDB";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _submissionService.ClearMissing();

        var episodes = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [nameof(Episode)],
                IsVirtualItem = false,
                Recursive = true
            })
            .OfType<Episode>()
            .ToList();

        _logger.LogInformation("IntroDB sync starting — {Count} episodes", episodes.Count);

        var total = episodes.Count;
        var done = 0;

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryGetImdbId(episode, out var imdbId) &&
                TryGetSeasonEpisodeNumbers(episode, out var season, out var ep))
            {
                try
                {
                    var result = await _client.GetSegmentsAsync(imdbId, season, ep, cancellationToken)
                        .ConfigureAwait(false);

                    var toStore = new List<StoredSegment>();
                    if (result?.Intro is not null) toStore.Add(new StoredSegment("intro", result.Intro.StartMs, result.Intro.EndMs, result.Intro.Confidence));
                    if (result?.Recap is not null) toStore.Add(new StoredSegment("recap", result.Recap.StartMs, result.Recap.EndMs, result.Recap.Confidence));
                    if (result?.Outro is not null) toStore.Add(new StoredSegment("outro", result.Outro.StartMs, result.Outro.EndMs, result.Outro.Confidence));

                    await _store.UpsertSegmentsAsync(imdbId, season, ep, toStore).ConfigureAwait(false);

                    if (toStore.Count == 0 || result is null)
                        _submissionService.RegisterMissing(imdbId, season, ep,
                            episode.SeriesName ?? string.Empty, episode.Name ?? string.Empty);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sync failed for {ImdbId} S{Season}E{Episode}", imdbId, season, ep);
                }

                await Task.Delay(InterRequestDelay, cancellationToken).ConfigureAwait(false);
            }

            done++;
            progress.Report((double)done / total * 100);
        }

        await _store.SetLastSyncUtcAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
        _logger.LogInformation("IntroDB sync complete — {Missing} episodes missing",
            _submissionService.GetMissingEpisodes().Count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    ];

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        if (episode.Series?.ProviderIds?.TryGetValue("Imdb", out var sid) == true && !string.IsNullOrWhiteSpace(sid))
        { imdbId = sid; return true; }
        if (episode.ProviderIds?.TryGetValue("Imdb", out var eid) == true && !string.IsNullOrWhiteSpace(eid))
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
