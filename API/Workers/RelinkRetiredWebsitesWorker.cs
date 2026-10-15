using System.Diagnostics.CodeAnalysis;
using API.LuaModules;
using API.MangaConnectors;
using API.Schema.MangaContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers;

/// <summary>
/// Moves Manga linked to a removed built-in website (<see cref="Tranga.RetiredMangaConnectorNames"/>) to an uploaded
/// FMD2 module for the same site (matched by the link's address), keeping the Manga, its downloaded chapters and
/// whether it is downloaded. Links no module handles yet are left as they are, so they can be moved once one is uploaded.
/// Runs at startup and after a module is uploaded or switched on.
/// </summary>
public class RelinkRetiredWebsitesWorker(IEnumerable<BaseWorker>? dependsOn = null) : BaseWorkerWithContexts(dependsOn)
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
    }

    /// <summary>Queues a run unless one is already waiting</summary>
    public static void Queue()
    {
        if (Tranga.GetKnownWorkers().Concat(Tranga.GetRunningWorkers())
            .OfType<RelinkRetiredWebsitesWorker>()
            .Any(w => w.State is >= WorkerExecutionState.Created and < WorkerExecutionState.Running))
            return;
        Tranga.AddWorker(new RelinkRetiredWebsitesWorker());
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        string[] retired = Tranga.RetiredMangaConnectorNames;
        List<MangaConnectorId<Manga>> links = await MangaContext.MangaConnectorToManga
            .Include(id => id.Obj)
            .ThenInclude(m => m.MangaConnectorIds)
            .Where(id => retired.Contains(id.MangaConnectorName))
            .ToListAsync(CancellationToken);
        if (links.Count == 0)
            return [];

        List<BaseWorker> followUp = [];
        List<string> waiting = [];
        foreach (MangaConnectorId<Manga> old in links)
        {
            if (CancellationToken.IsCancellationRequested)
                break;
            Manga manga = old.Obj;
            LuaMangaConnector? site = old.WebsiteUrl is { Length: > 0 } url
                ? LuaModuleStore.Connectors.FirstOrDefault(c => c.Enabled && c.UrlMatchesConnector(url))
                : null;
            if (site is null)
            {
                waiting.Add($"{manga.Name} ({old.MangaConnectorName})");
                continue;
            }

            MangaConnectorId<Manga>? moved = manga.MangaConnectorIds.FirstOrDefault(id => id.MangaConnectorName == site.Name);
            if (moved is null)
            {
                (Manga, MangaConnectorId<Manga>)? found;
                try
                {
                    found = site.GetMangaFromUrl(old.WebsiteUrl!);
                }
                catch (Exception e)
                {
                    Log.Debug($"{site.Name} failed on {old.WebsiteUrl}: {e.Message}");
                    found = null;
                }
                if (found is not { } f)
                {
                    waiting.Add($"{manga.Name} ({old.MangaConnectorName}: {site.Name} couldn't read {old.WebsiteUrl})");
                    continue;
                }
                moved = new MangaConnectorId<Manga>(manga, site.Name, f.Item2.IdOnConnectorSite, f.Item2.WebsiteUrl, old.UseForDownload);
                MangaContext.MangaConnectorToManga.Add(moved);
            }
            else if (old.UseForDownload)
                moved.UseForDownload = true;

            // The old website's chapter links can't download anything any more
            string oldName = old.MangaConnectorName;
            await MangaContext.MangaConnectorToChapter
                .Where(ch => ch.MangaConnectorName == oldName && ch.Obj.ParentMangaId == manga.Key)
                .ExecuteDeleteAsync(CancellationToken);
            MangaContext.MangaConnectorToManga.Remove(old);

            if (await MangaContext.Sync(CancellationToken, GetType(), "Moved to uploaded website") is { success: false } syncResult)
            {
                Log.Error($"Couldn't move {manga.Name} from {oldName} to {site.Name}: {syncResult.exceptionMessage}");
                continue;
            }
            Log.Info($"Moved {manga.Name} from the removed website {oldName} to {site.Name}");
            if (moved.UseForDownload)
                followUp.Add(new RetrieveMangaChaptersFromMangaconnectorWorker(moved, Tranga.Settings.DownloadLanguage));
        }

        if (waiting.Count > 0)
            Log.Info($"{waiting.Count} manga are linked to a removed built-in website and stay as they are until an FMD2 module " +
                     $"for that site is uploaded (or add them again from a working site): {string.Join("; ", waiting.Take(20))}");
        return followUp.ToArray();
    }
}
