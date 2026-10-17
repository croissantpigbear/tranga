using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.MangaDownloadWorkers;

/// <summary>
/// Retrieves the metadata of available chapters on the Mangaconnector
/// </summary>
/// <param name="mcId"></param>
/// <param name="language"></param>
/// <param name="dependsOn"></param>
public class RetrieveMangaChaptersFromMangaconnectorWorker(MangaConnectorId<Manga> mcId, string language, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    private readonly string _mangaConnectorIdId = mcId.Key;

    /// <summary>
    /// Key of the <see cref="MangaConnectorId{T}"/> this worker retrieves Chapters for
    /// </summary>
    public string MangaConnectorIdId => _mangaConnectorIdId;

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ActionsContext ActionsContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
        ActionsContext = GetContext<ActionsContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.DebugFormat("Getting Chapters for MangaConnectorId {0}...", _mangaConnectorIdId);
        // Getting MangaConnector info
        if (await MangaContext.MangaConnectorToManga
                .Include(id => id.Obj)
                .ThenInclude(m => m.Chapters)
                .ThenInclude(ch => ch.MangaConnectorIds)
                .FirstOrDefaultAsync(c => c.Key == _mangaConnectorIdId, CancellationToken) is not { } mangaConnectorId)
        {
            Log.Error("Could not get MangaConnectorId.");
            return []; //TODO Exception?
        }
        if (!Tranga.TryGetMangaConnector(mangaConnectorId.MangaConnectorName, out MangaConnector? mangaConnector))
        {
            Log.Error("Could not get MangaConnector.");
            return []; //TODO Exception?
        }
        Log.DebugFormat("Getting Chapters for MangaConnectorId {0}...", mangaConnectorId);
        
        Manga manga = mangaConnectorId.Obj;
        
        // Retrieve available Chapters from Connector
        (Chapter chapter, MangaConnectorId<Chapter> chapterId)[] allChapters =
            mangaConnector.GetChapters(mangaConnectorId, language).DistinctBy(c => c.Item1.Key).ToArray();
        Log.DebugFormat("Got {0} chapters from connector.", allChapters.Length);
        
        // Filter for new Chapters
        List<(Chapter chapter, MangaConnectorId<Chapter> chapterId)> newChapters = allChapters.Where<(Chapter chapter, MangaConnectorId<Chapter> chapterId)>(ch =>
            manga.Chapters.All(c => c.Key != ch.chapter.Key)).ToList();
        Log.DebugFormat("Got {0} new chapters.", newChapters.Count);

        // New chapters of a series Tranga already had are releases (for the release calendar)
        if (mangaConnectorId.UseForDownload && manga.Chapters.Count > 0)
            API.Downloads.ReleaseLog.Record(manga.Key, newChapters.Select(ch => ch.chapter.Key).ToList());

        // Add Chapters to Manga
        manga.Chapters = manga.Chapters.Union(newChapters.Select(ch => ch.chapter)).ToList();
        
        // Filter for new ChapterIds
        List<MangaConnectorId<Chapter>> existingChapterIds = manga.Chapters.SelectMany(c => c.MangaConnectorIds).ToList();
        List<MangaConnectorId<Chapter>> newIds = allChapters.Select(ch => ch.chapterId)
            .Where(newCh => !existingChapterIds.Any(existing =>
                existing.MangaConnectorName == newCh.MangaConnectorName &&
                existing.IdOnConnectorSite == newCh.IdOnConnectorSite))
            .ToList();
        // Match tracked entities of Chapters
        foreach (MangaConnectorId<Chapter> newId in newIds)
            newId.Obj = manga.Chapters.First(ch => ch.Key == newId.ObjId);
        Log.DebugFormat("Got {0} new download-Ids.", newIds.Count);
        
        // Add new ChapterIds to Database
        MangaContext.MangaConnectorToChapter.AddRange(newIds);

        // If Manga is marked for Download from Connector, mark its new chapter links as UseForDownload: new Chapters, and
        // Chapters already known from another website that weren't downloaded yet (e.g. after moving to another website)
        if (mangaConnectorId.UseForDownload)
        {
            // Chapters new to this Manga (their links are already attached to them, so they aren't in newIds)
            // Chapters before the series' "start from chapter" are left alone
            foreach ((Chapter chapter, MangaConnectorId<Chapter> chapterId) in newChapters)
                if (!Chapter.IsBeforeStart(chapter.ChapterNumber, manga.IgnoreChaptersBefore))
                    chapterId.UseForDownload = true;
            // New links for Chapters already known from another website
            foreach (MangaConnectorId<Chapter> chapterId in newIds)
            {
                if (!chapterId.Obj.Downloaded && !Chapter.IsBeforeStart(chapterId.Obj.ChapterNumber, manga.IgnoreChaptersBefore))
                    chapterId.UseForDownload = true;
            }
        }

        // An imported folder: chapters whose files are already there count as downloaded
        if (API.Imports.LibraryImport.IsPending(manga.Key))
        {
            await MangaContext.Entry(manga).Reference(m => m.Library).LoadAsync(CancellationToken);
            API.Imports.LibraryImport.MatchExisting(manga, manga.Chapters);
        }

        if(await MangaContext.Sync(CancellationToken, GetType(), "Chapters retrieved") is { success: false } mangaContextException)
            Log.ErrorFormat("Failed to save database changes: {0}", mangaContextException.exceptionMessage);

        ActionsContext.Actions.Add(new ChaptersRetrievedActionRecord(manga));
        if(await ActionsContext.Sync(CancellationToken, GetType(), "Chapters retrieved") is { success: false } actionsContextException)
            Log.ErrorFormat("Failed to save database changes: {0}", actionsContextException.exceptionMessage);

        return [];
    }

    public override string ToString() => $"{base.ToString()} {_mangaConnectorIdId}";
}