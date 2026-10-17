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
/// Importing series folders already in a library: lists folders Tranga doesn't know, and links one to a series so its
/// chapter files count as downloaded.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class LibraryImportController(MangaContext context) : ControllerBase
{
    /// <summary>A folder in a library that no series in Tranga uses</summary>
    /// <param name="FolderName">Folder name (the series name, usually)</param>
    /// <param name="ChapterFiles">Chapter files in it (.cbz etc.)</param>
    /// <param name="SizeBytes">Size of those files</param>
    /// <param name="FirstChapter">Lowest chapter number found in the file names</param>
    /// <param name="LastChapter">Highest chapter number found in the file names</param>
    public record ImportFolderDto(string FolderName, int ChapterFiles, long SizeBytes, string? FirstChapter, string? LastChapter);

    /// <param name="LibraryName">Library name</param>
    /// <param name="BasePath">Library folder</param>
    /// <param name="FolderFound">False if the library folder can't be read</param>
    /// <param name="Folders">Folders not used by any series, A–Z</param>
    public record ImportScanDto(string LibraryName, string BasePath, bool FolderFound, List<ImportFolderDto> Folders);

    /// <summary>Links a folder to a series</summary>
    /// <param name="FolderName">Folder in the library</param>
    /// <param name="MangaId">Manga.Key (e.g. a search result)</param>
    public record ImportAdoptRequest(string FolderName, string MangaId);

    /// <summary>
    /// Lists the folders in a library that no series in Tranga uses
    /// </summary>
    /// <param name="LibraryId">FileLibrary.Key</param>
    /// <response code="200"></response>
    /// <response code="404">Library not found</response>
    [HttpGet("{LibraryId}")]
    [ProducesResponseType<ImportScanDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<ImportScanDto>, NotFound<string>>> Scan(string LibraryId)
    {
        if (await context.FileLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Key == LibraryId, HttpContext.RequestAborted) is not { } library)
            return TypedResults.NotFound(nameof(LibraryId));
        if (!Directory.Exists(library.BasePath))
            return TypedResults.Ok(new ImportScanDto(library.LibraryName, library.BasePath, false, []));

        HashSet<string> used = (await context.Mangas
                .Where(m => m.LibraryId == LibraryId)
                .Select(m => m.DirectoryName)
                .ToListAsync(HttpContext.RequestAborted))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ImportFolderDto> folders = [];
        foreach (string dir in Directory.EnumerateDirectories(library.BasePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(dir);
            if (used.Contains(name) || name.StartsWith('.'))
                continue;
            List<string> files = Directory.EnumerateFiles(dir).Where(LibraryImport.IsChapterFile).ToList();
            if (files.Count == 0)
                continue;
            List<string> numbers = files.Select(f => LibraryImport.NumberFromFileName(Path.GetFileName(f), name))
                .OfType<string>().OrderBy(Chapter.NumberValue).ToList();
            folders.Add(new ImportFolderDto(name, files.Count, files.Sum(f => new FileInfo(f).Length),
                numbers.FirstOrDefault(), numbers.LastOrDefault()));
        }
        return TypedResults.Ok(new ImportScanDto(library.LibraryName, library.BasePath, true, folders));
    }

    /// <summary>
    /// Links a folder to a series: the series saves into that folder, and its chapter files there count as downloaded
    /// once its chapters are read from a website (switch on downloading from one next).
    /// </summary>
    /// <param name="LibraryId">FileLibrary.Key</param>
    /// <param name="request">Folder and series</param>
    /// <response code="200">Linked</response>
    /// <response code="400">Folder missing or already used, or the series already has downloaded chapters elsewhere</response>
    /// <response code="404">Library or series not found</response>
    /// <response code="500">Database error</response>
    [HttpPost("{LibraryId}/Adopt")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok, BadRequest<string>, NotFound<string>, InternalServerError<string>>> Adopt(string LibraryId, [FromBody] ImportAdoptRequest request)
    {
        if (await context.FileLibraries.FirstOrDefaultAsync(l => l.Key == LibraryId, HttpContext.RequestAborted) is not { } library)
            return TypedResults.NotFound(nameof(LibraryId));
        if (await context.Mangas
                .Include(m => m.Library)
                .Include(m => m.Chapters)
                .FirstOrDefaultAsync(m => m.Key == request.MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(request.MangaId));

        string folderName = Path.GetFileName(request.FolderName ?? "");
        if (folderName.Length == 0 || folderName != request.FolderName || !Directory.Exists(Path.Join(library.BasePath, folderName)))
            return TypedResults.BadRequest($"There's no folder called {request.FolderName} in {library.LibraryName}.");
        if (await context.Mangas.AnyAsync(m => m.Key != manga.Key && m.LibraryId == LibraryId && m.DirectoryName == folderName, HttpContext.RequestAborted))
            return TypedResults.BadRequest($"Another series already uses the folder {folderName}.");
        if (manga.Chapters.Any(c => c.Downloaded) && (manga.LibraryId != LibraryId || manga.DirectoryName != folderName))
            return TypedResults.BadRequest($"{manga.Name} already has downloaded chapters in another folder.");

        manga.Library = library;
        manga.SetDirectoryName(folderName);
        LibraryImport.MarkPending(manga.Key, folderName);
        LibraryImport.MatchExisting(manga, manga.Chapters);

        if (await context.Sync(HttpContext.RequestAborted, GetType(), "Import folder") is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage ?? "Database error");
        return TypedResults.Ok();
    }
}
