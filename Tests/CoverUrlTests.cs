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

/// <summary>Finding a cover on a series page when a module returns none</summary>
public class CoverFromPageTests
{
    [Fact]
    public void CoverFromHtml_PicturePrefersJpgOverAvif()
    {
        // MangaKatana's markup (Nov 2026): the FMD2 module's XPath //div[@class="cover"]/img misses the <picture>
        const string html = """
            <html><head><meta property="og:image" content="https://mangakatana.com/imgs/cover/09c/28/0a9c9.avif"/></head><body>
            <div class="cover"><picture><source srcset="https://mangakatana.com/imgs/cover/09c/28/0a9c9.avif" type="image/avif">
            <source srcset="https://mangakatana.com/imgs/cover/09c/28/0a9c9.webp" type="image/webp">
            <img src="https://mangakatana.com/imgs/cover/09c/28/0a9c9.jpg" alt="[Cover]"></picture></div></body></html>
            """;
        string? cover = API.MangaConnectors.LuaMangaConnector.CoverFromHtml(html, "https://mangakatana.com/manga/dodododo.27840", ["mangakatana.com"]);
        Assert.Equal("https://mangakatana.com/imgs/cover/09c/28/0a9c9.jpg", cover);
    }

    [Fact]
    public void CoverFromHtml_FallsBackToOgImage()
    {
        const string html = """<html><head><meta property="og:image" content="/img/c.png"/></head><body></body></html>""";
        Assert.Equal("https://site.com/img/c.png",
            API.MangaConnectors.LuaMangaConnector.CoverFromHtml(html, "https://site.com/manga/x", ["site.com"]));
    }

    [Fact]
    public void CoverFromHtml_NothingFound() =>
        Assert.Null(API.MangaConnectors.LuaMangaConnector.CoverFromHtml("<html><body><p>hi</p></body></html>", "https://site.com/x", ["site.com"]));
}
