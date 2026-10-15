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
    public record DownloadQueueEntryDto(string MangaId, string Name, int Position, int ChaptersLeft, int Downloading,
        string? NextChapter, List<string> Websites);

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
                    g.Select(ch => ch.MangaConnectorName).Distinct().Order().ToList());
            })
            .OrderBy(e => e.Position)
            .ToList();
    }
}
