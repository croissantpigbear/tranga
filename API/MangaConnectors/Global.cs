using API.Schema.MangaContext;

namespace API.MangaConnectors;

public class Global : MangaConnector
{
    public Global() : base("Global", ["all"], [""], "https://avatars.githubusercontent.com/u/13404778")
    {
    }

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.Debug("Searching Manga on all enabled connectors:");
        //Get all enabled Connectors
        MangaConnector[] enabledConnectors = Tranga.MangaConnectors.Where(c => c.Enabled && c.Name != "Global").ToArray();
        Log.Debug(string.Join(", ", enabledConnectors.Select(c => c.Name)));
        
        //Create Task for each MangaConnector to search simultaneously
        Task<(Manga, MangaConnectorId<Manga>)[]>[] tasks =
            enabledConnectors.Select(c => new Task<(Manga, MangaConnectorId<Manga>)[]>(() => c.SearchManga(mangaSearchName))).ToArray();
        foreach (Task<(Manga, MangaConnectorId<Manga>)[]> task in tasks)
            task.Start();
        
        //Wait for all tasks to finish
        do
        {
            Thread.Sleep(500);
            Log.DebugFormat("Waiting for search to finish: {0}", tasks.Count(t => !t.IsCompleted));
        }while(tasks.Any(t => !t.IsCompleted));
        
        //Concatenate all results into one
        (Manga, MangaConnectorId<Manga>)[] ret = tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : []).SelectMany(i => i).ToArray();
        Log.DebugFormat("Got {0} results.", ret.Length);
        return ret;
    }

    /// <summary>
    /// Tries every enabled website that handles <paramref name="url"/>: websites added from FMD2 modules first (the user
    /// uploaded them on purpose, often because the built-in one stopped working), then the built-in ones.
    /// </summary>
    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        IEnumerable<MangaConnector> candidates = Tranga.MangaConnectors
            .Where(c => c.Name != "Global" && c.Enabled && c.UrlMatchesConnector(url))
            .OrderBy(c => c is LuaMangaConnector ? 0 : 1);
        foreach (MangaConnector mc in candidates)
        {
            try
            {
                if (mc.GetMangaFromUrl(url) is { } result)
                    return result;
                Log.Debug($"{mc.Name} couldn't read {url}, trying the next website that handles it");
            }
            catch (Exception e)
            {
                Log.Debug($"{mc.Name} failed on {url}: {e.Message}");
            }
        }
        return null;
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        return null;
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId,
        string? language = null)
    {
        if (!Tranga.TryGetMangaConnector(mangaId.MangaConnectorName, out MangaConnector? mangaConnector))
            return [];
        return mangaConnector.GetChapters(mangaId, language);
    }

    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
        if (!Tranga.TryGetMangaConnector(chapterId.MangaConnectorName, out MangaConnector? mangaConnector))
            return [];
        return mangaConnector.GetChapterImageUrls(chapterId);
    }
}