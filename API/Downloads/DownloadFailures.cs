using API.Schema.MangaContext;
using log4net;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace API.Downloads;

/// <summary>
/// Chapters whose download failed. Instead of retrying every minute, a failed chapter waits before the next try
/// (5 min, 30 min, 2 h, 12 h); after <see cref="MaxAttempts"/> failures Tranga gives up on it until you retry it.
/// When it gives up and the chapter is also available from another website, that website is used instead.
/// Kept in download-failures.json next to settings.json.
/// </summary>
public static class DownloadFailures
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(DownloadFailures));
    private static readonly Lock StoreLock = new();
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "download-failures.json");

    /// <summary>Failed attempts before Tranga gives up on a chapter</summary>
    public const int MaxAttempts = 5;

    /// <summary>Wait after the 1st, 2nd, 3rd and 4th failure</summary>
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

    public class Entry
    {
        /// <summary>MangaConnectorId&lt;Chapter&gt;.Key (the chapter on one website)</summary>
        public string ChapterIdId { get; set; } = "";
        public string ChapterId { get; set; } = "";
        public string MangaId { get; set; } = "";
        public string MangaName { get; set; } = "";
        public string ChapterNumber { get; set; } = "";
        public string Website { get; set; } = "";
        public int Attempts { get; set; }
        public string LastError { get; set; } = "";
        public DateTime LastAttemptUtc { get; set; }
        public DateTime? NextRetryUtc { get; set; }
        public bool GaveUp { get; set; }
        /// <summary>Website Tranga switched this chapter to after giving up</summary>
        public string? SwitchedTo { get; set; }
    }

    private class Store
    {
        public bool NotifyWhenGivingUp { get; set; } = true;
        public Dictionary<string, Entry> Entries { get; set; } = new();
    }

    private static Store? _store;

    private static Store Data
    {
        get
        {
            if (_store is not null)
                return _store;
            try
            {
                if (File.Exists(FilePath))
                    _store = JsonConvert.DeserializeObject<Store>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {FilePath}: {e.Message}");
            }
            return _store ??= new Store();
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

    /// <summary>Send a notification when Tranga gives up on a chapter</summary>
    public static bool NotifyWhenGivingUp
    {
        get { lock (StoreLock) return Data.NotifyWhenGivingUp; }
        set { lock (StoreLock) { Data.NotifyWhenGivingUp = value; Save(); } }
    }

    public static List<Entry> List()
    {
        lock (StoreLock)
            return Data.Entries.Values.OrderBy(e => e.GaveUp ? 0 : 1).ThenByDescending(e => e.LastAttemptUtc).ToList();
    }

    public static Entry? Get(string chapterIdId)
    {
        lock (StoreLock)
            return Data.Entries.GetValueOrDefault(chapterIdId);
    }

    /// <summary>True while a failed chapter waits for its next try, or after Tranga gave up on it</summary>
    public static bool IsWaiting(string chapterIdId, DateTime nowUtc)
    {
        lock (StoreLock)
            return Data.Entries.TryGetValue(chapterIdId, out Entry? e) && (e.GaveUp || e.NextRetryUtc > nowUtc);
    }

    /// <summary>Records a failed attempt and schedules the next one (or gives up)</summary>
    public static Entry RecordFailure(MangaConnectorId<Chapter> chId, Chapter chapter, string error)
    {
        lock (StoreLock)
        {
            if (!Data.Entries.TryGetValue(chId.Key, out Entry? e))
            {
                e = new Entry { ChapterIdId = chId.Key };
                Data.Entries[chId.Key] = e;
            }
            e.ChapterId = chapter.Key;
            e.MangaId = chapter.ParentMangaId;
            e.MangaName = chapter.ParentManga?.Name ?? e.MangaName;
            e.ChapterNumber = chapter.ChapterNumber;
            e.Website = chId.MangaConnectorName;
            e.Attempts++;
            e.LastError = error.Length > 500 ? error[..500] : error;
            e.LastAttemptUtc = DateTime.UtcNow;
            e.GaveUp = e.Attempts >= MaxAttempts;
            e.NextRetryUtc = e.GaveUp ? null : DateTime.UtcNow + Backoff[Math.Min(e.Attempts - 1, Backoff.Length - 1)];
            Save();
            return e;
        }
    }

    public static void SetSwitchedTo(string chapterIdId, string website)
    {
        lock (StoreLock)
        {
            if (Data.Entries.TryGetValue(chapterIdId, out Entry? e))
            {
                e.SwitchedTo = website;
                Save();
            }
        }
    }

    /// <summary>A chapter downloaded: forget its failures on every website</summary>
    public static void RecordSuccess(string chapterId)
    {
        lock (StoreLock)
        {
            if (Data.Entries.Values.Where(e => e.ChapterId == chapterId).Select(e => e.ChapterIdId).ToList() is { Count: > 0 } keys)
            {
                keys.ForEach(k => Data.Entries.Remove(k));
                Save();
            }
        }
    }

    /// <summary>Tries again at the next download round, with a full set of attempts</summary>
    public static bool Retry(string chapterIdId)
    {
        lock (StoreLock)
        {
            if (!Data.Entries.Remove(chapterIdId))
                return false;
            Save();
            return true;
        }
    }

    /// <summary>Retries every chapter Tranga gave up on</summary>
    public static int RetryAll()
    {
        lock (StoreLock)
        {
            List<string> keys = Data.Entries.Values.Select(e => e.ChapterIdId).ToList();
            keys.ForEach(k => Data.Entries.Remove(k));
            if (keys.Count > 0)
                Save();
            return keys.Count;
        }
    }

    public static bool Remove(string chapterIdId) => Retry(chapterIdId);

    /// <summary>Forgets chapters that are no longer waiting to download (downloaded from elsewhere, unmarked, deleted)</summary>
    public static void Prune(IEnumerable<string> waitingChapterIdIds)
    {
        lock (StoreLock)
        {
            HashSet<string> waiting = waitingChapterIdIds.ToHashSet();
            List<string> gone = Data.Entries.Keys.Where(k => !waiting.Contains(k)).ToList();
            if (gone.Count == 0)
                return;
            gone.ForEach(k => Data.Entries.Remove(k));
            Save();
        }
    }

    /// <summary>
    /// After giving up: marks the same chapter on another website for download, if Tranga knows it there
    /// (the series is linked to that website and its chapters were read). Returns that website's name.
    /// </summary>
    public static async Task<string?> UseOtherWebsite(MangaContext context, MangaConnectorId<Chapter> failed, CancellationToken ct)
    {
        List<MangaConnectorId<Chapter>> others = await context.MangaConnectorToChapter
            .Where(id => id.ObjId == failed.ObjId && id.Key != failed.Key)
            .ToListAsync(ct);
        MangaConnectorId<Chapter>? alternative = others
            .Where(id => Tranga.TryGetMangaConnector(id.MangaConnectorName, out var c) && c.Enabled)
            .FirstOrDefault(id => Get(id.Key) is not { GaveUp: true });
        if (alternative is null)
            return null;
        alternative.UseForDownload = true;
        if (await context.Sync(ct, typeof(DownloadFailures), "Download from another website") is { success: false } result)
        {
            Log.Error($"Couldn't switch chapter {failed.ObjId} to {alternative.MangaConnectorName}: {result.exceptionMessage}");
            return null;
        }
        Retry(alternative.Key);
        SetSwitchedTo(failed.Key, alternative.MangaConnectorName);
        return alternative.MangaConnectorName;
    }
}
