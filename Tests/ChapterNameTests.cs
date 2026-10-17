using API.MangaConnectors;

namespace Tests;

/// <summary>Chapter numbers, volumes and titles read from website chapter names (FMD2 websites)</summary>
public class ChapterNameTests
{
    [Theory]
    [InlineData("Chapter 10", "10", null, null)]
    [InlineData("Vol.2 Chapter 10.5 - The Return", "10.5", 2, "The Return")]
    [InlineData("Ch. 007", "7", null, null)]
    [InlineData("Chapter 10,5", "10.5", null, null)]
    [InlineData("Episode 3: Start", "3", null, "Start")]
    [InlineData("第12話", "12", null, null)]
    [InlineData("Something 42", "42", null, null)]
    public void ParseChapterName_ReadsNumberVolumeAndTitle(string name, string number, int? volume, string? title)
    {
        (string n, int? v, string? t) = LuaMangaConnector.ParseChapterName(name, 1);
        Assert.Equal(number, n);
        Assert.Equal(volume, v);
        Assert.Equal(title, t);
    }

    [Fact]
    public void ParseChapterName_UsesPosition_WhenThereIsNoNumber()
    {
        Assert.Equal("5", LuaMangaConnector.ParseChapterName("Extra", 5).Number);
    }
}
