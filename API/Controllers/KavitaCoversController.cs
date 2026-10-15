using API.KavitaCovers;
using API.Schema.LibraryContext;
using API.Schema.LibraryContext.LibraryConnectors;
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
/// Setting Kavita's series covers to the covers Tranga downloaded
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class KavitaCoversController(LibraryContext libraryContext, MangaContext mangaContext) : ControllerBase
{
    /// <summary>Kavita cover state</summary>
    /// <param name="KavitaConnected">A Kavita server is set up under Libraries</param>
    /// <param name="Enabled">Covers are sent automatically</param>
    /// <param name="LockCover">Covers are locked in Kavita (its scans won't replace them)</param>
    /// <param name="Sent">Manga whose cover was sent</param>
    /// <param name="Waiting">Downloaded manga whose cover hasn't been sent (yet)</param>
    /// <param name="Running">Covers are being sent right now</param>
    /// <param name="LastRunUtc">Last time covers were checked</param>
    /// <param name="Recent">Recent outcomes, newest first</param>
    public record KavitaCoversStatusDto(bool KavitaConnected, bool Enabled, bool LockCover, int Sent, int Waiting, bool Running,
        DateTime? LastRunUtc, List<KavitaCoverSync.KavitaCoverResult> Recent);

    /// <summary>Change settings (leave a value out to keep it)</summary>
    public record KavitaCoversSettingsRequest(bool? Enabled, bool? LockCover);

    /// <summary>
    /// Whether covers are sent to Kavita, and how that's going
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<KavitaCoversStatusDto>(Status200OK, "application/json")]
    public async Task<Ok<KavitaCoversStatusDto>> GetStatus()
    {
        CancellationToken ct = HttpContext.RequestAborted;
        KavitaCoverSync.State state = KavitaCoverSync.Load();
        bool connected = (await libraryContext.LibraryConnectors.ToListAsync(ct)).OfType<Kavita>().Any();
        List<(string Key, string? CoverFileNameInCache)> downloaded = (await mangaContext.Mangas.AsNoTracking()
                .Where(m => m.CoverFileNameInCache != null && mangaContext.Chapters.Any(c => c.ParentMangaId == m.Key && c.Downloaded))
                .Select(m => new { m.Key, m.CoverFileNameInCache })
                .ToListAsync(ct))
            .Select(m => (m.Key, m.CoverFileNameInCache)).ToList();
        int sent = downloaded.Count(m => state.Pushed.TryGetValue(m.Key, out var p) && p.CoverFile == m.CoverFileNameInCache);
        bool running = Tranga.GetKnownWorkers().Concat(Tranga.GetRunningWorkers())
            .Any(w => w is PushKavitaCoversWorker or SyncKavitaCoversWorker && w.State == WorkerExecutionState.Running);
        List<KavitaCoverSync.KavitaCoverResult> recent;
        lock (KavitaCoverSync.RecentResults)
            recent = KavitaCoverSync.RecentResults.Take(20).ToList();
        return TypedResults.Ok(new KavitaCoversStatusDto(connected, state.Enabled, state.LockCover, sent, downloaded.Count - sent, running,
            KavitaCoverSync.LastRunUtc, recent));
    }

    /// <summary>
    /// Turn automatic covers on/off, and whether they're locked in Kavita
    /// </summary>
    /// <response code="200"></response>
    [HttpPatch]
    [ProducesResponseType(Status200OK)]
    public Ok SetSettings([FromBody] KavitaCoversSettingsRequest request)
    {
        KavitaCoverSync.State state = KavitaCoverSync.Load();
        if (request.Enabled is { } enabled)
            state.Enabled = enabled;
        if (request.LockCover is { } lockCover)
            state.LockCover = lockCover;
        KavitaCoverSync.Save(state);
        return TypedResults.Ok();
    }

    /// <summary>
    /// Sends covers to Kavita now: those not sent yet, or all of them with force=true
    /// </summary>
    /// <param name="force">Also re-send covers that were already sent</param>
    /// <response code="202"></response>
    /// <response code="412">No Kavita server set up</response>
    [HttpPost("Push")]
    [ProducesResponseType(Status202Accepted)]
    [ProducesResponseType(Status412PreconditionFailed)]
    public async Task<Results<Accepted, StatusCodeHttpResult>> PushAll([FromQuery] bool force = false)
    {
        if (!(await libraryContext.LibraryConnectors.ToListAsync(HttpContext.RequestAborted)).OfType<Kavita>().Any())
            return TypedResults.StatusCode(Status412PreconditionFailed);
        Tranga.AddWorker(new PushKavitaCoversWorker(null, force));
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>
    /// Sends one manga's cover to Kavita now (even if it was sent before)
    /// </summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <response code="202"></response>
    /// <response code="404">Manga not found</response>
    /// <response code="412">No Kavita server set up</response>
    [HttpPost("Push/{MangaId}")]
    [ProducesResponseType(Status202Accepted)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status412PreconditionFailed)]
    public async Task<Results<Accepted, NotFound<string>, StatusCodeHttpResult>> PushOne(string MangaId)
    {
        if (!await mangaContext.Mangas.AnyAsync(m => m.Key == MangaId, HttpContext.RequestAborted))
            return TypedResults.NotFound(nameof(MangaId));
        if (!(await libraryContext.LibraryConnectors.ToListAsync(HttpContext.RequestAborted)).OfType<Kavita>().Any())
            return TypedResults.StatusCode(Status412PreconditionFailed);
        Tranga.AddWorker(new PushKavitaCoversWorker(MangaId, true));
        return TypedResults.Accepted((string?)null);
    }
}
