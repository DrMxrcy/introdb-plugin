// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Configuration;
using IntroDbPlugin.Core;
using IntroDbPlugin.Services;
using IntroDbPlugin.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IntroDbPlugin.Controllers;

[ApiController]
[Route("IntroDB")]
[Authorize(Policy = "RequiresElevation")]
public class IntroDbController : ControllerBase
{
    private readonly IntroDbSubmissionService _submissionService;
    private readonly IntroDbClient _client;
    private readonly ITaskManager _taskManager;

    public IntroDbController(
        IntroDbSubmissionService submissionService,
        IntroDbClient client,
        ITaskManager taskManager)
    {
        _submissionService = submissionService;
        _client = client;
        _taskManager = taskManager;
    }

    [HttpGet("config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> GetConfig() =>
        Ok(Plugin.Instance?.Configuration ?? new PluginConfiguration());

    [HttpPost("config")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult SaveConfig([FromBody] PluginConfiguration config)
    {
        if (Plugin.Instance is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        Plugin.Instance.UpdateConfiguration(config);
        return NoContent();
    }

    [HttpGet("missingEpisodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MissingEpisodeDto>> GetMissingEpisodes() =>
        Ok(_submissionService.GetMissingEpisodes()
            .Select(m => new MissingEpisodeDto(m.ImdbId, m.Season, m.Episode, m.SeriesName, m.EpisodeTitle))
            .ToList());

    [HttpPost("submit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest req, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.ApiKey ?? string.Empty;

        var (success, error) = await _submissionService.SubmitAsync(
            req.ImdbId, req.Season, req.Episode, req.SegmentType, req.StartMs, req.EndMs,
            apiKey,
            (imdb, s, e, type, startSec, endSec, key, ct) =>
                _client.SubmitAsync(imdb, s, e, type, startSec, endSec, key, ct),
            cancellationToken).ConfigureAwait(false);

        return success ? Ok(new { success = true }) : BadRequest(new { error });
    }

    [HttpPost("syncNow")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult SyncNow()
    {
        var task = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask is LibrarySyncTask);

        if (task is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Sync task not registered." });

        _taskManager.QueueScheduledTask<LibrarySyncTask>(new TaskOptions());
        return Accepted();
    }
}

public sealed record MissingEpisodeDto(string ImdbId, int Season, int Episode, string SeriesName, string EpisodeTitle);
public sealed record SubmitRequest(string ImdbId, int Season, int Episode, string SegmentType, long StartMs, long EndMs);
