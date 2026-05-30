// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Core;

public sealed class IntroDbSubmissionService
{
    private readonly SegmentStore _store;
    private readonly ILogger<IntroDbSubmissionService> _logger;
    private readonly ConcurrentDictionary<(string ImdbId, int Season, int Episode), MissingEpisodeInfo> _missing = new();

    public IntroDbSubmissionService(SegmentStore store, ILogger<IntroDbSubmissionService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterMissing(string imdbId, int season, int episode, string seriesName, string episodeTitle) =>
        _missing.TryAdd((imdbId, season, episode), new MissingEpisodeInfo(imdbId, season, episode, seriesName, episodeTitle));

    public void ClearMissing() => _missing.Clear();

    public IReadOnlyList<MissingEpisodeInfo> GetMissingEpisodes() =>
        _missing.Values.OrderBy(m => m.SeriesName).ThenBy(m => m.Season).ThenBy(m => m.Episode).ToList();

    /// <summary>
    /// Submits a segment timestamp to IntroDB after dedup-checking the local SharedUploads table.
    /// The submitToApi delegate decouples this service from HttpClient.
    /// Returns (true, null) on success or (false, errorMessage) on failure.
    /// </summary>
    public async Task<(bool Success, string? Error)> SubmitAsync(
        string imdbId, int season, int episode, string segmentType,
        long startMs, long endMs, string apiKey,
        Func<string, int, int, string, double, double, string, CancellationToken, Task<bool>> submitToApi,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "API key is required to submit timestamps.");

        if (await _store.IsAlreadySubmittedAsync(imdbId, season, episode, segmentType, startMs, endMs).ConfigureAwait(false))
            return (false, "These timestamps have already been submitted.");

        var ok = await submitToApi(
            imdbId, season, episode, segmentType,
            startMs / 1000.0, endMs / 1000.0,
            apiKey, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            _logger.LogWarning("IntroDB API rejected submission for {ImdbId} S{Season}E{Episode} {Type}",
                imdbId, season, episode, segmentType);
            return (false, "IntroDB API rejected the submission. Check your API key and timestamp range.");
        }

        await _store.RecordSubmissionAsync(imdbId, season, episode, segmentType, startMs, endMs).ConfigureAwait(false);
        _missing.TryRemove((imdbId, season, episode), out _);
        _logger.LogInformation("Submitted {Type} for {ImdbId} S{Season}E{Episode}", segmentType, imdbId, season, episode);
        return (true, null);
    }
}

public sealed record MissingEpisodeInfo(
    string ImdbId, int Season, int Episode, string SeriesName, string EpisodeTitle);
