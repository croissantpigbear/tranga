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
