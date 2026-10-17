using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using API.ExtraMetadata;
using API.Naming;
using API.Schema.MangaContext;

namespace API.ComicInfo;

/// <summary>
/// Builds the ComicInfo.xml (Anansi ComicInfo v2.0 schema, plus the extra fields Kavita and Komga read) for a chapter,
/// from Tranga's own data and the stored extra details (MangaUpdates, MangaDex content rating).
/// </summary>
public static class ComicInfoBuilder
{
    public const string EntryName = "ComicInfo.xml";

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp"];

    /// <param name="chapter">The chapter</param>
    /// <param name="manga">Its Manga, with Authors, MangaTags, Links and AltTitles loaded</param>
    /// <param name="extra">Stored extra details, if any</param>
    /// <param name="pageCount">Number of images in the archive, if known</param>
    /// <param name="sourceName">Website the chapter was downloaded from</param>
    public static string Build(Chapter chapter, Manga manga, ExtraMetadataData? extra, int? pageCount, string? sourceName)
    {
        MangaUpdatesData? mu = extra?.MangaUpdates;
        AniListData? al = extra?.AniList;
        XElement root = new("ComicInfo",
            new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

        void Add(string name, object? value)
        {
            string? text = value switch
            {
                null => null,
                string s => s.Trim(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()?.Trim()
            };
            if (!string.IsNullOrEmpty(text))
                root.Add(new XElement(name, Clean(text)));
        }

        // Elements in the order of the ComicInfo schema
        Add("Title", string.IsNullOrWhiteSpace(chapter.Title) ? null : chapter.Title);
        Add("Series", manga.Name);
        Add("Number", chapter.ChapterNumber);
        Add("Count", TotalCount(mu, al));
        Add("Volume", chapter.VolumeNumber);
        Add("Summary", manga.Description);
        Add("Notes", Notes(mu, al, sourceName));
        Add("Year", Year(manga, mu, al));
        Add("Writer", Join(mu?.Writers is { Count: > 0 } w ? w
            : al?.Writers is { Count: > 0 } aw ? aw
            : manga.Authors.Select(a => a.AuthorName)));
        Add("Penciller", Join(mu?.Artists is { Count: > 0 } pa ? pa : al?.Artists ?? Enumerable.Empty<string>()));
        Add("Publisher", Join(mu?.OriginalPublishers ?? Enumerable.Empty<string>()));
        Add("Imprint", Join(mu?.Publications ?? Enumerable.Empty<string>())); // the magazine it runs in
        Add("Genre", Join(mu?.Genres is { Count: > 0 } g ? g
            : al?.Genres is { Count: > 0 } ag ? ag
            : manga.MangaTags.Select(t => t.Tag)));
        Add("Web", Join(WebLinks(manga, mu, al)));
        Add("PageCount", pageCount is > 0 ? pageCount : null);
        Add("LanguageISO", Tranga.Settings.DownloadLanguage);
        Add("BlackAndWhite", Tranga.Settings.BlackWhiteImages ? "Yes" : null);
        Add("Manga", ReadingDirection(manga.OriginalLanguage));
        Add("ScanInformation", sourceName);
        Add("AgeRating", AgeRating(extra?.ContentRating, (mu?.Genres ?? []).Concat(al?.Genres ?? [])));
        if (mu?.Rating is { } rating and > 0)
            Add("CommunityRating", Math.Round(Math.Clamp(rating / 2, 0, 5), 2));
        else if (al?.AverageScore is { } score and > 0)
            Add("CommunityRating", Math.Round(Math.Clamp(score / 20.0, 0, 5), 2));

        // Not in the schema, but read by Kavita (and partly Komga)
        Add("LocalizedSeries", NamingFormatter.EnglishTitle(manga));
        Add("Tags", Join(manga.MangaTags.Select(t => t.Tag)));

        XDocument doc = new(new XDeclaration("1.0", "utf-8", null), root);
        StringBuilder sb = new();
        using (XmlWriter writer = XmlWriter.Create(new Utf8StringWriter(sb),
                   new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            doc.Save(writer);
        return sb.ToString();
    }

    /// <summary>Total chapters, only when MangaUpdates or AniList says the series is complete (Kavita then marks it finished)</summary>
    private static long? TotalCount(MangaUpdatesData? mu, AniListData? al) =>
        mu is { Completed: true, LatestChapter: > 0 } ? mu.LatestChapter
        : al is { Status: "FINISHED", Chapters: > 0 } ? al.Chapters
        : null;

    private static string Notes(MangaUpdatesData? mu, AniListData? al, string? sourceName)
    {
        List<string> parts = ["Created by Tranga."];
        if (sourceName is not null)
            parts.Add($"Downloaded from {sourceName}.");
        if (mu is not null)
            parts.Add($"Series details from MangaUpdates ({mu.Url}).");
        if (al is not null)
            parts.Add($"Series details from AniList ({al.Url}).");
        return string.Join(' ', parts);
    }

    private static int? Year(Manga manga, MangaUpdatesData? mu, AniListData? al)
    {
        if (manga.Year is > 0 and < 10000)
            return (int)manga.Year.Value;
        foreach (string? s in new[] { mu?.StartYear, mu?.Year })
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) && y is > 1800 and < 3000)
                return y;
        return al?.StartYear is > 1800 and < 3000 ? al.StartYear : null;
    }

    private static IEnumerable<string> WebLinks(Manga manga, MangaUpdatesData? mu, AniListData? al)
    {
        List<string> links = manga.Links
            .Select(l => l.LinkUrl)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
            .ToList();
        if (al is not null && !string.IsNullOrEmpty(al.Url))
            links.Insert(0, al.Url);
        if (mu is not null && !string.IsNullOrEmpty(mu.Url))
            links.Insert(0, mu.Url);
        // Kavita splits on commas, so a URL containing one would break
        return links.Where(u => !u.Contains(',')).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Japanese manga read right-to-left; Korean/Chinese comics (manhwa/manhua) left-to-right</summary>
    private static string ReadingDirection(string? originalLanguage) =>
        originalLanguage?.ToLowerInvariant() is "ja" or "jp" or "ja-ro" ? "YesAndRightToLeft" : "Yes";

    /// <summary>
    /// MangaDex content rating → ComicInfo AgeRating. Errs on the strict side, since Kavita uses it to hide series from
    /// restricted profiles. "safe" only means no sexual content, so it maps to Teen, not Everyone.
    /// </summary>
    internal static string? AgeRating(string? contentRating, IEnumerable<string>? genres)
    {
        HashSet<string> g = (genres ?? Enumerable.Empty<string>()).Select(x => x.ToLowerInvariant()).ToHashSet();
        string? fromGenres =
            g.Contains("hentai") || g.Contains("adult") ? "X18+" :
            g.Contains("smut") ? "Adults Only 18+" :
            g.Contains("mature") || g.Contains("gore") ? "Mature 17+" : null;
        string? fromRating = contentRating?.ToLowerInvariant() switch
        {
            "pornographic" => "X18+",
            "erotica" => "Adults Only 18+",
            "suggestive" => "Teen",
            "safe" => "Teen",
            _ => null
        };
        // The stricter of the two
        string[] order = ["Teen", "Mature 17+", "Adults Only 18+", "X18+"];
        return new[] { fromGenres, fromRating }.Where(x => x is not null).MaxBy(x => Array.IndexOf(order, x));
    }

    private static string? Join(IEnumerable<string> values)
    {
        string[] list = values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length > 0 ? string.Join(", ", list) : null;
    }

    /// <summary>Removes characters XML can't contain</summary>
    private static string Clean(string s) => new(s.Where(XmlConvert.IsXmlChar).ToArray());

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    /// <summary>Number of images in a chapter archive</summary>
    public static int CountImages(ZipArchive archive) =>
        archive.Entries.Count(e => ImageExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()));

    /// <summary>
    /// Replaces (or adds) ComicInfo.xml in an existing chapter archive. Works on a copy, which then replaces the original,
    /// so a crash can't leave a half-written archive.
    /// </summary>
    /// <param name="archivePath">The .cbz</param>
    /// <param name="build">Gets the image count, returns the XML</param>
    public static async Task RewriteArchive(string archivePath, Func<int, string> build, CancellationToken ct)
    {
        string tempPath = archivePath + ".comicinfo.tmp";
        File.Copy(archivePath, tempPath, overwrite: true);
        try
        {
            using (ZipArchive archive = ZipFile.Open(tempPath, ZipArchiveMode.Update))
            {
                foreach (ZipArchiveEntry old in archive.Entries
                             .Where(e => e.FullName.Equals(EntryName, StringComparison.OrdinalIgnoreCase)).ToList())
                    old.Delete();
                string xml = build(CountImages(archive));
                ZipArchiveEntry entry = archive.CreateEntry(EntryName);
                await using Stream s = entry.Open();
                await s.WriteAsync(new UTF8Encoding(false).GetBytes(xml), ct);
            }
            File.Move(tempPath, archivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
