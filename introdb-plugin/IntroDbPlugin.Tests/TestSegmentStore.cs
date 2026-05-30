// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IntroDbPlugin.Core;
using IntroDbPlugin.Core.Models;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestSegmentStore : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentStore _store;

    public TestSegmentStore()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _store = new SegmentStore(_tempDir, new LoggerFactory().CreateLogger<SegmentStore>());
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void GetSegments_ReturnsNull_WhenNeverFetched()
    {
        Assert.Null(_store.GetSegments("tt0903747", 1, 1, cacheTtlDays: 7));
    }

    [Fact]
    public async Task GetSegments_ReturnsEmptyList_AfterUpsertWithNoSegments()
    {
        await _store.UpsertSegmentsAsync("tt0903747", 1, 1, []);
        var result = _store.GetSegments("tt0903747", 1, 1, cacheTtlDays: 7);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public async Task GetSegments_ReturnsSegments_AfterUpsert()
    {
        var segments = new List<StoredSegment>
        {
            new("intro", 12000, 70000, 0.95),
            new("outro", 2700000, 2760000, 1.0)
        };
        await _store.UpsertSegmentsAsync("tt0903747", 1, 1, segments);

        var result = _store.GetSegments("tt0903747", 1, 1, cacheTtlDays: 7);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(result, s => s.Type == "intro" && s.StartMs == 12000 && s.EndMs == 70000);
        Assert.Contains(result, s => s.Type == "outro" && s.StartMs == 2700000);
    }

    [Fact]
    public async Task UpsertSegments_ReplacesOldData_WhenCalledTwice()
    {
        await _store.UpsertSegmentsAsync("tt0903747", 1, 1, [new("intro", 12000, 70000, 0.95)]);
        await _store.UpsertSegmentsAsync("tt0903747", 1, 1, [new("outro", 2700000, 2760000, 1.0)]);

        var result = _store.GetSegments("tt0903747", 1, 1, cacheTtlDays: 7);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("outro", result[0].Type);
    }

    [Fact]
    public async Task IsAlreadySubmitted_ReturnsFalse_WhenNoHistory()
    {
        Assert.False(await _store.IsAlreadySubmittedAsync("tt0903747", 1, 1, "intro", 12000, 70000));
    }

    [Fact]
    public async Task IsAlreadySubmitted_ReturnsTrue_AfterRecording()
    {
        await _store.RecordSubmissionAsync("tt0903747", 1, 1, "intro", 12000, 70000);
        Assert.True(await _store.IsAlreadySubmittedAsync("tt0903747", 1, 1, "intro", 12000, 70000));
    }

    [Fact]
    public async Task IsAlreadySubmitted_ReturnsTrue_WithinToleranceWindow()
    {
        await _store.RecordSubmissionAsync("tt0903747", 1, 1, "intro", 12000, 70000);
        Assert.True(await _store.IsAlreadySubmittedAsync("tt0903747", 1, 1, "intro", 12500, 70000));
    }

    [Fact]
    public async Task IsAlreadySubmitted_ReturnsFalse_OutsideToleranceWindow()
    {
        await _store.RecordSubmissionAsync("tt0903747", 1, 1, "intro", 12000, 70000);
        Assert.False(await _store.IsAlreadySubmittedAsync("tt0903747", 1, 1, "intro", 14000, 70000));
    }

    [Fact]
    public async Task GetSetLastSyncUtc_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.SetLastSyncUtcAsync(now);
        var read = _store.GetLastSyncUtc();
        Assert.NotNull(read);
        Assert.Equal(now.ToUnixTimeSeconds(), read!.Value.ToUnixTimeSeconds());
    }
}
