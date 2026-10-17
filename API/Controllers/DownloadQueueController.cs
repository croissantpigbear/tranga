using API.Downloads;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MangaDownloadWorkers;
using API.Workers.PeriodicWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// The download queue: series with chapters waiting, in the order they download (first added, first downloaded)
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class DownloadQueueController(MangaContext context) : ControllerBase
{
    /// <summary>A series in the download queue</summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <param name="Name">Series name</param>
    /// <param name="Position">0 = downloads next</param>
    /// <param name="ChaptersLeft">Chapters not downloaded yet, including ones downloading now</param>
    /// <param name="Downloading">Chapters downloading right now</param>
    /// <param name="NextChapter">Lowest chapter number still to download</param>
    /// <param name="Websites">Websites the chapters come from</param>
    /// <param name="Failed">Chapters Tranga gave up on (see DownloadFailures)</param>
    /// <param name="Retrying">Chapters that failed and wait for their next try</param>
    /// <param name="Paused">This series' downloads are paused</param>
    public record DownloadQueueEntryDto(string MangaId, string Name, int Position, int ChaptersLeft, int Downloading,
        string? NextChapter, List<string> Websites, int Failed, int Retrying, bool Paused);

    /// <summary>Whether downloads are paused</summary>
    /// <param name="PausedAll">No new downloads start (running ones finish)</param>
    public record DownloadQueueStatusDto(bool PausedAll);

    /// <summary>
    /// Whether all downloads are paused
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Status")]
    [ProducesResponseType<DownloadQueueStatusDto>(Status200OK, "application/json")]
    public Ok<DownloadQueueStatusDto> GetStatus() => TypedResults.Ok(new DownloadQueueStatusDto(DownloadQueue.PausedAll));

    /// <summary>
    /// Pauses all downloads: no new chapters start; chapters downloading now finish
    /// </summary>
    /// <response code="200"></response>
    [HttpPost("Pause")]
    [ProducesResponseType<DownloadQueueStatusDto>(Status200OK, "application/json")]
    public Ok<DownloadQueueStatusDto> PauseAll()
    {
        DownloadQueue.PausedAll = true;
        return TypedResults.Ok(new DownloadQueueStatusDto(true));
    }

    /// <summary>
    /// Resumes all downloads (series paused one by one stay paused)
    /// </summary>
    /// <response code="200"></response>
    [HttpPost("Resume")]
    [ProducesResponseType<DownloadQueueStatusDto>(Status200OK, "application/json")]
    public Ok<DownloadQueueStatusDto> ResumeAll()
    {
        DownloadQueue.PausedAll = false;
        return TypedResults.Ok(new DownloadQueueStatusDto(false));
    }

    /// <summary>
    /// Pauses one series: its chapters don't start downloading until it is resumed (it keeps its place in the queue)
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="200">The queue</response>
    /// <response code="404">No such Manga</response>
    [HttpPost("{MangaId}/Pause")]
    [ProducesResponseType<List<DownloadQueueEntryDto>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Task<Results<Ok<List<DownloadQueueEntryDto>>, NotFound<string>>> Pause(string MangaId) => SetPaused(MangaId, true);

    /// <summary>
    /// Resumes one paused series
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="200">The queue</response>
    /// <response code="404">No such Manga</response>
    [HttpPost("{MangaId}/Resume")]
    [ProducesResponseType<List<DownloadQueueEntryDto>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Task<Results<Ok<List<DownloadQueueEntryDto>>, NotFound<string>>> Resume(string MangaId) => SetPaused(MangaId, false);

    private async Task<Results<Ok<List<DownloadQueueEntryDto>>, NotFound<string>>> SetPaused(string mangaId, bool paused)
    {
        if (!await context.Mangas.AnyAsync(m => m.Key == mangaId, HttpContext.RequestAborted))
            return TypedResults.NotFound(nameof(mangaId));
        DownloadQueue.SetPaused(mangaId, paused);
        return TypedResults.Ok(await Entries(HttpContext.RequestAborted));
    }

    /// <summary>
    /// Returns the series waiting to download, in download order
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<List<DownloadQueueEntryDto>>(Status200OK, "application/json")]
    public async Task<Ok<List<DownloadQueueEntryDto>>> GetQueue() => TypedResults.Ok(await Entries(HttpContext.RequestAborted));

    /// <summary>
    /// Moves a series to a position in the download queue (applies from the next download round, within a minute;
    /// chapters already downloading finish first)
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <param name="Position">0 = next; a number past the end = last</param>
    /// <response code="200">The queue after the move</response>
    /// <response code="404">The series isn't in the download queue</response>
    [HttpPost("{MangaId}/Move/{Position:int}")]
    [ProducesResponseType<List<DownloadQueueEntryDto>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<List<DownloadQueueEntryDto>>, NotFound<string>>> Move(string MangaId, int Position)
    {
        await Entries(HttpContext.RequestAborted); // make sure the queue includes series added since the last download round
        if (!DownloadQueue.Move(MangaId, Position))
            return TypedResults.NotFound(nameof(MangaId));
        return TypedResults.Ok(await Entries(HttpContext.RequestAborted));
    }

    private async Task<List<DownloadQueueEntryDto>> Entries(CancellationToken ct)
    {
        List<MangaConnectorId<Chapter>> missing = await StartNewChapterDownloadsWorker.GetMissingChapters(context, ct);
        missing.RemoveAll(ch => !Tranga.TryGetMangaConnector(ch.MangaConnectorName, out _));
        Dictionary<string, int> positions = DownloadQueue.Sync(missing.Select(ch => ch.Obj.ParentMangaId));

        HashSet<string> downloading = Tranga.GetRunningWorkers()
            .OfType<DownloadChapterFromMangaconnectorWorker>()
            .Where(w => w.State is >= WorkerExecutionState.Created and < WorkerExecutionState.Completed)
            .Select(w => w.ChapterIdId)
            .ToHashSet();

        List<string> mangaIds = positions.Keys.ToList();
        Dictionary<string, string> names = await context.Mangas
            .Where(m => mangaIds.Contains(m.Key))
            .ToDictionaryAsync(m => m.Key, m => m.Name, ct);

        Chapter.ChapterComparer comparer = new();
        return missing
            .GroupBy(ch => ch.Obj.ParentMangaId)
            .Select(g =>
            {
                string? next;
                try
                {
                    next = g.Select(ch => ch.Obj).Order(comparer).FirstOrDefault()?.ChapterNumber;
                }
                catch (ArgumentException)
                {
                    next = null;
                }
                return new DownloadQueueEntryDto(g.Key, names.GetValueOrDefault(g.Key, g.Key),
                    positions.GetValueOrDefault(g.Key, int.MaxValue), g.Select(ch => ch.ObjId).Distinct().Count(),
                    g.Count(ch => downloading.Contains(ch.Key)), next,
                    g.Select(ch => ch.MangaConnectorName).Distinct().Order().ToList(),
                    g.Count(ch => DownloadFailures.Get(ch.Key) is { GaveUp: true, SwitchedTo: null }),
                    g.Count(ch => DownloadFailures.Get(ch.Key) is { GaveUp: false }),
                    DownloadQueue.IsPaused(g.Key));
            })
            .OrderBy(e => e.Position)
            .ToList();
    }
}
