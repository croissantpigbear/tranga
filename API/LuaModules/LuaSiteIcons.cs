using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using log4net;

namespace API.LuaModules;

/// <summary>
/// Icons of websites added from FMD2 modules. Many sites don't serve /favicon.ico, or refuse images requested by
/// other pages, so Tranga fetches the icon itself (the one the site's home page declares, then /favicon.ico, then
/// Google's favicon service), keeps it in lua/icons for 30 days, and serves it from its own address.
/// </summary>
public static class LuaSiteIcons
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LuaSiteIcons));
    private static string Folder => Path.Join(LuaModuleStore.Root, "icons");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly ConcurrentDictionary<string, DateTime> NotFoundUntil = new();
    private const int MaxBytes = 512 * 1024;

    /// <summary>Address the website uses for a site's icon (served by the Tranga API through the website)</summary>
    public static string UrlFor(string siteName) => $"/v2/LuaModules/Sites/{Uri.EscapeDataString(siteName)}/Icon";

    private static string SafeName(string siteName) => Regex.Replace(siteName, @"[^A-Za-z0-9_.-]", "_");

    /// <summary>The icon file and its content type, fetching it if needed; null if the site has none Tranga can get</summary>
    public static async Task<(string Path, string ContentType)?> Get(LuaMangaConnector site, CancellationToken ct)
    {
        string baseName = Path.Join(Folder, SafeName(site.Name));
        if (Existing(baseName) is { } cached && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached.Path) < TimeSpan.FromDays(30))
            return cached;
        if (NotFoundUntil.TryGetValue(site.Name, out DateTime until) && until > DateTime.UtcNow)
            return Existing(baseName);

        SemaphoreSlim gate = Gates.GetOrAdd(site.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Existing(baseName) is { } again && DateTime.UtcNow - File.GetLastWriteTimeUtc(again.Path) < TimeSpan.FromDays(30))
                return again;
            if (await Fetch(site.RootUrl, ct) is { } icon)
            {
                Directory.CreateDirectory(Folder);
                foreach (string old in Directory.EnumerateFiles(Folder, Path.GetFileName(baseName) + ".*"))
                    File.Delete(old);
                string path = $"{baseName}.{icon.Extension}";
                await File.WriteAllBytesAsync(path, icon.Bytes, ct);
                return (path, icon.ContentType);
            }
            NotFoundUntil[site.Name] = DateTime.UtcNow.AddDays(1);
            return Existing(baseName); // an older icon is better than none
        }
        finally
        {
            gate.Release();
        }
    }

    private static (string Path, string ContentType)? Existing(string baseName)
    {
        if (!Directory.Exists(Folder))
            return null;
        string? path = Directory.EnumerateFiles(Folder, Path.GetFileName(baseName) + ".*").FirstOrDefault();
        return path is null ? null : (path, ContentTypeOf(Path.GetExtension(path)));
    }

    private static string ContentTypeOf(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => "image/png",
        "svg" => "image/svg+xml",
        "gif" => "image/gif",
        "jpg" => "image/jpeg",
        "webp" => "image/webp",
        _ => "image/x-icon"
    };

    private sealed record Icon(byte[] Bytes, string ContentType, string Extension);

    private static async Task<Icon?> Fetch(string rootUrl, CancellationToken ct)
    {
        List<string> candidates = [];
        try
        {
            LuaHttp.Response home = await Task.Run(() => LuaHttp.Send("GET", rootUrl, "", "", "", "", ct), ct);
            if (home.Status is >= 200 and < 300 && home.Body.Length > 0)
            {
                HtmlDocument doc = new();
                doc.LoadHtml(home.Body);
                Uri baseUri = new(home.FinalUrl);
                // Biggest declared icon first (apple-touch-icons are usually 180px)
                candidates.AddRange((doc.DocumentNode.SelectNodes("//link[@href]") ?? Enumerable.Empty<HtmlNode>())
                    .Select(l => (Rel: l.GetAttributeValue("rel", "").ToLowerInvariant(), Href: WebUtilityDecode(l.GetAttributeValue("href", "")),
                        Size: SizeOf(l.GetAttributeValue("sizes", ""))))
                    .Where(l => l.Rel.Contains("icon") && l.Href.Length > 0)
                    .OrderByDescending(l => Math.Min(l.Size, 256))
                    .Select(l => Uri.TryCreate(baseUri, l.Href, out Uri? u) ? u.ToString() : null)
                    .OfType<string>());
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug($"Couldn't read {rootUrl} for its icon: {e.Message}");
        }
        if (Uri.TryCreate(rootUrl, UriKind.Absolute, out Uri? root))
        {
            candidates.Add($"{root.Scheme}://{root.Host}/favicon.ico");
            candidates.Add($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(root.Host)}&sz=64");
        }

        foreach (string url in candidates.Distinct().Take(6))
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", LuaHttp.DefaultUserAgent);
                request.Headers.TryAddWithoutValidation("Referer", rootUrl);
                using HttpResponseMessage response = await LuaHttp.Client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    continue;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length is 0 or > MaxBytes || Sniff(bytes, response.Content.Headers.ContentType?.MediaType) is not { } type)
                    continue;
                return new Icon(bytes, type.ContentType, type.Extension);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Debug($"Icon {url}: {e.Message}");
            }
        }
        return null;
    }

    private static string WebUtilityDecode(string s) => System.Net.WebUtility.HtmlDecode(s).Trim();

    private static int SizeOf(string sizes) =>
        Regex.Match(sizes, @"(\d+)x\d+") is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n) ? n : sizes == "any" ? 128 : 16;

    /// <summary>Accepts only real images (by their first bytes), never an HTML error page</summary>
    private static (string ContentType, string Extension)? Sniff(byte[] b, string? declared)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ("image/png", "png");
        if (b.Length > 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return ("image/x-icon", "ico");
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ("image/jpeg", "jpg");
        if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return ("image/gif", "gif");
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return ("image/webp", "webp");
        string head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512)).TrimStart();
        if (declared == "image/svg+xml" || head.StartsWith("<svg") || (head.StartsWith("<?xml") && head.Contains("<svg")))
            return ("image/svg+xml", "svg");
        return null;
    }
}
