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
    public async Task GetIntroAsync_ReturnsResult_WhenResponseOk()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(
                "http://api.introdb.app/intro?imdb_id=tt0903747&season=1&episode=1",
                request.RequestUri?.ToString());

            var payload = """
                {
                  "imdb_id": "tt0903747",
                  "season": 1,
                  "episode": 1,
                  "start_ms": 2500,
                  "end_ms": 58000,
                  "confidence": 0.95,
                  "submission_count": 12
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://api.introdb.app")
        };

        var logger = new LoggerFactory().CreateLogger<IntroDbClient>();
        var client = new IntroDbClient(httpClient, logger);

        var result = await client.GetIntroAsync("tt0903747", 1, 1, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2.5, result!.StartSeconds);
        Assert.Equal(58, result.EndSeconds);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal(12, result.SubmissionCount);
    }

    [Fact]
    public async Task GetIntroAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://api.introdb.app")
        };

        var logger = new LoggerFactory().CreateLogger<IntroDbClient>();
        var client = new IntroDbClient(httpClient, logger);

        var result = await client.GetIntroAsync("tt0903747", 1, 1, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetIntroAsync_ReturnsNull_WhenBadRequest()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://api.introdb.app")
        };

        var logger = new LoggerFactory().CreateLogger<IntroDbClient>();
        var client = new IntroDbClient(httpClient, logger);

        var result = await client.GetIntroAsync("tt0903747", 1, 1, CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(_handler(request));
        }
    }
}
