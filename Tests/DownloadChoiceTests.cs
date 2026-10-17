using API.Schema.MangaContext;
using API.Workers.PeriodicWorkers;

namespace Tests;

/// <summary>"Start from chapter" and which website a chapter downloads from</summary>
public class DownloadChoiceTests
{
    [Theory]
    [InlineData("10", 10)]
    [InlineData("10.5", 10.5)]
    [InlineData("10.5.1", 10.5)]
    [InlineData("0", 0)]
    public void NumberValue_ReadsChapterNumbers(string number, double expected) =>
        Assert.Equal(expected, Chapter.NumberValue(number), 3);

    [Theory]
    [InlineData("4", 5f, true)]
    [InlineData("4.9", 5f, true)]
    [InlineData("5", 5f, false)]
    [InlineData("12", 5f, false)]
    [InlineData("1", 0f, false)]
    public void IsBeforeStart_SkipsEarlierChapters(string number, float start, bool expected) =>
        Assert.Equal(expected, Chapter.IsBeforeStart(number, start));

    private static (Manga Manga, Chapter Ch1, Chapter Ch2) Series()
    {
        Manga manga = new("Test Series", "", "https://example.com/c.jpg", MangaReleaseStatus.Continuing, [], [], [], []);
        return (manga, new Chapter(manga, "1", null), new Chapter(manga, "2", null));
    }

    [Fact]
    public void PickOneWebsitePerChapter_UsesPreferredWebsiteWhenItHasTheChapter()
    {
        (Manga manga, Chapter ch1, Chapter ch2) = Series();
        MangaConnectorId<Chapter>[] links =
        [
            new(ch1, "Alpha", "a1", null, true), new(ch1, "Beta", "b1", null, true),
            new(ch2, "Alpha", "a2", null, true),
        ];
        List<MangaConnectorId<Chapter>> picked = StartNewChapterDownloadsWorker.PickOneWebsitePerChapter(links,
            id => id == manga.Key ? "Beta" : null, _ => false);

        Assert.Equal(2, picked.Count);
        Assert.Equal("Beta", picked.Single(l => l.ObjId == ch1.Key).MangaConnectorName);
        Assert.Equal("Alpha", picked.Single(l => l.ObjId == ch2.Key).MangaConnectorName); // only Alpha has it
    }

    [Fact]
    public void PickOneWebsitePerChapter_SkipsWebsitesGivenUpOn()
    {
        (Manga _, Chapter ch1, Chapter _) = Series();
        MangaConnectorId<Chapter> alpha = new(ch1, "Alpha", "a1", null, true);
        MangaConnectorId<Chapter> beta = new(ch1, "Beta", "b1", null, true);
        List<MangaConnectorId<Chapter>> picked = StartNewChapterDownloadsWorker.PickOneWebsitePerChapter([alpha, beta],
            _ => "Alpha", key => key == alpha.Key);

        Assert.Equal("Beta", Assert.Single(picked).MangaConnectorName);
    }
}
