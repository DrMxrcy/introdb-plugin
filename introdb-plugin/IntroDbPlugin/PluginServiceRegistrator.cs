// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroDbPlugin.Providers;
using IntroDbPlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin;

/// <summary>
/// Register IntroDB services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IMediaSegmentProvider, IntroDbSegmentProvider>();
        serviceCollection.AddSingleton(sp =>
        {
            var httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds)
            };

            return new IntroDbClient(httpClient, sp.GetRequiredService<ILogger<IntroDbClient>>());
        });
    }
}
