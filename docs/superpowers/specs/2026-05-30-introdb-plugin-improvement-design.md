# IntroDB Plugin — Improvement Design

**Date:** 2026-05-30  
**Status:** Approved  
**Scope:** Jellyfin-first (10.10 + 10.11), Emby maintained

---

## Problem Statement

The current plugin has four critical gaps:

1. **Wrong API endpoint.** Uses the legacy `GET /intro` which is returning "Not Found" for shows that *do* have data in the newer `GET /segments` endpoint. Directly causes missed skip functionality.
2. **Only intro segments.** The IntroDB API returns intro, recap, and outro in a single call — the plugin ignores recap and outro entirely.
3. **No persistence.** Every playback triggers an API call. No cache. No data survives a restart.
4. **No contribution path.** The API supports `POST /submit` for crowdsourcing, but the plugin is read-only with no UI to surface missing episodes or confirm submissions.

Additionally: minor code bugs (duplicate constructor assignments, manual `HttpClient` construction, hardcoded `meta.json` ABI version, warnings-not-errors).

---

## Goals

- Fix the broken API endpoint and support all three segment types (intro, recap, outro)
- Add a SQLite-backed segment store for fast playback lookups and persistence across restarts
- Add a nightly sync task that pre-fetches the whole library
- Add a Jellyfin admin config page with Settings and Submit Queue tabs
- Keep the plugin lightweight — no local audio processing
- Maintain Emby compatibility (core functionality only, no config UI)
- Fix all identified bugs

---

## Non-Goals

- Local audio fingerprinting / Chromaprint detection (separate project)
- GPU-accelerated intro detection (separate project)
- Emby config UI

---

## Architecture

Three layers: shared Core, Jellyfin adapter (primary), Emby adapter (maintained).

```
IntroDbPlugin.Core          — platform-agnostic business logic
IntroDbPlugin               — Jellyfin adapter (full features)
IntroDbPlugin.Emby          — Emby adapter (core only)
IntroDbPlugin.Tests         — tests targeting Core
```

### Project structure changes

```
IntroDbPlugin/
  Core/
    IntroDbClient.cs           (upgraded)
    SegmentStore.cs            (new — SQLite)
    IntroDbSubmissionService.cs (new)
    PluginConfiguration.cs     (expanded)
    Models/
      IntroDbSegmentsResult.cs (new — replaces IntroDbIntroResult)
      SegmentEntry.cs          (new)
  Plugin.cs
  PluginServiceRegistrator.cs  (updated — IHttpClientFactory)
  Providers/
    IntroDbSegmentProvider.cs  (updated)
  Tasks/
    LibrarySyncTask.cs         (new)
  Controllers/
    IntroDbController.cs       (new)
  Configuration/
    configurationpage.html     (new — embedded resource)
```

---

## Component Details

### `IntroDbClient` (upgraded)

**Endpoint change:** `GET /segments?imdb_id=&season=&episode=` replaces `GET /intro`.

**Response shape:**
```json
{
  "imdb_id": "tt0903747",
  "season": 1,
  "episode": 1,
  "intro":  { "start_sec": 12, "end_sec": 70, "start_ms": 12000, "end_ms": 70000, "confidence": 0.95, "submission_count": 8, "updated_at": "2026-04-01T00:00:00Z" },
  "recap":  null,
  "outro":  { "start_sec": 2700, "end_sec": 2760, "confidence": 1.0, "submission_count": 3, "updated_at": "2026-04-14T12:21:01Z" }
}
```

Each non-null segment is returned as a `SegmentEntry`. A null field means IntroDB has no data for that type.

**429 handling:** single retry after 3 s fixed backoff. Does not retry on 404 or 400.

**Bug fix:** remove duplicate field assignments in constructor (lines 47–49 currently assign `_httpClient` and `_logger` twice).

**`IHttpClientFactory`:** client is registered via `AddHttpClient<IntroDbClient>()` in `PluginServiceRegistrator`, replacing the manual `new HttpClient()`. Timeout set at registration time.

---

### `SegmentStore` (new)

SQLite database at `{DataPath}/introdb/segments.db`. WAL journal mode. `SemaphoreSlim(1,1)` for thread safety.

**Schema:**

