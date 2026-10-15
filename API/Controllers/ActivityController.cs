using System.Text;
using System.Text.RegularExpressions;
using API.Logging;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MangaDownloadWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// What Tranga is doing right now (readable worker list) and its recent log, for the website's Activity page
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public partial class ActivityController(MangaContext context) : ControllerBase
{
    /// <summary>Recent log lines</summary>
    /// <param name="Entries">Oldest first</param>
    /// <param name="LastId">Id of the newest line Tranga has (pass as afterId to get only new lines)</param>
    /// <param name="OldestUtc">Oldest line still kept in memory</param>
    /// <param name="Warnings24h">Warnings in the last 24 hours</param>
    /// <param name="Errors24h">Errors in the last 24 hours</param>
    public record LogsDto(List<MemoryLogAppender.LogEntry> Entries, long LastId, DateTime? OldestUtc, int Warnings24h, int Errors24h);

    /// <summary>A job Tranga is running, waiting to run, or runs on a schedule</summary>
    /// <param name="Key">Worker key (for /v2/Worker/{WorkerId}/Stop)</param>
    /// <param name="Type">Class name</param>
    /// <param name="Title">Readable name, e.g. "Downloading chapter"</param>
    /// <param name="State">Running, Waiting, Created, …</param>
    /// <param name="MangaId">Manga it works on, if any</param>
    /// <param name="MangaName">Its name</param>
    /// <param name="ChapterNumber">Chapter it works on, if any</param>
    /// <param name="Source">Website, if any</param>
    /// <param name="Progress">0–1, if known</param>
    /// <param name="ProgressDescription">e.g. "Downloading images"</param>
    /// <param name="Periodic">Runs on a schedule</param>
    /// <param name="LastRunUtc">Scheduled jobs: last run</param>
    /// <param name="NextRunUtc">Scheduled jobs: next run</param>
    /// <param name="IntervalMinutes">Scheduled jobs: minutes between runs</param>
    public record ActivityWorkerDto(string Key, string Type, string Title, string State, string? MangaId, string? MangaName,
        string? ChapterNumber, string? Source, float? Progress, string? ProgressDescription, bool Periodic,
        DateTime? LastRunUtc, DateTime? NextRunUtc, double? IntervalMinutes);

    /// <summary>
    /// Recent log lines (kept in memory: the last 5000 lines since the API started)
    /// </summary>
    /// <param name="afterId">Only lines after this id (for polling)</param>
    /// <param name="minLevel">DEBUG, INFO, WARN or ERROR</param>
    /// <param name="search">Text to look for (message, source or error)</param>
    /// <param name="limit">Max lines (newest), default 500, max 5000</param>
    /// <response code="200"></response>
    [HttpGet("Logs")]
    [ProducesResponseType<LogsDto>(Status200OK, "application/json")]
    public Ok<LogsDto> GetLogs([FromQuery] long afterId = 0, [FromQuery] string? minLevel = null, [FromQuery] string? search = null,
        [FromQuery] int limit = 500)
    {
        MemoryLogAppender log = MemoryLogAppender.Instance;
        (int warnings, int errors) = log.Problems(DateTime.UtcNow.AddHours(-24));
        return TypedResults.Ok(new LogsDto(log.Query(afterId, minLevel, search, Math.Clamp(limit, 1, MemoryLogAppender.Capacity)),
            log.LastId, log.OldestUtc, warnings, errors));
    }

    /// <summary>
    /// The recent log lines as a text file
    /// </summary>
    /// <param name="minLevel">DEBUG, INFO, WARN or ERROR</param>
    /// <response code="200"></response>
    [HttpGet("Logs/Download")]
    [ProducesResponseType<string>(Status200OK, "text/plain")]
    public FileContentHttpResult DownloadLogs([FromQuery] string? minLevel = null)
    {
        StringBuilder sb = new();
        foreach (MemoryLogAppender.LogEntry e in MemoryLogAppender.Instance.Query(0, minLevel, null, MemoryLogAppender.Capacity))
        {
            sb.Append($"{e.TimeUtc:yyyy-MM-dd HH:mm:ss.fff}Z {e.Level,-5} {e.Logger} - {e.Message}\n");
            if (e.Exception is not null)
                sb.Append(e.Exception).Append('\n');
        }
        return TypedResults.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain",
            $"tranga-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.txt");
    }

    /// <summary>
    /// Jobs that are running or waiting, and scheduled jobs, with readable names and the manga/chapter they work on
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Workers")]
    [ProducesResponseType<List<ActivityWorkerDto>>(Status200OK, "application/json")]
    public async Task<Ok<List<ActivityWorkerDto>>> GetWorkers()
    {
        CancellationToken ct = HttpContext.RequestAborted;
        BaseWorker[] workers = Tranga.GetKnownWorkers().Concat(Tranga.GetRunningWorkers()).DistinctBy(w => w.Key).ToArray();

        // Ids at the end of ToString() ("<key> <id>") → look up what they refer to, in bulk
        Dictionary<BaseWorker, string> ids = workers
            .Select(w => (w, id: TargetId(w)))
            .Where(x => x.id is not null)
            .ToDictionary(x => x.w, x => x.id!);
        List<string> idList = ids.Values.Distinct().ToList();

        var chapterTargets = await context.MangaConnectorToChapter.AsNoTracking()
            .Where(id => idList.Contains(id.Key))
            .Select(id => new { id.Key, id.MangaConnectorName, id.Obj.ChapterNumber, id.Obj.ParentMangaId, MangaName = id.Obj.ParentManga.Name })
            .ToDictionaryAsync(x => x.Key, ct);
        var mangaConnectorTargets = await context.MangaConnectorToManga.AsNoTracking()
            .Where(id => idList.Contains(id.Key))
            .Select(id => new { id.Key, id.MangaConnectorName, id.ObjId, MangaName = id.Obj.Name })
            .ToDictionaryAsync(x => x.Key, ct);
        var mangaTargets = await context.Mangas.AsNoTracking()
            .Where(m => idList.Contains(m.Key))
            .Select(m => new { m.Key, m.Name })
            .ToDictionaryAsync(x => x.Key, ct);

        List<ActivityWorkerDto> result = [];
        foreach (BaseWorker w in workers)
        {
            if (w.State is WorkerExecutionState.Completed or WorkerExecutionState.Cancelled or WorkerExecutionState.Failed
                && w is not IPeriodic)
                continue;
            string? mangaId = null, mangaName = null, chapter = null, source = null;
            if (ids.TryGetValue(w, out string? id))
            {
                if (chapterTargets.TryGetValue(id, out var c))
                    (mangaId, mangaName, chapter, source) = (c.ParentMangaId, c.MangaName, c.ChapterNumber, c.MangaConnectorName);
                else if (mangaConnectorTargets.TryGetValue(id, out var mc))
                    (mangaId, mangaName, source) = (mc.ObjId, mc.MangaName, mc.MangaConnectorName);
                else if (mangaTargets.TryGetValue(id, out var m))
                    (mangaId, mangaName) = (m.Key, m.Name);
            }
            IPeriodic? periodic = w as IPeriodic;
            bool ran = periodic is not null && periodic.LastExecution > DateTime.UnixEpoch;
            result.Add(new ActivityWorkerDto(w.Key, w.GetType().Name, Title(w), w.State.ToString(), mangaId, mangaName, chapter, source,
                w.Progress > 0 ? w.Progress : null, w.ProgressDescription, periodic is not null,
                ran ? periodic!.LastExecution : null,
                periodic is null ? null : ran ? periodic.NextExecution : DateTime.UtcNow,
                periodic?.Interval.TotalMinutes));
        }

        // Running first, then waiting, then scheduled (soonest first)
        return TypedResults.Ok(result
            .OrderBy(r => r.Periodic && r.State != nameof(WorkerExecutionState.Running) ? 1 : 0)
            .ThenBy(r => r.State == nameof(WorkerExecutionState.Running) ? 0 : 1)
            .ThenBy(r => r.NextRunUtc ?? DateTime.MaxValue)
            .ThenBy(r => r.MangaName)
            .ToList());
    }

    private static string? TargetId(BaseWorker w) => w switch
    {
        DownloadChapterFromMangaconnectorWorker d => d.ChapterIdId,
        RetrieveMangaChaptersFromMangaconnectorWorker r => r.MangaConnectorIdId,
        FetchExtraMetadataWorker f => f.MangaId,
        RewriteComicInfoWorker c => c.MangaId,
        _ => w.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts ? parts[^1] : null
    };

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["DownloadChapterFromMangaconnectorWorker"] = "Downloading chapter",
        ["RetrieveMangaChaptersFromMangaconnectorWorker"] = "Looking for new chapters",
        ["DownloadCoverFromMangaconnectorWorker"] = "Downloading cover",
        ["FetchExtraMetadataWorker"] = "Fetching series details (MangaUpdates)",
        ["RewriteComicInfoWorker"] = "Updating reader metadata in files",
        ["MoveFileOrFolderWorker"] = "Moving files",
        ["RefreshLibrariesWorker"] = "Asking Komga/Kavita to rescan",
        ["CheckForNewChaptersWorker"] = "Check all manga for new chapters",
        ["StartNewChapterDownloadsWorker"] = "Start downloads of new chapters",
        ["UpdateMetadataWorker"] = "Update metadata from linked sites",
        ["UpdateCoversWorker"] = "Update covers",
        ["UpdateChaptersDownloadedWorker"] = "Check which chapters are on disk",
        ["SendNotificationsWorker"] = "Send notifications",
        ["FillExtraMetadataWorker"] = "Fetch missing series details",
        ["SyncKavitaCoversWorker"] = "Send new covers to Kavita",
        ["BuildLuaTitleListWorker"] = "Reading the title list of an FMD2 site",
        ["RelinkRetiredWebsitesWorker"] = "Moving manga off removed websites",
        ["PushKavitaCoversWorker"] = "Sending covers to Kavita",
        ["CleanupMangaCoversWorker"] = "Clean up unused covers",
        ["CleanupMangaconnectorIdsWithoutConnector"] = "Clean up removed websites",
        ["RemoveOldNotificationsWorker"] = "Remove old notifications",
    };

    private static string Title(BaseWorker w)
    {
        string type = w.GetType().Name;
        if (Titles.TryGetValue(type, out string? title))
            return w is RewriteComicInfoWorker { MangaId: null } ? "Updating reader metadata in all files" : title;
        string words = CamelCase().Replace(type.Replace("Worker", ""), " $1").Trim();
        return words.Length > 0 ? char.ToUpper(words[0]) + words[1..].ToLowerInvariant() : type;
    }

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex CamelCase();
}
