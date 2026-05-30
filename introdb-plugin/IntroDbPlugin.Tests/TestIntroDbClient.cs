// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Services;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestIntroDbClient
{
    [Fact]
    public async Task GetSegmentsAsync_ReturnsResult_WhenResponseOk()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(
                "https://api.introdb.app/segments?imdb_id=tt0903747&season=1&episode=1",
                request.RequestUri?.ToString());

            var payload = """
                {
                  "imdb_id": "tt0903747",
                  "season": 1,
                  "episode": 1,
                  "intro": {
                    "start_sec": 12.0,
                    "end_sec": 70.0,
                    "start_ms": 12000,
                    "end_ms": 70000,
                    "confidence": 0.95,
                    "submission_count": 8,
                    "updated_at": "2026-04-01T00:00:00Z"
                  },
                  "recap": null,
                  "outro": {
                    "start_sec": 2700.0,
                    "end_sec": 2760.0,
                    "start_ms": 2700000,
                    "end_ms": 2760000,
                    "confidence": 1.0,
                    "submission_count": 3,
                    "updated_at": "2026-04-14T12:21:01Z"
                  }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.introdb.app") };
        var client = new IntroDbClient(httpClient, new LoggerFactory().CreateLogger<IntroDbClient>());

        var result = await client.GetSegmentsAsync("tt0903747", 1, 1, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.Intro);
        Assert.Equal(12.0, result.Intro!.StartSec);
        Assert.Equal(70.0, result.Intro.EndSec);
        Assert.Equal(0.95, result.Intro.Confidence);
        Assert.Equal(8, result.Intro.SubmissionCount);
        Assert.Null(result.Recap);
        Assert.NotNull(result.Outro);
        Assert.Equal(2700.0, result.Outro!.StartSec);
        Assert.Equal(1.0, result.Outro.Confidence);
    }

    [Fact]
    public async Task GetSegmentsAsync_ReturnsResultWithNullFields_WhenAllSegmentsNull()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var payload = """{"imdb_id":"tt0903747","season":1,"episode":1,"intro":null,"recap":null,"outro":null}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.introdb.app") };
        var client = new IntroDbClient(httpClient, new LoggerFactory().CreateLogger<IntroDbClient>());

        var result = await client.GetSegmentsAsync("tt0903747", 1, 1, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.Intro);
        Assert.Null(result.Recap);
        Assert.Null(result.Outro);
    }

    [Fact]
    public async Task GetSegmentsAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.introdb.app") };
        var client = new IntroDbClient(httpClient, new LoggerFactory().CreateLogger<IntroDbClient>());

        var result = await client.GetSegmentsAsync("tt0903747", 1, 1, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSegmentsAsync_ReturnsNull_WhenBadRequest()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.introdb.app") };
        var client = new IntroDbClient(httpClient, new LoggerFactory().CreateLogger<IntroDbClient>());

        var result = await client.GetSegmentsAsync("tt0903747", 1, 1, CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(_handler(request));
        }
    }
}
