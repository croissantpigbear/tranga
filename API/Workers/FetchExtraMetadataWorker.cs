using System.Diagnostics.CodeAnalysis;
using API.ExtraMetadata;
using API.Schema.ExtraMetadataContext;
using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers;

/// <summary>
/// Fetches and stores the extra details (MangaUpdates, MangaDex content rating) of one Manga.
/// Queued when a Manga is marked for download, or on request.
/// </summary>
public class FetchExtraMetadataWorker(string mangaId, bool force = false, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    public string MangaId { get; } = mangaId;

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
        ExtraMetadataStore.Result result =
            await ExtraMetadataStore.FetchAndStore(MangaContext, ExtraMetadataContext, MangaId, force, CancellationToken);
        Log.DebugFormat("Extra metadata for {0}: {1}", MangaId, result);
        // Chapters downloaded before the details arrived get them in their ComicInfo.xml now
        if (result == ExtraMetadataStore.Result.Stored && Constants.CreateComicInfoXml && !RewriteComicInfoWorker.IsQueued(MangaId)
            && await MangaContext.Chapters.AnyAsync(c => c.ParentMangaId == MangaId && c.Downloaded, CancellationToken))
            return [new RewriteComicInfoWorker(MangaId)];
        return [];
    }

    public override string ToString() => $"{base.ToString()} {MangaId}";
}
