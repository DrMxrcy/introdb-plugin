// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

#if EMBY
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace IntroDbPlugin.Emby;

/// <summary>
/// Scheduled task to fetch intro timestamps from IntroDB and store them.
/// </summary>
public class IntroDbScheduledTask : IScheduledTask
{
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private static readonly Regex ImdbIdRegex = new Regex(ImdbIdPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpisodeRegex = new Regex(SeasonEpisodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly ILogger _logger;
    private readonly IntroDbClient _introDbClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbScheduledTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository for chapter storage.</param>
    /// <param name="logManager">Log manager.</param>
    public IntroDbScheduledTask(ILibraryManager libraryManager, IItemRepository itemRepository, ILogManager logManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        _logger = logManager?.GetLogger(Name) ?? throw new ArgumentNullException(nameof(logManager));

        var httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds)
        };
        _introDbClient = new IntroDbClient(httpClient, new EmbyLoggerAdapter<IntroDbClient>(logManager));
    }

    /// <inheritdoc />
    public string Name => "IntroDB Intro Fetcher";

    /// <inheritdoc />
    public string Key => "IntroDbIntroFetcher";

    /// <inheritdoc />
    public string Description => "Fetches intro timestamps from IntroDB for TV episodes.";

    /// <inheritdoc />
    public string Category => "IntroDB";

    /// <inheritdoc />
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { nameof(Episode) },
            IsVirtualItem = false,
            Recursive = true
        }).OfType<Episode>().ToList();

        var total = episodes.Count;
        var current = 0;

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ProcessEpisodeAsync(episode, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error processing episode {0}", ex, episode.Name);
            }

            current++;
            progress.Report((double)current / total * 100);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    private async Task ProcessEpisodeAsync(Episode episode, CancellationToken cancellationToken)
    {
        if (!TryGetImdbId(episode, out var imdbId))
        {
            return;
        }

        if (!TryGetSeasonEpisodeNumbers(episode, out var seasonNumber, out var episodeNumber))
        {
            return;
        }

        var result = await _introDbClient
            .GetIntroAsync(imdbId, seasonNumber, episodeNumber, cancellationToken)
            .ConfigureAwait(false);

        if (result == null)
        {
            return;
        }

        var startTicks = (long)(result.StartSeconds * TicksPerSecond);
        var endTicks = (long)(result.EndSeconds * TicksPerSecond);

        if (endTicks <= startTicks)
        {
            return;
        }

        if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0 && endTicks > episode.RunTimeTicks.Value)
        {
            return;
        }

        // Get existing chapters from repository
        var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

        // Remove existing intro markers (both old named style and proper MarkerType)
        chapters.RemoveAll(c =>
            c.MarkerType == MarkerType.IntroStart ||
            c.MarkerType == MarkerType.IntroEnd ||
            c.Name?.StartsWith("[IntroDB]", StringComparison.Ordinal) == true);

        // Add intro start marker with proper MarkerType
        chapters.Add(new ChapterInfo
        {
            StartPositionTicks = startTicks,
            MarkerType = MarkerType.IntroStart
        });

        // Add intro end marker with proper MarkerType
        chapters.Add(new ChapterInfo
        {
            StartPositionTicks = endTicks,
            MarkerType = MarkerType.IntroEnd
        });

        // Sort by position
        chapters = chapters.OrderBy(c => c.StartPositionTicks).ToList();

        // Save chapters back to repository
        _itemRepository.SaveChapters(episode.InternalId, chapters);

        _logger.Info("Updated intro markers for {0} S{1}E{2}", episode.SeriesName, seasonNumber, episodeNumber);
    }

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        imdbId = string.Empty;

        if (episode.Series != null)
        {
            if (episode.Series.ProviderIds != null &&
                episode.Series.ProviderIds.TryGetValue(MetadataProviders.Imdb.ToString(), out var seriesImdbId) &&
                !string.IsNullOrWhiteSpace(seriesImdbId))
            {
                imdbId = seriesImdbId;
                return true;
            }
        }

        if (episode.ProviderIds != null &&
            episode.ProviderIds.TryGetValue(MetadataProviders.Imdb.ToString(), out var providerImdbId) &&
            !string.IsNullOrWhiteSpace(providerImdbId))
        {
            imdbId = providerImdbId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = ImdbIdRegex.Match(episode.Path);
            if (match.Success)
            {
                imdbId = match.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSeasonEpisodeNumbers(Episode episode, out int seasonNumber, out int episodeNumber)
    {
        seasonNumber = episode.ParentIndexNumber ?? 0;
        episodeNumber = episode.IndexNumber ?? 0;

        if (seasonNumber > 0 && episodeNumber > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = SeasonEpisodeRegex.Match(episode.Path);
            if (match.Success &&
                int.TryParse(match.Groups["season"].Value, out var parsedSeason) &&
                int.TryParse(match.Groups["episode"].Value, out var parsedEpisode))
            {
                seasonNumber = parsedSeason;
                episodeNumber = parsedEpisode;
                return seasonNumber > 0 && episodeNumber > 0;
            }
        }

        return seasonNumber > 0 && episodeNumber > 0;
    }
}
#endif