```sql
CREATE TABLE Segments (
    ImdbId    TEXT    NOT NULL,
    Season    INTEGER NOT NULL,
    Episode   INTEGER NOT NULL,
    Type      TEXT    NOT NULL,   -- "intro" | "recap" | "outro"
    StartMs   INTEGER NOT NULL,
    EndMs     INTEGER NOT NULL,
    Confidence REAL   NOT NULL,
    FetchedAt TEXT    NOT NULL    -- ISO-8601 UTC
);
CREATE UNIQUE INDEX IX_Segments_Key ON Segments (ImdbId, Season, Episode, Type);

CREATE TABLE Metadata (
    Key   TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL
);
-- Key: "LastSyncUtc"

CREATE TABLE SharedUploads (
    Id          INTEGER PRIMARY KEY,
    ImdbId      TEXT    NOT NULL,
    Season      INTEGER NOT NULL,
    Episode     INTEGER NOT NULL,
    Type        TEXT    NOT NULL,
    StartMs     INTEGER NOT NULL,
    EndMs       INTEGER NOT NULL,
    SharedAtUtc TEXT    NOT NULL
);
CREATE INDEX IX_SharedUploads ON SharedUploads (ImdbId, Season, Episode, Type);
```

**Key operations:**

- `GetSegments(imdbId, season, episode)` — returns all stored rows for the episode; null if none exist or all rows are older than `CacheTtlDays`
- `UpsertSegmentsAsync(imdbId, season, episode, segments)` — within a single transaction: delete all existing rows for `(ImdbId, Season, Episode)`, then insert the new segment rows. The delete-first ensures stale segment types are removed if IntroDB's data changes (e.g., previously had an intro, now returns null for that type).
- `MarkNullAsync(imdbId, season, episode)` — within a single transaction: delete all existing rows for the episode, then write a sentinel row (`Type = "none"`, StartMs/EndMs = 0) so the store knows we've checked and IntroDB had no data; avoids repeat API calls for confirmed-missing episodes until TTL expires
- `GetLastSyncUtc()` / `SetLastSyncUtcAsync()`
- `IsAlreadySubmitted(imdbId, season, episode, type, startMs, endMs)` — dedup check with ±1000 ms tolerance
- `RecordSubmissionAsync(...)` — writes to `SharedUploads` after a successful API submission

Stored in `{DataPath}/introdb/` — separate from intro-skipper's `introskipper/` subdirectory.

---

### `LibrarySyncTask` (new — Jellyfin `IScheduledTask`)

**Default trigger:** daily at 3 AM (user-configurable interval).

**Steps:**
1. Query `ILibraryManager` for all `Episode` items
2. For each episode: resolve IMDb ID + season/episode numbers (same logic as current `TryGetImdbId` / `TryGetSeasonEpisodeNumbers`)
3. Call `IntroDbClient.GetSegmentsAsync()` with a 100 ms inter-request delay
4. Write results to `SegmentStore` (upsert hits, `MarkNull` for misses)
5. Update `LastSyncUtc` in Metadata
6. Rebuild the in-memory missing set in `IntroDbSubmissionService`
7. Report progress via `IProgress<double>`

The sync is additive — it doesn't wipe existing rows. Rows are updated in-place via `UPSERT`. This means episodes added between syncs get picked up on the next run; new episodes played before the sync fall through to the on-demand API call in the segment provider.

---

### `IntroDbSegmentProvider` (updated)

Lookup order:
1. `SegmentStore.GetSegments(imdbId, season, episode)`
2. If null (miss or expired): call `IntroDbClient.GetSegmentsAsync()` → `SegmentStore.UpsertSegmentsAsync()` or `MarkNullAsync()`
3. Filter by `MinConfidence` and `EnabledTypes` from config
4. Map to `MediaSegmentDto` list (start/end in ticks)
5. If zero segments after filtering: register episode with `IntroDbSubmissionService` as missing

On-demand fallback ensures new episodes work immediately without waiting for nightly sync.

---

### `IntroDbSubmissionService` (new)

Holds an in-memory `ConcurrentDictionary<(string imdbId, int season, int episode), MissingEpisodeInfo>` populated by the segment provider and the sync task.

**`MissingEpisodeInfo`** carries: series name, episode title, Jellyfin `ItemId`, and existing Jellyfin segment data if another provider has set timestamps (read via `IMediaSegmentManager`).

