using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LibraryCoverMaker;

/// <summary>
/// Library Cover Maker: renders an animated cover for every library and keeps it fresh.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin GUID. Must match the guid in manifest.json and the release workflow.</summary>
    public const string PluginGuid = "ba3b88b5-029c-4a6f-90b3-734af8385652";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Library Cover Maker";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Keeps an animated cover on every library, rendered on the server and renewed on a schedule.";

    /// <summary>No pages yet: the Dashboard page is registered here once it exists.</summary>
    public IEnumerable<PluginPageInfo> GetPages() => Array.Empty<PluginPageInfo>();
}

/// <summary>Settings that apply to every library.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Whether a library's Primary image is the animated or the still version of its cover.</summary>
    public PrimaryFormat PrimaryFormat { get; set; } = PrimaryFormat.Animated;

    /// <summary>The content mode a library starts with.</summary>
    public CoverMode DefaultMode { get; set; } = CoverMode.Art;

    /// <summary>Width of a rendered cover, in pixels.</summary>
    public int OutputWidth { get; set; } = 640;

    /// <summary>Height of a rendered cover, in pixels.</summary>
    public int OutputHeight { get; set; } = 360;

    /// <summary>How many covers each library keeps in its history, besides the original Primary image.</summary>
    public int HistorySize { get; set; } = 8;
}
