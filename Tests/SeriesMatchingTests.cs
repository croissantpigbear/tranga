using API.Schema.MangaContext;

namespace Tests;

/// <summary>"Title" and "Title [R18]" / "Title (Colored)" are different series</summary>
public class SeriesMatchingTests
{
    [Theory]
    [InlineData("Danjo-hi 1:39 no Heikou Sekai wa Omoi no Hoka Futsuu", "")]
    [InlineData("Danjo-hi 1:39 no Heikou Sekai wa Omoi no Hoka Futsuu [R18]", "r18")]
    [InlineData("Title (Colored)", "color")]
    [InlineData("Title (Colour)", "color")]
    [InlineData("Official Webtoon", "official,webtoon")]
    public void EditionTags_FindsEditionMarkers(string name, string expected)
    {
        Assert.Equal(expected, string.Join(',', MangaContext.EditionTags(name).Order()));
    }

    [Fact]
    public void EditionTags_DifferBetweenNormalAndAdultVersion()
    {
        Assert.False(MangaContext.EditionTags("Destiny Lovers").SetEquals(MangaContext.EditionTags("Destiny Lovers [R18]")));
    }
}
