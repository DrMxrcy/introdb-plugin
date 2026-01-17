// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

#if EMBY
using System;
using System.Collections.Generic;
using IntroDbPlugin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace IntroDbPlugin.Emby;

/// <summary>
/// IntroDB Emby plugin.
/// </summary>
public class EmbyPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EmbyPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public EmbyPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static EmbyPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "IntroDB";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b04f5f3e-3d3a-4a3c-9cd9-9f4f9a0c8a62");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return Array.Empty<PluginPageInfo>();
    }
}
#endif
