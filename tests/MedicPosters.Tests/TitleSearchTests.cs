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
