using API.ExtraMetadata;
using API.Schema.ExtraMetadataContext;
using API.Schema.MangaContext;
using API.Workers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Extra details stored once per Manga (writers, artists, publishers, genres, content rating, ...)
/// from MangaUpdates and MangaDex, so clients don't have to query those sites themselves.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class ExtraMetadataController(ExtraMetadataContext context, MangaContext mangaContext) : ControllerBase
{
    /// <summary>Stored extra details of one Manga</summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <param name="FetchedAt">When the details were fetched (UTC)</param>
    /// <param name="Data">The details</param>
    public record MangaExtraMetadataDto(string MangaId, DateTime FetchedAt, ExtraMetadataData Data);

    /// <summary>
    /// Returns the stored extra details of all Manga (one request for the whole library, e.g. for filtering)
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<List<MangaExtraMetadataDto>>(Status200OK, "application/json")]
    public async Task<Ok<List<MangaExtraMetadataDto>>> GetAll()
    {
        List<MangaExtraMetadata> rows = await context.Entries.AsNoTracking().ToListAsync(HttpContext.RequestAborted);
        List<MangaExtraMetadataDto> result = rows
            .Select(r => (row: r, data: ExtraMetadataStore.Parse(r.Data)))
            .Where(x => x.data is not null)
            .Select(x => new MangaExtraMetadataDto(x.row.MangaId, x.row.FetchedAt, x.data!))
            .ToList();
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns the stored extra details of one Manga
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="200"></response>
    /// <response code="404">No details stored (yet) for this Manga</response>
    [HttpGet("{MangaId}")]
    [ProducesResponseType<MangaExtraMetadataDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<MangaExtraMetadataDto>, NotFound<string>>> Get(string MangaId)
    {
        if (await context.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.MangaId == MangaId, HttpContext.RequestAborted) is not { } row
            || ExtraMetadataStore.Parse(row.Data) is not { } data)
            return TypedResults.NotFound(nameof(MangaId));
        return TypedResults.Ok(new MangaExtraMetadataDto(row.MangaId, row.FetchedAt, data));
    }

    /// <summary>
    /// (Re-)fetch the extra details of a Manga from MangaUpdates and MangaDex now
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="202">Fetch queued</response>
    /// <response code="404">Manga not found</response>
    [HttpPost("{MangaId}/Refresh")]
    [ProducesResponseType(Status202Accepted)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Accepted, NotFound<string>>> Refresh(string MangaId)
    {
        if (!await mangaContext.Mangas.AnyAsync(m => m.Key == MangaId, HttpContext.RequestAborted))
            return TypedResults.NotFound(nameof(MangaId));
        if (!Tranga.GetKnownWorkers().OfType<FetchExtraMetadataWorker>().Any(w => w.MangaId == MangaId))
            Tranga.AddWorker(new FetchExtraMetadataWorker(MangaId, force: true));
        return TypedResults.Accepted((string?)null);
    }
}
