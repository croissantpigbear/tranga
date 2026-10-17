using System.Net;
using System.Text.RegularExpressions;
using API.Schema.MangaContext;
using log4net;
using Newtonsoft.Json.Linq;

namespace API.ExtraMetadata;

/// <summary>Subset of an AniList manga entry (graphql.anilist.co)</summary>
public class AniListData
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    /// <summary>"link" (AniList link stored from MangaDex) or "title" (exact title match)</summary>
    public string MatchedBy { get; set; } = "link";
    public string? TitleRomaji { get; set; }
    public string? TitleEnglish { get; set; }
    public string? TitleNative { get; set; }
    public List<string> Synonyms { get; set; } = [];
    /// <summary>Synopsis as plain text</summary>
    public string? Description { get; set; }
    /// <summary>MANGA, ONE_SHOT, NOVEL</summary>
    public string? Format { get; set; }
    /// <summary>FINISHED, RELEASING, NOT_YET_RELEASED, CANCELLED, HIATUS</summary>
    public string? Status { get; set; }
    public int? StartYear { get; set; }
    public int? StartMonth { get; set; }
    public int? StartDay { get; set; }
    public int? EndYear { get; set; }
    public int? EndMonth { get; set; }
    public int? EndDay { get; set; }
    public int? Chapters { get; set; }
    public int? Volumes { get; set; }
    /// <summary>JP, KR, CN, TW</summary>
    public string? CountryOfOrigin { get; set; }
    public bool? IsAdult { get; set; }
    /// <summary>0–100</summary>
    public int? AverageScore { get; set; }
    public List<string> Genres { get; set; } = [];
    /// <summary>Non-spoiler tags that apply strongly (rank ≥ 60)</summary>
    public List<string> Tags { get; set; } = [];
    public List<string> Writers { get; set; } = [];
    public List<string> Artists { get; set; } = [];
    public string? CoverUrl { get; set; }

    /// <summary>Every title AniList knows</summary>
    public IEnumerable<string> AllTitles() =>
        new[] { TitleRomaji, TitleEnglish, TitleNative }.Concat(Synonyms).Where(t => !string.IsNullOrWhiteSpace(t))!;
}

