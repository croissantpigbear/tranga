using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using API.MangaConnectors;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace API.LuaModules;

/// <summary>
/// The full list of titles on an FMD2 site (FMD2's "manga list"), which is how searching works for these sites:
/// FMD2 modules can list a site's titles, but most can't search it. Built in the background and kept on disk.
/// </summary>
public static class LuaTitleList
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LuaTitleList));
    private static string Folder => Path.Join(LuaModuleStore.Root, "titles");

    /// <summary>Site → "building" / last error</summary>
    public static readonly ConcurrentDictionary<string, string> Status = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Stored
    {
        public DateTime BuiltUtc { get; set; }
        public int PagesRead { get; set; }
        public List<string[]> Titles { get; set; } = [];
    }

    private static readonly ConcurrentDictionary<string, Stored> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static string PathFor(string site) =>
        Path.Join(Folder, Regex.Replace(site, @"[^A-Za-z0-9_\- ]", "_") + ".json");

    private static Stored? Load(string site)
    {
        if (Cache.TryGetValue(site, out Stored? cached))
            return cached;
        string path = PathFor(site);
        if (!File.Exists(path))
            return null;
        try
        {
            Stored? stored = JsonConvert.DeserializeObject<Stored>(File.ReadAllText(path));
            if (stored is not null)
                Cache[site] = stored;
            return stored;
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't read title list of {site}: {e.Message}");
            return null;
        }
    }

    /// <summary>Number of titles and when the list was built (null if never)</summary>
    public static (int Count, DateTime? BuiltUtc) Info(string site) =>
        Load(site) is { } s ? (s.Titles.Count, s.BuiltUtc) : (0, null);

    /// <summary>Reads the site's whole title list (can take minutes)</summary>
    public static void Build(LuaMangaConnector connector, CancellationToken ct)
    {
        Status[connector.Name] = "building";
        try
        {
            string source = LuaModuleStore.ReadModule(connector.FileName) ?? throw new LuaModuleException("Module file missing");
            TimeSpan budget = Constants.WorkerTimeout - TimeSpan.FromSeconds(30);
            using LuaModuleRunner runner = LuaModuleRunner.Load(source, connector.FileName, budget, ct);
            JObject result = JObject.Parse(runner.Call("__fmd_directory", connector.ModuleId, 300));
            JArray links = result["links"] as JArray ?? new JArray();
            JArray names = result["names"] as JArray ?? new JArray();
            Dictionary<string, string[]> titles = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < links.Count; i++)
            {
                string link = links[i].ToString().Trim();
                string name = i < names.Count ? names[i].ToString().Trim() : "";
                if (link.Length > 0 && name.Length > 0)
                    titles.TryAdd(link, [name, link]);
            }
            if (titles.Count == 0)
                throw new LuaModuleException(runner.Host.TimedOut
                    ? "Stopped before any titles were found (the site was too slow)."
                    : "The module didn't return any titles. The site may have changed, or need JavaScript.");
            Stored stored = new()
            {
                BuiltUtc = DateTime.UtcNow,
                PagesRead = result["pagesRead"]?.Value<int>() ?? 0,
                Titles = titles.Values.ToList()
            };
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(connector.Name), JsonConvert.SerializeObject(stored));
            Cache[connector.Name] = stored;
            Status.TryRemove(connector.Name, out _);
            Log.Info($"{connector.Name}: title list has {stored.Titles.Count} titles ({stored.PagesRead} pages read).");
        }
        catch (Exception e)
        {
            Status[connector.Name] = e is LuaModuleException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            Log.Warn($"{connector.Name}: building the title list failed: {e.Message}");
        }
    }

    public static void DeleteFor(IEnumerable<string> sites)
    {
        foreach (string site in sites)
        {
            Cache.TryRemove(site, out _);
            Status.TryRemove(site, out _);
            string path = PathFor(site);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>Best matches for a search (name, url)</summary>
    public static List<(string Name, string Url)> Search(string site, string query, int max)
    {
        if (Load(site) is not { } stored)
            return [];
        string q = Normalize(query);
        if (q.Length == 0)
            return [];
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return stored.Titles
            .Select(t => (Title: t, Score: Score(Normalize(t[0]), q, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title[0].Length)
            .Take(max)
            .Select(x => (x.Title[0], x.Title[1]))
            .ToList();
    }

    private static int Score(string title, string query, string[] words)
    {
        if (title == query)
            return 100;
        if (title.StartsWith(query, StringComparison.Ordinal))
            return 85;
        if (title.Contains(query, StringComparison.Ordinal))
            return 70;
        int hits = words.Count(w => title.Contains(w, StringComparison.Ordinal));
        return hits == words.Length ? 50 : hits * 40 / Math.Max(1, words.Length) >= 30 ? 20 : 0;
    }

    private static string Normalize(string s)
    {
        string decomposed = s.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
