using Jellyfin.Plugin.MedicPosters.Configuration;
using Jellyfin.Plugin.MedicPosters.Posters;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;

namespace MedicPosters.Tests;

/// <summary>New films and series get a poster soon after they're added.</summary>
public class NewTitlesTests
{
    [Fact]
    public void A_new_film_series_home_video_or_music_video_is_its_own_title()
    {
        BaseItem[] items = { new Movie(), new Series(), new Video(), new MusicVideo() };
        foreach (var item in items)
        {
            item.Id = Guid.NewGuid();
            Assert.Equal(item.Id, NewTitleWatcher.TitleFor(item));
        }
    }

    [Fact]
    public void A_new_episode_counts_for_its_series()
    {
        var series = Guid.NewGuid();
        Assert.Equal(series, NewTitleWatcher.TitleFor(new Episode { Id = Guid.NewGuid(), SeriesId = series }));
        Assert.Null(NewTitleWatcher.TitleFor(new Episode { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void Extras_missing_episodes_seasons_and_music_get_nothing()
    {
        Assert.Null(NewTitleWatcher.TitleFor(null));
        Assert.Null(NewTitleWatcher.TitleFor(new Movie { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer }));
        Assert.Null(NewTitleWatcher.TitleFor(new Episode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid(), IsVirtualItem = true }));
        Assert.Null(NewTitleWatcher.TitleFor(new Season { Id = Guid.NewGuid() }));
        Assert.Null(NewTitleWatcher.TitleFor(new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void Making_new_titles_soon_is_on_by_default()
    {
        Assert.True(new PluginConfiguration().MakeNewTitlesSoon);
    }
}

/// <summary>Library covers are left to Jellyfin Animated Posters while it's installed, so the two don't fight.</summary>
public class CoverPluginTests
{
    private static LocalPlugin Plugin(Guid id, PluginStatus status) =>
        new("/plugins/x", true, new PluginManifest { Id = id, Status = status });

    [Fact]
    public void Covers_are_left_alone_only_while_Jellyfin_Animated_Posters_is_on()
    {
        Assert.False(PosterService.IsCoverPluginActive(Array.Empty<LocalPlugin>()));
        Assert.False(PosterService.IsCoverPluginActive(new[] { Plugin(Guid.NewGuid(), PluginStatus.Active) }));
        Assert.False(PosterService.IsCoverPluginActive(new[] { Plugin(PosterService.CoverPluginId, PluginStatus.Disabled) }));
        Assert.True(PosterService.IsCoverPluginActive(new[] { Plugin(PosterService.CoverPluginId, PluginStatus.Active) }));
    }
}
