using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using API.LuaModules;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using Newtonsoft.Json.Linq;

namespace API.MangaConnectors;

/// <summary>
/// A website added by uploading an FMD2 Lua module. Manga pages, chapter lists and page images come from the module;
/// search uses the module's title list (built in the background, see <see cref="LuaTitleList"/>).
/// </summary>
public class LuaMangaConnector : MangaConnector
{
    public string FileName { get; }
    public string ModuleId { get; }
    public string RootUrl { get; }
    public string Category { get; }
    public string ModuleName { get; }
    public bool HasTitleList { get; }
    public bool HasImageHeaders { get; }

    private static readonly TimeSpan InfoBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PagesBudget = TimeSpan.FromMinutes(4);

    /// <summary>Headers to send with each page image (from the module's BeforeDownloadImage), by image URL</summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> ImageHeaders = new();

    internal LuaMangaConnector(string connectorName, string fileName, LuaModuleMeta meta)
        : base(connectorName, [LanguageOf(meta.Category)], BaseUrisOf(meta.RootUrl), LuaSiteIcons.UrlFor(connectorName))
    {
        this.downloadClient = new HttpDownloadClient();
        FileName = fileName;
        ModuleId = meta.Id;
        RootUrl = meta.RootUrl.TrimEnd('/');
        Category = meta.Category;
        ModuleName = meta.Name;
        HasTitleList = meta.OnGetNameAndLink.Length > 0;
        HasImageHeaders = meta.OnBeforeDownloadImage.Length > 0;
    }

    private static string[] BaseUrisOf(string rootUrl)
    {
        if (!Uri.TryCreate(rootUrl, UriKind.Absolute, out Uri? uri))
            return [];
        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return [Regex.Escape(host), Regex.Escape("www." + host)];
    }


    /// <summary>FMD2 category ("English", "Spanish-Scanlation", "Raw") → language code</summary>
    internal static string LanguageOf(string category)
    {
        string c = category.Split('-', ' ')[0].ToLowerInvariant();
        return c switch
        {
            "english" or "webcomics" or "h-sites" => "en",
            "spanish" => "es",
            "portuguese" => "pt-br",
            "french" => "fr",
            "german" => "de",
            "italian" => "it",
            "russian" => "ru",
            "ukrainian" => "uk",
            "polish" => "pl",
            "turkish" => "tr",
            "indonesian" => "id",
            "vietnamese" => "vi",
            "thai" => "th",
            "arabic" => "ar",
            "raw" or "japanese" => "ja",
            "korean" => "ko",
            "chinese" => "zh",
            _ => "en"
        };
    }