/// <summary>
/// Looks series up on AniList, to fill in what the website a manga came from doesn't provide (synopsis, alternative
/// titles, genres, staff, dates). AniList allows 90 requests a minute (temporarily 30); Tranga sends at most one every
/// 2.5 seconds. Only an AniList link or an exact title match is used, never a "close" match.
/// </summary>
internal static class AniListFetcher
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AniListFetcher));
    private const string Endpoint = "https://graphql.anilist.co";

    private const string Fields = """
        id siteUrl format status isAdult averageScore countryOfOrigin chapters volumes genres synonyms
        title { romaji english native }
        description(asHtml: false)
        startDate { year month day }
        endDate { year month day }
        coverImage { extraLarge large }
        tags { name rank isGeneralSpoiler isMediaSpoiler }
        staff(perPage: 25, sort: [RELEVANCE]) { edges { role node { name { full } } } }
        """;

    private const string ByIdQuery = "query ($id: Int) { Media(id: $id, type: MANGA) { " + Fields + " } }";
    private const string SearchQuery = "query ($search: String) { Page(perPage: 10) { media(search: $search, type: MANGA) { " + Fields + " } } }";

    private static readonly Regex AniListUrl = new(@"anilist\.co/manga/(\d+)", RegexOptions.IgnoreCase);

    public static long? IdFromLinks(IEnumerable<Link> links)
    {
        foreach (Link link in links)
            if (AniListUrl.Match(link.LinkUrl) is { Success: true } m && long.TryParse(m.Groups[1].Value, out long id))
                return id;
        return null;
    }

    /// <summary>
    /// Finds the series on AniList. Several exact title matches (e.g. two different series with the same name) are
    /// told apart by year and adult flag; if that's not enough, nothing is returned rather than a guess.
    /// </summary>
    /// <param name="titles">Name first, then alternative titles</param>
    /// <param name="year">Start year, if known</param>
    /// <param name="adult">True if the series is marked R18/adult, if known</param>
    /// <exception cref="ExtraMetadataFetcher.SourceUnavailableException">AniList couldn't be reached</exception>
    public static async Task<AniListData?> Find(Manga manga, IReadOnlyList<string> titles, int? year, bool? adult, CancellationToken ct)
    {
        if (IdFromLinks(manga.Links) is { } id)
        {
            JToken? res = await ExtraMetadataFetcher.AniList(ByIdQuery, new { id }, ct);
            if (res?["data"]?["Media"] is JObject media)
                return Parse(media, "link");
        }

        HashSet<string> wanted = titles.Select(ExtraMetadataFetcher.Normalise).Where(t => t.Length > 0).ToHashSet();
        if (wanted.Count == 0)
            return null;
        // Search by the name, then by the first alternative title that's different (at most two searches)
        foreach (string search in titles.Where(t => !string.IsNullOrWhiteSpace(t))
                     .DistinctBy(ExtraMetadataFetcher.Normalise).Take(2))
        {
            JToken? res = await ExtraMetadataFetcher.AniList(SearchQuery, new { search }, ct);
            if (res?["data"]?["Page"]?["media"] is not JArray results)
                continue;
            List<JObject> exact = results.OfType<JObject>()
                .Where(r => Titles(r).Any(t => wanted.Contains(ExtraMetadataFetcher.Normalise(t))))
                .ToList();
            if (exact.Count > 1 && year is not null)
                exact = exact.Where(r => r["startDate"]?["year"]?.Value<int?>() == year).ToList();
            if (exact.Count > 1 && adult is not null)
                exact = exact.Where(r => r["isAdult"]?.Value<bool?>() == adult).ToList();
            if (exact.Count == 1)
                return Parse(exact[0], "title");
            if (exact.Count > 1)
            {
                Log.Debug($"AniList: {exact.Count} series match \"{search}\" exactly; not guessing");
                return null;
            }
        }
        return null;
    }

    private static IEnumerable<string> Titles(JObject r) =>
        new[] { r["title"]?["romaji"], r["title"]?["english"], r["title"]?["native"] }
            .Concat(r["synonyms"] as JArray ?? new JArray())
            .Select(t => t?.Type == JTokenType.String ? t.Value<string>() : null)
            .Where(t => !string.IsNullOrWhiteSpace(t))!;

    private static string? Str(JToken? t) => t?.Type == JTokenType.String && t.Value<string>() is { Length: > 0 } s ? s.Trim() : null;
    private static int? Int(JToken? t) => t?.Type == JTokenType.Integer ? t.Value<int>() : null;

    private static AniListData Parse(JObject m, string matchedBy)
    {
        List<(string Role, string Name)> staff = (m["staff"]?["edges"] as JArray ?? new JArray())
            .Select(e => (Role: Str(e["role"]) ?? "", Name: Str(e["node"]?["name"]?["full"]) ?? ""))
            .Where(s => s.Name.Length > 0)
            .ToList();
        // Roles look like "Story & Art", "Story", "Art", "Original Story", "Assistant" (ignored), "Touch-up Art & Lettering" (ignored)
        bool IsWriter(string role) => Regex.IsMatch(role, @"^(original )?(story|creator|author)", RegexOptions.IgnoreCase);
        bool IsArtist(string role) => Regex.IsMatch(role, @"^(story & )?art\b|^(character design|illustration)", RegexOptions.IgnoreCase);

        return new AniListData
        {
            Id = m["id"]?.Value<long>() ?? 0,
            Url = Str(m["siteUrl"]) ?? $"https://anilist.co/manga/{m["id"]}",
            MatchedBy = matchedBy,
            TitleRomaji = Str(m["title"]?["romaji"]),
            TitleEnglish = Str(m["title"]?["english"]),
            TitleNative = Str(m["title"]?["native"]),
            Synonyms = (m["synonyms"] as JArray ?? new JArray()).Select(Str).OfType<string>().Distinct().ToList(),
            Description = CleanDescription(Str(m["description"])),
            Format = Str(m["format"]),
            Status = Str(m["status"]),
            StartYear = Int(m["startDate"]?["year"]),
            StartMonth = Int(m["startDate"]?["month"]),
            StartDay = Int(m["startDate"]?["day"]),
            EndYear = Int(m["endDate"]?["year"]),
            EndMonth = Int(m["endDate"]?["month"]),
            EndDay = Int(m["endDate"]?["day"]),
            Chapters = Int(m["chapters"]),
            Volumes = Int(m["volumes"]),
            CountryOfOrigin = Str(m["countryOfOrigin"]),
            IsAdult = m["isAdult"]?.Type == JTokenType.Boolean ? m["isAdult"]!.Value<bool>() : null,
            AverageScore = Int(m["averageScore"]),
            Genres = (m["genres"] as JArray ?? new JArray()).Select(Str).OfType<string>().Distinct().ToList(),
            Tags = (m["tags"] as JArray ?? new JArray())
                .Where(t => t["isGeneralSpoiler"]?.Value<bool?>() != true && t["isMediaSpoiler"]?.Value<bool?>() != true
                            && (t["rank"]?.Value<int?>() ?? 0) >= 60)
                .Select(t => Str(t["name"])).OfType<string>().Distinct().ToList(),
            Writers = staff.Where(s => IsWriter(s.Role)).Select(s => s.Name).Distinct().ToList(),
            Artists = staff.Where(s => IsArtist(s.Role)).Select(s => s.Name).Distinct().ToList(),
            CoverUrl = Str(m["coverImage"]?["extraLarge"]) ?? Str(m["coverImage"]?["large"]),
        };
    }

    /// <summary>AniList descriptions contain &lt;br&gt;, &lt;i&gt; and entities even as "plain text"</summary>
    private static string? CleanDescription(string? html)
    {
        if (html is null)
            return null;
        string text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text.Length > 0 ? text : null;
    }
}
