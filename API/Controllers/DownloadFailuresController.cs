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
/// Failed chapter downloads: Tranga retries them after 5 min, 30 min, 2 h and 12 h, then gives up (and uses another
/// website for the chapter if it knows one). Retry, skip or switch website here.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class DownloadFailuresController(MangaContext context) : ControllerBase
{
    /// <summary>A chapter whose download failed</summary>
    /// <param name="ChapterIdId">The chapter on one website (MangaConnectorId&lt;Chapter&gt;.Key); use it for the actions</param>
    /// <param name="ChapterId">Chapter.Key</param>
    /// <param name="MangaId">Manga.Key</param>
    /// <param name="MangaName">Series name</param>
    /// <param name="ChapterNumber">Chapter number</param>
    /// <param name="Website">Website it failed on</param>
    /// <param name="Attempts">Failed attempts so far</param>
    /// <param name="LastError">Why the last attempt failed</param>
    /// <param name="LastAttemptUtc">When</param>
    /// <param name="NextRetryUtc">Next automatic try (null once Tranga gave up)</param>
    /// <param name="GaveUp">Tranga stopped retrying</param>
    /// <param name="SwitchedTo">Website Tranga now downloads this chapter from instead</param>
    /// <param name="OtherWebsites">Other websites Tranga knows this chapter on (for "Use website")</param>
    public record DownloadFailureDto(string ChapterIdId, string ChapterId, string MangaId, string MangaName, string ChapterNumber,
        string Website, int Attempts, string LastError, DateTime LastAttemptUtc, DateTime? NextRetryUtc, bool GaveUp,
        string? SwitchedTo, List<string> OtherWebsites);

    /// <summary>Failed downloads and settings</summary>
    /// <param name="MaxAttempts">Attempts before Tranga gives up on a chapter</param>
    /// <param name="NotifyWhenGivingUp">Send a notification when Tranga gives up on a chapter</param>
    /// <param name="Failures">Failed chapters, given-up ones first</param>
    public record DownloadFailuresDto(int MaxAttempts, bool NotifyWhenGivingUp, List<DownloadFailureDto> Failures);

    /// <summary>Settings for failed downloads</summary>
    /// <param name="NotifyWhenGivingUp">Send a notification when Tranga gives up on a chapter</param>
    public record DownloadFailuresSettingsRequest(bool NotifyWhenGivingUp);

    /// <summary>
    /// Returns the chapters whose download failed
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<DownloadFailuresDto>(Status200OK, "application/json")]
    public async Task<Ok<DownloadFailuresDto>> GetFailures() => TypedResults.Ok(await Current(HttpContext.RequestAborted));

    /// <summary>
    /// Changes whether Tranga sends a notification when it gives up on a chapter
    /// </summary>
    /// <response code="200"></response>
    [HttpPatch]
    [ProducesResponseType<DownloadFailuresDto>(Status200OK, "application/json")]
    public async Task<Ok<DownloadFailuresDto>> SetSettings([FromBody] DownloadFailuresSettingsRequest request)
    {
        DownloadFailures.NotifyWhenGivingUp = request.NotifyWhenGivingUp;
        return TypedResults.Ok(await Current(HttpContext.RequestAborted));
    }

    /// <summary>
    /// Tries a failed chapter again at the next download round (within a minute), with a full set of attempts
    /// </summary>
    /// <param name="ChapterIdId">From the failures list</param>
    /// <response code="200"></response>
    /// <response code="404">Not in the failures list</response>
    [HttpPost("{ChapterIdId}/Retry")]
    [ProducesResponseType<DownloadFailuresDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<DownloadFailuresDto>, NotFound<string>>> Retry(string ChapterIdId)
    {
        if (!DownloadFailures.Retry(ChapterIdId))
            return TypedResults.NotFound(nameof(ChapterIdId));
        return TypedResults.Ok(await Current(HttpContext.RequestAborted));
    }

    /// <summary>
    /// Tries every failed chapter again at the next download round
    /// </summary>
    /// <response code="200"></response>
    [HttpPost("Retry")]
    [ProducesResponseType<DownloadFailuresDto>(Status200OK, "application/json")]
    public async Task<Ok<DownloadFailuresDto>> RetryAll()
    {
        DownloadFailures.RetryAll();
        return TypedResults.Ok(await Current(HttpContext.RequestAborted));
    }

    /// <summary>
    /// Stops downloading a failed chapter (it is no longer marked for download from that website)
    /// </summary>
    /// <param name="ChapterIdId">From the failures list</param>
    /// <response code="200"></response>
    /// <response code="404">Not found</response>
    /// <response code="500">Database error</response>
    [HttpPost("{ChapterIdId}/Skip")]
    [ProducesResponseType<DownloadFailuresDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok<DownloadFailuresDto>, NotFound<string>, InternalServerError<string>>> Skip(string ChapterIdId)
    {
        if (await context.MangaConnectorToChapter.FirstOrDefaultAsync(id => id.Key == ChapterIdId, HttpContext.RequestAborted) is not { } chId)
            return TypedResults.NotFound(nameof(ChapterIdId));
        chId.UseForDownload = false;
        if (await context.Sync(HttpContext.RequestAborted, GetType(), "Skip failed chapter") is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage ?? "");
        DownloadFailures.Remove(ChapterIdId);
        return TypedResults.Ok(await Current(HttpContext.RequestAborted));
    }

    /// <summary>
    /// Downloads a failed chapter from another website Tranga knows it on, instead of the failing one
    /// </summary>
    /// <param name="ChapterIdId">From the failures list</param>
    /// <param name="MangaConnectorName">One of the entry's OtherWebsites</param>
    /// <response code="200"></response>
    /// <response code="404">Chapter not found, or not known on that website</response>
    /// <response code="500">Database error</response>
    [HttpPost("{ChapterIdId}/UseWebsite/{MangaConnectorName}")]
    [ProducesResponseType<DownloadFailuresDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok<DownloadFailuresDto>, NotFound<string>, InternalServerError<string>>> UseWebsite(string ChapterIdId,
        string MangaConnectorName)
    {
        CancellationToken ct = HttpContext.RequestAborted;
        if (await context.MangaConnectorToChapter.FirstOrDefaultAsync(id => id.Key == ChapterIdId, ct) is not { } failed)
            return TypedResults.NotFound(nameof(ChapterIdId));
        if (await context.MangaConnectorToChapter.FirstOrDefaultAsync(id => id.ObjId == failed.ObjId
                && id.MangaConnectorName == MangaConnectorName && id.Key != failed.Key, ct) is not { } other)
            return TypedResults.NotFound(nameof(MangaConnectorName));
        failed.UseForDownload = false;
        other.UseForDownload = true;
        if (await context.Sync(ct, GetType(), "Download chapter from another website") is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage ?? "");
        DownloadFailures.Remove(ChapterIdId);
        DownloadFailures.Retry(other.Key);
        return TypedResults.Ok(await Current(ct));
    }

    private async Task<DownloadFailuresDto> Current(CancellationToken ct)
    {
        List<DownloadFailures.Entry> entries = DownloadFailures.List();
        List<string> chapterIds = entries.Select(e => e.ChapterId).Distinct().ToList();
        var links = await context.MangaConnectorToChapter
            .Where(id => chapterIds.Contains(id.ObjId))
            .Select(id => new { id.Key, id.ObjId, id.MangaConnectorName })
            .ToListAsync(ct);
        List<DownloadFailureDto> failures = entries.Select(e => new DownloadFailureDto(e.ChapterIdId, e.ChapterId, e.MangaId,
                e.MangaName, e.ChapterNumber, e.Website, e.Attempts, e.LastError, e.LastAttemptUtc, e.NextRetryUtc, e.GaveUp,
                e.SwitchedTo,
                links.Where(l => l.ObjId == e.ChapterId && l.Key != e.ChapterIdId
                                 && Tranga.TryGetMangaConnector(l.MangaConnectorName, out var c) && c.Enabled)
                    .Select(l => l.MangaConnectorName).Distinct().Order().ToList()))
            .ToList();
        return new DownloadFailuresDto(DownloadFailures.MaxAttempts, DownloadFailures.NotifyWhenGivingUp, failures);
    }
}
