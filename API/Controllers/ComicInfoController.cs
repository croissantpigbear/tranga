using System.IO.Compression;
using API.ComicInfo;
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
/// ComicInfo.xml metadata inside chapter archives (read by Kavita, Komga and most comic readers)
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class ComicInfoController(MangaContext mangaContext, ExtraMetadataContext extraContext) : ControllerBase
{
    /// <summary>ComicInfo.xml state</summary>
    /// <param name="Enabled">False if CREATE_COMICINFO_XML=false (then nothing is written)</param>
    /// <param name="Running">A rewrite of all chapters is in progress</param>
    /// <param name="Total">Chapters in the current/last full rewrite</param>
    /// <param name="Done">Rewritten so far</param>
    /// <param name="Failed">Couldn't be rewritten (e.g. file missing or damaged)</param>
    /// <param name="FinishedUtc">When the last full rewrite finished</param>
    public record StatusDto(bool Enabled, bool Running, int Total, int Done, int Failed, DateTime? FinishedUtc);

    /// <summary>Preview of a chapter's ComicInfo.xml</summary>
    /// <param name="ChapterId">Chapter.Key</param>
    /// <param name="Generated">What Tranga would write now</param>
    /// <param name="InFile">What's currently in the downloaded file (null if not downloaded or none)</param>
    public record PreviewDto(string ChapterId, string Generated, string? InFile);

    /// <summary>
    /// Whether ComicInfo.xml is written, and progress of a rewrite of all chapters
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Status")]
    [ProducesResponseType<StatusDto>(Status200OK, "application/json")]
    public Ok<StatusDto> GetStatus() =>
        TypedResults.Ok(new StatusDto(Constants.CreateComicInfoXml, RewriteComicInfoWorker.IsQueued(null),
            RewriteComicInfoWorker.Progress.Total, RewriteComicInfoWorker.Progress.Done,
            RewriteComicInfoWorker.Progress.Failed, RewriteComicInfoWorker.Progress.FinishedUtc));

    /// <summary>
    /// The ComicInfo.xml Tranga would write for a chapter, and the one in its file
    /// </summary>
    /// <param name="ChapterId">Chapter.Key</param>
    /// <response code="200"></response>
    /// <response code="404">Chapter not found</response>
    [HttpGet("Chapter/{ChapterId}")]
    [ProducesResponseType<PreviewDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<PreviewDto>, NotFound<string>>> Preview(string ChapterId)
    {
        CancellationToken ct = HttpContext.RequestAborted;
        if (await mangaContext.Chapters.AsNoTracking()
                .Include(c => c.ParentManga).ThenInclude(m => m.Library)
                .Include(c => c.MangaConnectorIds)
                .FirstOrDefaultAsync(c => c.Key == ChapterId, ct) is not { } chapter)
            return TypedResults.NotFound(nameof(ChapterId));

        Manga manga = await mangaContext.MangaWithMetadata().AsNoTracking().FirstAsync(m => m.Key == chapter.ParentMangaId, ct);
        ExtraMetadataData? extra = await extraContext.Entries.AsNoTracking()
            .Where(e => e.MangaId == manga.Key).Select(e => e.Data)
            .FirstOrDefaultAsync(ct) is { } json ? ExtraMetadataStore.Parse(json) : null;
        string? source = chapter.MangaConnectorIds.FirstOrDefault(id => id.UseForDownload)?.MangaConnectorName
                         ?? chapter.MangaConnectorIds.FirstOrDefault()?.MangaConnectorName;

        int? pages = null;
        string? inFile = null;
        try
        {
            if (chapter.FullArchiveFilePath is { } path && System.IO.File.Exists(path))
            {
                using ZipArchive archive = ZipFile.OpenRead(path);
                pages = ComicInfoBuilder.CountImages(archive);
                if (archive.Entries.FirstOrDefault(e => e.FullName.Equals(ComicInfoBuilder.EntryName, StringComparison.OrdinalIgnoreCase))
                    is { } entry)
                {
                    using StreamReader reader = new(entry.Open());
                    inFile = await reader.ReadToEndAsync(ct);
                }
            }
        }
        catch (Exception)
        {
            // Library not loaded / damaged archive: just no file preview
        }

        return TypedResults.Ok(new PreviewDto(chapter.Key, ComicInfoBuilder.Build(chapter, manga, extra, pages, source), inFile));
    }

    /// <summary>
    /// Rewrites ComicInfo.xml in all downloaded chapters of a Manga (in the background)
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="202">Queued; body = number of downloaded chapters</response>
    /// <response code="404">Manga not found</response>
    /// <response code="412">ComicInfo.xml is turned off (CREATE_COMICINFO_XML=false)</response>
    [HttpPost("Rewrite/Manga/{MangaId}")]
    [ProducesResponseType<int>(Status202Accepted, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status412PreconditionFailed, "text/plain")]
    public async Task<Results<Accepted<int>, NotFound<string>, StatusCodeHttpResult>> RewriteManga(string MangaId)
    {
        if (!Constants.CreateComicInfoXml)
            return TypedResults.StatusCode(Status412PreconditionFailed);
        if (!await mangaContext.Mangas.AnyAsync(m => m.Key == MangaId, HttpContext.RequestAborted))
            return TypedResults.NotFound(nameof(MangaId));
        int count = await mangaContext.Chapters.CountAsync(c => c.ParentMangaId == MangaId && c.Downloaded, HttpContext.RequestAborted);
        if (count > 0 && !RewriteComicInfoWorker.IsQueued(MangaId))
            Tranga.AddWorker(new RewriteComicInfoWorker(MangaId));
        return TypedResults.Accepted((string?)null, count);
    }

    /// <summary>
    /// Rewrites ComicInfo.xml in every downloaded chapter (in the background; progress in GET Status)
    /// </summary>
    /// <response code="202">Queued; body = number of downloaded chapters</response>
    /// <response code="409">Already running</response>
    /// <response code="412">ComicInfo.xml is turned off (CREATE_COMICINFO_XML=false)</response>
    [HttpPost("Rewrite")]
    [ProducesResponseType<int>(Status202Accepted, "application/json")]
    [ProducesResponseType(Status409Conflict)]
    [ProducesResponseType<string>(Status412PreconditionFailed, "text/plain")]
    public async Task<Results<Accepted<int>, StatusCodeHttpResult>> RewriteAll()
    {
        if (!Constants.CreateComicInfoXml)
            return TypedResults.StatusCode(Status412PreconditionFailed);
        if (RewriteComicInfoWorker.IsQueued(null))
            return TypedResults.StatusCode(Status409Conflict);
        int count = await mangaContext.Chapters.CountAsync(c => c.Downloaded, HttpContext.RequestAborted);
        if (count > 0)
            Tranga.AddWorker(new RewriteComicInfoWorker());
        return TypedResults.Accepted((string?)null, count);
    }
}
