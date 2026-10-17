using API.Workers;
using API.Workers.MangaDownloadWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// How many chapters download (overall and per website) and how many jobs run at the same time, and what's running now
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class DownloadLimitsController : ControllerBase
{
    /// <summary>Download limits</summary>
    /// <param name="MaxConcurrentDownloads">Chapters downloading at the same time, all websites together</param>
    /// <param name="MaxConcurrentDownloadsPerConnector">Chapters downloading at the same time from one website</param>
    /// <param name="RequestsPerMinute">Web requests per minute, all websites together (REQUESTS_PER_MINUTE)</param>
    /// <param name="ProcessorCount">CPU cores the API sees (the default overall limit is 75% of these)</param>
    /// <param name="Downloading">Chapters downloading right now, per website</param>
    /// <param name="MaxConcurrentWorkers">Jobs (downloads, chapter checks, metadata updates, …) running at the same time</param>
    /// <param name="RunningWorkers">Jobs running right now</param>
    /// <param name="TotalMemoryBytes">Memory available to the API (the container limit, if one is set)</param>
    /// <param name="Architecture">CPU architecture the API runs on (X64, Arm64, …)</param>
    /// <param name="PagesAtOnce">Pages of one chapter downloaded at the same time</param>
    public record DownloadLimitsDto(int MaxConcurrentDownloads, int MaxConcurrentDownloadsPerConnector, int RequestsPerMinute,
        int ProcessorCount, Dictionary<string, int> Downloading, int MaxConcurrentWorkers, int RunningWorkers,
        long TotalMemoryBytes, string Architecture, int PagesAtOnce);

    /// <summary>New limits (leave a value out to keep it)</summary>
    /// <param name="MaxConcurrentDownloads">1–20</param>
    /// <param name="MaxConcurrentDownloadsPerConnector">1–10</param>
    /// <param name="MaxConcurrentWorkers">2–64, and at least MaxConcurrentDownloads + 2 (so chapter checks can run during downloads)</param>
    /// <param name="PagesAtOnce">1–8 pages of one chapter at the same time</param>
    public record DownloadLimitsRequest(int? MaxConcurrentDownloads, int? MaxConcurrentDownloadsPerConnector, int? MaxConcurrentWorkers = null,
        int? PagesAtOnce = null);

    /// <summary>
    /// Current download limits and what's downloading now
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<DownloadLimitsDto>(Status200OK, "application/json")]
    public Ok<DownloadLimitsDto> Get() => TypedResults.Ok(Current());

    /// <summary>
    /// Changes the download limits (applies from the next download round, within a minute; running downloads aren't stopped)
    /// </summary>
    /// <response code="200">New limits</response>
    /// <response code="400">Value out of range</response>
    [HttpPatch]
    [ProducesResponseType<DownloadLimitsDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    public Results<Ok<DownloadLimitsDto>, BadRequest<string>> Set([FromBody] DownloadLimitsRequest request)
    {
        if (request.MaxConcurrentDownloads is < 1 or > 20)
            return TypedResults.BadRequest($"{nameof(request.MaxConcurrentDownloads)} must be 1–20");
        if (request.MaxConcurrentDownloadsPerConnector is < 1 or > 10)
            return TypedResults.BadRequest($"{nameof(request.MaxConcurrentDownloadsPerConnector)} must be 1–10");
        if (request.PagesAtOnce is < 1 or > 8)
            return TypedResults.BadRequest($"{nameof(request.PagesAtOnce)} must be 1–8");
        if (request.MaxConcurrentWorkers is < 2 or > 64)
            return TypedResults.BadRequest($"{nameof(request.MaxConcurrentWorkers)} must be 2–64");
        int downloads = request.MaxConcurrentDownloads ?? Tranga.Settings.MaxConcurrentDownloads;
        int workers = request.MaxConcurrentWorkers ?? Tranga.Settings.MaxConcurrentWorkers;
        if (workers < downloads + 2)
            return TypedResults.BadRequest($"Jobs at once ({workers}) must be at least downloads at once ({downloads}) + 2");
        if (request.MaxConcurrentWorkers is { } w)
            Tranga.Settings.SetMaxConcurrentWorkers(w);
        if (request.MaxConcurrentDownloads is { } t)
            Tranga.Settings.SetMaxConcurrentDownloads(t);
        if (request.MaxConcurrentDownloadsPerConnector is { } p)
            Tranga.Settings.SetMaxConcurrentDownloadsPerConnector(p);
        if (request.PagesAtOnce is { } pages)
            Tranga.Settings.SetPagesAtOnce(pages);
        return TypedResults.Ok(Current());
    }

    private static DownloadLimitsDto Current() => new(
        Tranga.Settings.MaxConcurrentDownloads,
        Tranga.Settings.MaxConcurrentDownloadsPerConnector,
        Constants.RequestsPerMinute,
        Environment.ProcessorCount,
        Tranga.GetRunningWorkers()
            .OfType<DownloadChapterFromMangaconnectorWorker>()
            .Where(w => w.State is >= WorkerExecutionState.Created and < WorkerExecutionState.Completed)
            .GroupBy(w => w.MangaConnectorName)
            .ToDictionary(g => g.Key, g => g.Count()),
        Tranga.Settings.MaxConcurrentWorkers,
        Tranga.GetRunningWorkers().Length,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        Tranga.Settings.PagesAtOnce);
}
