using System;
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
}
