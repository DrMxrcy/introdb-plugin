// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroDbPlugin.Core;
using IntroDbPlugin.Providers;
using IntroDbPlugin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace IntroDbPlugin;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient<IntroDbClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.introdb.app", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds);
        });

        serviceCollection.AddSingleton<SegmentStore>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SegmentStore>>();
            return new SegmentStore(appPaths.DataPath, logger);
        });

        // SegmentStore is registered as a singleton above; MS.DI tracks IDisposable
        // singletons automatically, so no separate IDisposable registration is needed.

        serviceCollection.AddSingleton<IntroDbSubmissionService>();
        serviceCollection.AddSingleton<IMediaSegmentProvider, IntroDbSegmentProvider>();
    }
}
