using System.Text;
using API.Downloads;
using API.Imports;
using API.Schema.MangaContext;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Series list as CSV: export every series (one row per series per website), and import a list to add many series at
/// once, by link or by title. See <see cref="SeriesListCsv"/> for the columns.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class SeriesListController(MangaContext context) : ControllerBase
{
    /// <summary>A list to import</summary>
    /// <param name="Csv">The CSV text</param>
    /// <param name="LibraryId">Library for rows that don't name one (null: the first library)</param>
    public record SeriesListImportRequest(string Csv, string? LibraryId = null);

    /// <summary>A row of the list, as Tranga read it</summary>
    public record SeriesListPreviewRowDto(int Line, string? Name, string? Url, string? Website, string? Library,
        float? StartFrom, bool Download, bool Preferred, string? Problem);

    /// <summary>What Tranga read from a list</summary>
    /// <param name="Rows">Every row</param>
    /// <param name="Series">How many series that is (rows with the same title are one series)</param>
    public record SeriesListPreviewDto(List<SeriesListPreviewRowDto> Rows, int Series);

    /// <summary>One row's result</summary>
    public record SeriesListRowResultDto(int Line, string Input, string Status, string Message, string? MangaId, string? MangaName);

    /// <summary>Progress of the current (or last) import</summary>
    public record SeriesListImportStatusDto(bool Running, bool Cancelled, DateTime StartedUtc, DateTime? FinishedUtc,
        int Total, int Done, string? Current, List<SeriesListRowResultDto> Results);

    private const int MaxRows = 5000;

    /// <summary>
    /// Downloads every series in Tranga as a CSV (one row per series per website)
    /// </summary>
    /// <response code="200">The .csv</response>
    [HttpGet("Export")]
    [ProducesResponseType<FileContentResult>(Status200OK, "text/csv")]
    public async Task<FileContentHttpResult> Export()
    {
        CancellationToken ct = HttpContext.RequestAborted;
        List<Manga> mangas = await context.Mangas
            .Include(m => m.MangaConnectorIds)
            .Include(m => m.Library)
            .AsNoTracking()
            .Where(m => m.LibraryId != null || m.MangaConnectorIds.Any(id => id.UseForDownload))
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
        // Chapter numbers per series, for the "downloaded" and "latest" columns
        var chapters = (await context.Chapters
                .AsNoTracking()
                .Select(c => new { c.ParentMangaId, c.ChapterNumber, c.Downloaded })
                .ToListAsync(ct))
            .GroupBy(c => c.ParentMangaId)
            .ToDictionary(g => g.Key, g => (Downloaded: g.Count(c => c.Downloaded),
                Latest: g.Select(c => c.ChapterNumber).MaxBy(Chapter.NumberValue)));

        List<string?[]> rows = [];
        foreach (Manga m in mangas)
        {
            string? preferred = SeriesPreferences.PreferredWebsite(m.Key);
            (int Downloaded, string? Latest) ch = chapters.GetValueOrDefault(m.Key);
            IEnumerable<Schema.MangaContext.MangaConnectorId<Manga>> links = m.MangaConnectorIds
                .OrderByDescending(id => id.UseForDownload).ThenBy(id => id.MangaConnectorName);
            foreach (Schema.MangaContext.MangaConnectorId<Manga> id in links)
                rows.Add([
                    m.Name, id.MangaConnectorName, id.WebsiteUrl, id.UseForDownload ? "yes" : "no",
                    string.Equals(preferred, id.MangaConnectorName, StringComparison.OrdinalIgnoreCase) ? "yes" : "",
                    m.Library?.LibraryName, m.IgnoreChaptersBefore > 0 ? m.IgnoreChaptersBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                    m.ReleaseStatus.ToString(), m.Year?.ToString(), ch.Downloaded.ToString(), ch.Latest
                ]);
        }
        byte[] bytes = Encoding.UTF8.GetBytes(SeriesListCsv.Write(rows));
        return TypedResults.File(bytes, "text/csv", $"tranga-series-{DateTime.Now:yyyy-MM-dd}.csv");
    }

    private static int SeriesCount(List<SeriesListCsv.Row> rows) =>
        rows.Where(r => r.Problem is null)
            .Select(r => r.Name is not null ? $"n:{SeriesListImport.Normalise(r.Name)}|{r.Library?.ToLowerInvariant()}" : $"u:{r.Url}")
            .Distinct().Count();

    /// <summary>
    /// Reads a list without adding anything, to show what would be imported
    /// </summary>
    /// <response code="200"></response>
    /// <response code="400">Empty, or too many rows</response>
    [HttpPost("Import/Preview")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType<SeriesListPreviewDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    public Results<Ok<SeriesListPreviewDto>, BadRequest<string>> Preview([FromBody] SeriesListImportRequest request)
    {
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse(request.Csv ?? "");
        if (rows.Count == 0)
            return TypedResults.BadRequest("The file has no rows Tranga can read.");
        if (rows.Count > MaxRows)
            return TypedResults.BadRequest($"That's {rows.Count} rows; Tranga imports up to {MaxRows} at a time. Split the file.");
        return TypedResults.Ok(new SeriesListPreviewDto(
            rows.Select(r => new SeriesListPreviewRowDto(r.Line, r.Name, r.Url, r.Website, r.Library, r.StartFrom, r.Download, r.Preferred, r.Problem)).ToList(),
            SeriesCount(rows)));
    }

    /// <summary>
    /// Starts importing a list in the background (one at a time). Follow it with GET Import.
    /// </summary>
    /// <response code="202">Started</response>
    /// <response code="400">Empty, too many rows, or the library doesn't exist</response>
    /// <response code="409">An import is already running</response>
    [HttpPost("Import")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType<SeriesListImportStatusDto>(Status202Accepted, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    [ProducesResponseType<string>(Status409Conflict, "text/plain")]
    public async Task<Results<Accepted<SeriesListImportStatusDto>, BadRequest<string>, Conflict<string>>> Import([FromBody] SeriesListImportRequest request)
    {
        List<SeriesListCsv.Row> rows = SeriesListCsv.Parse(request.Csv ?? "");
        if (rows.Count == 0)
            return TypedResults.BadRequest("The file has no rows Tranga can read.");
        if (rows.Count > MaxRows)
            return TypedResults.BadRequest($"That's {rows.Count} rows; Tranga imports up to {MaxRows} at a time. Split the file.");
        if (request.LibraryId is { } lib && !await context.FileLibraries.AnyAsync(l => l.Key == lib, HttpContext.RequestAborted))
            return TypedResults.BadRequest("That library doesn't exist.");
        if (SeriesListImport.Start(rows, request.LibraryId) is not { } job)
            return TypedResults.Conflict("An import is already running. Wait for it to finish or stop it first.");
        return TypedResults.Accepted((string?)null, StatusOf(job));
    }

    /// <summary>
    /// Progress and results of the current import, or the last one
    /// </summary>
    /// <response code="200"></response>
    /// <response code="204">No import since Tranga started</response>
    [HttpGet("Import")]
    [ProducesResponseType<SeriesListImportStatusDto>(Status200OK, "application/json")]
    [ProducesResponseType(Status204NoContent)]
    public Results<Ok<SeriesListImportStatusDto>, NoContent> Status() =>
        SeriesListImport.Current is { } job ? TypedResults.Ok(StatusOf(job)) : TypedResults.NoContent();

    /// <summary>
    /// Stops the running import after the series it's on (series already added stay)
    /// </summary>
    /// <response code="200">Stopping</response>
    /// <response code="404">No import is running</response>
    [HttpPost("Import/Stop")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Ok, NotFound<string>> Stop() =>
        SeriesListImport.Stop() ? TypedResults.Ok() : TypedResults.NotFound("No import is running.");

    private static SeriesListImportStatusDto StatusOf(SeriesListImport.Job job)
    {
        List<SeriesListRowResultDto> results = job.Snapshot(out string? current)
            .Select(r => new SeriesListRowResultDto(r.Line, r.Input, r.Status, r.Message, r.MangaId, r.MangaName)).ToList();
        return new SeriesListImportStatusDto(job.Running, job.Cancelled, job.StartedUtc, job.FinishedUtc, job.Total, results.Count,
            current, results);
    }
}
