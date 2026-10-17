using System.Diagnostics.CodeAnalysis;
using API.ExtraMetadata;
using API.Schema.ExtraMetadataContext;
using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Fetches extra details for every Manga in the library (downloaded from at least one MangaConnector) that has none yet,
/// e.g. existing Manga or ones whose fetch failed, and removes details of Manga that no longer exist.
/// Details are only fetched once; they aren't refreshed on a schedule.
/// </summary>
public class FillExtraMetadataWorker(TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromMinutes(30);

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ExtraMetadataContext ExtraMetadataContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
        ExtraMetadataContext = GetContext<ExtraMetadataContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        // Remove details of deleted Manga
        List<string> allManga = await MangaContext.Mangas.Select(m => m.Key).ToListAsync(CancellationToken);
        int removed = await ExtraMetadataContext.Entries
            .Where(e => !allManga.Contains(e.MangaId))
            .ExecuteDeleteAsync(CancellationToken);
        if (removed > 0)
            Log.DebugFormat("Removed extra metadata of {0} deleted Manga.", removed);

        List<string> library = await MangaContext.MangaConnectorToManga
            .Where(id => id.UseForDownload)
            .Select(id => id.ObjId)
            .Distinct()
            .ToListAsync(CancellationToken);
        // Stored details from before AniList was added count as missing (fetched again once)
        HashSet<string> stored = (await ExtraMetadataContext.Entries.AsNoTracking().Select(e => new { e.MangaId, e.Data }).ToListAsync(CancellationToken))
            .Where(e => ExtraMetadataStore.Parse(e.Data) is { Version: >= ExtraMetadataData.CurrentVersion })
            .Select(e => e.MangaId)
            .ToHashSet();
        List<string> missing = library.Where(id => !stored.Contains(id)).ToList();
        if (missing.Count == 0)
            return [];

        // Stay well inside the worker timeout; whatever is left is done on the next run
        DateTime stopAt = DateTime.UtcNow + Constants.WorkerTimeout - TimeSpan.FromMinutes(1);
        int done = 0;
        List<string> newlyStored = [];
        Log.InfoFormat("Fetching extra metadata for {0} Manga...", missing.Count);
        foreach (string mangaId in missing)
        {
            if (DateTime.UtcNow > stopAt || CancellationToken.IsCancellationRequested)
                break;
            ExtraMetadataStore.Result result =
                await ExtraMetadataStore.FetchAndStore(MangaContext, ExtraMetadataContext, mangaId, true, CancellationToken);
            if (result == ExtraMetadataStore.Result.SourceUnavailable)
                break; // MangaUpdates/MangaDex down: try again next run
            if (result == ExtraMetadataStore.Result.Stored)
                newlyStored.Add(mangaId);
            done++;
        }
        Log.InfoFormat("Fetched extra metadata for {0} of {1} Manga.", done, missing.Count);

        // Put the new details into the ComicInfo.xml of chapters already downloaded
        if (!Constants.CreateComicInfoXml || newlyStored.Count == 0)
            return [];
        List<string> withChapters = await MangaContext.Chapters
            .Where(c => c.Downloaded && newlyStored.Contains(c.ParentMangaId))
            .Select(c => c.ParentMangaId).Distinct().ToListAsync(CancellationToken);
        return withChapters.Where(id => !RewriteComicInfoWorker.IsQueued(id))
            .Select(id => (BaseWorker)new RewriteComicInfoWorker(id)).ToArray();
    }
}
