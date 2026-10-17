using log4net;
using Newtonsoft.Json;

namespace API.Downloads;

/// <summary>
/// The preferred website of each series: when a series downloads from more than one website, each chapter comes from the
/// preferred one if it has the chapter, and from the others only for chapters it doesn't have (or after it failed).
/// Kept in series-preferences.json next to settings.json.
/// </summary>
public static class SeriesPreferences
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SeriesPreferences));
    private static readonly Lock PrefLock = new();
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "series-preferences.json");

    private class State
    {
        /// <summary>Manga.Key → website name</summary>
        public Dictionary<string, string> PreferredWebsite { get; set; } = [];
    }

    private static State? _state;

    private static State Data
    {
        get
        {
            if (_state is not null)
                return _state;
            try
            {
                if (File.Exists(FilePath))
                    _state = JsonConvert.DeserializeObject<State>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {FilePath}: {e.Message}");
            }
            return _state ??= new State();
        }
    }

    private static void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Data, Formatting.Indented));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't save {FilePath}: {e.Message}");
        }
    }

    public static string? PreferredWebsite(string mangaId)
    {
        lock (PrefLock)
            return Data.PreferredWebsite.GetValueOrDefault(mangaId);
    }

    /// <summary>Sets (or with null/empty, clears) the preferred website of a series</summary>
    public static void SetPreferredWebsite(string mangaId, string? website)
    {
        lock (PrefLock)
        {
            if (string.IsNullOrWhiteSpace(website))
                Data.PreferredWebsite.Remove(mangaId);
            else
                Data.PreferredWebsite[mangaId] = website;
            Save();
        }
    }

    /// <summary>Forgets series that no longer exist</summary>
    public static void Prune(IEnumerable<string> existingMangaIds)
    {
        lock (PrefLock)
        {
            HashSet<string> keep = existingMangaIds.ToHashSet();
            List<string> gone = Data.PreferredWebsite.Keys.Where(k => !keep.Contains(k)).ToList();
            if (gone.Count == 0)
                return;
            foreach (string k in gone)
                Data.PreferredWebsite.Remove(k);
            Save();
        }
    }
}
