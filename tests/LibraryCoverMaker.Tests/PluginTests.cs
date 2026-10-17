using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.LibraryCoverMaker;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Xunit;

namespace LibraryCoverMaker.Tests;

public sealed class PluginTests
{
    [Fact]
    public void Plugin_guid_is_fixed() =>
        Assert.Equal(Guid.Parse("ba3b88b5-029c-4a6f-90b3-734af8385652"), Guid.Parse(Plugin.PluginGuid));

    [Fact]
    public void Configuration_defaults_match_the_spec()
    {
        var c = new PluginConfiguration();
        Assert.Equal(PrimaryFormat.Animated, c.PrimaryFormat);
        Assert.Equal(CoverMode.Art, c.DefaultMode);
        Assert.Equal((640, 360), (c.OutputWidth, c.OutputHeight));
        Assert.Equal(8, c.HistorySize);
    }

    [Fact]
    public void The_dashboard_page_and_its_script_are_registered_and_embedded()
    {
        // GetPages reads no instance state, so the plugin is made without its constructor (which needs Jellyfin's
        // application paths).
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var pages = plugin.GetPages().ToList();

        var page = Assert.Single(pages, p => p.EmbeddedResourcePath.EndsWith(".html", StringComparison.Ordinal));
        Assert.Equal("LibraryCoverMaker", page.Name);
        Assert.Equal("Library Cover Maker", page.DisplayName);
        Assert.True(page.EnableInMainMenu);
        Assert.Equal("image", page.MenuIcon);
        Assert.Equal("Jellyfin.Plugin.LibraryCoverMaker.Web.covers.html", page.EmbeddedResourcePath);

        var script = Assert.Single(pages, p => p.EmbeddedResourcePath.EndsWith(".js", StringComparison.Ordinal));
        Assert.Equal("librarycovers.js", script.Name);
        Assert.False(script.EnableInMainMenu);
        Assert.Equal("Jellyfin.Plugin.LibraryCoverMaker.Web.covers.js", script.EmbeddedResourcePath);
        Assert.Equal(2, pages.Count);

        // Jellyfin serves the pages from the DLL by these names; the page loads the script by its page name.
        var assembly = typeof(Plugin).Assembly;
        foreach (var info in pages)
        {
            using var stream = assembly.GetManifestResourceStream(info.EmbeddedResourcePath);
            Assert.NotNull(stream);
        }

        // Only the page and its script come from the Web folder: the image filter's source lives there too.
        Assert.Equal(
            pages.Select(info => info.EmbeddedResourcePath).Order(StringComparer.Ordinal),
            assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("Jellyfin.Plugin.LibraryCoverMaker.Web.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));

        using var html = new StreamReader(assembly.GetManifestResourceStream(page.EmbeddedResourcePath)!);
        Assert.Contains("data-controller=\"__plugin/librarycovers.js\"", html.ReadToEnd(), StringComparison.Ordinal);
    }
}
