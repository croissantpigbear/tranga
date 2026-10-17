using API.Workers;
using log4net;
using Newtonsoft.Json;

namespace API.Scheduling;

/// <summary>
/// How often Tranga's scheduled jobs run, changeable from the website. Saved in schedules.json next to settings.json;
/// a job without a saved value keeps its built-in default (or the value from its environment variable).
/// Changes apply straight away: the scheduler re-reads the interval while it waits.
/// </summary>
public static class TaskSchedules
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TaskSchedules));
    private static readonly Lock FileLock = new();
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "schedules.json");

    /// <summary>A scheduled job that can be tuned</summary>
    /// <param name="Id">Job type name</param>
    /// <param name="Group">downloads, metadata, notifications, maintenance</param>
    public sealed record Definition(string Id, string Group, string Title, string Description, int RecommendedMinutes,
        int MinMinutes, int MaxMinutes, Func<IPeriodic> Worker);

    public static readonly Definition[] All =
    [
        new(nameof(Tranga.CheckForNewChaptersWorker), "downloads", "Check for new chapters",
            "Asks every website your series come from whether new chapters are out. More often finds chapters sooner but sends more requests to the sites.",
            180, 30, 7 * 24 * 60, () => Tranga.CheckForNewChaptersWorker),
        new(nameof(Tranga.StartNewChapterDownloadsWorker), "downloads", "Start waiting downloads",
            "Starts downloading chapters that are waiting, within the download limits. Cheap; keep it short.",
            1, 1, 60, () => Tranga.StartNewChapterDownloadsWorker),
        new(nameof(Tranga.FillExtraMetadataWorker), "metadata", "Fill in series details",
            "Looks up MangaUpdates, AniList and MangaDex details for series that don't have them yet (each series only once).",
            30, 10, 24 * 60, () => Tranga.FillExtraMetadataWorker),
        new(nameof(Tranga.UpdateMetadataWorker), "metadata", "Update linked metadata",
            "Refreshes details from metadata sites you linked a series to (e.g. MyAnimeList) on its series page.",
            12 * 60, 60, 7 * 24 * 60, () => Tranga.UpdateMetadataWorker),
        new(nameof(Tranga.UpdateCoversWorker), "metadata", "Refresh covers",
            "Downloads series covers again, in case the website changed them.",
            6 * 60, 60, 7 * 24 * 60, () => Tranga.UpdateCoversWorker),
        new(nameof(Tranga.SyncKavitaCoversWorker), "metadata", "Send covers to Kavita",
            "Sets newly downloaded series' covers in Kavita (only if Kavita covers are switched on).",
            15, 5, 24 * 60, () => Tranga.SyncKavitaCoversWorker),
        new(nameof(Tranga.CheckLuaUpdatesWorker), "metadata", "Check FMD2 modules for updates",
            "Compares your FMD2 website modules with FMD2 on GitHub (GitHub allows 60 checks an hour).",
            24 * 60, 60, 7 * 24 * 60, () => Tranga.CheckLuaUpdatesWorker),
        new(nameof(Tranga.SendNotificationsWorker), "notifications", "Send notifications",
            "Sends waiting notifications (new chapters, failures) to your notification services.",
            1, 1, 60, () => Tranga.SendNotificationsWorker),
        new(nameof(Tranga.RemoveOldNotificationsWorker), "maintenance", "Remove old notifications",
            "Clears notifications that were already sent.",
            60, 15, 7 * 24 * 60, () => Tranga.RemoveOldNotificationsWorker),
        new(nameof(Tranga.CleanupMangaCoversWorker), "maintenance", "Clean up cached covers",
            "Deletes cover images of series that are no longer in Tranga.",
            24 * 60, 60, 30 * 24 * 60, () => Tranga.CleanupMangaCoversWorker),
        new(nameof(Tranga.UpdateChaptersDownloadedWorker), "maintenance", "Check chapter files on disk",
            "Checks which chapter files are on disk, e.g. after you deleted or moved some by hand. Slow on big libraries.",
            24 * 60, 60, 30 * 24 * 60, () => Tranga.UpdateChaptersDownloadedWorker),
    ];

    /// <summary>Built-in intervals, captured before any saved value is applied</summary>
    private static readonly Dictionary<string, TimeSpan> Defaults = All.ToDictionary(d => d.Id, d => d.Worker().Interval);

    /// <summary>Jobs asked to run now (picked up by the scheduler within a few seconds)</summary>
    private static readonly HashSet<IPeriodic> RunNowRequests = [];

    private static Dictionary<string, int> LoadSaved()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {FilePath}: {e.Message}");
            }
            return new();
        }
    }

    private static void SaveAll(Dictionary<string, int> saved)
    {
        lock (FileLock)
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(saved, Formatting.Indented));
    }

    public static TimeSpan DefaultOf(Definition d) => Defaults.GetValueOrDefault(d.Id, TimeSpan.FromMinutes(d.RecommendedMinutes));

    /// <summary>Applies the saved intervals (at startup)</summary>
    public static void Apply()
    {
        _ = Defaults.Count; // capture defaults first
        Dictionary<string, int> saved = LoadSaved();
        foreach (Definition d in All)
            if (saved.TryGetValue(d.Id, out int minutes))
                d.Worker().Interval = TimeSpan.FromMinutes(Math.Clamp(minutes, d.MinMinutes, d.MaxMinutes));
    }

    /// <summary>Sets intervals (minutes); returns an error for an unknown job or a value out of range</summary>
    public static string? Set(Dictionary<string, int> minutes)
    {
        foreach ((string id, int value) in minutes)
        {
            if (All.FirstOrDefault(d => d.Id == id) is not { } d)
                return $"Unknown job {id}";
            if (value < d.MinMinutes || value > d.MaxMinutes)
                return $"{d.Title}: must be between {d.MinMinutes} and {d.MaxMinutes} minutes";
        }
        _ = Defaults.Count;
        Dictionary<string, int> saved = LoadSaved();
        foreach ((string id, int value) in minutes)
        {
            Definition d = All.First(x => x.Id == id);
            saved[id] = value;
            d.Worker().Interval = TimeSpan.FromMinutes(value);
            Log.Info($"{d.Title}: every {value} minutes");
        }
        SaveAll(saved);
        return null;
    }

    /// <summary>Back to Tranga's recommended values</summary>
    public static void ResetAll()
    {
        _ = Defaults.Count;
        foreach (Definition d in All)
            d.Worker().Interval = TimeSpan.FromMinutes(d.RecommendedMinutes);
        SaveAll(All.ToDictionary(d => d.Id, d => d.RecommendedMinutes));
    }

    public static bool RequestRunNow(string id)
    {
        if (All.FirstOrDefault(d => d.Id == id) is not { } d)
            return false;
        lock (RunNowRequests)
            RunNowRequests.Add(d.Worker());
        return true;
    }

    /// <summary>True (once) if the job was asked to run now</summary>
    internal static bool TakeRunNow(IPeriodic worker)
    {
        lock (RunNowRequests)
            return RunNowRequests.Remove(worker);
    }
}