On `SubmitAsync(imdbId, season, episode, type, startMs, endMs)`:
1. Check `SegmentStore.IsAlreadySubmitted(...)` — reject if duplicate
2. Call `IntroDbClient.SubmitAsync(...)` with configured API key
3. On success: call `SegmentStore.RecordSubmissionAsync(...)` + remove from missing set

---

### `IntroDbController` (new — Jellyfin API controller)

Endpoints consumed by the config page:

| Method | Path | Description |
|---|---|---|
| `GET` | `/IntroDB/config` | Returns current `PluginConfiguration` as JSON |
| `POST` | `/IntroDB/config` | Saves `PluginConfiguration` |
| `GET` | `/IntroDB/missingEpisodes` | Returns missing episode list from submission service |
| `POST` | `/IntroDB/submit` | Submits one episode's timestamps; body: `{imdbId, season, episode, type, startMs, endMs}` |
| `POST` | `/IntroDB/syncNow` | Triggers `LibrarySyncTask` immediately |

All endpoints require admin auth (`[Authorize(Policy = "RequiresElevation")]`).

---

### Config Page (`configurationpage.html`)

Embedded HTML/JS resource registered via `IHasWebPages`. Two tabs:

**Settings tab:**
- API Key (password input, stored in `PluginConfiguration`)
- Minimum Confidence (number, 0–1, default 0.5)
- Segment types (checkboxes: Intro ✓, Recap ✓, Outro ✓)
- Sync Interval (hours, default 24)
- Cache TTL (days, default 7)
- "Save" button → `POST /IntroDB/config`
- "Run Sync Now" button → `POST /IntroDB/syncNow`

**Submit Queue tab:**
- Loads `GET /IntroDB/missingEpisodes` on tab open
- Table: Series | S/E | Existing segment (if any) | Start (s) | End (s) | Submit
- Each Submit button calls `POST /IntroDB/submit` for that row
- After success, row is removed from the table
- No batch submit — one at a time, admin reads each

---

### `PluginConfiguration` (expanded)

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    public string ApiKey { get; set; } = string.Empty;
    public double MinConfidence { get; set; } = 0.5;
    public bool EnableIntro { get; set; } = true;
    public bool EnableRecap { get; set; } = true;
    public bool EnableOutro { get; set; } = true;
    public int SyncIntervalHours { get; set; } = 24;
    public int CacheTtlDays { get; set; } = 7;
}
```

---

## Bug Fixes

| Bug | Location | Fix |
|---|---|---|
| Duplicate field assignment | `IntroDbClient.cs:47-49` | Remove second assignment of `_httpClient` and `_logger` |
| Manual `HttpClient` construction | `PluginServiceRegistrator.cs` | Use `IHttpClientFactory` via `AddHttpClient<IntroDbClient>()` |
| Hardcoded `meta.json` targetAbi | `meta.json` + `build.sh` | Replace with `{{targetAbi}}` token, substituted at build time per version |
| `TreatWarningsAsErrors = false` | `Directory.Build.props` | Flip to `true`; fix any warnings that surface |
| Legacy `/intro` endpoint | `IntroDbClient.cs` | Replace with `/segments` endpoint |
| Missing 10.11 in `meta.json` | `meta.json` | Handled by per-version token substitution in build script |

---

## Emby Compatibility

`IntroDbScheduledTask` is updated to use the same `SegmentStore` and upgraded `IntroDbClient` from Core. All three segment types map to Emby `MarkerType.IntroStart`/`IntroEnd` (recap and outro use the same marker types available in Emby's API). No config page. API key can be set by editing the XML config file directly.

---

## Build & Packaging

- `build.sh` gains a `--token-substitute` step replacing `{{targetAbi}}` in `meta.json` with `10.10.0.0` or `10.11.0.0` per build variant, removing the fragile `sed -i` patch
- `IntroDbPlugin.Tests` gains tests for `SegmentStore` (using an in-memory SQLite connection), `IntroDbSubmissionService` dedup logic, and the upgraded `IntroDbClient` response parsing
- `.gitignore` updated to exclude `.superpowers/`

---

## Future / Out of Scope

- **GPU-accelerated local intro detection** — separate standalone tool that analyzes media files and submits timestamps to IntroDB via `POST /submit`. Shares the same API client design. Does not run inside Jellyfin.
- **Batch API endpoint** — if IntroDB adds bulk lookup, the sync task can be updated to use it with no architectural changes
