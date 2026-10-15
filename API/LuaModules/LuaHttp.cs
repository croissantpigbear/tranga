using System.Net;
using System.Net.Http.Headers;
using System.Text;
using API.MangaDownloadClients;
using log4net;

namespace API.LuaModules;

/// <summary>
/// Web requests made by FMD2 modules. Goes through Tranga's shared rate limiter (REQUESTS_PER_MINUTE), uses Tranga's
/// user agent unless the module sets its own, and retries Cloudflare-blocked GETs through FlareSolverr when configured.
/// </summary>
internal static class LuaHttp
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LuaHttp));

    internal static readonly HttpClient Client = new(Tranga.RateLimitHandler, false)
    {
        Timeout = TimeSpan.FromSeconds(Constants.HttpRequestTimeout)
    };

    static LuaHttp()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // Only needed for sites in legacy encodings
        }
    }

    /// <summary>
    /// What FMD2 sends: a current desktop Chrome. Many sites (Cloudflare bot rules) refuse non-browser user agents
    /// such as Tranga's default. A user agent changed in Tranga's settings is used as it is.
    /// </summary>
    internal const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    internal static string DefaultUserAgent =>
        Tranga.Settings.UserAgent == TrangaSettings.DefaultUserAgent || string.IsNullOrWhiteSpace(Tranga.Settings.UserAgent)
            ? BrowserUserAgent
            : Tranga.Settings.UserAgent;

    internal record Response(int Status, string Body, string FinalUrl, string Headers);

    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Disposition" };

    public static Response Send(string method, string url, string body, string headerBlock, string mimeType, string userAgent,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            return new Response(0, "", url, "");

        using HttpRequestMessage request = new(new HttpMethod(string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant()), uri);
        string? contentType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType.Trim();
        bool hasUserAgent = false, hasAccept = false, hasLanguage = false;
        foreach (string line in (headerBlock ?? "").Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0)
                continue;
            if (ContentHeaders.Contains(name))
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    contentType ??= value;
                continue;
            }
            if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                hasUserAgent = true;
            if (name.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                hasAccept = true;
            if (name.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase))
                hasLanguage = true;
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (!hasUserAgent)
            request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        if (!hasAccept)
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
        if (!hasLanguage)
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put || request.Method == HttpMethod.Patch)
        {
            StringContent content = new(body ?? "", Encoding.UTF8);
            content.Headers.ContentType = null;
            string type = contentType ?? "application/x-www-form-urlencoded";
            if (!content.Headers.TryAddWithoutValidation("Content-Type", type))
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            request.Content = content;
        }

        try
        {
            // SendAsync (not Send): Tranga's rate limiter only hooks the async path
            using HttpResponseMessage response = Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).GetAwaiter().GetResult();
            if ((response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
                && request.Method == HttpMethod.Get
                && response.Headers.Server.Any(s => (s.Product?.Name ?? "").Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrEmpty(Tranga.Settings.FlareSolverrUrl))
            {
                Log.Debug($"Cloudflare blocked {url}, retrying with FlareSolverr");
                using HttpResponseMessage solved = new FlareSolverrDownloadClient(Client)
                    .MakeRequest(url, RequestType.Default, cancellationToken: ct).GetAwaiter().GetResult();
                return Read(solved, url);
            }
            return Read(response, url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log.Debug($"{method} {url} failed: {e.Message}");
            return new Response(0, "", url, "");
        }
    }

    private static Response Read(HttpResponseMessage response, string url)
    {
        byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        string body = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
        StringBuilder headers = new();
        foreach (KeyValuePair<string, IEnumerable<string>> h in response.Headers.Concat(response.Content.Headers))
            headers.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append('\n');
        string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        return new Response((int)response.StatusCode, body, finalUrl, headers.ToString());
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0)
            return "";
        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
                // unknown charset: assume UTF-8
            }
        }
        string text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
