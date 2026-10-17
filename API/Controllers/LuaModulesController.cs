using API.LuaModules;
using API.MangaConnectors;
using API.Workers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Adding websites by uploading Free Manga Downloader 2 (FMD2) Lua modules
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class LuaModulesController : ControllerBase
{
    /// <summary>A website added by a module</summary>
    /// <param name="Name">Name in Tranga (used for its manga and chapters)</param>
    /// <param name="ModuleName">Name the module gives it</param>
    /// <param name="RootUrl">Site address</param>
    /// <param name="Category">FMD2 category (language / type)</param>
    /// <param name="Language">Language code Tranga uses for it</param>
    /// <param name="Enabled">Used for search and downloads</param>
    /// <param name="CanListTitles">The module can list all titles (needed for search)</param>
    /// <param name="TitleCount">Titles in the stored title list</param>
    /// <param name="TitleListBuiltUtc">When the title list was read</param>
    /// <param name="TitleListStatus">"building", or why the last attempt failed</param>
    public record LuaSiteDto(string Name, string ModuleName, string RootUrl, string Category, string Language, bool Enabled,
        bool CanListTitles, int TitleCount, DateTime? TitleListBuiltUtc, string? TitleListStatus);

    /// <summary>An uploaded module file</summary>
    /// <param name="FileName">File name</param>
    /// <param name="Error">Why it couldn't be loaded</param>
    /// <param name="Warnings">Things Tranga can't do for it</param>
    /// <param name="Sites">Websites it adds</param>
    public record LuaModuleFileDto(string FileName, string? Error, List<string> Warnings, List<LuaSiteDto> Sites);

    /// <summary>All uploaded FMD2 files</summary>
    /// <param name="Folder">Where they're stored inside the API container</param>
    /// <param name="Modules">Module files</param>
    /// <param name="Templates">Template files (lua/templates)</param>
    /// <param name="Helpers">Helper files (lua/utils)</param>
    public record LuaModulesOverviewDto(string Folder, List<LuaModuleFileDto> Modules, List<string> Templates, List<string> Helpers);

    /// <summary>An uploaded file</summary>
    /// <param name="FileName">e.g. Toonily.lua</param>
    /// <param name="Content">The file's text</param>
    /// <param name="Kind">module, template or helper; empty to detect</param>
    public record LuaUploadRequest(string FileName, string Content, string? Kind);

    /// <summary>Result of an upload</summary>
    /// <param name="FileName">Stored file name</param>
    /// <param name="Kind">module, template or helper</param>
    /// <param name="Error">Why it failed (to save, or to load)</param>
    /// <param name="Sites">Websites it added (modules)</param>
    /// <param name="Warnings">Things Tranga can't do for it</param>
    public record LuaUploadResultDto(string FileName, string Kind, string? Error, List<string> Sites, List<string> Warnings);

    /// <summary>A manga page to try a site with</summary>
    /// <param name="Url">Link to a manga on the site</param>
    public record LuaTestRequest(string Url);

    /// <summary>A chapter found while testing</summary>
    public record LuaTestChapterDto(string Name, string Number, int? Volume, string Url);

    /// <summary>What a test found</summary>
    /// <param name="Ok">Info and chapters were read</param>
    /// <param name="Error">What went wrong</param>
    /// <param name="Title">Manga title</param>
    /// <param name="CoverUrl">Cover</param>
    /// <param name="Status">Status (FMD2 code: 0 completed, 1 ongoing, 2 hiatus, 3 cancelled)</param>
    /// <param name="Authors">Authors and artists</param>
    /// <param name="Genres">Genres</param>
    /// <param name="Summary">Description</param>
    /// <param name="ChapterCount">Chapters found</param>
    /// <param name="Chapters">First and last few chapters, as Tranga numbers them</param>
    /// <param name="PageCount">Pages found in the newest chapter</param>
    /// <param name="SamplePages">First page image URLs</param>
    /// <param name="PagesError">Why pages couldn't be read</param>
    /// <param name="Messages">What the module logged</param>
    /// <param name="Requests">Requests the module made</param>
    public record LuaTestResultDto(bool Ok, string? Error, string Title, string CoverUrl, string Status, string Authors, string Genres,
        string Summary, int ChapterCount, List<LuaTestChapterDto> Chapters, int PageCount, List<string> SamplePages, string? PagesError,
        List<string> Messages, List<string> Requests);

    private static LuaSiteDto SiteDto(LuaMangaConnector c)
    {
        (int count, DateTime? built) = LuaTitleList.Info(c.Name);
        string? status = BuildLuaTitleListWorker.IsQueued(c.Name) ? "building"
            : LuaTitleList.Status.TryGetValue(c.Name, out string? s) ? s : null;
        return new LuaSiteDto(c.Name, c.ModuleName, c.RootUrl, c.Category, c.SupportedLanguages.FirstOrDefault() ?? "", c.Enabled,
            c.HasTitleList, count, built, status);
    }

    private static string KindName(LuaFileKind kind) => kind switch
    {
        LuaFileKind.Module => "module",
        LuaFileKind.Template => "template",
        _ => "helper"
    };

    private static LuaFileKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "module" or "modules" => LuaFileKind.Module,
        "template" or "templates" => LuaFileKind.Template,
        "helper" or "helpers" or "util" or "utils" => LuaFileKind.Helper,
        _ => null
    };

    /// <summary>
    /// Uploaded modules, templates and helpers, and the websites they add
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<LuaModulesOverviewDto>(Status200OK, "application/json")]
    public Ok<LuaModulesOverviewDto> Get()
    {
        List<LuaModuleFileDto> modules = LuaModuleStore.Files.Select(f => new LuaModuleFileDto(f.FileName, f.Error, f.Warnings,
            LuaModuleStore.Connectors.Where(c => c.FileName == f.FileName).Select(SiteDto).ToList())).ToList();
        return TypedResults.Ok(new LuaModulesOverviewDto(LuaModuleStore.Root, modules,
            LuaModuleStore.List(LuaFileKind.Template), LuaModuleStore.List(LuaFileKind.Helper)));
    }

    /// <summary>
    /// Uploads a module, template or helper (as text). Modules are loaded straight away, and their title lists are read in the background.
    /// </summary>
    /// <response code="200">Saved (check Error: a module may be saved but fail to load)</response>
    /// <response code="400">Not saved (see message)</response>
    [HttpPost("Upload")]
    [ProducesResponseType<LuaUploadResultDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    public Results<Ok<LuaUploadResultDto>, BadRequest<string>> Upload([FromBody] LuaUploadRequest request)
    {
        string content = request.Content ?? "";
        LuaFileKind kind = ParseKind(request.Kind) ?? LuaModuleStore.Detect(content);
        string fileName = Path.GetFileName(request.FileName ?? "");
        if (!fileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            return TypedResults.BadRequest("Only .lua files can be uploaded.");
        if (LuaModuleStore.Save(fileName, content, kind) is { } error)
            return TypedResults.BadRequest(error);

        if (kind != LuaFileKind.Module)
            return TypedResults.Ok(new LuaUploadResultDto(fileName, KindName(kind), null, [], LuaModuleStore.Warnings(content)));

        LuaModuleStore.ModuleFile? file = LuaModuleStore.Files.FirstOrDefault(f => f.FileName == fileName);
        foreach (LuaMangaConnector c in LuaModuleStore.Connectors.Where(c => c.FileName == fileName && c.HasTitleList))
            if (LuaTitleList.Info(c.Name).BuiltUtc is null && !BuildLuaTitleListWorker.IsQueued(c.Name))
                Tranga.AddWorker(new BuildLuaTitleListWorker(c.Name));
        if (file?.SiteNames.Count > 0)
            RelinkRetiredWebsitesWorker.Queue(); // manga on a removed built-in website may belong to this site
        return TypedResults.Ok(new LuaUploadResultDto(fileName, KindName(kind), file?.Error, file?.SiteNames ?? [], file?.Warnings ?? []));
    }

    /// <summary>
    /// Deletes an uploaded file. Deleting a module removes its websites; their manga links are removed at the next restart.
    /// </summary>
    /// <param name="Kind">module, template or helper</param>
    /// <param name="FileName">File name</param>
    /// <response code="200"></response>
    /// <response code="404">No such file</response>
    [HttpDelete("{Kind}/{FileName}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Ok, NotFound<string>> Delete(string Kind, string FileName)
    {
        if (ParseKind(Kind) is not { } kind || !LuaModuleStore.Delete(FileName, kind))
            return TypedResults.NotFound(nameof(FileName));
        return TypedResults.Ok();
    }

    /// <summary>
    /// Turns a website added by a module on or off (off: not searched, not downloaded from)
    /// </summary>
    /// <param name="SiteName">Website name in Tranga</param>
    /// <param name="Enabled">On or off</param>
    /// <response code="200"></response>
    /// <response code="404">No such website</response>
    [HttpPatch("Sites/{SiteName}/Enabled/{Enabled}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Ok, NotFound<string>> SetEnabled(string SiteName, bool Enabled)
    {
        if (!LuaModuleStore.SetEnabled(SiteName, Enabled))
            return TypedResults.NotFound(nameof(SiteName));
        if (Enabled)
            RelinkRetiredWebsitesWorker.Queue();
        return TypedResults.Ok();
    }

    /// <summary>
    /// Reads (again) the full title list of a website, used for searching it (in the background; can take minutes)
    /// </summary>
    /// <param name="SiteName">Website name in Tranga</param>
    /// <response code="202"></response>
    /// <response code="404">No such website</response>
    /// <response code="412">The module can't list titles</response>
    [HttpPost("Sites/{SiteName}/TitleList")]
    [ProducesResponseType(Status202Accepted)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status412PreconditionFailed)]
    public Results<Accepted, NotFound<string>, StatusCodeHttpResult> BuildTitleList(string SiteName)
    {
        if (LuaModuleStore.Connectors.FirstOrDefault(c => c.Name.Equals(SiteName, StringComparison.OrdinalIgnoreCase)) is not { } connector)
            return TypedResults.NotFound(nameof(SiteName));
        if (!connector.HasTitleList)
            return TypedResults.StatusCode(Status412PreconditionFailed);
        if (!BuildLuaTitleListWorker.IsQueued(connector.Name))
        {
            LuaTitleList.Status[connector.Name] = "building";
            Tranga.AddWorker(new BuildLuaTitleListWorker(connector.Name));
        }
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>
    /// Tries a website on one manga page: reads its info and chapters, and the pages of its newest chapter. Nothing is saved.
    /// </summary>
    /// <param name="SiteName">Website name in Tranga</param>
    /// <param name="request">Manga link</param>
    /// <response code="200">Result (see Ok / Error)</response>
    /// <response code="404">No such website</response>
    [HttpPost("Sites/{SiteName}/Test")]
    [ProducesResponseType<LuaTestResultDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Ok<LuaTestResultDto>, NotFound<string>> Test(string SiteName, [FromBody] LuaTestRequest request)
    {
        if (LuaModuleStore.Connectors.FirstOrDefault(c => c.Name.Equals(SiteName, StringComparison.OrdinalIgnoreCase)) is not { } connector)
            return TypedResults.NotFound(nameof(SiteName));
        CancellationToken ct = HttpContext.RequestAborted;

        LuaMangaInfo info;
        try
        {
            info = connector.GetInfo(request.Url ?? "", ct);
        }
        catch (Exception e)
        {
            LuaHost? host = (e as LuaModuleException)?.Host;
            return TypedResults.Ok(new LuaTestResultDto(false, e.Message, "", "", "", "", "", "", 0, [], 0, [], null,
                host?.Messages ?? [], host?.RequestLog ?? []));
        }

        List<(string Name, string Url, string Number, int? Volume)> chapters = [];
        for (int i = 0; i < info.ChapterLinks.Count; i++)
        {
            string name = i < info.ChapterNames.Count ? info.ChapterNames[i] : "";
            (string number, int? volume, _) = LuaMangaConnector.ParseChapterName(name, i + 1);
            chapters.Add((name, info.ChapterLinks[i], number, volume));
        }
        List<LuaTestChapterDto> sample = (chapters.Count <= 10 ? chapters : chapters.Take(5).Concat(chapters.TakeLast(5)))
            .Select(c => new LuaTestChapterDto(c.Name, c.Number, c.Volume, c.Url)).ToList();

        int pageCount = 0;
        List<string> samplePages = [];
        string? pagesError = null;
        List<string> messages = [.. info.Messages];
        List<string> requests = [.. info.Requests];
        if (chapters.Count > 0)
        {
            try
            {
                string source = LuaModuleStore.ReadModule(connector.FileName) ?? throw new LuaModuleException("Module file missing");
                using LuaModuleRunner runner = LuaModuleRunner.Load(source, connector.FileName, TimeSpan.FromMinutes(2), ct);
                JObject result = JObject.Parse(runner.Call("__fmd_pages", connector.ModuleId, connector.IdOf(chapters[^1].Url)));
                List<string> pages = (result["pages"] as JArray ?? new JArray()).Select(p => p.ToString()).Where(p => p.Length > 0).ToList();
                pageCount = pages.Count;
                samplePages = pages.Take(3).ToList();
                if (pageCount == 0)
                    pagesError = "No pages found in the newest chapter.";
                messages.AddRange(runner.Host.Messages);
                requests.AddRange(runner.Host.RequestLog);
            }
            catch (Exception e)
            {
                pagesError = e.Message;
                if (e is LuaModuleException { Host: { } host })
                {
                    messages.AddRange(host.Messages);
                    requests.AddRange(host.RequestLog);
                }
            }
        }

        bool ok = info.Title.Length > 0 && chapters.Count > 0;
        string? blocked = BlockedStatus(requests);
        string? error = ok ? null
            : blocked is not null ? BlockedMessage(blocked)
            : info.Title.Length == 0 ? "The module didn't find a title on that page. Is it a manga page on this site?"
            : "The module found the manga but no chapters.";
        if (ok && pagesError is not null && blocked is not null)
            pagesError = BlockedMessage(blocked);
        // A title read from a "blocked" page isn't the manga's
        string title = blocked is not null && !ok ? "" : info.Title;
        return TypedResults.Ok(new LuaTestResultDto(ok, error, title, info.CoverLink, info.Status, string.Join(", ",
                new[] { info.Authors, info.Artists }.Where(s => s.Length > 0)), info.Genres, info.Summary, chapters.Count, sample,
            pageCount, samplePages, pagesError, messages, requests));
    }

    /// <summary>First "refused" status (403, 429, 503) among the module's requests</summary>
    private static string? BlockedStatus(IEnumerable<string> requests) => requests
        .Select(r => r[(r.LastIndexOf('→') + 1)..].Trim())
        .FirstOrDefault(code => code is "403" or "429" or "503");

    private static string BlockedMessage(string status) => status == "429"
        ? "The website said Tranga is sending too many requests (HTTP 429). Wait a few minutes and try again."
        : string.IsNullOrEmpty(Tranga.Settings.FlareSolverrUrl)
            ? $"The website refused Tranga's requests (HTTP {status}). This is usually Cloudflare's bot protection. Set up FlareSolverr (Settings → Network → FlareSolverr) and test again: Tranga then retries blocked pages through it."
            : $"The website refused Tranga's requests (HTTP {status}), even with FlareSolverr set up. Check that FlareSolverr is running, or the site may block automated downloads altogether.";

    /// <summary>An uploaded Lua file compared with FMD2</summary>
    /// <param name="Kind">module, template or helper</param>
    /// <param name="FileName">File name</param>
    /// <param name="Status">upToDate, updateAvailable, notInFmd2 (not in the FMD2 repository, e.g. renamed or your own)</param>
    /// <param name="RemoteChangedUtc">When FMD2 last changed the file</param>
    /// <param name="RemoteChangeMessage">Description of that change</param>
    /// <param name="CanUndo">An older version is kept</param>
    /// <param name="UpdatedUtc">When Tranga last updated it</param>
    public record LuaFileUpdateDto(string Kind, string FileName, string Status, DateTime? RemoteChangedUtc, string? RemoteChangeMessage,
        bool CanUndo, DateTime? UpdatedUtc);

    /// <summary>Update check results and settings</summary>
    /// <param name="Repository">GitHub repository the files come from</param>
    /// <param name="Branch">Its branch</param>
    /// <param name="AutoUpdate">Install updates automatically after the daily check</param>
    /// <param name="LastCheckUtc">Last check</param>
    /// <param name="LastError">Why the last check failed</param>
    /// <param name="Files">Every uploaded file</param>
    public record LuaUpdatesDto(string Repository, string Branch, bool AutoUpdate, DateTime? LastCheckUtc, string? LastError,
        List<LuaFileUpdateDto> Files);

    /// <summary>Update settings (leave a value out to keep it)</summary>
    public record LuaUpdateSettingsRequest(bool? AutoUpdate, string? Repository, string? Branch);

    /// <summary>Result of updating a file</summary>
    public record LuaUpdateResultDto(string FileName, bool Ok, string Message);

    private static LuaUpdatesDto UpdatesDto(LuaUpdateChecker.State state) => new(state.Repository, state.Branch, state.AutoUpdate,
        state.LastCheckUtc, state.LastError,
        state.Files.Select(f => new LuaFileUpdateDto(f.Kind, f.FileName, f.Status, f.RemoteChangedUtc, f.RemoteChangeMessage,
            f.HasBackup, f.UpdatedUtc)).ToList());

    /// <summary>
    /// Last update check of the uploaded Lua files against FMD2 (checked daily)
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Updates")]
    [ProducesResponseType<LuaUpdatesDto>(Status200OK, "application/json")]
    public Ok<LuaUpdatesDto> GetUpdates() => TypedResults.Ok(UpdatesDto(LuaUpdateChecker.Load()));

    /// <summary>
    /// Checks the uploaded Lua files against FMD2 on GitHub now
    /// </summary>
    /// <response code="200">Results (LastError is set if GitHub couldn't be reached)</response>
    [HttpPost("Updates/Check")]
    [ProducesResponseType<LuaUpdatesDto>(Status200OK, "application/json")]
    public async Task<Ok<LuaUpdatesDto>> CheckUpdates() =>
        TypedResults.Ok(UpdatesDto(await LuaUpdateChecker.Check(HttpContext.RequestAborted)));

    /// <summary>
    /// Changes the update settings
    /// </summary>
    /// <response code="200"></response>
    [HttpPatch("Updates")]
    [ProducesResponseType<LuaUpdatesDto>(Status200OK, "application/json")]
    public Ok<LuaUpdatesDto> SetUpdateSettings([FromBody] LuaUpdateSettingsRequest request)
    {
        LuaUpdateChecker.SetSettings(request.AutoUpdate, request.Repository, request.Branch);
        return TypedResults.Ok(UpdatesDto(LuaUpdateChecker.Load()));
    }

    /// <summary>
    /// Installs FMD2's current version of one file (the old one is kept for Undo, and put back if the new one doesn't load)
    /// </summary>
    /// <param name="Kind">module, template or helper</param>
    /// <param name="FileName">File name</param>
    /// <response code="200"></response>
    /// <response code="404">Unknown kind</response>
    [HttpPost("Updates/{Kind}/{FileName}")]
    [ProducesResponseType<LuaUpdateResultDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<LuaUpdateResultDto>, NotFound<string>>> UpdateFile(string Kind, string FileName)
    {
        if (ParseKind(Kind) is not { } kind)
            return TypedResults.NotFound(nameof(Kind));
        LuaUpdateChecker.UpdateResult r = await LuaUpdateChecker.Update(kind, FileName, HttpContext.RequestAborted);
        return TypedResults.Ok(new LuaUpdateResultDto(r.FileName, r.Ok, r.Message));
    }

    /// <summary>
    /// Installs every available update (templates and helpers first)
    /// </summary>
    /// <response code="200">One result per file</response>
    [HttpPost("Updates/All")]
    [ProducesResponseType<List<LuaUpdateResultDto>>(Status200OK, "application/json")]
    public async Task<Ok<List<LuaUpdateResultDto>>> UpdateAll() =>
        TypedResults.Ok((await LuaUpdateChecker.UpdateAll(HttpContext.RequestAborted))
            .Select(r => new LuaUpdateResultDto(r.FileName, r.Ok, r.Message)).ToList());

    /// <summary>
    /// Puts back the version from before the last update
    /// </summary>
    /// <param name="Kind">module, template or helper</param>
    /// <param name="FileName">File name</param>
    /// <response code="200"></response>
    /// <response code="404">Unknown kind</response>
    [HttpPost("Updates/{Kind}/{FileName}/Undo")]
    [ProducesResponseType<LuaUpdateResultDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Ok<LuaUpdateResultDto>, NotFound<string>> UndoUpdate(string Kind, string FileName)
    {
        if (ParseKind(Kind) is not { } kind)
            return TypedResults.NotFound(nameof(Kind));
        LuaUpdateChecker.UpdateResult r = LuaUpdateChecker.Undo(kind, FileName);
        return TypedResults.Ok(new LuaUpdateResultDto(r.FileName, r.Ok, r.Message));
    }

    /// <summary>
    /// A website's icon (fetched from the site once, then kept for 30 days)
    /// </summary>
    /// <param name="SiteName">Website name in Tranga</param>
    /// <response code="200">The image</response>
    /// <response code="404">No such website, or it has no icon Tranga can get</response>
    [HttpGet("Sites/{SiteName}/Icon")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<PhysicalFileHttpResult, NotFound<string>>> SiteIcon(string SiteName)
    {
        if (LuaModuleStore.Connectors.FirstOrDefault(c => c.Name.Equals(SiteName, StringComparison.OrdinalIgnoreCase)) is not { } site)
            return TypedResults.NotFound(nameof(SiteName));
        if (await LuaSiteIcons.Get(site, HttpContext.RequestAborted) is not { } icon)
            return TypedResults.NotFound("No icon");
        Response.Headers.CacheControl = "public, max-age=86400";
        return TypedResults.PhysicalFile(icon.Path, icon.ContentType);
    }
}
