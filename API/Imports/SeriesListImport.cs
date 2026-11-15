using API.Downloads;
using API.MangaConnectors;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MangaDownloadWorkers;
using log4net;
using Microsoft.EntityFrameworkCore;

namespace API.Imports;

/// <summary>
/// Adds the series of a CSV list in the background, one after another: by link (any website Tranga knows), or by
/// title (searched; only an exact title match is added). Rows with the same title are one series on several websites.
/// One import runs at a time; its progress and per-row results stay available until the next one starts.
/// </summary>
public static class SeriesListImport
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SeriesListImport));
    private static readonly Lock JobLock = new();

    /// <summary>What happened to one row</summary>
    /// <param name="Status">added, website-added, exists, not-found, ambiguous, failed, skipped</param>
    public record RowResult(int Line, string Input, string Status, string Message, string? MangaId, string? MangaName);

    public class Job
    {
        public required string Id { get; init; }
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public int Total { get; init; }
        public List<RowResult> Results { get; } = [];
        public bool Cancelled { get; set; }
        public string? Current { get; set; }
        internal CancellationTokenSource Cancel { get; } = new();
        public bool Running => FinishedUtc is null;

        /// <summary>A copy of the results so far, safe to read while the import adds more</summary>
        public List<RowResult> Snapshot(out string? current)
        {
            lock (JobLock)
            {
                current = Current;
                return Results.OrderBy(r => r.Line).ToList();
            }
        }
    }

    private static Job? _job;

    public static Job? Current
    {
        get { lock (JobLock) return _job; }
    }

    /// <summary>Starts an import; null if one is already running</summary>
    public static Job? Start(List<SeriesListCsv.Row> rows, string? defaultLibraryId)
    {
        lock (JobLock)
        {
            if (_job is { Running: true })
                return null;
            List<SeriesListCsv.Row> usable = rows.Where(r => r.Problem is null).ToList();
            Job job = new() { Id = TokenGen.CreateToken("SeriesImport"), Total = rows.Count };
            foreach (SeriesListCsv.Row bad in rows.Where(r => r.Problem is not null))
                job.Results.Add(new RowResult(bad.Line, InputOf(bad), "failed", bad.Problem!, null, null));
            _job = job;
            _ = Task.Run(() => Run(job, usable, defaultLibraryId));
            return job;
        }
    }

    public static bool Stop()
    {
        lock (JobLock)
        {
            if (_job is not { Running: true } job)
                return false;
            job.Cancelled = true;
            job.Cancel.Cancel();
            return true;
        }
    }

    private static string InputOf(SeriesListCsv.Row r) =>
        r.Url ?? (r.Website is null ? r.Name ?? "" : $"{r.Name} ({r.Website})");

    private static void Add(Job job, RowResult r)
    {
        lock (JobLock)
            job.Results.Add(r);
    }

    /// <summary>"Title: The Thing!" and "title the thing" are the same title</summary>
    internal static string Normalise(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static async Task Run(Job job, List<SeriesListCsv.Row> rows, string? defaultLibraryId)
    {
        CancellationToken ct = job.Cancel.Token;
        Log.Info($"Series list import: {rows.Count} rows");
        // Series this import added (title + library → manga key), so further rows of a series add websites to it
        Dictionary<string, string> addedHere = new();
        try
        {
            foreach (SeriesListCsv.Row row in rows)
            {
                if (ct.IsCancellationRequested)
                {
                    Add(job, new RowResult(row.Line, InputOf(row), "skipped", "Import stopped", null, null));
                    continue;
                }
                lock (JobLock)
                    job.Current = InputOf(row);
                try
                {
                    Add(job, await ImportRow(row, defaultLibraryId, addedHere, ct));
                }
                catch (OperationCanceledException)
                {
                    Add(job, new RowResult(row.Line, InputOf(row), "skipped", "Import stopped", null, null));
                }
                catch (Exception e)
                {
                    Log.Warn($"Series list import, line {row.Line}: {e.Message}");
                    Add(job, new RowResult(row.Line, InputOf(row), "failed", e.GetBaseException().Message, null, null));
                }
                // Be gentle with the websites
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            lock (JobLock)
            {
                job.Current = null;
                job.FinishedUtc = DateTime.UtcNow;
            }
            Log.Info($"Series list import finished: {string.Join(", ", job.Results.GroupBy(r => r.Status).Select(g => $"{g.Count()} {g.Key}"))}");
        }
    }

    private static async Task<RowResult> ImportRow(SeriesListCsv.Row row, string? defaultLibraryId, Dictionary<string, string> addedHere, CancellationToken ct)
    {
        string input = InputOf(row);
        if (Tranga.ServiceProvider is null)
            return new RowResult(row.Line, input, "failed", "Tranga is still starting", null, null);
        using IServiceScope scope = Tranga.ServiceProvider.CreateScope();
        MangaContext context = scope.ServiceProvider.GetRequiredService<MangaContext>();

        // 1. Find the series on its website
        (Manga, MangaConnectorId<Manga>)? found;
        if (row.Url is { } url)
        {
            if (!Tranga.TryGetMangaConnector("Global", out MangaConnector? global))
                return new RowResult(row.Line, input, "failed", "Tranga's website list isn't ready", null, null);
            found = await Task.Run(() => global.GetMangaFromUrl(url), ct);
            if (found is null)
                return new RowResult(row.Line, input, "not-found",
                    "No website Tranga knows could open this link (is the website's module uploaded and switched on?)", null, null);
        }
        else
        {
            MangaConnector? connector = null;
            if (row.Website is { } site && !Tranga.TryGetMangaConnector(site, out connector))
                return new RowResult(row.Line, input, "failed", $"Tranga has no website called \"{site}\"", null, null);
            if (connector is { Enabled: false })
                return new RowResult(row.Line, input, "failed", $"{connector.Name} is switched off", null, null);
            if (connector is null && !Tranga.TryGetMangaConnector("Global", out connector))
                return new RowResult(row.Line, input, "failed", "Tranga's website list isn't ready", null, null);

            (Manga, MangaConnectorId<Manga>)[] results = await Task.Run(() => connector!.SearchManga(row.Name!), ct);
            string wanted = Normalise(row.Name!);
            List<(Manga, MangaConnectorId<Manga>)> exact = results
                .Where(r => Normalise(r.Item1.Name) == wanted || (r.Item1.AltTitles ?? []).Any(a => Normalise(a.Title) == wanted))
                .ToList();
            // The same series from several websites counts once (first website wins); different series don't
            List<IGrouping<string, (Manga, MangaConnectorId<Manga>)>> series = exact.GroupBy(r => r.Item1.Key).ToList();
            if (series.Count == 0)
                return new RowResult(row.Line, input, "not-found",
                    results.Length == 0
                        ? "Nothing found. Add a link column, or check the title."
                        : $"No exact title match. Closest: {string.Join(", ", results.Take(3).Select(r => $"{r.Item1.Name} ({r.Item2.MangaConnectorName})"))}",
                    null, null);
            if (series.Count > 1)
                return new RowResult(row.Line, input, "ambiguous",
                    $"Several series have this title: {string.Join(", ", series.Select(g => $"{g.First().Item1.Name} ({g.First().Item2.MangaConnectorName})"))}. Use a link instead.",
                    null, null);
            found = series[0].First();
        }

        // 2. Into the database (merges with a series already there)
        if (await context.AddMangaToContext(found.Value, ct) is not { } added)
            return new RowResult(row.Line, input, "failed", "Couldn't save the series to the database", null, null);
        Manga manga = await context.Mangas
            .Include(m => m.MangaConnectorIds)
            .Include(m => m.Library)
            .FirstAsync(m => m.Key == added.manga.Key, ct);
        MangaConnectorId<Manga> mcId = manga.MangaConnectorIds.First(id => id.Key == added.id.Key);

        string groupKey = $"{Normalise(row.Name ?? manga.Name)}|{row.Library?.ToLowerInvariant()}";
        bool partOfThisImport = addedHere.TryGetValue(groupKey, out string? groupManga) && groupManga == manga.Key;
        bool alreadyInTranga = !partOfThisImport && manga.MangaConnectorIds.Any(id => id.UseForDownload);
        if (alreadyInTranga)
            return new RowResult(row.Line, input, "exists", "Already in Tranga; left as it is", manga.Key, manga.Name);

        // 3. Library, start from chapter, download
        if (manga.Library is null)
        {
            FileLibrary? library = null;
            if (row.Library is { } lib)
                library = await context.FileLibraries.FirstOrDefaultAsync(l =>
                    l.LibraryName.ToLower() == lib.ToLower() || l.BasePath == lib, ct);
            if (library is null && row.Library is not null && defaultLibraryId is null)
                return new RowResult(row.Line, input, "failed", $"There's no library called \"{row.Library}\"", manga.Key, manga.Name);
            library ??= defaultLibraryId is null ? null : await context.FileLibraries.FirstOrDefaultAsync(l => l.Key == defaultLibraryId, ct);
            library ??= await context.FileLibraries.OrderBy(l => l.LibraryName).FirstOrDefaultAsync(ct);
            if (library is null)
                return new RowResult(row.Line, input, "failed", "Add a library (download folder) in Settings → Libraries first", manga.Key, manga.Name);
            manga.Library = library;
        }
        if (row.StartFrom is { } start)
            manga.IgnoreChaptersBefore = start;
        mcId.UseForDownload = row.Download;

        if (await context.Sync(ct, typeof(SeriesListImport), "Series list import") is { success: false } result)
            return new RowResult(row.Line, input, "failed", result.exceptionMessage ?? "Database error", manga.Key, manga.Name);

        if (row.Preferred)
            SeriesPreferences.SetPreferredWebsite(manga.Key, mcId.MangaConnectorName);

        // 4. Same as switching a website on in the website: cover, chapters, extra details
        List<BaseWorker> workers = [new DownloadCoverFromMangaconnectorWorker(mcId)];
        if (row.Download)
            workers.Add(new RetrieveMangaChaptersFromMangaconnectorWorker(mcId, Tranga.Settings.DownloadLanguage));
        Tranga.AddWorkers(workers);
        if (row.Download && !Tranga.GetKnownWorkers().OfType<FetchExtraMetadataWorker>().Any(w => w.MangaId == manga.Key))
            Tranga.AddWorker(new FetchExtraMetadataWorker(manga.Key));

        addedHere[groupKey] = manga.Key;
        return partOfThisImport
            ? new RowResult(row.Line, input, "website-added", $"Added {mcId.MangaConnectorName} as another website", manga.Key, manga.Name)
            : new RowResult(row.Line, input, "added",
                $"Added from {mcId.MangaConnectorName} to {manga.Library?.LibraryName}{(row.Download ? "" : " (not downloading)")}", manga.Key, manga.Name);
    }
}
