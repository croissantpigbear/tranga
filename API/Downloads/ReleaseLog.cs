using log4net;
using Newtonsoft.Json;

namespace API.Downloads;

/// <summary>
/// When Tranga first found each new chapter of a series it already had (the closest thing to a release date the websites
/// give). Used by the release calendar. Kept for a year in releases.json next to settings.json.
/// </summary>
public static class ReleaseLog
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ReleaseLog));
    private static readonly Lock LogLock = new();
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "releases.json");
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(366);

    /// <summary>More new chapters than this in one check isn't a release (e.g. a newly linked website's back catalogue)</summary>
    public const int MaxPerCheck = 10;

    public record Entry(string MangaId, string ChapterId, DateTime FoundUtc);

    private static List<Entry>? _entries;

    private static List<Entry> Entries
    {
        get
        {
            if (_entries is not null)
                return _entries;
            try
            {
                if (File.Exists(FilePath))
                    _entries = JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {FilePath}: {e.Message}");
            }
            return _entries ??= [];
        }
    }

    private static void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Entries));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't save {FilePath}: {e.Message}");
        }
    }

    /// <summary>Notes chapters found now (ignored if there are too many to be a release)</summary>
    public static void Record(string mangaId, IReadOnlyCollection<string> chapterIds)
    {
        if (chapterIds.Count == 0 || chapterIds.Count > MaxPerCheck)
            return;
        DateTime now = DateTime.UtcNow;
        lock (LogLock)
        {
            HashSet<string> known = Entries.Select(e => e.ChapterId).ToHashSet();
            Entries.AddRange(chapterIds.Where(id => !known.Contains(id)).Select(id => new Entry(mangaId, id, now)));
            Entries.RemoveAll(e => now - e.FoundUtc > KeepFor);
            Save();
        }
    }

    public static List<Entry> Between(DateTime fromUtc, DateTime toUtc)
    {
        lock (LogLock)
            return Entries.Where(e => e.FoundUtc >= fromUtc && e.FoundUtc < toUtc).ToList();
    }

    /// <summary>The whole log (for working out how often each series releases)</summary>
    public static List<Entry> All()
    {
        lock (LogLock)
            return Entries.ToList();
    }
}
