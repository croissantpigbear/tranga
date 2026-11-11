using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using log4net;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace API.MangaConnectors;

[PrimaryKey("Name")]
public abstract class MangaConnector(string name, string[] supportedLanguages, string[] baseUris, string iconUrl)
{
    [NotMapped] internal IDownloadClient downloadClient { get; init; } = null!;
    [NotMapped] protected ILog Log { get; init; } = LogManager.GetLogger(name);
    [StringLength(32)] public string Name { get; init; } = name;
    [StringLength(8)] public string[] SupportedLanguages { get; init; } = supportedLanguages;
    [StringLength(2048)] public string IconUrl { get; init; } = iconUrl;
    [StringLength(256)] public string[] BaseUris { get; init; } = baseUris;
    public bool Enabled { get; internal set; } = true;

    /// <summary>Most chapters this website allows downloading at once (0: no limit of its own, use Tranga's setting)</summary>
    [NotMapped] public virtual int MaxConcurrentDownloads => 0;
    
    public abstract (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName);

    public abstract (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url);

    public abstract (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite);
    
    public abstract (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId,
        string? language = null);

    internal abstract string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId);

    public bool UrlMatchesConnector(string url) => BaseUris.Any(baseUri => Regex.IsMatch(url, "https?://" + baseUri + "/.*"));
    
    /// <summary>
    /// A cover link as a full address: websites (and FMD2 modules) sometimes give "//cdn.site/x.jpg" or "/img/x.jpg".
    /// Those are completed from the series' page on the website (or the website's address). Null if it can't be.
    /// </summary>
    internal static string? AbsoluteUrl(string? url, string? pageUrl, IEnumerable<string> baseUris)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url))
            return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? abs) && abs.Scheme is "http" or "https" && abs.Host.Length > 0)
            return abs.ToString();
        if (url.StartsWith("//"))
            return Uri.TryCreate("https:" + url, UriKind.Absolute, out Uri? p) && p.Host.Length > 0 ? p.ToString() : null;
        string? site = pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? page) && page.Scheme is "http" or "https"
            ? page.ToString()
            : baseUris.Select(b => $"https://{b.Trim('/')}/").FirstOrDefault(b => Uri.TryCreate(b, UriKind.Absolute, out _));
        if (site is null)
            return null;
        Uri siteUri = new(site);
        // "/img/x.jpg" is built by hand: on Linux .NET would read it as a file path
        string joined = url.StartsWith('/') ? $"{siteUri.Scheme}://{siteUri.Authority}{url}" : new Uri(siteUri, url).ToString();
        return Uri.TryCreate(joined, UriKind.Absolute, out Uri? combined) && combined.Scheme is "http" or "https" && combined.Host.Length > 0
            ? combined.ToString()
            : null;
    }

    /// <summary>A cover picture on a series page (for websites whose own cover link is missing); null if not supported</summary>
    internal virtual string? FindCoverOnPage(string pageUrl) => null;

    internal string? SaveCoverImageToCache(MangaConnectorId<Manga> mangaId, int retries = 3)
    {
        if(retries < 0)
            return null;

        // A saved link that isn't usable (e.g. empty because the module missed it): look on the series page instead
        string? coverUrl = AbsoluteUrl(mangaId.Obj.CoverUrl, mangaId.WebsiteUrl, BaseUris);
        if (coverUrl is null && mangaId.WebsiteUrl is { Length: > 0 } page)
            coverUrl = FindCoverOnPage(page);
        if (coverUrl is null)
        {
            Log.Warn($"{Name}: the cover link of {mangaId.Obj.Name} isn't a web address (\"{mangaId.Obj.CoverUrl}\"); no cover.");
            return null;
        }
        Uri coverUri = new(coverUrl);
        string extension = Path.GetExtension(coverUri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (extension.Length is 0 or > 5 || extension == "avif")
            extension = "jpg";
        string filename = $"{coverUri.Host}-{mangaId.ObjId}.{mangaId.MangaConnectorName}.{extension}".CleanNameForWindows();
        string saveImagePath = Path.Join(TrangaSettings.CoverImageCacheOriginal, filename);

        if (File.Exists(saveImagePath))
            return filename;

        // Tranga can't read AVIF images, so for a ….avif cover try the same picture as .jpg / .webp first (sites that
        // offer AVIF usually keep those next to it)
        List<string> candidates = [];
        if (coverUri.AbsolutePath.EndsWith(".avif", StringComparison.OrdinalIgnoreCase))
            foreach (string ext in new[] { ".jpg", ".webp", ".png" })
                candidates.Add(new UriBuilder(coverUri) { Path = coverUri.AbsolutePath[..^5] + ext }.Uri.ToString());
        candidates.Add(coverUrl);

        string? lastProblem = null;
        foreach (string candidate in candidates)
        {
            byte[]? imageBytes = null;
            for (int attempt = 0; attempt <= Math.Max(0, retries) && imageBytes is null; attempt++)
            {
                try
                {
                    HttpResponseMessage coverResult = downloadClient
                        .MakeRequest(candidate, RequestType.MangaCover, $"{coverUri.Scheme}://{coverUri.Host}/").Result;
                    if (!coverResult.IsSuccessStatusCode)
                    {
                        lastProblem = $"{(int)coverResult.StatusCode} from {candidate}";
                        if ((int)coverResult.StatusCode is 404 or 410)
                            break; // that version doesn't exist; try the next
                        continue;
                    }
                    using MemoryStream ms = new();
                    coverResult.Content.ReadAsStream().CopyTo(ms);
                    imageBytes = ms.ToArray();
                }
                catch (Exception e)
                {
                    lastProblem = e.GetBaseException().Message;
                }
            }
            if (imageBytes is null)
                continue;

            try
            {
                using Image image = Image.Load(imageBytes);
                Directory.CreateDirectory(TrangaSettings.CoverImageCacheOriginal);
                File.WriteAllBytes(saveImagePath, imageBytes);

                Directory.CreateDirectory(TrangaSettings.CoverImageCacheLarge);
                using Image large = image.Clone(x => x.Resize(new ResizeOptions
                    { Size = Constants.ImageLgSize, Mode = ResizeMode.Max }));
                large.SaveAsJpeg(Path.Join(TrangaSettings.CoverImageCacheLarge, filename), new (){ Quality = 40 });

                Directory.CreateDirectory(TrangaSettings.CoverImageCacheMedium);
                using Image medium = image.Clone(x => x.Resize(new ResizeOptions
                    { Size = Constants.ImageMdSize, Mode = ResizeMode.Max }));
                medium.SaveAsJpeg(Path.Join(TrangaSettings.CoverImageCacheMedium, filename), new (){ Quality = 40 });

                Directory.CreateDirectory(TrangaSettings.CoverImageCacheSmall);
                using Image small = image.Clone(x => x.Resize(new ResizeOptions
                    { Size = Constants.ImageSmSize, Mode = ResizeMode.Max }));
                small.SaveAsJpeg(Path.Join(TrangaSettings.CoverImageCacheSmall, filename), new (){ Quality = 40 });
                return filename;
            }
            catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
            {
                // e.g. an AVIF picture: not kept, so it isn't mistaken for a cover later
                lastProblem = $"{candidate} isn't an image Tranga can read ({e.Message})";
            }
            catch (Exception e)
            {
                lastProblem = e.Message;
            }
        }

        Log.Warn($"{Name}: couldn't get a cover for {mangaId.Obj.Name}: {lastProblem}");
        return null;
    }
    
    public virtual async Task<Stream?> DownloadImage(string imageUrl, CancellationToken ct)
    {
        HttpResponseMessage requestResult = await downloadClient.MakeRequest(imageUrl, RequestType.MangaImage, cancellationToken: ct);
        return requestResult.IsSuccessStatusCode ? await requestResult.Content.ReadAsStreamAsync(ct) : null;
    }
}