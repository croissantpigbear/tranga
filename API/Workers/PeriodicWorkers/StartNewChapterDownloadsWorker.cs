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
        List<DownloadChapterFromMangaconnectorWorker> running = Tranga.GetRunningWorkers()
            .OfType<DownloadChapterFromMangaconnectorWorker>()
            .Where(w => w.State is >= WorkerExecutionState.Created and < WorkerExecutionState.Completed)
            .ToList();
        HashSet<string> chaptersDownloading = running.Select(w => w.ChapterIdId).ToHashSet();
        // Chapters linked to websites Tranga no longer has (removed built-ins, deleted modules) can't be downloaded
        missingChapters.RemoveAll(ch => !Tranga.TryGetMangaConnector(ch.MangaConnectorName, out _));
        // Series download in queue order (first added, first downloaded); counted before removing running chapters,
        // so a series whose last chapters are downloading keeps its place
        Dictionary<string, int> queuePosition = DownloadQueue.Sync(missingChapters.Select(ch => ch.Obj.ParentMangaId));
        missingChapters.RemoveAll(ch => chaptersDownloading.Contains(ch.Key));
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
    
    internal static async Task<List<MangaConnectorId<Chapter>>> GetMissingChapters(MangaContext ctx, CancellationToken cancellationToken) => await ctx.MangaConnectorToChapter
        .Include(id => id.Obj)
        .Where(id => !id.Obj.Downloaded && id.UseForDownload)
        .ToListAsync(cancellationToken);
}