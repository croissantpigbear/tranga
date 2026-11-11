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

    internal string? SaveCoverImageToCache(MangaConnectorId<Manga> mangaId, int retries = 3)
    {
        if(retries < 0)
            return null;

        if (AbsoluteUrl(mangaId.Obj.CoverUrl, mangaId.WebsiteUrl, BaseUris) is not { } coverUrl)
        {
            Log.Warn($"{Name}: the cover link of {mangaId.Obj.Name} isn't a web address (\"{mangaId.Obj.CoverUrl}\"); no cover.");
            return null;
        }
        Uri coverUri = new(coverUrl);
        string extension = Path.GetExtension(coverUri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (extension.Length is 0 or > 5)
            extension = "jpg";
        string filename = $"{coverUri.Host}-{mangaId.ObjId}.{mangaId.MangaConnectorName}.{extension}";
        string saveImagePath = Path.Join(TrangaSettings.CoverImageCacheOriginal, filename);

        if (File.Exists(saveImagePath))
            return filename;

        HttpResponseMessage coverResult;
        try
        {
            coverResult = downloadClient.MakeRequest(coverUrl, RequestType.MangaCover, $"{coverUri.Scheme}://{coverUri.Host}").Result;
        }
        catch (Exception e)
        {
            Log.Warn($"{Name}: couldn't download the cover of {mangaId.Obj.Name} ({e.GetBaseException().Message}).");
            return null;
        }
        if ((int)coverResult.StatusCode < 200 || (int)coverResult.StatusCode >= 300)
            return SaveCoverImageToCache(mangaId, retries - 1);
            
        try
        {
            using MemoryStream ms = new();
            coverResult.Content.ReadAsStream().CopyTo(ms);
            byte[] imageBytes = ms.ToArray();
            Directory.CreateDirectory(TrangaSettings.CoverImageCacheOriginal);
            File.WriteAllBytes(saveImagePath, imageBytes);

            using Image image = Image.Load(imageBytes);
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
        }
        catch (Exception e)
        {
            Log.Error(e);
        }


        return filename.CleanNameForWindows();
    }
    
    public virtual async Task<Stream?> DownloadImage(string imageUrl, CancellationToken ct)
    {
        HttpResponseMessage requestResult = await downloadClient.MakeRequest(imageUrl, RequestType.MangaImage, cancellationToken: ct);
        return requestResult.IsSuccessStatusCode ? await requestResult.Content.ReadAsStreamAsync(ct) : null;
    }
}