using API.Imports;

namespace Tests;

/// <summary>Reading and writing series lists (CSV export / import)</summary>
public class SeriesListCsvTests
{
    [Fact]
    public void Parse_TrangasOwnExport()
    {
        string csv = SeriesListCsv.Write([
            ["Destiny Lovers", "MangaDex", "https://mangadex.org/title/abc", "yes", "yes", "Manga", "5", "Continuing", "2019", "10", "12"],
            ["Destiny Lovers", "MangaFire", "https://mangafire.to/manga/x.y", "no", "", "Manga", "5", "Continuing", "2019", "10", "12"],
        ]);
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse(csv);
        Assert.Equal(2, rows.Count);
        SeriesListCsv.Row first = rows[0];
        Assert.Equal("Destiny Lovers", first.Name);
        Assert.Equal("https://mangadex.org/title/abc", first.Url);
        Assert.Equal("Manga", first.Library);
        Assert.Equal(5f, first.StartFrom);
        Assert.True(first.Download);
        Assert.True(first.Preferred);
        Assert.Null(first.Problem);
        Assert.False(rows[1].Download);
        Assert.False(rows[1].Preferred);
    }

    [Fact]
    public void Parse_JustLinksNoHeader()
    {
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse("https://mangadex.org/title/a\n\nhttps://mangafire.to/manga/b.c\r\n");
        Assert.Equal(new[] { "https://mangadex.org/title/a", "https://mangafire.to/manga/b.c" }, rows.Select(r => r.Url));
        Assert.All(rows, r => Assert.True(r.Download));
    }

    [Fact]
    public void Parse_JustTitlesNoHeader()
    {
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse("One Piece\nChainsaw Man");
        Assert.Equal(new[] { "One Piece", "Chainsaw Man" }, rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.Null(r.Url));
    }

    [Fact]
    public void Parse_OwnColumnsAnyOrderSemicolons()
    {
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse("Title;Site;Start\n\"Kaiju No. 8; Special\";MangaDex;12,5\n");
        SeriesListCsv.Row r = Assert.Single(rows);
        Assert.Equal("Kaiju No. 8; Special", r.Name);
        Assert.Equal("MangaDex", r.Website);
        Assert.Equal(12.5f, r.StartFrom);
    }

    [Fact]
    public void Parse_QuotesAndLineBreaksInsideCells()
    {
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse("name,url\n\"He said \"\"hi\"\"\nthen left\",https://site.com/a\nNext,https://site.com/b\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("He said \"hi\"\nthen left", rows[0].Name);
        Assert.Equal(4, rows[1].Line);
    }

    [Fact]
    public void Parse_ReportsBadRows()
    {
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse("name,url,start_from_chapter\nA,notalink,\nB,https://s.com/b,abc\n,,\n");
        Assert.Equal(2, rows.Count); // the empty row is dropped
        Assert.Contains("isn't a web link", rows[0].Problem);
        Assert.Contains("isn't a chapter number", rows[1].Problem);
    }

    [Fact]
    public void Write_QuotesAndDefusesFormulas()
    {
        string csv = SeriesListCsv.Write([["=HYPERLINK(\"x\")", "a,b", null, "", "", "", "", "", "", "", ""]]);
        string dataLine = csv.Split("\r\n")[1];
        Assert.StartsWith("\"'=HYPERLINK(\"\"x\"\")\",\"a,b\",", dataLine);
        Assert.StartsWith("﻿name,website,url", csv);
    }

    [Theory]
    [InlineData("Kaiju No. 8", "kaiju no 8")]
    [InlineData("Chainsaw-Man!", "CHAINSAW MAN")]
    public void Normalise_IgnoresCaseAndPunctuation(string a, string b) =>
        Assert.Equal(SeriesListImport.Normalise(a), SeriesListImport.Normalise(b));
}
