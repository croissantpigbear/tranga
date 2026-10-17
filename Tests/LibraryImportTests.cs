using API.Imports;

namespace Tests;

/// <summary>Reading chapter numbers from files already in a library folder</summary>
public class LibraryImportTests
{
    [Theory]
    [InlineData("Some Series - Vol.2 Ch.10.5.cbz", "Some Series", "10.5")]
    [InlineData("Some Series Chapter 007.cbz", "Some Series", "7")]
    [InlineData("Some Series c012 [Group].cbz", "Some Series", "12")]
    [InlineData("Some Series 045.cbz", "Some Series", "45")]
    [InlineData("Some Series - 12 (2019) (Digital).cbz", "Some Series", "12")]
    [InlineData("Danjo-hi 1:39 Ch. 3.cbz", "Danjo-hi 1:39", "3")]
    [InlineData("Some Series v03.cbz", "Some Series", null)]
    [InlineData("Ch.10,5.cbz", "Other", "10.5")]
    public void NumberFromFileName_FindsTheChapter(string file, string series, string? expected) =>
        Assert.Equal(expected, LibraryImport.NumberFromFileName(file, series));

    [Theory]
    [InlineData("007", "7")]
    [InlineData("10.50", "10.5")]
    [InlineData("10,5", "10.5")]
    [InlineData("0", "0")]
    [InlineData("abc", null)]
    public void Normalise_MatchesTrangasChapterNumbers(string input, string? expected) =>
        Assert.Equal(expected, LibraryImport.Normalise(input));

    [Theory]
    [InlineData("a.cbz", true)]
    [InlineData("a.CBZ", true)]
    [InlineData("cover.jpg", false)]
    [InlineData("ComicInfo.xml", false)]
    public void IsChapterFile_OnlyArchives(string file, bool expected) =>
        Assert.Equal(expected, LibraryImport.IsChapterFile(file));
}
