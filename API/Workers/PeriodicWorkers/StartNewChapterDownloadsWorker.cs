using System.Diagnostics.CodeAnalysis;
using API.Downloads;
using API.Schema.MangaContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Create new Workers for Chapters on Manga marked for Download, that havent been downloaded yet.
/// </summary>
public class StartNewChapterDownloadsWorker(TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{

    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromMinutes(1);
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Debug("Checking for missing chapters...");
        
        // Get missing chapters
        List<MangaConnectorId<Chapter>> missingChapters = await GetMissingChapters(MangaContext, CancellationToken);
        
        Log.DebugFormat("Found {0} missing chapters.", missingChapters.Count);
        // Running downloads, and ones queued behind the worker limit (they aren't "running" yet, but will be): counting
        // only running ones queued the same chapter again every minute while Tranga was busy
        List<DownloadChapterFromMangaconnectorWorker> running = Tranga.GetRunningWorkers()
            .Concat(Tranga.GetKnownWorkers())
            .Distinct()
            .OfType<DownloadChapterFromMangaconnectorWorker>()
            .Where(w => w.State is >= WorkerExecutionState.Created and < WorkerExecutionState.Completed)
            .ToList();
        HashSet<string> chaptersDownloading = running.Select(w => w.ChapterIdId).ToHashSet();
        // Chapters linked to websites Tranga no longer has (removed built-ins, deleted modules) can't be downloaded
        missingChapters.RemoveAll(ch => !Tranga.TryGetMangaConnector(ch.MangaConnectorName, out _));
        // Series download in queue order (first added, first downloaded); counted before removing running chapters,
        // so a series whose last chapters are downloading keeps its place
        Dictionary<string, int> queuePosition = DownloadQueue.Sync(missingChapters.Select(ch => ch.Obj.ParentMangaId));
        // Forget failures of chapters no longer waiting; skip chapters waiting for their next try or given up on
        DownloadFailures.Prune(missingChapters.Select(ch => ch.Key));
        DateTime now = DateTime.UtcNow;
        // A chapter can be marked on several websites (a series downloading from more than one): download it once,
        // from the series' preferred website if that has it
        HashSet<string> chaptersBeingDownloaded = missingChapters.Where(ch => chaptersDownloading.Contains(ch.Key))
            .Select(ch => ch.ObjId).ToHashSet();
        missingChapters = PickOneWebsitePerChapter(missingChapters, SeriesPreferences.PreferredWebsite,
            key => DownloadFailures.Get(key)?.GaveUp == true);
        missingChapters.RemoveAll(ch => chaptersBeingDownloaded.Contains(ch.ObjId) || DownloadFailures.IsWaiting(ch.Key, now)
                                        || DownloadQueue.IsPaused(ch.Obj.ParentMangaId));
        PartialDownloads.CleanupOld();
        if (DownloadQueue.PausedAll)
        {
            Log.Debug("Downloads are paused.");
            return [];
        }
        Log.DebugFormat("{0} chapter not being downloaded", missingChapters.Count);
        
        // Maximum concurrent downloads, overall and per website
        int perConnector = Math.Max(1, Tranga.Settings.MaxConcurrentDownloadsPerConnector);
        int amountNewWorkers = Math.Max(0, Tranga.Settings.MaxConcurrentDownloads - running.Count);
        Dictionary<string, int> perConnectorRunning = running
            .GroupBy(w => w.MangaConnectorName)
            .ToDictionary(g => g.Key, g => g.Count());
        
        Log.DebugFormat("{0} running download Workers ({1}). {2} available new download Workers, max {3} per website.",
            running.Count, string.Join(", ", perConnectorRunning.Select(kv => $"{kv.Key}: {kv.Value}")), amountNewWorkers, perConnector);

        List<MangaConnectorId<Chapter>> newDownloadChapters = [];
        IEnumerable<MangaConnectorId<Chapter>> inQueueOrder = missingChapters
            .OrderBy(ch => queuePosition.GetValueOrDefault(ch.Obj.ParentMangaId, int.MaxValue))
            .ThenBy(ch => ch.Obj, new Chapter.ChapterComparer());
        foreach (MangaConnectorId<Chapter> ch in inQueueOrder)
        {
            if (newDownloadChapters.Count >= amountNewWorkers)
                break;
            int already = perConnectorRunning.GetValueOrDefault(ch.MangaConnectorName);
            if (already >= perConnector)
                continue; // this website is busy; try chapters from other websites
            perConnectorRunning[ch.MangaConnectorName] = already + 1;
            newDownloadChapters.Add(ch);
        }

        // Create new jobs
        List<BaseWorker> newWorkers = newDownloadChapters.Select(mcId => new DownloadChapterFromMangaconnectorWorker(mcId)).ToList<BaseWorker>();
        
        return newWorkers.ToArray();
    }
    
    /// <summary>
    /// One website per chapter: the series' preferred website first, then the others by name, skipping websites Tranga
    /// gave up on for that chapter. While the chosen website waits to retry, the chapter waits for it.
    /// </summary>
    internal static List<MangaConnectorId<Chapter>> PickOneWebsitePerChapter(IEnumerable<MangaConnectorId<Chapter>> links,
        Func<string, string?> preferredWebsiteOfManga, Func<string, bool> gaveUp) =>
        links.GroupBy(l => l.ObjId)
            .Select(g =>
            {
                Chapter chapter = g.First().Obj;
                // ParentMangaId comes from the database; a chapter made in code only has ParentManga
                string? preferred = preferredWebsiteOfManga(chapter.ParentMangaId ?? chapter.ParentManga?.Key ?? "");
                return g.Where(l => !gaveUp(l.Key))
                           .OrderBy(l => string.Equals(l.MangaConnectorName, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                           .ThenBy(l => l.MangaConnectorName, StringComparer.OrdinalIgnoreCase)
                           .FirstOrDefault()
                       ?? g.First();
            })
            .ToList();

    internal static async Task<List<MangaConnectorId<Chapter>>> GetMissingChapters(MangaContext ctx, CancellationToken cancellationToken) => await ctx.MangaConnectorToChapter
        .Include(id => id.Obj)
        .Where(id => !id.Obj.Downloaded && id.UseForDownload)
        .ToListAsync(cancellationToken);
}