using API.Naming;
using API.Schema.MangaContext;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Series folder naming, and previews of folder/chapter names for a naming scheme.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class NamingController(MangaContext context) : ControllerBase
{
    /// <summary>Request for previews</summary>
    /// <param name="ChapterScheme">Chapter naming scheme to preview (default: the current setting)</param>
    /// <param name="SeriesScheme">Series folder scheme to preview (default: the current setting)</param>
    /// <param name="MangaId">Manga to use for the examples (default: one with chapters)</param>
    public record PreviewRequest(string? ChapterScheme, string? SeriesScheme, string? MangaId);

    /// <summary>Preview of names</summary>
    /// <param name="MangaName">Manga used for the examples</param>
    /// <param name="SeriesFolder">Folder name for that Manga</param>
    /// <param name="ChapterFiles">Up to 3 chapter file names for that Manga</param>
    public record PreviewResponse(string? MangaName, string? SeriesFolder, List<string> ChapterFiles);

    /// <summary>One Manga in a rename plan</summary>
    /// <param name="Status">"same" (nothing to do), "rename", or "conflict" (target exists or is used twice)</param>
    public record RenamePlanItem(string MangaId, string Name, string CurrentFolder, string NewFolder, string Status);

    /// <summary>Which Manga to rename (default: all in the plan with status "rename")</summary>
    public record RenameRequest(List<string>? MangaIds);

    /// <summary>Result of renaming</summary>
    public record RenameResult(int Renamed, List<RenameSkipped> Skipped);

    /// <summary>A Manga that was not renamed, and why</summary>
    public record RenameSkipped(string MangaId, string Name, string Reason);

    /// <summary>
    /// Returns the series folder naming scheme (placeholders: %M name, %E English title, %Y year, %A first author; ?X(...) only if %X has a value)
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("SeriesFolderScheme")]
    [ProducesResponseType<string>(Status200OK, "text/plain")]
    public Ok<string> GetSeriesFolderScheme() => TypedResults.Ok(SeriesNamingSettings.SeriesFolderScheme);

    /// <summary>
    /// Sets the series folder naming scheme, used for newly added Manga (existing folders are only renamed via SeriesFolders/Rename)
    /// </summary>
    /// <response code="200"></response>
    [HttpPatch("SeriesFolderScheme")]
    [ProducesResponseType(Status200OK)]
    public Ok SetSeriesFolderScheme([FromBody] string scheme)
    {
        SeriesNamingSettings.SeriesFolderScheme = scheme;
        return TypedResults.Ok();
    }

    /// <summary>
    /// Previews series folder and chapter file names for (possibly unsaved) schemes, using a real Manga
    /// </summary>
    /// <response code="200"></response>
    [HttpPost("Preview")]
    [ProducesResponseType<PreviewResponse>(Status200OK, "application/json")]
    public async Task<Ok<PreviewResponse>> Preview([FromBody] PreviewRequest request)
    {
        string chapterScheme = string.IsNullOrWhiteSpace(request.ChapterScheme) ? Tranga.Settings.ChapterNamingScheme : request.ChapterScheme;
        string seriesScheme = string.IsNullOrWhiteSpace(request.SeriesScheme) ? SeriesNamingSettings.SeriesFolderScheme : request.SeriesScheme;

        IQueryable<Manga> query = context.Mangas
            .Include(m => m.Authors)
            .Include(m => m.AltTitles)
            .Include(m => m.Chapters)
            .AsNoTracking();
        Manga? manga;
        if (request.MangaId is not null)
            manga = await query.FirstOrDefaultAsync(m => m.Key == request.MangaId, HttpContext.RequestAborted);
        else
        {
            // Prefer a Manga that has chapters, so there are chapter examples
            manga = await query
                .Where(m => context.Chapters.Any(c => c.ParentMangaId == m.Key))
                .OrderBy(m => m.Name)
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
            manga ??= await query.OrderBy(m => m.Name).FirstOrDefaultAsync(HttpContext.RequestAborted);
        }
        if (manga is null)
            return TypedResults.Ok(new PreviewResponse(null, null, []));

        List<string> chapters = manga.Chapters
            .OrderBy(c => c.VolumeNumber ?? 0)
            .ThenBy(c => float.TryParse(c.ChapterNumber, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n) ? n : float.MaxValue)
            .Take(3)
            .Select(c => NamingFormatter.ChapterFileName(chapterScheme, c, manga))
            .ToList();
        return TypedResults.Ok(new PreviewResponse(manga.Name, NamingFormatter.SeriesFolderName(seriesScheme, manga), chapters));
    }

    private async Task<List<(Manga manga, RenamePlanItem item)>> BuildPlan(string scheme, CancellationToken ct)
    {
        List<Manga> mangas = await context.Mangas
            .Include(m => m.Library)
            .Include(m => m.Authors)
            .Include(m => m.AltTitles)
            .Where(m => m.LibraryId != null)
            .OrderBy(m => m.Name)
            .ToListAsync(ct);

        List<(Manga, RenamePlanItem)> plan = [];
        HashSet<string> claimed = new(StringComparer.InvariantCultureIgnoreCase);
        foreach (Manga m in mangas)
        {
            string newFolder = NamingFormatter.SeriesFolderName(scheme, m);
            string status;
            if (newFolder == m.DirectoryName)
                status = "same";
            else
            {
                string target = Path.Join(m.Library!.BasePath, newFolder);
                bool targetExists = Directory.Exists(target) && !string.Equals(newFolder, m.DirectoryName, StringComparison.InvariantCultureIgnoreCase);
                bool usedByOther = mangas.Any(o => o != m && o.LibraryId == m.LibraryId
                                                         && string.Equals(o.DirectoryName, newFolder, StringComparison.InvariantCultureIgnoreCase));
                status = targetExists || usedByOther || !claimed.Add($"{m.LibraryId}/{newFolder}") ? "conflict" : "rename";
            }
            plan.Add((m, new RenamePlanItem(m.Key, m.Name, m.DirectoryName, newFolder, status)));
        }
        return plan;
    }

    /// <summary>
    /// Shows which series folders would be renamed with <paramref name="scheme"/> (default: the current setting)
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("SeriesFolders/Plan")]
    [ProducesResponseType<List<RenamePlanItem>>(Status200OK, "application/json")]
    public async Task<Ok<List<RenamePlanItem>>> GetRenamePlan([FromQuery] string? scheme = null)
    {
        string s = string.IsNullOrWhiteSpace(scheme) ? SeriesNamingSettings.SeriesFolderScheme : scheme;
        return TypedResults.Ok((await BuildPlan(s, HttpContext.RequestAborted)).Select(p => p.item).ToList());
    }

    /// <summary>
    /// Renames existing series folders to match the current series folder scheme.
    /// Folders whose new name already exists are skipped. Library servers (Komga/Kavita) may see renamed folders as new series.
    /// </summary>
    /// <response code="200"></response>
    /// <response code="500">Database error after renaming</response>
    [HttpPost("SeriesFolders/Rename")]
    [ProducesResponseType<RenameResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok<RenameResult>, InternalServerError<string>>> Rename([FromBody] RenameRequest request)
    {
        List<(Manga manga, RenamePlanItem item)> plan = await BuildPlan(SeriesNamingSettings.SeriesFolderScheme, HttpContext.RequestAborted);
        HashSet<string>? only = request.MangaIds is { Count: > 0 } ids ? ids.ToHashSet() : null;
        int renamed = 0;
        List<RenameSkipped> skipped = [];

        foreach ((Manga manga, RenamePlanItem item) in plan)
        {
            if (only is not null && !only.Contains(manga.Key))
                continue;
            if (item.Status == "same")
                continue;
            if (item.Status == "conflict")
            {
                skipped.Add(new RenameSkipped(manga.Key, manga.Name, $"\"{item.NewFolder}\" already exists or is used by another series"));
                continue;
            }
            string from = Path.Join(manga.Library!.BasePath, manga.DirectoryName);
            string to = Path.Join(manga.Library!.BasePath, item.NewFolder);
            try
            {
                if (Directory.Exists(from))
                {
                    if (string.Equals(from, to, StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Only the letter case changes: go via a temporary name (case-insensitive file systems)
                        string temp = from + ".tranga-rename";
                        Directory.Move(from, temp);
                        Directory.Move(temp, to);
                    }
                    else
                        Directory.Move(from, to);
                }
                manga.SetDirectoryName(item.NewFolder);
                renamed++;
            }
            catch (Exception e)
            {
                skipped.Add(new RenameSkipped(manga.Key, manga.Name, e.Message));
            }
        }

        if (await context.Sync(HttpContext.RequestAborted, GetType(), "Rename series folders") is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage ?? "Database error");
        return TypedResults.Ok(new RenameResult(renamed, skipped));
    }
}
