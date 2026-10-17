using log4net;
using Newtonsoft.Json;

namespace API.Downloads;

/// <summary>
/// The order in which series download: first come, first served. A series joins the end of the queue when it first has
/// chapters waiting, and leaves it when they're all downloaded. Chapters download series by series (lowest chapter
/// first) instead of mixing series. Websites still have their own limit, so when the first series' website is busy,
/// the next series from another website can download alongside it. The order can be changed (Activity → Queue) and is
/// kept in download-queue.json next to settings.json.
/// </summary>
public static class DownloadQueue
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(DownloadQueue));
    private static readonly Lock QueueLock = new();
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "download-queue.json");
    private class State
    {
        public List<string> Order { get; set; } = [];
        /// <summary>Series whose downloads are paused</summary>
        public HashSet<string> Paused { get; set; } = [];
        /// <summary>All downloads paused</summary>
        public bool PausedAll { get; set; }
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
                {
                    string json = File.ReadAllText(FilePath).TrimStart();
                    // The first version stored only the order, as a list
                    _state = json.StartsWith('[')
                        ? new State { Order = JsonConvert.DeserializeObject<List<string>>(json) ?? [] }
                        : JsonConvert.DeserializeObject<State>(json);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {FilePath}: {e.Message}");
            }
            return _state ??= new State();
        }
    }

    private static List<string> Order => Data.Order;

    private static void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Data));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't save {FilePath}: {e.Message}");
        }
    }

    /// <summary>No new downloads start while true (running ones finish)</summary>
    public static bool PausedAll
    {
        get { lock (QueueLock) return Data.PausedAll; }
        set { lock (QueueLock) { Data.PausedAll = value; Save(); } }
    }

    public static bool IsPaused(string mangaId)
    {
        lock (QueueLock)
            return Data.Paused.Contains(mangaId);
    }

    /// <summary>Pauses or resumes one series (it keeps its place in the queue)</summary>
    public static void SetPaused(string mangaId, bool paused)
    {
        lock (QueueLock)
        {
            if (paused ? Data.Paused.Add(mangaId) : Data.Paused.Remove(mangaId))
                Save();
        }
    }

    /// <summary>
    /// Brings the queue in line with the series that have chapters waiting (keeps their order, adds new ones at the end,
    /// removes finished ones) and returns each series' position (0 = next).
    /// </summary>
    /// <param name="waitingMangaIds">Manga.Key of every series with chapters waiting, including ones downloading now</param>
    public static Dictionary<string, int> Sync(IEnumerable<string> waitingMangaIds)
    {
        lock (QueueLock)
        {
            HashSet<string> waiting = waitingMangaIds.ToHashSet();
            List<string> order = Order;
            int before = order.Count;
            int removed = order.RemoveAll(id => !waiting.Contains(id));
            HashSet<string> queued = order.ToHashSet();
            List<string> added = waiting.Where(id => !queued.Contains(id)).ToList();
            order.AddRange(added);
            if (removed > 0 || added.Count > 0)
            {
                Save();
                Log.Debug($"Download queue: {before} → {order.Count} series ({added.Count} added, {removed} finished)");
            }
            return order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        }
    }

    /// <summary>Series in download order (Manga.Key)</summary>
    public static List<string> Snapshot()
    {
        lock (QueueLock)
            return Order.ToList();
    }

    /// <summary>Moves a series to a position in the queue (0 = next; beyond the end = last)</summary>
    /// <returns>False if the series isn't in the queue</returns>
    public static bool Move(string mangaId, int position)
    {
        lock (QueueLock)
        {
            List<string> order = Order;
            if (!order.Remove(mangaId))
                return false;
            order.Insert(Math.Clamp(position, 0, order.Count), mangaId);
            Save();
            return true;
        }
    }
}