    private string Absolute(string url)
    {
        url = url.Trim();
        if (url.StartsWith("//"))
            return "https:" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? abs) && abs.Scheme is "http" or "https")
            return url;
        return RootUrl + (url.StartsWith('/') ? url : "/" + url);
    }

    /// <summary>
    /// Id stored for a URL on this site: the path (and query) if it's on the site's host, else the full URL.
    /// This is also the form FMD2 hands chapter links to modules ("/series/chapters/1"): some modules build API URLs
    /// from it (Asura Scans: API_URL .. URL), so they don't work with a full URL.
    /// </summary>
    internal string IdOf(string url)
    {
        string absolute = Absolute(url);
        if (Uri.TryCreate(absolute, UriKind.Absolute, out Uri? uri) && Uri.TryCreate(RootUrl, UriKind.Absolute, out Uri? root)
            && uri.Host.Equals(root.Host, StringComparison.OrdinalIgnoreCase))
            return uri.PathAndQuery;
        return absolute;
    }

    private string Source() => LuaModuleStore.ReadModule(FileName)
                               ?? throw new LuaModuleException($"Module file {FileName} is missing");

    internal LuaMangaInfo GetInfo(string url, CancellationToken ct = default)
    {
        using LuaModuleRunner runner = LuaModuleRunner.Load(Source(), FileName, InfoBudget, ct);
        JObject result = JObject.Parse(runner.Call("__fmd_info", ModuleId, Absolute(url)));
        return LuaMangaInfo.From(result, runner.Host);
    }

    private (Manga, MangaConnectorId<Manga>)? MangaFromInfo(LuaMangaInfo info, string url)
    {
        string name = info.Title.Trim();
        if (name.Length == 0)
            return null;
        if (name.Length > 512)
            name = name[..512];
        MangaReleaseStatus status = info.Status switch
        {
            "0" => MangaReleaseStatus.Completed,
            "1" => MangaReleaseStatus.Continuing,
            "2" => MangaReleaseStatus.OnHiatus,
            "3" => MangaReleaseStatus.Cancelled,
            _ => MangaReleaseStatus.Unreleased
        };
        List<Author> authors = SplitList(info.Authors + "," + info.Artists)
            .Select(a => a.Length > 128 ? a[..128] : a)
            .DistinctBy(a => a.ToLowerInvariant())
            .Select(a => new Author(a)).ToList();
        List<MangaTag> tags = SplitList(info.Genres)
            .Select(g => g.Length > 64 ? g[..64] : g)
            .DistinctBy(g => g.ToLowerInvariant())
            .Select(g => new MangaTag(g)).ToList();
        List<AltTitle> altTitles = info.AltTitles
            .Split([',', ';', '|', '\n', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0 && !t.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Length > 256 ? t[..256] : t)
            .Distinct().Take(20)
            .Select(t => new AltTitle("un", t)).ToList();
        string cover = info.CoverLink.Length > 0 ? Absolute(info.CoverLink) : "";
        string description = System.Net.WebUtility.HtmlDecode(info.Summary).Trim();

        Manga manga = new(name, description, cover, status, authors, tags, [], altTitles);
        string id = IdOf(url);
        if (id.Length > 256)
            return null;
        string website = Absolute(url);
        MangaConnectorId<Manga> mcId = new(manga, this, id, website.Length <= 512 ? website : null);
        manga.MangaConnectorIds.Add(mcId);
        return (manga, mcId);
    }

    private static IEnumerable<string> SplitList(string s) =>
        s.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 0);

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        if (!Enabled || !HasTitleList)
            return [];
        List<(string Name, string Url)> matches = LuaTitleList.Search(Name, mangaSearchName, 8);
        List<(Manga, MangaConnectorId<Manga>)> results = [];
        DateTime stopAt = DateTime.UtcNow.AddSeconds(45);
        foreach ((string _, string url) in matches)
        {
            if (DateTime.UtcNow > stopAt)
                break;
            try
            {
                if (MangaFromInfo(GetInfo(url), url) is { } m)
                    results.Add(m);
            }
            catch (Exception e)
            {
                Log.Debug($"Search result {url} failed: {e.Message}");
            }
        }
        return results.ToArray();
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        if (!Enabled)
            return null;
        try
        {
            return MangaFromInfo(GetInfo(url), url);
        }
        catch (Exception e)
        {
            Log.Info($"{Name}: couldn't read {url}: {e.Message}");
            return null;
        }
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite) => GetMangaFromUrl(mangaIdOnSite);

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId, string? language = null)
    {
        LuaMangaInfo info;
        try
        {
            info = GetInfo(mangaId.IdOnConnectorSite);
        }
        catch (Exception e)
        {
            Log.Warn($"{Name}: couldn't get chapters of {mangaId.IdOnConnectorSite}: {e.Message}");
            return [];
        }

        List<(Chapter, MangaConnectorId<Chapter>)> chapters = [];
        HashSet<string> numbers = [];
        int count = Math.Min(info.ChapterLinks.Count, info.ChapterNames.Count > 0 ? info.ChapterNames.Count : info.ChapterLinks.Count);
        for (int i = 0; i < count; i++)
        {
            string link = info.ChapterLinks[i];
            string chapterName = i < info.ChapterNames.Count ? info.ChapterNames[i] : "";
            if (string.IsNullOrWhiteSpace(link))
                continue;
            (string number, int? volume, string? title) = ParseChapterName(chapterName, i + 1);
            if (!numbers.Add(number))
                continue; // same chapter from another group: keep the first
            string id = IdOf(link);
            if (id.Length > 256)
                continue;
            try
            {
                Chapter chapter = new(mangaId.Obj, number, volume, title);
                string website = Absolute(link);
                MangaConnectorId<Chapter> chId = new(chapter, this, id, website.Length <= 512 ? website : null);
                chapter.MangaConnectorIds.Add(chId);
                chapters.Add((chapter, chId));
            }
            catch (ArgumentException e)
            {
                Log.Debug($"{Name}: skipped chapter '{chapterName}': {e.Message}");
            }
        }
        Log.Info($"{Name}: {chapters.Count} chapters for {mangaId.Obj?.Name}");
        return chapters.OrderBy(c => c.Item1, new Chapter.ChapterComparer()).ToArray();
    }

    private static readonly Regex VolumeRex = new(@"\b(?:vol(?:ume)?|tome|tomo|season|s)\.?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChapterRex = new(@"(?:chapter|chap|ch|episode|ep|cap[ií]tulo|cap|chapitre|kapitel|bab|chương)\.?\s*#?\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CjkChapterRex = new(@"第\s*(\d+(?:\.\d+)?)\s*[话話章回]", RegexOptions.Compiled);
    private static readonly Regex NumberRex = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex TitleRex = new(@"^(?:.*?(?:chapter|chap|ch|episode|ep)\.?\s*#?\s*\d+(?:[.,]\d+)?)\s*[:\-–—]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Chapter number, volume and title from a chapter name like "Vol.2 Chapter 10.5 - The Return"</summary>
    internal static (string Number, int? Volume, string? Title) ParseChapterName(string name, int position)
    {
        name = System.Net.WebUtility.HtmlDecode(name ?? "").Trim();
        int? volume = null;
        Match vol = VolumeRex.Match(name);
        if (vol.Success && int.TryParse(vol.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            volume = v;

        string? number = null;
        Match ch = ChapterRex.Match(name);
        if (ch.Success)
            number = ch.Groups[1].Value.Replace(',', '.');
        else if (CjkChapterRex.Match(name) is { Success: true } cjk)
            number = cjk.Groups[1].Value;
        else
        {
            // Last number that isn't the volume
            string withoutVolume = vol.Success ? name.Remove(vol.Index, vol.Length) : name;
            MatchCollection all = NumberRex.Matches(withoutVolume);
            if (all.Count > 0)
                number = all[^1].Value;
        }
        number = NormalizeNumber(number) ?? position.ToString(CultureInfo.InvariantCulture);

        string? title = null;
        Match t = TitleRex.Match(name);
        if (t.Success)
        {
            title = t.Groups[1].Value.Trim();
            if (title.Length > 256)
                title = title[..256];
            if (title.Length == 0)
                title = null;
        }
        return (number, volume, title);
    }

    /// <summary>"010.50" → "10.5"; null if not a number Tranga accepts</summary>
    private static string? NormalizeNumber(string? number)
    {
        if (string.IsNullOrEmpty(number))
            return null;
        string[] parts = number.Split('.');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p.Length > 6 || !p.All(char.IsAsciiDigit)))
            return null;
        string whole = int.Parse(parts[0], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (parts.Length == 1)
            return whole;
        string fraction = parts[1].TrimEnd('0');
        return fraction.Length == 0 ? whole : $"{whole}.{int.Parse(fraction, CultureInfo.InvariantCulture)}";
    }

    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
        string chapterUrl = Absolute(chapterId.IdOnConnectorSite);
        try
        {
            using LuaModuleRunner runner = LuaModuleRunner.Load(Source(), FileName, PagesBudget, CancellationToken.None);
            JObject result = JObject.Parse(runner.Call("__fmd_pages", ModuleId, IdOf(chapterId.IdOnConnectorSite)));
            string[] pages = (result["pages"] as JArray ?? new JArray())
                .Select(p => p.ToString().Trim())
                .Where(p => p.Length > 0)
                .Select(Absolute)
                .ToArray();
            if (pages.Length == 0)
                Log.Warn($"{Name}: no pages found for {chapterUrl}. Module messages: {string.Join(" | ", runner.Host.Messages)}");
            RememberImageHeaders(pages, chapterUrl);
            return pages;
        }
        catch (Exception e)
        {
            Log.Warn($"{Name}: couldn't get pages of {chapterUrl}: {e.Message}");
            return [];
        }
    }

    /// <summary>Asks the module which headers (Referer, cookies…) its images need</summary>
    private void RememberImageHeaders(string[] pages, string chapterUrl)
    {
        if (pages.Length == 0)
            return;
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase) { ["Referer"] = RootUrl + "/" };
        if (HasImageHeaders)
        {
            try
            {
                using LuaModuleRunner runner = LuaModuleRunner.Load(Source(), FileName, InfoBudget, CancellationToken.None);
                JObject result = JObject.Parse(runner.Call("__fmd_beforedownload", ModuleId, pages[0], chapterUrl));
                if (result["headers"] is JObject h)
                    foreach (JProperty p in h.Properties())
                        headers[p.Name] = p.Value.ToString();
            }
            catch (Exception e)
            {
                Log.Debug($"{Name}: BeforeDownloadImage failed: {e.Message}");
            }
        }
        if (ImageHeaders.Count > 20_000)
            ImageHeaders.Clear();
        foreach (string page in pages)
            ImageHeaders[page] = headers;
    }

    public override async Task<Stream?> DownloadImage(string imageUrl, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, imageUrl);
        Dictionary<string, string> headers = ImageHeaders.TryRemove(imageUrl, out Dictionary<string, string>? h)
            ? h
            : new Dictionary<string, string> { ["Referer"] = RootUrl + "/" };
        bool userAgent = false;
        foreach ((string name, string value) in headers)
        {
            if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                userAgent = true;
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (!userAgent)
            request.Headers.TryAddWithoutValidation("User-Agent", LuaHttp.DefaultUserAgent);
        try
        {
            HttpResponseMessage response = await LuaHttp.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"{Name}: image {imageUrl} returned {(int)response.StatusCode}");
                response.Dispose();
                return null;
            }
            MemoryStream ms = new();
            await response.Content.CopyToAsync(ms, ct);
            response.Dispose();
            ms.Position = 0;
            return ms;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Debug($"{Name}: image {imageUrl} failed: {e.Message}");
            return null;
        }
    }
}

