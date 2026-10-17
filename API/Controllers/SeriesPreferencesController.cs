using API.Downloads;
using API.Schema.MangaContext;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Per-series download preferences: the preferred website, for series that download from more than one website.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class SeriesPreferencesController(MangaContext context) : ControllerBase
{
    /// <summary>Download preferences of a series</summary>
    /// <param name="PreferredWebsite">Website chapters come from when more than one has them (null: Tranga picks)</param>
    public record SeriesPreferencesDto(string? PreferredWebsite);

    /// <summary>
    /// Returns the download preferences of a series
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="200"></response>
    /// <response code="404">Manga not found</response>
    [HttpGet("{MangaId}")]
    [ProducesResponseType<SeriesPreferencesDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<SeriesPreferencesDto>, NotFound<string>>> Get(string MangaId)
    {
        if (!await context.Mangas.AnyAsync(m => m.Key == MangaId, HttpContext.RequestAborted))
            return TypedResults.NotFound(nameof(MangaId));
        return TypedResults.Ok(new SeriesPreferencesDto(SeriesPreferences.PreferredWebsite(MangaId)));
    }

    /// <summary>
    /// Sets the download preferences of a series
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <param name="preferences">PreferredWebsite: a website the series is linked to, or null for no preference</param>
    /// <response code="200"></response>
    /// <response code="400">The series isn't linked to that website</response>
    /// <response code="404">Manga not found</response>
    [HttpPut("{MangaId}")]
    [ProducesResponseType<SeriesPreferencesDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<SeriesPreferencesDto>, BadRequest<string>, NotFound<string>>> Set(string MangaId, [FromBody] SeriesPreferencesDto preferences)
    {
        if (await context.Mangas
                .Include(m => m.MangaConnectorIds)
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        string? website = string.IsNullOrWhiteSpace(preferences.PreferredWebsite) ? null : preferences.PreferredWebsite.Trim();
        if (website is not null)
        {
            if (manga.MangaConnectorIds.FirstOrDefault(id =>
                    string.Equals(id.MangaConnectorName, website, StringComparison.OrdinalIgnoreCase)) is not { } link)
                return TypedResults.BadRequest($"{manga.Name} isn't linked to {website}.");
            website = link.MangaConnectorName;
        }
        SeriesPreferences.SetPreferredWebsite(MangaId, website);
        return TypedResults.Ok(new SeriesPreferencesDto(website));
    }
}
