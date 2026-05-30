// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Tests;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Core;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestIntroDbSubmissionService : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentStore _store;
    private readonly IntroDbSubmissionService _service;

    public TestIntroDbSubmissionService()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _store = new SegmentStore(_tempDir, new LoggerFactory().CreateLogger<SegmentStore>());
        _service = new IntroDbSubmissionService(_store, new LoggerFactory().CreateLogger<IntroDbSubmissionService>());
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void GetMissingEpisodes_ReturnsEmpty_Initially()
    {
        Assert.Empty(_service.GetMissingEpisodes());
    }

    [Fact]
    public void RegisterMissing_AddsEpisode()
    {
        _service.RegisterMissing("tt0903747", 1, 1, "Breaking Bad", "Pilot");
        var missing = _service.GetMissingEpisodes();
        Assert.Single(missing);
        Assert.Equal("tt0903747", missing[0].ImdbId);
    }

    [Fact]
    public void RegisterMissing_DoesNotDuplicate()
    {
        _service.RegisterMissing("tt0903747", 1, 1, "Breaking Bad", "Pilot");
        _service.RegisterMissing("tt0903747", 1, 1, "Breaking Bad", "Pilot");
        Assert.Single(_service.GetMissingEpisodes());
    }

    [Fact]
    public async Task SubmitAsync_ReturnsFalse_WhenApiKeyEmpty()
    {
        var (success, error) = await _service.SubmitAsync(
            "tt0903747", 1, 1, "intro", 12000, 70000,
            apiKey: string.Empty,
            submitToApi: (_, _, _, _, _, _, _, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.False(success);
        Assert.Contains("API key", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAsync_ReturnsFalse_WhenAlreadySubmitted()
    {
        await _store.RecordSubmissionAsync("tt0903747", 1, 1, "intro", 12000, 70000);

        var (success, error) = await _service.SubmitAsync(
            "tt0903747", 1, 1, "intro", 12000, 70000,
            apiKey: "idb_test",
            submitToApi: (_, _, _, _, _, _, _, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.False(success);
        Assert.Contains("already submitted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAsync_RecordsSubmissionAndRemovesFromQueue_WhenSuccessful()
    {
        _service.RegisterMissing("tt0903747", 1, 1, "Breaking Bad", "Pilot");
        var apiCalled = false;

        var (success, _) = await _service.SubmitAsync(
            "tt0903747", 1, 1, "intro", 12000, 70000,
            apiKey: "idb_test",
            submitToApi: (_, _, _, _, _, _, _, _) => { apiCalled = true; return Task.FromResult(true); },
            CancellationToken.None);

        Assert.True(success);
        Assert.True(apiCalled);
        Assert.True(await _store.IsAlreadySubmittedAsync("tt0903747", 1, 1, "intro", 12000, 70000));
        Assert.Empty(_service.GetMissingEpisodes());
    }
}
