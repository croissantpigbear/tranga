using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using log4net;

namespace API.LuaModules;

/// <summary>
/// The functions Tranga gives an FMD2 module (registered in Lua as __h_*). Only plain values cross into Lua, never .NET
/// objects, so a module can't reach anything else. These never throw (an exception thrown into Lua could crash the
/// native Lua runtime); failures are returned as values the prelude turns into Lua errors.
/// </summary>
public sealed class LuaHost
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LuaHost));

    private readonly FmdQueryEngine _query = new();
    private readonly DateTime _deadline;
    private readonly CancellationToken _ct;
    private readonly string _logName;
    private LuaHttp.Response _last = new(0, "", "", "");
    private int _requests;

    /// <summary>Lines logged by the module (shown on the website's test panel)</summary>
    public List<string> Messages { get; } = [];
    /// <summary>Requests made (method + URL + status), for the test panel</summary>
    public List<string> RequestLog { get; } = [];
    public bool TimedOut { get; private set; }

    internal const int MaxRequestsPerCall = 400;

    internal LuaHost(TimeSpan budget, CancellationToken ct, string logName)
    {
        _deadline = DateTime.UtcNow + budget;
        _ct = ct;
        _logName = logName;
    }

    private bool OutOfTime()
    {
        if (DateTime.UtcNow > _deadline || _ct.IsCancellationRequested)
            TimedOut = true;
        return TimedOut;
    }

    /// <summary>Status code; 0 = network error; -2 = stopped (time budget used up or too many requests)</summary>
    public int Http(string method, string url, string body, string headers, string mimeType, string userAgent)
    {
        try
        {
            if (OutOfTime() || ++_requests > MaxRequestsPerCall)
            {
                TimedOut = true;
                return -2;
            }
            url = LuaText.FromLua(url);
            _last = LuaHttp.Send(LuaText.FromLua(method), url, LuaText.FromLua(body), LuaText.FromLua(headers),
                LuaText.FromLua(mimeType), LuaText.FromLua(userAgent), _ct);
            if (RequestLog.Count < 200)
                RequestLog.Add($"{LuaText.FromLua(method)} {url} → {(_last.Status == 0 ? "failed" : _last.Status.ToString(CultureInfo.InvariantCulture))}");
            return _last.Status;
        }
        catch (Exception e)
        {
            Log.Debug($"[{_logName}] request failed: {e.Message}");
            _last = new LuaHttp.Response(0, "", url ?? "", "");
            return OutOfTime() ? -2 : 0;
        }
    }

    public string Body() => LuaText.ToLua(_last.Body);
    public string LastUrl() => LuaText.ToLua(_last.FinalUrl);
    public string ResponseHeaders() => LuaText.ToLua(_last.Headers);

    public int Doc(string text)
    {
        try
        {
            return _query.NewDocument(LuaText.FromLua(text));
        }
        catch (Exception e)
        {
            Log.Debug($"[{_logName}] parse failed: {e.Message}");
            return 0;
        }
    }

    /// <summary>Ids of the results ("1,2,3"); "!" if the module ran out of time</summary>
    public string Query(int doc, int context, string expression)
    {
        try
        {
            if (OutOfTime())
                return "!";
            return _query.Query(doc, context, LuaText.FromLua(expression));
        }
        catch (Exception e)
        {
            Log.Debug($"[{_logName}] query failed: {expression}: {e.Message}");
            return "";
        }
    }

    public string Str(int id) => Safe(() => LuaText.ToLua(_query.ItemString(id)));
    public string Attr(int id, string name) => Safe(() => LuaText.ToLua(_query.ItemAttribute(id, LuaText.FromLua(name))));
    public int Prop(int id, string name)
    {
        try
        {
            return _query.ItemProperty(id, LuaText.FromLua(name));
        }
        catch (Exception)
        {
            return 0;
        }
    }
    public string Html(int id, int mode) => Safe(() => LuaText.ToLua(_query.ItemHtml(id, mode)));

    public string Crypto(string op, string input, string extra)
    {
        return Safe(() =>
        {
            // Lua strings are byte strings: work on their raw bytes, like FMD2 (binary data stays intact)
            byte[] raw = LuaText.Bytes(input);
            string text = LuaText.FromLua(input);
            return op switch
            {
                "b64e" => Convert.ToBase64String(raw),
                "b64d" => LuaText.FromBytes(DecodeBase64(text)),
                "b64ue" => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                "b64ud" => LuaText.FromBytes(DecodeBase64(text.Replace('-', '+').Replace('_', '/'))),
                "urle" => EscapeUrl(raw),
                "urlelem" => LuaText.ToLua(Uri.EscapeDataString(text)),
                "urld" => LuaText.FromBytes(WebUtility.UrlDecodeToBytes(raw, 0, raw.Length) ?? []),
                "htmle" => LuaText.ToLua(WebUtility.HtmlEncode(text)),
                "htmld" => LuaText.ToLua(WebUtility.HtmlDecode(text)),
                "md5" => Convert.ToHexStringLower(MD5.HashData(raw)),
                "sha1" => Convert.ToHexStringLower(SHA1.HashData(raw)),
                "sha256" => Convert.ToHexStringLower(SHA256.HashData(raw)),
                "sha512" => Convert.ToHexStringLower(SHA512.HashData(raw)),
                "hex" => Convert.ToHexStringLower(raw),
                "unhex" => LuaText.FromBytes(Convert.FromHexString(text)),
                _ => ""
            };
        });
    }

    /// <summary>Percent-encodes spaces and non-ASCII characters, keeping the URL's structure (FMD2's EncodeURL)</summary>
    private static string EscapeUrl(byte[] input)
    {
        StringBuilder sb = new();
        foreach (byte b in input)
        {
            char c = (char)b;
            if (b < 128 && b > 32 && c != '"' && c != '<' && c != '>' && c != '`' && c != '{' && c != '}' && c != '|' && c != '\\' && c != '^')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] DecodeBase64(string input)
    {
        string s = new(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
        int pad = s.Length % 4;
        if (pad > 0)
            s += new string('=', 4 - pad);
        return Convert.FromBase64String(s);
    }

    public void Sleep(int milliseconds)
    {
        if (milliseconds <= 0 || OutOfTime())
            return;
        TimeSpan wait = TimeSpan.FromMilliseconds(Math.Min(milliseconds, 5000));
        _ct.WaitHandle.WaitOne(wait);
    }

    public void Message(int level, string message)
    {
        message = LuaText.FromLua(message);
        if (Messages.Count < 200)
            Messages.Add(message);
        switch (level)
        {
            case >= 3: Log.Warn($"[{_logName}] {message}"); break;
            case 2: Log.Info($"[{_logName}] {message}"); break;
            default: Log.Debug($"[{_logName}] {message}"); break;
        }
    }

    /// <summary>Source of an uploaded template/helper (templates.Madara, utils.lzstring), or "" if none</summary>
    public string Require(string name) => Safe(() => LuaText.ToLua(LuaModuleStore.ReadLibrary(LuaText.FromLua(name)) ?? ""));

    private string Safe(Func<string> f)
    {
        try
        {
            return f();
        }
        catch (Exception e)
        {
            Log.Debug($"[{_logName}] {e.Message}");
            return "";
        }
    }

    /// <summary>Lua name → method, registered for every run</summary>
    /// <summary>MODULE.Storage[key] (persisted per module)</summary>
    public string StoreGet(string moduleId, string key)
    {
        try
        {
            return LuaText.ToLua(LuaModuleStorage.Get(LuaText.FromLua(moduleId), LuaText.FromLua(key)));
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>MODULE.Storage[key] = value</summary>
    public void StoreSet(string moduleId, string key, string value)
    {
        try
        {
            LuaModuleStorage.Set(LuaText.FromLua(moduleId), LuaText.FromLua(key), LuaText.FromLua(value));
        }
        catch (Exception e)
        {
            Log.Debug($"[{_logName}] storage: {e.Message}");
        }
    }

    internal static readonly (string LuaName, string Method)[] Functions =
    [
        ("__h_http", nameof(Http)), ("__h_body", nameof(Body)), ("__h_lasturl", nameof(LastUrl)),
        ("__h_resheaders", nameof(ResponseHeaders)), ("__h_doc", nameof(Doc)), ("__h_q", nameof(Query)),
        ("__h_str", nameof(Str)), ("__h_attr", nameof(Attr)), ("__h_prop", nameof(Prop)), ("__h_html", nameof(Html)),
        ("__h_crypto", nameof(Crypto)), ("__h_sleep", nameof(Sleep)), ("__h_log", nameof(Message)),
        ("__h_require", nameof(Require)), ("__h_sget", nameof(StoreGet)), ("__h_sset", nameof(StoreSet))
    ];
}

/// <summary>
/// Lua strings are bytes. The Lua state uses Latin-1 (one char per byte), so binary data passes through unchanged;
/// text is converted to and from UTF-8 explicitly here.
/// </summary>
internal static class LuaText
{
    public static readonly Encoding StateEncoding = Encoding.Latin1;

    /// <summary>Lua string (bytes as Latin-1 chars) → .NET text (decoded as UTF-8)</summary>
    public static string FromLua(string? s) => string.IsNullOrEmpty(s) ? "" : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(s));

    /// <summary>.NET text → Lua string holding its UTF-8 bytes</summary>
    public static string ToLua(string? s) => string.IsNullOrEmpty(s) ? "" : Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(s));

    /// <summary>Raw bytes of a Lua string</summary>
    public static byte[] Bytes(string? s) => string.IsNullOrEmpty(s) ? [] : Encoding.Latin1.GetBytes(s);

    /// <summary>Raw bytes → Lua string</summary>
    public static string FromBytes(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
