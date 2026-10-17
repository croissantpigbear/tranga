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
/// Downloaded chapter files on disk: sizes, deleting a single chapter's file, and the chapter-check schedule.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class ChapterFilesController(MangaContext context) : ControllerBase
{
    /// <summary>A downloaded chapter archive on disk</summary>
    /// <param name="ChapterId">Chapter.Key</param>
    /// <param name="FileName">Name of the .cbz</param>
    /// <param name="SizeBytes">File size in bytes</param>
    /// <param name="ModifiedUtc">When the file was last written (≈ when it was downloaded)</param>
    public record ChapterFileDto(string ChapterId, string FileName, long SizeBytes, DateTime ModifiedUtc);

    /// <summary>Downloaded chapter files of a Manga</summary>
    /// <param name="TotalBytes">Sum of all files</param>
    /// <param name="Files">One entry per chapter whose file exists</param>
    public record MangaFilesDto(long TotalBytes, List<ChapterFileDto> Files);

    /// <summary>When Tranga next checks all Manga for new chapters</summary>
    /// <param name="NextChapterCheckUtc">Next periodic check (UTC)</param>
    /// <param name="IntervalHours">Hours between checks</param>
    public record ScheduleDto(DateTime NextChapterCheckUtc, double IntervalHours);

    private static ChapterFileDto? FileOf(Chapter chapter)
    {
        if (chapter.FullArchiveFilePath is not { } path || !System.IO.File.Exists(path))
            return null;
        FileInfo info = new(path);
        return new ChapterFileDto(chapter.Key, info.Name, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Returns the downloaded chapter files (size, date) of a Manga
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="200"></response>
    /// <response code="404">Manga not found</response>
    [HttpGet("Manga/{MangaId}")]
    [ProducesResponseType<MangaFilesDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<MangaFilesDto>, NotFound<string>>> GetMangaFiles(string MangaId)
    {
        if (!await context.Mangas.AnyAsync(m => m.Key == MangaId, HttpContext.RequestAborted))
            return TypedResults.NotFound(nameof(MangaId));

        List<Chapter> chapters = await context.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .Where(c => c.ParentMangaId == MangaId && c.Downloaded)
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted);

        List<ChapterFileDto> files = chapters.Select(FileOf).OfType<ChapterFileDto>().ToList();
        return TypedResults.Ok(new MangaFilesDto(files.Sum(f => f.SizeBytes), files));
    }

    /// <summary>
    /// Deletes a downloaded chapter's file from disk and stops it from being downloaded again
    /// (the chapter stays listed and can be re-enabled for download).
    /// </summary>
    /// <param name="ChapterId">Chapter.Key</param>
    /// <response code="200">File deleted (or there was none) and chapter no longer marked for download</response>
    /// <response code="404">Chapter not found</response>
    /// <response code="500">File couldn't be deleted or database error</response>
    [HttpDelete("{ChapterId}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok, NotFound<string>, InternalServerError<string>>> DeleteChapterFile(string ChapterId)
    {
        if (await context.Chapters
                .Include(c => c.ParentManga)
                .ThenInclude(m => m.Library)
                .Include(c => c.MangaConnectorIds)
                .FirstOrDefaultAsync(c => c.Key == ChapterId, HttpContext.RequestAborted) is not { } chapter)
            return TypedResults.NotFound(nameof(ChapterId));

        // Stop it being downloaded again first, so a running download pass doesn't re-fetch it
        foreach (MangaConnectorId<Chapter> id in chapter.MangaConnectorIds)
            id.UseForDownload = false;

        if (chapter.FullArchiveFilePath is { } path && System.IO.File.Exists(path))
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception e)
            {
                await context.Sync(HttpContext.RequestAborted, GetType(), "Unmark chapter for download");
                return TypedResults.InternalServerError($"Could not delete {path}: {e.Message}");
            }
        }
        chapter.Downloaded = false;

        if (await context.Sync(HttpContext.RequestAborted, GetType(), "Delete chapter file") is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage ?? "Database error");
        return TypedResults.Ok();
    }

    /// <summary>
    /// Downloads a chapter again, from the website given (e.g. when the file has missing or broken pages): deletes the
    /// current file and downloads the chapter from that website only, starting now.
    /// </summary>
    /// <param name="ChapterId">Chapter.Key</param>
    /// <param name="MangaConnectorName">Website to download it from (one the chapter is listed on)</param>
    /// <response code="202">Old file deleted (if there was one) and the download started</response>
    /// <response code="404">Chapter not found, or not listed on that website</response>
    /// <response code="500">File couldn't be deleted or database error</response>
    [HttpPost("{ChapterId}/Redownload/{MangaConnectorName}")]
    [ProducesResponseType(Status202Accepted)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Accepted, NotFound<string>, InternalServerError<string>>> Redownload(string ChapterId, string MangaConnectorName)
    {
        if (await context.Chapters
                .Include(c => c.ParentManga)
                .ThenInclude(m => m.Library)
                .Include(c => c.MangaConnectorIds)
                .FirstOrDefaultAsync(c => c.Key == ChapterId, HttpContext.RequestAborted) is not { } chapter)
            return TypedResults.NotFound(nameof(ChapterId));
        if (chapter.MangaConnectorIds.FirstOrDefault(id =>
                string.Equals(id.MangaConnectorName, MangaConnectorName, StringComparison.OrdinalIgnoreCase)) is not { } from
            || !Tranga.TryGetMangaConnector(from.MangaConnectorName, out _))
            return TypedResults.NotFound(nameof(MangaConnectorName));
        if (Tranga.GetRunningWorkers().OfType<Workers.MangaDownloadWorkers.DownloadChapterFromMangaconnectorWorker>()
            .Any(w => chapter.MangaConnectorIds.Any(id => id.Key == w.ChapterIdId) && w.State < WorkerExecutionState.Completed))
            return TypedResults.InternalServerError("This chapter is downloading right now; try again when it's finished.");

        // Only the chosen website, so the download scheduler doesn't pick another one
        foreach (MangaConnectorId<Chapter> id in chapter.MangaConnectorIds)
        {
            id.UseForDownload = id == from;
            Downloads.DownloadFailures.Retry(id.Key);
        }

        if (chapter.FullArchiveFilePath is { } path && System.IO.File.Exists(path))
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception e)
            {
                return TypedResults.InternalServerError($"Could not delete {path}: {e.Message}");
            }
        }
        chapter.Downloaded = false;
        chapter.FileName = null; // named again with the current naming settings

        if (await context.Sync(HttpContext.RequestAborted, GetType(), "Redownload chapter") is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage ?? "Database error");

        Tranga.AddWorker(new Workers.MangaDownloadWorkers.DownloadChapterFromMangaconnectorWorker(from));
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>Disk use of all downloaded chapters</summary>
    /// <param name="TotalBytes">Sum of all chapter files</param>
    /// <param name="FileCount">Number of chapter files found on disk</param>
    /// <param name="MangaCount">Number of Manga with at least one file</param>
    public record LibrarySummaryDto(long TotalBytes, int FileCount, int MangaCount);

    /// <summary>
    /// Returns how much disk space all downloaded chapters use
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Summary")]
    [ProducesResponseType<LibrarySummaryDto>(Status200OK, "application/json")]
    public async Task<Ok<LibrarySummaryDto>> GetSummary()
    {
        List<Chapter> chapters = await context.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .Where(c => c.Downloaded)
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted);
        List<ChapterFileDto> files = chapters.Select(FileOf).OfType<ChapterFileDto>().ToList();
        HashSet<string> withFile = files.Select(f => f.ChapterId).ToHashSet();
        int mangaCount = chapters.Where(c => withFile.Contains(c.Key)).Select(c => c.ParentMangaId).Distinct().Count();
        return TypedResults.Ok(new LibrarySummaryDto(files.Sum(f => f.SizeBytes), files.Count, mangaCount));
    }

    /// <summary>
    /// Returns when Tranga next checks for new chapters
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Schedule")]
    [ProducesResponseType<ScheduleDto>(Status200OK, "application/json")]
    public Ok<ScheduleDto> GetSchedule()
    {
        IPeriodic check = Tranga.CheckForNewChaptersWorker;
        return TypedResults.Ok(new ScheduleDto(check.NextExecution, check.Interval.TotalHours));
    }
}
