using API.Naming;
using API.Schema.LibraryContext;
using API.Schema.LibraryContext.LibraryConnectors;
using API.Schema.MangaContext;
using log4net;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace API.KavitaCovers;

/// <summary>
/// Sets each manga's series cover in Kavita to the cover Tranga downloaded. A manga is only sent once (again if its cover
/// file changes), and only after Kavita has the series, i.e. after the first chapter was downloaded and Kavita scanned it.
/// State and settings live in kavita-covers.json next to settings.json.
/// </summary>
public static class KavitaCoverSync
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(KavitaCoverSync));
    private static readonly Lock FileLock = new();
    private static string StatePath => Path.Join(TrangaSettings.WorkingDirectory, "kavita-covers.json");

    public class PushedEntry
    {
        public int SeriesId { get; set; }
        public string CoverFile { get; set; } = "";
        public DateTime AtUtc { get; set; }
    }

    public class State
    {
        /// <summary>Send covers to Kavita automatically</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Lock the cover in Kavita, so its scans don't replace it with the first page</summary>
        public bool LockCover { get; set; } = true;
        public Dictionary<string, PushedEntry> Pushed { get; set; } = new();
    }

    /// <param name="MangaId">Manga.Key</param>
    /// <param name="MangaName">Its name</param>
    /// <param name="Status">set, notInKavita, noCover, failed</param>
    /// <param name="Message">Details</param>
    /// <param name="AtUtc">When</param>
    public record KavitaCoverResult(string MangaId, string MangaName, string Status, string Message, DateTime AtUtc);

    /// <summary>Recent outcomes, newest first (shown in Settings)</summary>
    public static readonly List<KavitaCoverResult> RecentResults = [];
    public static DateTime? LastRunUtc { get; private set; }

    public static State Load()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonConvert.DeserializeObject<State>(File.ReadAllText(StatePath)) ?? new State();
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {StatePath}: {e.Message}");
            }
            return new State();
        }
    }

    public static void Save(State state)
    {
        lock (FileLock)
            File.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    private static void Remember(KavitaCoverResult entry)
    {
        lock (RecentResults)
        {
            RecentResults.RemoveAll(r => r.MangaId == entry.MangaId);
            RecentResults.Insert(0, entry);
            if (RecentResults.Count > 50)
                RecentResults.RemoveRange(50, RecentResults.Count - 50);
        }
    }

    /// <summary>
    /// Sends covers that Kavita doesn't have yet.
    /// </summary>
    /// <param name="onlyMangaId">Just this manga (null = all downloaded manga)</param>
    /// <param name="force">Send even if already sent with the same cover</param>
    /// <returns>Number of covers set</returns>
    public static async Task<int> Run(MangaContext mangaContext, LibraryContext libraryContext, string? onlyMangaId, bool force,
        DateTime stopAt, CancellationToken ct)
    {
        State state = Load();
        if (!state.Enabled && !force)
            return 0;
        List<Kavita> kavitas = (await libraryContext.LibraryConnectors.ToListAsync(ct)).OfType<Kavita>().ToList();
        if (kavitas.Count == 0)
            return 0;
        LastRunUtc = DateTime.UtcNow;

        // Manga with at least one downloaded chapter (so Kavita can know the series)
        IQueryable<Manga> query = mangaContext.MangaWithMetadata().AsNoTracking()
            .Where(m => m.CoverFileNameInCache != null && mangaContext.Chapters.Any(c => c.ParentMangaId == m.Key && c.Downloaded));
        if (onlyMangaId is not null)
            query = query.Where(m => m.Key == onlyMangaId);
        List<Manga> mangas = await query.ToListAsync(ct);

        int set = 0;
        bool changed = false;
        foreach (Manga manga in mangas)
        {
            if (DateTime.UtcNow > stopAt || ct.IsCancellationRequested)
                break;
            if (!force && state.Pushed.TryGetValue(manga.Key, out PushedEntry? done) && done.CoverFile == manga.CoverFileNameInCache)
                continue;

            if (await manga.GetCoverImage(TrangaSettings.CoverImageCacheOriginal, ct) is not { } cover)
            {
                Remember(new(manga.Key, manga.Name, "noCover", "Tranga has no cover file for it yet.", DateTime.UtcNow));
                continue;
            }
            byte[] image = cover.stream.ToArray();
            await cover.stream.DisposeAsync();

            string[] titles = new[] { manga.Name, NamingFormatter.EnglishTitle(manga), manga.DirectoryName }
                .Concat(manga.AltTitles.Select(a => a.Title))
                .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).Distinct().ToArray();

            bool found = false;
            foreach (Kavita kavita in kavitas)
            {
                try
                {
                    if (await kavita.FindSeries(titles, ct) is not { } series)
                        continue;
                    found = true;
                    (bool ok, string message) = await kavita.UploadSeriesCover(series.Id, image, state.LockCover, ct);
                    if (ok)
                    {
                        state.Pushed[manga.Key] = new PushedEntry { SeriesId = series.Id, CoverFile = manga.CoverFileNameInCache!, AtUtc = DateTime.UtcNow };
                        changed = true;
                        set++;
                        Remember(new(manga.Key, manga.Name, "set", $"Cover set on Kavita series \"{series.Name}\" (id {series.Id}).", DateTime.UtcNow));
                        Log.Info($"Set Kavita cover of {manga.Name} (series {series.Id}).");
                    }
                    else
                    {
                        Remember(new(manga.Key, manga.Name, "failed", message, DateTime.UtcNow));
                        Log.Warn($"Couldn't set Kavita cover of {manga.Name}: {message}");
                    }
                }
                catch (Exception e)
                {
                    Remember(new(manga.Key, manga.Name, "failed", e.Message, DateTime.UtcNow));
                    Log.Warn($"Couldn't set Kavita cover of {manga.Name}: {e.Message}");
                }
            }
            if (!found)
                Remember(new(manga.Key, manga.Name, "notInKavita",
                    "Kavita doesn't have this series yet (it appears after Kavita scans the downloaded chapters). Tried again later.",
                    DateTime.UtcNow));
        }

        // Forget manga that no longer exist
        HashSet<string> all = (await mangaContext.Mangas.Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        foreach (string gone in state.Pushed.Keys.Where(k => !all.Contains(k)).ToList())
        {
            state.Pushed.Remove(gone);
            changed = true;
        }
        if (changed)
            Save(state);
        return set;
    }
}
