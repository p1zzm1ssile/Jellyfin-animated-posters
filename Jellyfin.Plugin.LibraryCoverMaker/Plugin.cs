using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LibraryCoverMaker;

/// <summary>
/// Jellyfin Animated Posters: renders an animated cover for every library and keeps it fresh.
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

    public override string Name => "Jellyfin Animated Posters";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Keeps an animated cover on every library, rendered on the server and renewed on a schedule.";

    /// <summary>
    /// The Dashboard page and its script. The page loads the script by its page name
    /// (<c>data-controller="__plugin/librarycovers.js"</c>), so the two names go together.
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "LibraryCoverMaker",
            DisplayName = "Jellyfin Animated Posters",
            EmbeddedResourcePath = GetType().Namespace + ".Web.covers.html",

            // Adds the page to the Plugins section of the Dashboard's sidebar.
            EnableInMainMenu = true,
            MenuIcon = "image",
        },
        new PluginPageInfo
        {
            Name = "librarycovers.js",
            EmbeddedResourcePath = GetType().Namespace + ".Web.covers.js",
        },
    ];
}

/// <summary>Settings that apply to every library.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Whether a library's Primary image is the animated or the still version of its cover.</summary>
    public PrimaryFormat PrimaryFormat { get; set; } = PrimaryFormat.Animated;

    /// <summary>The content mode a library starts with.</summary>
    public CoverMode DefaultMode { get; set; } = CoverMode.Scenes;

    /// <summary>Width of a rendered cover, in pixels.</summary>
    public int OutputWidth { get; set; } = 640;

    /// <summary>Height of a rendered cover, in pixels.</summary>
    public int OutputHeight { get; set; } = 360;

    /// <summary>How many covers each library keeps in its history, besides the original Primary image.</summary>
    public int HistorySize { get; set; } = 8;
}
