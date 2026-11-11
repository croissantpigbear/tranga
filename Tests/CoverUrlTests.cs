using API.MangaConnectors;

namespace Tests;

/// <summary>Cover links that aren't full web addresses</summary>
public class CoverUrlTests
{
    [Theory]
    [InlineData("https://cdn.site.com/c/1.jpg", null, "https://cdn.site.com/c/1.jpg")]
    [InlineData("//cdn.site.com/c/1.jpg", null, "https://cdn.site.com/c/1.jpg")]
    [InlineData("/covers/1.jpg", "https://site.com/manga/abc", "https://site.com/covers/1.jpg")]
    [InlineData("covers/1.jpg", "https://site.com/manga/", "https://site.com/manga/covers/1.jpg")]
    [InlineData("/covers/1.jpg", null, "https://site.com/covers/1.jpg")] // from the website's address
    [InlineData("", "https://site.com/manga/abc", null)]
    [InlineData("data:image/png;base64,AAAA", "https://site.com/manga/abc", null)]
    public void AbsoluteUrl_CompletesPartialLinks(string url, string? page, string? expected) =>
        Assert.Equal(expected, MangaConnector.AbsoluteUrl(url, page, ["site.com"]));
}
