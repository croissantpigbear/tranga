using API.ExtraMetadata;
using API.Schema.MangaContext;

namespace Tests;

public class MetadataTests
{
    [Theory]
    [InlineData("https://www.mangaupdates.com/series/pb8uwds/some-title", 55099564912L)]
    [InlineData("https://www.mangaupdates.com/series.html?id=12345", null)] // old-style id, not usable with the API
    public void MangaUpdatesIdFromLinks_DecodesSeriesIds(string url, long? expected)
    {
        Assert.Equal(expected, ExtraMetadataFetcher.MangaUpdatesIdFromLinks([new Link("MangaUpdates", url)]));
    }
}
