using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using API.Schema.MangaContext;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace API.ExtraMetadata;

/// <summary>
/// Extra details stored once per Manga (see <see cref="Schema.ExtraMetadataContext.MangaExtraMetadata"/>).
/// </summary>
public class ExtraMetadataData
{
    /// <summary>Series info from MangaUpdates, null if no confident match</summary>
    public MangaUpdatesData? MangaUpdates { get; set; }
    /// <summary>safe | suggestive | erotica | pornographic, null if unknown</summary>
    public string? ContentRating { get; set; }
    /// <summary>"MangaDex" or "MangaUpdates genres"</summary>
    public string? ContentRatingSource { get; set; }
    /// <summary>MangaDex id used for the content rating</summary>
    public string? MangaDexId { get; set; }
    /// <summary>Series info from AniList, null if no confident match</summary>
    public AniListData? AniList { get; set; }
    /// <summary>Which sources were asked (older entries are fetched again once to add new sources)</summary>
    public int Version { get; set; }

    /// <summary>Current <see cref="Version"/>: 2 = MangaUpdates, MangaDex and AniList</summary>
    public const int CurrentVersion = 2;
}

/// <summary>Subset of a MangaUpdates series (api.mangaupdates.com/v1/series/{id})</summary>
public class MangaUpdatesData
{
    public long SeriesId { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>"link" (via the MangaUpdates link from MangaDex) or "title" (exact title match)</summary>
    public string MatchedBy { get; set; } = "link";
    public string? Year { get; set; }
    public string? StartYear { get; set; }
    public string? EndYear { get; set; }
    /// <summary>Raw status text, e.g. "27 Volumes (Ongoing)"</summary>
    public string? Status { get; set; }
    public int? Volumes { get; set; }
    public string? StatusWord { get; set; }
    public bool? Completed { get; set; }
    public bool? Licensed { get; set; }
    public long? LatestChapter { get; set; }
    public double? Rating { get; set; }
    public long? RatingVotes { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Associated { get; set; } = [];
    public List<string> Writers { get; set; } = [];
    public List<string> Artists { get; set; } = [];
    public List<string> OriginalPublishers { get; set; } = [];
    public List<string> EnglishPublishers { get; set; } = [];
    public List<string> Publications { get; set; } = [];
}

/// <summary>
/// Fetches <see cref="ExtraMetadataData"/> from MangaUpdates and MangaDex.
/// MangaUpdates asks API users to space out requests, so those are sent one at a time, at least a second apart.
/// </summary>
public static class ExtraMetadataFetcher
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ExtraMetadataFetcher));

    public static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tranga/2 (self-hosted; extra metadata)");
        return client;
    }

    private static readonly SemaphoreSlim MangaUpdatesGate = new(1, 1);
    private static DateTime _lastMangaUpdatesRequest = DateTime.MinValue;
    private static readonly SemaphoreSlim MangaDexGate = new(1, 1);
    private static DateTime _lastMangaDexRequest = DateTime.MinValue;

    /// <summary>Thrown when a source couldn't be reached, so the result shouldn't be stored as final</summary>
    public class SourceUnavailableException(string message) : Exception(message);

    private static async Task<JToken?> Send(SemaphoreSlim gate, Func<DateTime> getLast, Action<DateTime> setLast, TimeSpan gap,
        HttpMethod method, string url, object? body, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            TimeSpan wait = getLast() + gap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            using HttpRequestMessage request = new(method, url);
            request.Headers.Accept.ParseAdd("application/json");
            if (body is not null)
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SourceUnavailableException($"{url}: {e.Message}");
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                    throw new SourceUnavailableException($"{url}: HTTP {(int)response.StatusCode}");
                if (!response.IsSuccessStatusCode)
                {
                    Log.DebugFormat("{0} returned {1}", url, (int)response.StatusCode);
                    return null;
                }
                return JToken.Parse(await response.Content.ReadAsStringAsync(ct));
            }
        }
        finally
        {
            setLast(DateTime.UtcNow);
            gate.Release();
        }
    }

    private static Task<JToken?> MangaUpdates(HttpMethod method, string path, object? body, CancellationToken ct) =>
        Send(MangaUpdatesGate, () => _lastMangaUpdatesRequest, t => _lastMangaUpdatesRequest = t, TimeSpan.FromSeconds(1),
            method, $"https://api.mangaupdates.com/v1/{path}", body, ct);

    private static readonly SemaphoreSlim AniListGate = new(1, 1);
    private static DateTime _lastAniListRequest = DateTime.MinValue;

    internal static Task<JToken?> AniList(string query, object variables, CancellationToken ct) =>
        Send(AniListGate, () => _lastAniListRequest, t => _lastAniListRequest = t, TimeSpan.FromSeconds(2.5),
            HttpMethod.Post, "https://graphql.anilist.co", new { query, variables }, ct);

    private static Task<JToken?> MangaDex(string path, CancellationToken ct) =>
        Send(MangaDexGate, () => _lastMangaDexRequest, t => _lastMangaDexRequest = t, TimeSpan.FromMilliseconds(300),
            HttpMethod.Get, $"https://api.mangadex.org/{path}", null, ct);

    /// <summary>
    /// Gathers the extra details for a Manga (Links, AltTitles and MangaConnectorIds must be loaded).
    /// </summary>
    /// <exception cref="SourceUnavailableException">MangaUpdates or MangaDex couldn't be reached; try again later</exception>
    public static async Task<ExtraMetadataData> Fetch(Manga manga, CancellationToken ct)
    {
        ExtraMetadataData data = new();

        // Content rating from MangaDex
        data.MangaDexId = MangaDexId(manga);
        if (data.MangaDexId is not null)
        {
            JToken? md = await MangaDex($"manga/{data.MangaDexId}", ct);
            string? rating = md?["data"]?["attributes"]?["contentRating"]?.Value<string>();
            if (rating is "safe" or "suggestive" or "erotica" or "pornographic")
            {
                data.ContentRating = rating;
                data.ContentRatingSource = "MangaDex";
            }
        }

        // Series info from MangaUpdates
        string matchedBy = "link";
        long? seriesId = MangaUpdatesIdFromLinks(manga.Links);
        if (seriesId is null)
        {
            matchedBy = "title";
            seriesId = await FindByTitle([manga.Name, ..manga.AltTitles.Select(t => t.Title)], ct);
        }
        if (seriesId is not null && await MangaUpdates(HttpMethod.Get, $"series/{seriesId}", null, ct) is JObject series)
        {
            data.MangaUpdates = ParseSeries(series, matchedBy);
            if (data.ContentRating is null && RatingFromGenres(data.MangaUpdates.Genres) is { } estimated)
            {
                data.ContentRating = estimated;
                data.ContentRatingSource = "MangaUpdates genres";
            }
        }

        // Series info from AniList (fills in what the source website and MangaUpdates don't have)
        // "Destiny Lovers (2020)" / "Destiny Lovers (2)": Tranga's name for a second series with the same title
        string name = manga.Name;
        int? yearInName = null;
        if (Regex.Match(name, @"^(.+?) \((\d{4}|\d{1,2})\)$") is { Success: true } suffix)
        {
            name = suffix.Groups[1].Value;
            if (suffix.Groups[2].Value.Length == 4)
                yearInName = int.Parse(suffix.Groups[2].Value);
        }
        int? year = manga.Year is > 0 and < 10000 ? (int)manga.Year.Value
            : yearInName ?? (int.TryParse(data.MangaUpdates?.StartYear ?? data.MangaUpdates?.Year, out int y) ? y : null);
        // Tells apart e.g. a series and its adult version that share a title
        bool? adult = MangaContext.EditionTags(manga.Name).Contains("r18") ? true
            : data.ContentRating switch
            {
                "pornographic" or "erotica" => true,
                "safe" or "suggestive" when data.ContentRatingSource == "MangaDex" => false,
                _ => null
            };
        List<string> titles = [name, ..manga.AltTitles.Select(t => t.Title), ..data.MangaUpdates?.Associated ?? []];
        if (data.MangaUpdates?.Title is { Length: > 0 } muTitle)
            titles.Insert(1, muTitle);
        data.AniList = await AniListFetcher.Find(manga, titles, year, adult, ct);
        if (data.ContentRating is null && data.AniList?.IsAdult == true)
        {
            data.ContentRating = "pornographic";
            data.ContentRatingSource = "AniList";
        }

        data.Version = ExtraMetadataData.CurrentVersion;
        return data;
    }

    private static readonly Regex MangaDexUuid = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase);
    private static readonly Regex MangaDexUrl = new(@"mangadex\.org/title/([0-9a-f-]{36})", RegexOptions.IgnoreCase);

    private static string? MangaDexId(Manga manga)
    {
        foreach (MangaConnectorId<Manga> id in manga.MangaConnectorIds)
        {
            if (id.MangaConnectorName.Equals("MangaDex", StringComparison.InvariantCultureIgnoreCase) && MangaDexUuid.IsMatch(id.IdOnConnectorSite))
                return id.IdOnConnectorSite.ToLowerInvariant();
            if (id.WebsiteUrl is not null && MangaDexUrl.Match(id.WebsiteUrl) is { Success: true } m)
                return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// MangaUpdates series id from a stored link (MangaDex provides one). New-style ids are base-36 slugs
    /// (e.g. "pb8uwds") or already-decoded numbers; old 1-6 digit ids can't be used with the API.
    /// </summary>
    public static long? MangaUpdatesIdFromLinks(IEnumerable<Link> links)
    {
        foreach (Link link in links)
        {
            if (!link.LinkUrl.Contains("mangaupdates.com", StringComparison.InvariantCultureIgnoreCase))
                continue;
            Match m = Regex.Match(link.LinkUrl, @"[?&]id=([0-9a-z]+)", RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(link.LinkUrl, @"/series/([0-9a-z]+)", RegexOptions.IgnoreCase);
            if (!m.Success)
                continue;
            string raw = m.Groups[1].Value.ToLowerInvariant();
            if (raw.All(char.IsAsciiDigit))
            {
                if (raw.Length >= 8 && long.TryParse(raw, out long numeric))
                    return numeric;
                continue; // legacy id
            }
            if (raw.Length > 12)
                continue;
            long id = 0;
            foreach (char c in raw)
                id = id * 36 + (char.IsAsciiDigit(c) ? c - '0' : c - 'a' + 10);
            if (id > 0)
                return id;
        }
        return null;
    }

    private static string Decode(string? s) => WebUtility.HtmlDecode(s ?? "").Trim();

    internal static string Normalise(string s)
    {
        string decomposed = Decode(s).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder sb = new();
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Only an exact (normalised) title match is accepted, to avoid storing details of the wrong series</summary>
    private static async Task<long?> FindByTitle(string[] titles, CancellationToken ct)
    {
        HashSet<string> wanted = titles.Select(Normalise).Where(t => t.Length > 0).ToHashSet();
        if (wanted.Count == 0)
            return null;
        JToken? res = await MangaUpdates(HttpMethod.Post, "series/search", new { search = titles[0], perpage = 10 }, ct);
        if (res?["results"] is not JArray results)
            return null;
        foreach (JToken r in results)
        {
            string? title = r["record"]?["title"]?.Value<string>();
            string? hit = r["hit_title"]?.Value<string>();
            if ((title is not null && wanted.Contains(Normalise(title))) || (hit is not null && wanted.Contains(Normalise(hit))))
                return r["record"]?["series_id"]?.Value<long?>();
        }
        return null;
    }

    private static List<string> Distinct(IEnumerable<string?> values) =>
        values.Select(Decode).Where(v => v.Length > 0).DistinctBy(v => v.ToLowerInvariant()).ToList();

    private static MangaUpdatesData ParseSeries(JObject s, string matchedBy)
    {
        string? year = Decode(s["year"]?.Value<string>()) is { Length: > 0 } y ? y : null;
        string[] years = year?.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        string? status = Decode(s["status"]?.Value<string>()) is { Length: > 0 } st ? st : null;
        Match volumes = status is null ? Match.Empty : Regex.Match(status, @"(\d+)\s+Volumes?", RegexOptions.IgnoreCase);
        Match word = status is null ? Match.Empty : Regex.Match(status, @"\(([^)]+)\)");
        long? latest = s["latest_chapter"]?.Type == JTokenType.Integer ? s["latest_chapter"]!.Value<long>() : null;

        IEnumerable<JToken> authors = s["authors"] as JArray ?? new JArray();
        IEnumerable<JToken> publishers = s["publishers"] as JArray ?? new JArray();
        IEnumerable<JToken> publications = s["publications"] as JArray ?? new JArray();
        bool Role(JToken a, string pattern) => Regex.IsMatch(a["type"]?.Value<string>() ?? "", pattern, RegexOptions.IgnoreCase);

        return new MangaUpdatesData
        {
            SeriesId = s["series_id"]?.Value<long>() ?? 0,
            Url = s["url"]?.Value<string>() ?? "",
            Title = Decode(s["title"]?.Value<string>()),
            MatchedBy = matchedBy,
            Year = year,
            StartYear = years.ElementAtOrDefault(0),
            EndYear = years.ElementAtOrDefault(1),
            Status = status,
            Volumes = volumes.Success ? int.Parse(volumes.Groups[1].Value) : null,
            StatusWord = word.Success ? word.Groups[1].Value : null,
            Completed = s["completed"]?.Type == JTokenType.Boolean ? s["completed"]!.Value<bool>() : null,
            Licensed = s["licensed"]?.Type == JTokenType.Boolean ? s["licensed"]!.Value<bool>() : null,
            LatestChapter = latest > 0 ? latest : null,
            Rating = (s["bayesian_rating"]?.Type is JTokenType.Float or JTokenType.Integer) ? s["bayesian_rating"]!.Value<double>() : null,
            RatingVotes = s["rating_votes"]?.Type == JTokenType.Integer ? s["rating_votes"]!.Value<long>() : null,
            Genres = Distinct((s["genres"] as JArray ?? new JArray()).Select(g => g["genre"]?.Value<string>())),
            Associated = Distinct((s["associated"] as JArray ?? new JArray()).Select(a => a["title"]?.Value<string>())),
            Writers = Distinct(authors.Where(a => Role(a, "author|story|original")).Select(a => a["name"]?.Value<string>())),
            Artists = Distinct(authors.Where(a => Role(a, "artist|art|illustrat")).Select(a => a["name"]?.Value<string>())),
            OriginalPublishers = Distinct(publishers.Where(p => Role(p, "original")).Select(p => p["publisher_name"]?.Value<string>())),
            EnglishPublishers = Distinct(publishers.Where(p => Role(p, "english")).Select(p => p["publisher_name"]?.Value<string>())),
            Publications = Distinct(publications.Select(p => p["publication_name"]?.Value<string>())),
        };
    }

    private static string? RatingFromGenres(List<string> genres)
    {
        HashSet<string> g = genres.Select(x => x.ToLowerInvariant()).ToHashSet();
        if (g.Contains("hentai") || g.Contains("adult")) return "pornographic";
        if (g.Contains("smut") || g.Contains("mature")) return "erotica";
        if (g.Contains("ecchi")) return "suggestive";
        return null;
    }
}
