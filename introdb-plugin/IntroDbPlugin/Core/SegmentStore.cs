// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Core;

public sealed class SegmentStore : IDisposable
{
    private const long DeduplicateToleranceMs = 1000;

    private readonly SqliteConnection _connection;
    private readonly ILogger<SegmentStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public SegmentStore(string dataPath, ILogger<SegmentStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = Path.Combine(dataPath, "introdb");
        Directory.CreateDirectory(dir);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, "segments.db"),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _connection.Open();
        InitializeSchema();
    }

    /// <summary>
    /// Returns stored segments, or null if data is missing/stale.
    /// Empty list means IntroDB confirmed no segments for this episode.
    /// </summary>
    public IReadOnlyList<StoredSegment>? GetSegments(string imdbId, int season, int episode, int cacheTtlDays)
    {
        _semaphore.Wait();
        try
        {
            using var fetchCmd = _connection.CreateCommand();
            fetchCmd.CommandText =
                "SELECT FetchedAt FROM Fetches WHERE ImdbId=@i AND Season=@s AND Episode=@e";
            fetchCmd.Parameters.AddWithValue("@i", imdbId);
            fetchCmd.Parameters.AddWithValue("@s", season);
            fetchCmd.Parameters.AddWithValue("@e", episode);
            var raw = fetchCmd.ExecuteScalar() as string;

            if (raw is null) return null;

            if (DateTimeOffset.TryParseExact(raw, "O", null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var fetchedAt) &&
                DateTimeOffset.UtcNow - fetchedAt > TimeSpan.FromDays(cacheTtlDays))
            {
                return null;
            }

            using var segCmd = _connection.CreateCommand();
            segCmd.CommandText =
                "SELECT Type, StartMs, EndMs, Confidence FROM Segments WHERE ImdbId=@i AND Season=@s AND Episode=@e";
            segCmd.Parameters.AddWithValue("@i", imdbId);
            segCmd.Parameters.AddWithValue("@s", season);
            segCmd.Parameters.AddWithValue("@e", episode);

            var results = new List<StoredSegment>();
            using var reader = segCmd.ExecuteReader();
            while (reader.Read())
                results.Add(new StoredSegment(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3)));

            return results;
        }
        finally { _semaphore.Release(); }
    }

    /// <summary>
    /// Replaces all stored data for the episode in a single transaction.
    /// Delete-first ensures stale segment types are removed if IntroDB's data changes.
    /// </summary>
    public async Task UpsertSegmentsAsync(
        string imdbId, int season, int episode, IReadOnlyList<StoredSegment> segments)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                DeleteEpisode(imdbId, season, episode, tx);
                InsertFetch(imdbId, season, episode, tx);

                foreach (var seg in segments)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO Segments(ImdbId,Season,Episode,Type,StartMs,EndMs,Confidence) VALUES(@i,@s,@e,@t,@sm,@em,@c)";
                    cmd.Parameters.AddWithValue("@i", imdbId);
                    cmd.Parameters.AddWithValue("@s", season);
                    cmd.Parameters.AddWithValue("@e", episode);
                    cmd.Parameters.AddWithValue("@t", seg.Type);
                    cmd.Parameters.AddWithValue("@sm", seg.StartMs);
                    cmd.Parameters.AddWithValue("@em", seg.EndMs);
                    cmd.Parameters.AddWithValue("@c", seg.Confidence);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }
        finally { _semaphore.Release(); }
    }

    public async Task<bool> IsAlreadySubmittedAsync(
        string imdbId, int season, int episode, string type, long startMs, long endMs)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT 1 FROM SharedUploads
                WHERE ImdbId=@i AND Season=@s AND Episode=@e AND Type=@t
                  AND ABS(StartMs-@sm) <= @tol AND ABS(EndMs-@em) <= @tol
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@i", imdbId);
            cmd.Parameters.AddWithValue("@s", season);
            cmd.Parameters.AddWithValue("@e", episode);
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@sm", startMs);
            cmd.Parameters.AddWithValue("@em", endMs);
            cmd.Parameters.AddWithValue("@tol", DeduplicateToleranceMs);
            return await cmd.ExecuteScalarAsync().ConfigureAwait(false) is not null;
        }
        finally { _semaphore.Release(); }
    }

    public async Task RecordSubmissionAsync(
        string imdbId, int season, int episode, string type, long startMs, long endMs)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO SharedUploads(ImdbId,Season,Episode,Type,StartMs,EndMs,SharedAtUtc) VALUES(@i,@s,@e,@t,@sm,@em,@dt)";
            cmd.Parameters.AddWithValue("@i", imdbId);
            cmd.Parameters.AddWithValue("@s", season);
            cmd.Parameters.AddWithValue("@e", episode);
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@sm", startMs);
            cmd.Parameters.AddWithValue("@em", endMs);
            cmd.Parameters.AddWithValue("@dt", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _semaphore.Release(); }
    }

    public DateTimeOffset? GetLastSyncUtc()
    {
        _semaphore.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Metadata WHERE Key='LastSyncUtc'";
            var raw = cmd.ExecuteScalar() as string;
            return raw is not null &&
                   DateTimeOffset.TryParseExact(raw, "O", null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt : null;
        }
        finally { _semaphore.Release(); }
    }

    public async Task SetLastSyncUtcAsync(DateTimeOffset timestamp)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO Metadata(Key,Value) VALUES('LastSyncUtc',@v) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            cmd.Parameters.AddWithValue("@v", timestamp.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _semaphore.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
        _connection.Dispose();
    }

    private void DeleteEpisode(string imdbId, int season, int episode, SqliteTransaction tx)
    {
        foreach (var table in new[] { "Segments", "Fetches" })
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE ImdbId=@i AND Season=@s AND Episode=@e";
            cmd.Parameters.AddWithValue("@i", imdbId);
            cmd.Parameters.AddWithValue("@s", season);
            cmd.Parameters.AddWithValue("@e", episode);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertFetch(string imdbId, int season, int episode, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO Fetches(ImdbId,Season,Episode,FetchedAt) VALUES(@i,@s,@e,@dt)";
        cmd.Parameters.AddWithValue("@i", imdbId);
        cmd.Parameters.AddWithValue("@s", season);
        cmd.Parameters.AddWithValue("@e", episode);
        cmd.Parameters.AddWithValue("@dt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private void InitializeSchema()
    {
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("""
            CREATE TABLE IF NOT EXISTS Fetches (
                ImdbId TEXT NOT NULL, Season INTEGER NOT NULL, Episode INTEGER NOT NULL,
                FetchedAt TEXT NOT NULL, PRIMARY KEY (ImdbId, Season, Episode))
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS Segments (
                ImdbId TEXT NOT NULL, Season INTEGER NOT NULL, Episode INTEGER NOT NULL,
                Type TEXT NOT NULL, StartMs INTEGER NOT NULL, EndMs INTEGER NOT NULL, Confidence REAL NOT NULL)
            """);
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS IX_Segments ON Segments(ImdbId,Season,Episode,Type)");
        Exec("""
            CREATE TABLE IF NOT EXISTS SharedUploads (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ImdbId TEXT NOT NULL, Season INTEGER NOT NULL, Episode INTEGER NOT NULL,
                Type TEXT NOT NULL, StartMs INTEGER NOT NULL, EndMs INTEGER NOT NULL, SharedAtUtc TEXT NOT NULL)
            """);
        Exec("CREATE INDEX IF NOT EXISTS IX_SharedUploads ON SharedUploads(ImdbId,Season,Episode,Type)");
        Exec("""
            CREATE TABLE IF NOT EXISTS Metadata (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL)
            """);
        _logger.LogDebug("SegmentStore schema initialized");
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
