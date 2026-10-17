using API.Schema.ExtraMetadataContext;
using API.Schema.MangaContext;
using log4net;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace API.ExtraMetadata;

/// <summary>
/// Fetches and saves the extra details of a Manga (once; only re-fetched when forced).
/// </summary>
public static class ExtraMetadataStore
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ExtraMetadataStore));

    public enum Result { Stored, AlreadyStored, MangaNotFound, SourceUnavailable, SaveFailed }

    public static async Task<Result> FetchAndStore(MangaContext mangaContext, ExtraMetadataContext extraContext, string mangaId,
        bool force, CancellationToken ct)
    {
        if (!force && await extraContext.Entries.AnyAsync(e => e.MangaId == mangaId, ct))
            return Result.AlreadyStored;

        if (await mangaContext.MangaWithMetadata()
                .Include(m => m.MangaConnectorIds)
                .FirstOrDefaultAsync(m => m.Key == mangaId, ct) is not { } manga)
            return Result.MangaNotFound;

        ExtraMetadataData data;
        try
        {
            data = await ExtraMetadataFetcher.Fetch(manga, ct);
        }
        catch (ExtraMetadataFetcher.SourceUnavailableException e)
        {
            // Don't store a half result; it'll be retried by FillExtraMetadataWorker
            Log.InfoFormat("Extra metadata for {0} not fetched, source unavailable: {1}", mangaId, e.Message);
            return Result.SourceUnavailable;
        }

        await FillMissing(mangaContext, manga, data, ct);

        string json = JsonConvert.SerializeObject(data, ExtraMetadataFetcher.JsonSettings);
        if (await extraContext.Entries.FindAsync(new object?[] { mangaId }, ct) is { } existing)
        {
            existing.Data = json;
            existing.FetchedAt = DateTime.UtcNow;
        }
        else
        {
            extraContext.Entries.Add(new MangaExtraMetadata { MangaId = mangaId, Data = json, FetchedAt = DateTime.UtcNow });
        }

        if (await extraContext.Sync(ct, typeof(ExtraMetadataStore), "Extra metadata") is { success: false })
            return Result.SaveFailed;
        Log.DebugFormat("Stored extra metadata for {0} (MangaUpdates: {1}, rating: {2})", mangaId,
            data.MangaUpdates?.SeriesId.ToString() ?? "no match", data.ContentRating ?? "unknown");
        return Result.Stored;
    }

    /// <summary>
    /// Fills in what the website the manga came from left empty: synopsis, alternative titles, genres, authors and
    /// release status, from AniList (and MangaUpdates). Never overwrites anything the website provided.
    /// </summary>
    private static async Task FillMissing(MangaContext context, Manga manga, ExtraMetadataData data, CancellationToken ct)
    {
        AniListData? al = data.AniList;
        MangaUpdatesData? mu = data.MangaUpdates;
        if (al is null && mu is null)
            return;
        List<string> filled = [];

        if (string.IsNullOrWhiteSpace(manga.Description) && al?.Description is { } description)
        {
            manga.Description = description;
            filled.Add("synopsis");
        }

        if (manga.ReleaseStatus == MangaReleaseStatus.Unreleased && al?.Status is { } status)
        {
            MangaReleaseStatus? mapped = status switch
            {
                "FINISHED" => MangaReleaseStatus.Completed,
                "RELEASING" => MangaReleaseStatus.Continuing,
                "HIATUS" => MangaReleaseStatus.OnHiatus,
                "CANCELLED" => MangaReleaseStatus.Cancelled,
                _ => null
            };
            if (mapped is { } m)
            {
                manga.ReleaseStatus = m;
                filled.Add("status");
            }
        }

        // Alternative titles: add the ones Tranga doesn't have yet (at most 30 in total)
        HashSet<string> known = manga.AltTitles.Select(t => t.Title.Trim().ToLowerInvariant()).Append(manga.Name.Trim().ToLowerInvariant()).ToHashSet();
        string nativeLanguage = al?.CountryOfOrigin switch { "KR" => "ko", "CN" or "TW" => "zh", _ => "ja" };
        IEnumerable<(string Language, string? Title)> candidates = new (string, string?)[]
            {
                ("en", al?.TitleEnglish), ($"{nativeLanguage}-ro", al?.TitleRomaji), (nativeLanguage, al?.TitleNative)
            }
            .Concat((al?.Synonyms ?? []).Select(t => ("un", (string?)t)))
            .Concat((mu?.Associated ?? []).Select(t => ("un", (string?)t)));
        int added = 0;
        foreach ((string language, string? title) in candidates)
        {
            if (manga.AltTitles.Count >= 30)
                break;
            string t = title?.Trim() ?? "";
            if (t.Length is 0 or > 256 || !known.Add(t.ToLowerInvariant()))
                continue;
            manga.AltTitles.Add(new AltTitle(language, t));
            added++;
        }
        if (added > 0)
            filled.Add($"{added} alternative titles");

        if (manga.MangaTags.Count == 0)
        {
            List<string> genres = (mu?.Genres is { Count: > 0 } g ? g : al?.Genres ?? [])
                .Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 64).DistinctBy(x => x.ToLowerInvariant()).ToList();
            foreach (string genre in genres)
                manga.MangaTags.Add(await context.Tags.FindAsync([genre], ct) ?? new MangaTag(genre));
            if (genres.Count > 0)
                filled.Add("genres");
        }

        if (manga.Authors.Count == 0)
        {
            IEnumerable<string> people = mu?.Writers is { Count: > 0 } ? mu.Writers.Concat(mu.Artists) : (al?.Writers ?? []).Concat(al?.Artists ?? []);
            List<string> names = people.Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 128).DistinctBy(x => x.ToLowerInvariant()).ToList();
            foreach (string name in names)
            {
                Author author = new(name);
                manga.Authors.Add(await context.Authors.FindAsync([author.Key], ct) ?? author);
            }
            if (names.Count > 0)
                filled.Add("authors");
        }

        if (filled.Count == 0)
            return;
        if (await context.Sync(ct, typeof(ExtraMetadataStore), "Fill in missing details") is { success: false } result)
            Log.Warn($"Couldn't save the details filled in for {manga.Name}: {result.exceptionMessage}");
        else
            Log.Info($"Filled in {string.Join(", ", filled)} for {manga.Name} from {(al is not null ? "AniList" : "MangaUpdates")}");
    }

    public static ExtraMetadataData? Parse(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<ExtraMetadataData>(json, ExtraMetadataFetcher.JsonSettings);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