/// <summary>What an FMD2 module's GetInfo returned</summary>
internal sealed class LuaMangaInfo
{
    public int Code { get; init; }
    public string Title { get; init; } = "";
    public string AltTitles { get; init; } = "";
    public string CoverLink { get; init; } = "";
    public string Authors { get; init; } = "";
    public string Artists { get; init; } = "";
    public string Genres { get; init; } = "";
    public string Status { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> ChapterLinks { get; init; } = [];
    public List<string> ChapterNames { get; init; } = [];
    public List<string> Messages { get; init; } = [];
    public List<string> Requests { get; init; } = [];

    internal static LuaMangaInfo From(JObject o, LuaHost host)
    {
        static List<string> List(JToken? t) => t is JArray a ? a.Select(x => x.ToString()).ToList() : [];
        return new LuaMangaInfo
        {
            Code = o["code"]?.Value<int>() ?? 0,
            Title = o["title"]?.ToString() ?? "",
            AltTitles = o["altTitles"]?.ToString() ?? "",
            CoverLink = o["coverLink"]?.ToString() ?? "",
            Authors = o["authors"]?.ToString() ?? "",
            Artists = o["artists"]?.ToString() ?? "",
            Genres = o["genres"]?.ToString() ?? "",
            Status = o["status"]?.ToString() ?? "",
            Summary = o["summary"]?.ToString() ?? "",
            ChapterLinks = List(o["chapterLinks"]),
            ChapterNames = List(o["chapterNames"]),
            Messages = host.Messages.ToList(),
            Requests = host.RequestLog.ToList()
        };
    }
}
