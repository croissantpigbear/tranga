using System.Diagnostics.CodeAnalysis;
using API.ComicInfo;
using API.ExtraMetadata;
using API.Schema.ExtraMetadataContext;
using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers;

/// <summary>
/// Rewrites ComicInfo.xml inside already-downloaded chapter archives (one Manga, or all), so they carry the current
/// metadata. Queued from the API, and automatically when a Manga's extra details are stored after chapters were downloaded.
/// Stops before the worker timeout and queues itself again for the rest.
/// </summary>
public class RewriteComicInfoWorker(string? mangaId = null, int skip = 0, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    /// <summary>null = all Manga</summary>
    public string? MangaId { get; } = mangaId;

    /// <summary>Progress of the current or last run (shown in the website)</summary>
    public static class Progress
    {
        private static readonly Lock Lock = new();
        public static int Total { get; private set; }
        public static int Done { get; private set; }
        public static int Failed { get; private set; }
        public static bool Running { get; private set; }
        public static DateTime? FinishedUtc { get; private set; }

        internal static void Start(int total, bool continuing)
        {
            lock (Lock)
            {
                if (!continuing) { Total = total; Done = 0; Failed = 0; }
                Running = true;
            }
        }
        internal static void Step(bool ok) { lock (Lock) { if (ok) Done++; else Failed++; } }
        internal static void Finish(bool more) { lock (Lock) { Running = more; if (!more) FinishedUtc = DateTime.UtcNow; } }
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ExtraMetadataContext ExtraMetadataContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
        ExtraMetadataContext = GetContext<ExtraMetadataContext>(serviceScope);
    }

    /// <summary>True if a rewrite covering <paramref name="mangaId"/> is already queued or running</summary>
    public static bool IsQueued(string? mangaId) =>
        Tranga.GetKnownWorkers().Concat(Tranga.GetRunningWorkers())
            .OfType<RewriteComicInfoWorker>()
            .Any(w => w.State is >= WorkerExecutionState.Created and < WorkerExecutionState.Completed && (w.MangaId is null || w.MangaId == mangaId));

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        IQueryable<Chapter> query = MangaContext.Chapters.Where(c => c.Downloaded);
        if (MangaId is not null)
            query = query.Where(c => c.ParentMangaId == MangaId);
        List<string> chapterIds = await query.OrderBy(c => c.ParentMangaId).ThenBy(c => c.Key)
            .Select(c => c.Key).ToListAsync(CancellationToken);

        bool trackProgress = MangaId is null;
        if (trackProgress)
            Progress.Start(chapterIds.Count, continuing: skip > 0);

        DateTime stopAt = DateTime.UtcNow + Constants.WorkerTimeout - TimeSpan.FromMinutes(1);
        int index = skip;
        Dictionary<string, (Manga manga, ExtraMetadataData? extra, string? source)> mangaCache = new();
        HashSet<string> touchedFolders = [];

        for (; index < chapterIds.Count; index++)
        {
            if (DateTime.UtcNow > stopAt || CancellationToken.IsCancellationRequested)
                break;
            bool ok = await RewriteOne(chapterIds[index], mangaCache, touchedFolders);
            if (trackProgress)
                Progress.Step(ok);
        }

        // Kavita skips folders whose timestamp hasn't changed since its last scan
        foreach (string folder in touchedFolders)
        {
            try { Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); }
            catch (Exception e) { Log.Debug($"Couldn't touch {folder}: {e.Message}"); }
        }

        bool more = index < chapterIds.Count && !CancellationToken.IsCancellationRequested;
        if (trackProgress)
            Progress.Finish(more);
        Log.InfoFormat("Rewrote ComicInfo.xml in {0} of {1} chapters{2}.", index - skip, chapterIds.Count - skip,
            MangaId is null ? "" : $" of {MangaId}");

        if (more)
            return [new RewriteComicInfoWorker(MangaId, index)];
        if (touchedFolders.Count > 0)
            return [new RefreshLibrariesWorker()]; // ask Kavita/Komga to rescan
        return [];
    }

    private async Task<bool> RewriteOne(string chapterId,
        Dictionary<string, (Manga manga, ExtraMetadataData? extra, string? source)> mangaCache, HashSet<string> touchedFolders)
    {
        try
        {
            if (await MangaContext.Chapters
                    .Include(c => c.ParentManga).ThenInclude(m => m.Library)
                    .Include(c => c.MangaConnectorIds)
                    .FirstOrDefaultAsync(c => c.Key == chapterId, CancellationToken) is not { } chapter)
                return false;
            if (chapter.FullArchiveFilePath is not { } path || !File.Exists(path))
                return false;

            if (!mangaCache.TryGetValue(chapter.ParentMangaId, out var info))
            {
                Manga manga = await MangaContext.MangaWithMetadata().AsNoTracking()
                    .FirstAsync(m => m.Key == chapter.ParentMangaId, CancellationToken);
                ExtraMetadataData? extra = await ExtraMetadataContext.Entries.AsNoTracking()
                    .Where(e => e.MangaId == chapter.ParentMangaId)
                    .Select(e => e.Data)
                    .FirstOrDefaultAsync(CancellationToken) is { } json ? ExtraMetadataStore.Parse(json) : null;
                info = (manga, extra, null);
                mangaCache[chapter.ParentMangaId] = info;
            }

            string? source = chapter.MangaConnectorIds.FirstOrDefault(id => id.UseForDownload)?.MangaConnectorName
                             ?? chapter.MangaConnectorIds.FirstOrDefault()?.MangaConnectorName;
            await ComicInfoBuilder.RewriteArchive(path,
                pages => ComicInfoBuilder.Build(chapter, info.manga, info.extra, pages, source), CancellationToken);
            if (Path.GetDirectoryName(path) is { } dir)
                touchedFolders.Add(dir);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't rewrite ComicInfo.xml of chapter {chapterId}: {e.Message}");
            return false;
        }
    }

    public override string ToString() => $"{base.ToString()} {MangaId ?? "all"}";
}
