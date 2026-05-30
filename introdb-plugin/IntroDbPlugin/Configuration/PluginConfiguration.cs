// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.Plugins;

namespace IntroDbPlugin.Configuration;

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
