using Jellyfin.Plugin.MedicPosters.Posters;

namespace MedicPosters.Tests;

/// <summary>Searching for a title matches any of its words, whatever the case, accents or punctuation.</summary>
public class TitleSearchTests
{
    [Theory]
    [InlineData("The Dark Knight", "the dark knight")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Spider-Man: No Way Home", "spider man no way home")]
    [InlineData("  Léon:  The Professional ", "leon the professional")]
    [InlineData("Брат 2", "брат 2")]
    [InlineData(null, "")]
    public void Words_are_plain_and_lower_case(string? text, string expected)
    {
        Assert.Equal(expected, PosterService.SearchWords(text));
    }
}

/// <summary>Streams, such as IPTV films, have nothing to cut moments from, so they're never offered as titles.</summary>
public class StreamTests
{
    [Theory]
    [InlineData("/media/films/Heat (1995)/Heat.mkv", false)]
    [InlineData("/media/tv/Solo Leveling", false)]
    [InlineData("http://iptv.example:8080/movie/user/pass/123.mkv", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void Only_files_on_this_server_are_titles(string? path, bool stream)
    {
        Assert.Equal(stream, PosterService.IsStream(new MediaBrowser.Controller.Entities.Movies.Movie { Path = path! }));
    }
}

/// <summary>How an episode is named when picking one for a moment.</summary>
public class EpisodeNameTests
{
    [Theory]
    [InlineData(1, 3, "Arise", "S1E3 Arise")]
    [InlineData(2, 10, "", "S2E10")]
    [InlineData(null, null, "Pilot", "Pilot")]
    public void Episodes_are_named_by_season_and_number(int? season, int? episode, string name, string expected)
    {
        var e = new MediaBrowser.Controller.Entities.TV.Episode { ParentIndexNumber = season, IndexNumber = episode, Name = name };
        Assert.Equal(expected, PosterService.EpisodeName(e));
    }
}
