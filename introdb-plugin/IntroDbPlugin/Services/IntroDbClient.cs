// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Core.Models;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Services;

public sealed class IntroDbClient
{
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.introdb.app";
    private const string SegmentsPath = "/segments";
    private const string SubmitPath = "/submit";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly ILogger<IntroDbClient> _logger;

    public IntroDbClient(HttpClient httpClient, ILogger<IntroDbClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IntroDbSegmentsResult?> GetSegmentsAsync(
        string imdbId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);
        if (seasonNumber <= 0) throw new ArgumentOutOfRangeException(nameof(seasonNumber));
        if (episodeNumber <= 0) throw new ArgumentOutOfRangeException(nameof(episodeNumber));

        var uri = BuildUri(SegmentsPath,
            $"imdb_id={Uri.EscapeDataString(imdbId)}&season={seasonNumber}&episode={episodeNumber}");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                _logger.LogDebug("IntroDB rate-limited for {ImdbId} S{Season}E{Episode}; retrying",
                    imdbId, seasonNumber, episodeNumber);
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("IntroDB request failed for {ImdbId} S{Season}E{Episode}: {Status}",
                    imdbId, seasonNumber, episodeNumber, response.StatusCode);
                return null;
            }

#if EMBY
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#endif
            var payload = await JsonSerializer
                .DeserializeAsync<SegmentsApiResponse>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                _logger.LogWarning("IntroDB response unparseable for {ImdbId} S{Season}E{Episode}",
                    imdbId, seasonNumber, episodeNumber);
                return null;
            }

            return new IntroDbSegmentsResult(
                payload.ImdbId, payload.Season, payload.Episode,
                ToEntry(payload.Intro), ToEntry(payload.Recap), ToEntry(payload.Outro));
        }

        return null;
    }

    public async Task<bool> SubmitAsync(
        string imdbId, int seasonNumber, int episodeNumber,
        string segmentType, double startSec, double endSec,
        string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var body = JsonSerializer.Serialize(new
        {
            imdb_id = imdbId, season = seasonNumber, episode = episodeNumber,
            segment_type = segmentType, start_sec = startSec, end_sec = endSec
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(SubmitPath, string.Empty))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("IntroDB submit failed for {ImdbId} S{Season}E{Episode}: {Status}",
                imdbId, seasonNumber, episodeNumber, response.StatusCode);
            return false;
        }

        return true;
    }

    private static SegmentEntry? ToEntry(SegmentApiEntry? e)
    {
        if (e is null || e.EndMs <= e.StartMs || e.StartMs < 0) return null;
        return new SegmentEntry(e.StartSec, e.EndSec, e.StartMs, e.EndMs, e.Confidence, e.SubmissionCount, e.UpdatedAt);
    }

    private Uri BuildUri(string path, string query)
    {
        var base_ = _httpClient.BaseAddress ?? new Uri(BaseUrl, UriKind.Absolute);
        return new UriBuilder(new Uri(base_, path)) { Query = query }.Uri;
    }

    private sealed class SegmentsApiResponse
    {
        [JsonPropertyName("imdb_id")] public string ImdbId { get; set; } = string.Empty;
        [JsonPropertyName("season")] public int Season { get; set; }
        [JsonPropertyName("episode")] public int Episode { get; set; }
        [JsonPropertyName("intro")] public SegmentApiEntry? Intro { get; set; }
        [JsonPropertyName("recap")] public SegmentApiEntry? Recap { get; set; }
        [JsonPropertyName("outro")] public SegmentApiEntry? Outro { get; set; }
    }

    private sealed class SegmentApiEntry
    {
        [JsonPropertyName("start_sec")] public double StartSec { get; set; }
        [JsonPropertyName("end_sec")] public double EndSec { get; set; }
        [JsonPropertyName("start_ms")] public long StartMs { get; set; }
        [JsonPropertyName("end_ms")] public long EndMs { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("submission_count")] public int SubmissionCount { get; set; }
        [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
    }
}
