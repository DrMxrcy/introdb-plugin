// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

#if EMBY
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Core;
using IntroDbPlugin.Core.Models;
using IntroDbPlugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace IntroDbPlugin.Emby;

public class IntroDbScheduledTask : IScheduledTask
{
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";
    private const long TicksPerMs = TimeSpan.TicksPerMillisecond;
    private static readonly TimeSpan InterRequestDelay = TimeSpan.FromMilliseconds(100);
    private static readonly Regex ImdbIdRegex = new Regex(ImdbIdPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpisodeRegex = new Regex(SeasonEpisodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly ILogger _logger;
    private readonly IntroDbClient _client;
    private readonly SegmentStore _store;

    public IntroDbScheduledTask(
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        ILogManager logManager,
        SegmentStore store)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        _logger = logManager?.GetLogger(Name) ?? throw new ArgumentNullException(nameof(logManager));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        var httpClient = new System.Net.Http.HttpClient
            { Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds) };
        _client = new IntroDbClient(httpClient, new EmbyLoggerAdapter<IntroDbClient>(logManager));
    }

    public string Name => "IntroDB Intro Fetcher";
    public string Key => "IntroDbIntroFetcher";
    public string Description => "Fetches intro, recap, and outro timestamps from IntroDB for TV episodes.";
    public string Category => "IntroDB";

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { nameof(Episode) },
            IsVirtualItem = false,
            Recursive = true
        }).OfType<Episode>().ToList();

        var total = episodes.Count;
        var done = 0;

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await ProcessEpisodeAsync(episode, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.ErrorException("Error processing {0}", ex, episode.Name); }

            done++;
            progress.Report((double)done / total * 100);
            await Task.Delay(InterRequestDelay, cancellationToken).ConfigureAwait(false);
        }

        await _store.SetLastSyncUtcAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks } };

    private async Task ProcessEpisodeAsync(Episode episode, CancellationToken cancellationToken)
    {
        if (!TryGetImdbId(episode, out var imdbId)) return;
        if (!TryGetSeasonEpisodeNumbers(episode, out var season, out var ep)) return;

        var stored = _store.GetSegments(imdbId, season, ep, cacheTtlDays: 7);
        if (stored is not null) return;

        var result = await _client.GetSegmentsAsync(imdbId, season, ep, cancellationToken).ConfigureAwait(false);
        if (result is null) return;

        var toStore = new List<StoredSegment>();
        if (result.Intro is not null) toStore.Add(new StoredSegment("intro", result.Intro.StartMs, result.Intro.EndMs, result.Intro.Confidence));
        if (result.Recap is not null) toStore.Add(new StoredSegment("recap", result.Recap.StartMs, result.Recap.EndMs, result.Recap.Confidence));
        if (result.Outro is not null) toStore.Add(new StoredSegment("outro", result.Outro.StartMs, result.Outro.EndMs, result.Outro.Confidence));

        await _store.UpsertSegmentsAsync(imdbId, season, ep, toStore).ConfigureAwait(false);
        if (toStore.Count == 0) return;

        var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
        chapters.RemoveAll(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd
                                || c.Name?.StartsWith("[IntroDB]", StringComparison.Ordinal) == true);

        foreach (var seg in toStore)
        {
            chapters.Add(new ChapterInfo { StartPositionTicks = seg.StartMs * TicksPerMs, MarkerType = MarkerType.IntroStart });
            chapters.Add(new ChapterInfo { StartPositionTicks = seg.EndMs * TicksPerMs, MarkerType = MarkerType.IntroEnd });
        }

        _itemRepository.SaveChapters(episode.InternalId, chapters.OrderBy(c => c.StartPositionTicks).ToList());
        _logger.Info("Updated markers for {0} S{1}E{2}", episode.SeriesName, season, ep);
    }

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        imdbId = string.Empty;
        if (episode.Series?.ProviderIds?.TryGetValue(MetadataProviders.Imdb.ToString(), out var sid) == true && !string.IsNullOrWhiteSpace(sid)) { imdbId = sid; return true; }
        if (episode.ProviderIds?.TryGetValue(MetadataProviders.Imdb.ToString(), out var eid) == true && !string.IsNullOrWhiteSpace(eid)) { imdbId = eid; return true; }
        if (!string.IsNullOrWhiteSpace(episode.Path)) { var m = ImdbIdRegex.Match(episode.Path); if (m.Success) { imdbId = m.Value; return true; } }
        return false;
    }

    private static bool TryGetSeasonEpisodeNumbers(Episode episode, out int season, out int ep)
    {
        season = episode.ParentIndexNumber ?? 0;
        ep = episode.IndexNumber ?? 0;
        if (season > 0 && ep > 0) return true;
        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var m = SeasonEpisodeRegex.Match(episode.Path);
            if (m.Success && int.TryParse(m.Groups["season"].Value, out var s) && int.TryParse(m.Groups["episode"].Value, out var e))
            { season = s; ep = e; return season > 0 && ep > 0; }
        }
        return season > 0 && ep > 0;
    }
}
#endif
