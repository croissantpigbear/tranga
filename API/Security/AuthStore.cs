using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using log4net;
using Newtonsoft.Json;

namespace API.Security;

/// <summary>
/// Optional login for Tranga (off by default, switched on in Settings → Login). One user name and password,
/// stored as a salted PBKDF2 hash in auth.json next to settings.json, with login sessions (cookie) and an optional
/// API key for other apps. Forgot the password? Start the API with TRANGA_DISABLE_LOGIN=true, or delete auth.json.
/// </summary>
public static class AuthStore
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AuthStore));
    private static readonly Lock FileLock = new();
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "auth.json");

    public const string CookieName = "tranga_session";
    private const int Iterations = 120_000;
    private static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan SessionFor = TimeSpan.FromHours(12);

    /// <summary>TRANGA_DISABLE_LOGIN=true turns the login off whatever is saved (to get back in)</summary>
    public static bool ForcedOff => string.Equals(Environment.GetEnvironmentVariable("TRANGA_DISABLE_LOGIN"), "true", StringComparison.OrdinalIgnoreCase);

    private class Session
    {
        public string TokenHash { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
        public bool Remember { get; set; }
    }

    private class Data
    {
        public bool Enabled { get; set; }
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Salt { get; set; } = "";
        public int Iterations { get; set; } = AuthStore.Iterations;
        public string? ApiKeyHash { get; set; }
        public DateTime? ApiKeyCreatedUtc { get; set; }
        public List<Session> Sessions { get; set; } = [];
    }

    private static Data? _data;

    private static Data Current
    {
        get
        {
            lock (FileLock)
            {
                if (_data is not null)
                    return _data;
                try
                {
                    if (File.Exists(FilePath))
                        _data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(FilePath));
                }
                catch (Exception e)
                {
                    Log.Error($"Couldn't read {FilePath}; the login stays off until it's fixed or deleted: {e.Message}");
                }
                return _data ??= new Data();
            }
        }
    }

    private static void Save()
    {
        lock (FileLock)
        {
            Data data = Current;
            data.Sessions.RemoveAll(s => s.ExpiresUtc < DateTime.UtcNow);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented));
            File.Move(tmp, FilePath, true);
            try
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // not supported on this file system
            }
        }
    }

    /// <summary>Login is switched on (and not forced off)</summary>
    public static bool Enabled => !ForcedOff && Current.Enabled && Current.PasswordHash.Length > 0;
    public static string Username => Current.Username;
    public static bool HasApiKey => Current.ApiKeyHash is not null;
    public static DateTime? ApiKeyCreatedUtc => Current.ApiKeyCreatedUtc;

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string HashPassword(string password, string salt, int iterations) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt),
            iterations, HashAlgorithmName.SHA256, 32));

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Checks the password problems the website shows before saving</summary>
    public static string? PasswordProblem(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > 64)
            return "Choose a user name (up to 64 characters).";
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "The password needs at least 8 characters.";
        if (password.Length > 256)
            return "The password is too long.";
        return null;
    }

    /// <summary>Switches the login on (or changes the user name and password) and ends every other session</summary>
    public static void SetCredentials(string username, string password)
    {
        lock (FileLock)
        {
            Data data = Current;
            data.Username = username.Trim();
            data.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            data.Iterations = Iterations;
            data.PasswordHash = HashPassword(password, data.Salt, data.Iterations);
            data.Enabled = true;
            data.Sessions.Clear();
            Save();
        }
        Log.Info($"Login switched on for user {username.Trim()}");
    }

    public static void Disable()
    {
        lock (FileLock)
        {
            Data data = Current;
            data.Enabled = false;
            data.Sessions.Clear();
            Save();
        }
        Log.Info("Login switched off");
    }

    public static bool CheckPassword(string username, string password)
    {
        Data data = Current;
        if (data.PasswordHash.Length == 0 || !string.Equals(username.Trim(), data.Username, StringComparison.OrdinalIgnoreCase))
        {
            // Same work either way, so the answer time doesn't reveal the user name
            HashPassword(password, Convert.ToBase64String(new byte[16]), Iterations);
            return false;
        }
        string attempt = HashPassword(password, data.Salt, data.Iterations);
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(attempt), Convert.FromBase64String(data.PasswordHash));
    }

    /// <summary>Starts a session; returns its token (for the cookie) and when it ends</summary>
    public static (string Token, DateTime ExpiresUtc) StartSession(bool remember)
    {
        string token = NewToken();
        DateTime expires = DateTime.UtcNow + (remember ? RememberFor : SessionFor);
        lock (FileLock)
        {
            Current.Sessions.Add(new Session { TokenHash = Hash(token), ExpiresUtc = expires, Remember = remember });
            Save();
        }
        return (token, expires);
    }

    public static void EndSession(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        lock (FileLock)
        {
            string hash = Hash(token);
            if (Current.Sessions.RemoveAll(s => s.TokenHash == hash) > 0)
                Save();
        }
    }

    private static DateTime _lastSlideSave = DateTime.MinValue;

    /// <summary>A valid session token or API key (sessions are extended while in use)</summary>
    public static bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        string hash = Hash(token);
        lock (FileLock)
        {
            Data data = Current;
            if (data.ApiKeyHash is { } key && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(hash)))
                return true;
            Session? session = data.Sessions.FirstOrDefault(s => s.TokenHash == hash);
            if (session is null || session.ExpiresUtc < DateTime.UtcNow)
                return false;
            DateTime extended = DateTime.UtcNow + (session.Remember ? RememberFor : SessionFor);
            if (extended - session.ExpiresUtc > TimeSpan.FromHours(1))
            {
                session.ExpiresUtc = extended;
                if (DateTime.UtcNow - _lastSlideSave > TimeSpan.FromMinutes(10))
                {
                    _lastSlideSave = DateTime.UtcNow;
                    Save();
                }
            }
            return true;
        }
    }

    /// <summary>Creates a new API key (replacing the old one); the key is only shown now</summary>
    public static string CreateApiKey()
    {
        string key = "tranga_" + NewToken();
        lock (FileLock)
        {
            Current.ApiKeyHash = Hash(key);
            Current.ApiKeyCreatedUtc = DateTime.UtcNow;
            Save();
        }
        return key;
    }

    public static void DeleteApiKey()
    {
        lock (FileLock)
        {
            Current.ApiKeyHash = null;
            Current.ApiKeyCreatedUtc = null;
            Save();
        }
    }

    /// <summary>Token from the request: cookie, "Authorization: Bearer …" or "X-Api-Key"</summary>
    public static string? TokenOf(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out string? cookie) && cookie.Length > 0)
            return cookie;
        string auth = context.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth[7..].Trim();
        string apiKey = context.Request.Headers["X-Api-Key"].ToString();
        return apiKey.Length > 0 ? apiKey : null;
    }

    public static bool IsAuthenticated(HttpContext context) => !Enabled || IsValid(TokenOf(context));

    // --- Slowing down password guessing ---
    private static readonly ConcurrentDictionary<string, (int Failures, DateTime First, DateTime Last)> Failures = new();

    /// <summary>Seconds to wait before this address may try again: after 5 wrong passwords within 10 minutes, 30 s more per try (up to 5 min)</summary>
    public static int RetryAfterSeconds(string address)
    {
        if (!Failures.TryGetValue(address, out var f) || DateTime.UtcNow - f.First > TimeSpan.FromMinutes(10) || f.Failures < 5)
            return 0;
        TimeSpan wait = TimeSpan.FromSeconds(Math.Min(300, 30 * (f.Failures - 4)));
        double left = (f.Last + wait - DateTime.UtcNow).TotalSeconds;
        return left > 0 ? (int)Math.Ceiling(left) : 0;
    }

    public static void RecordFailure(string address) =>
        Failures.AddOrUpdate(address, _ => (1, DateTime.UtcNow, DateTime.UtcNow),
            (_, f) => DateTime.UtcNow - f.First > TimeSpan.FromMinutes(10)
                ? (1, DateTime.UtcNow, DateTime.UtcNow)
                : (f.Failures + 1, f.First, DateTime.UtcNow));

    public static void RecordSuccess(string address) => Failures.TryRemove(address, out _);
}
