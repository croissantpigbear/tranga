using System.IO.Compression;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Backs up and restores Tranga's settings files (settings, naming, schedules, queue, login, series preferences) and the
/// uploaded FMD2 modules. Series, chapters, libraries and notification/library connections are in the database, which the
/// backup container saves every day.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class SettingsBackupController(IHostApplicationLifetime lifetime) : ControllerBase
{
    private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(SettingsBackupController));

    /// <summary>Files in the settings folder that belong in a backup</summary>
    private static readonly string[] Files =
    [
        "settings.json", "naming.json", "schedules.json", "download-queue.json", "download-failures.json",
        "kavita-covers.json", "series-preferences.json", "library-import.json", "releases.json", "auth.json",
        "lua/state.json", "lua/storage.json", "lua/updates.json"
    ];

    /// <summary>Folders whose files belong in a backup (FMD2 modules, templates, helpers)</summary>
    private static readonly string[] Folders = ["lua/modules", "lua/templates", "lua/utils"];

    private const string ManifestName = "tranga-settings-backup.json";
    private const long MaxBytes = 40 * 1024 * 1024;

    private record Manifest(int Version, DateTime CreatedUtc, List<string> Files);

    /// <summary>What a settings backup contains</summary>
    /// <param name="Files">Paths inside the settings folder</param>
    /// <param name="TotalBytes">Size of the files</param>
    /// <param name="CreatedUtc">When the backup was made (restore preview only)</param>
    public record SettingsBackupInfoDto(List<string> Files, long TotalBytes, DateTime? CreatedUtc);

    /// <summary>A settings backup to restore</summary>
    /// <param name="ContentBase64">The .zip made by Download, base64-encoded</param>
    /// <param name="DryRun">Only check it and say what it contains</param>
    public record SettingsRestoreRequest(string ContentBase64, bool DryRun = false);

    private static string Root => TrangaSettings.WorkingDirectory;

    private static IEnumerable<string> CurrentFiles()
    {
        foreach (string f in Files)
            if (System.IO.File.Exists(Path.Join(Root, f)))
                yield return f;
        foreach (string folder in Folders)
        {
            string dir = Path.Join(Root, folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (string path in Directory.GetFiles(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                yield return $"{folder}/{Path.GetFileName(path)}";
        }
    }

    /// <summary>True for a path a backup may contain (no folders outside the list, no "..")</summary>
    internal static bool Allowed(string entry)
    {
        string name = entry.Replace('\\', '/');
        if (name.Contains("..") || name.StartsWith('/') || name.Contains(':'))
            return false;
        if (Files.Contains(name))
            return true;
        int slash = name.LastIndexOf('/');
        return slash > 0 && Folders.Contains(name[..slash]) && name.Length > slash + 1;
    }

    private static byte[] MakeZip(IEnumerable<string> files)
    {
        List<string> list = files.ToList();
        using MemoryStream ms = new();
        using (ZipArchive zip = new(ms, ZipArchiveMode.Create, true))
        {
            foreach (string f in list)
                zip.CreateEntryFromFile(Path.Join(Root, f), f, CompressionLevel.Optimal);
            ZipArchiveEntry manifest = zip.CreateEntry(ManifestName);
            using StreamWriter w = new(manifest.Open());
            w.Write(JsonConvert.SerializeObject(new Manifest(1, DateTime.UtcNow, list), Formatting.Indented));
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Lists what a backup made now would contain
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<SettingsBackupInfoDto>(Status200OK, "application/json")]
    public Ok<SettingsBackupInfoDto> Info()
    {
        List<string> files = CurrentFiles().ToList();
        return TypedResults.Ok(new SettingsBackupInfoDto(files, files.Sum(f => new FileInfo(Path.Join(Root, f)).Length), null));
    }

    /// <summary>
    /// Downloads a .zip of Tranga's settings files and uploaded FMD2 modules
    /// </summary>
    /// <response code="200">The .zip</response>
    [HttpGet("Download")]
    [ProducesResponseType<FileContentResult>(Status200OK, "application/zip")]
    public FileContentHttpResult Download() =>
        TypedResults.File(MakeZip(CurrentFiles()), "application/zip", $"tranga-settings-{DateTime.Now:yyyy-MM-dd_HHmm}.zip");

    /// <summary>
    /// Restores a settings backup: replaces the settings files and FMD2 modules with the ones in the backup (files the
    /// backup doesn't have are kept, except FMD2 modules, which become exactly the backup's), keeps a copy of the current
    /// ones as settings-before-restore-….zip in the settings folder, and restarts Tranga.
    /// </summary>
    /// <response code="200">Checked (DryRun) or restored; Tranga restarts a moment later</response>
    /// <response code="400">Not a Tranga settings backup</response>
    [HttpPost("Restore")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    [ProducesResponseType<SettingsBackupInfoDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    public Results<Ok<SettingsBackupInfoDto>, BadRequest<string>> Restore([FromBody] SettingsRestoreRequest request)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.ContentBase64);
        }
        catch (FormatException)
        {
            return TypedResults.BadRequest("That isn't a file Tranga can read.");
        }
        if (bytes.Length > MaxBytes)
            return TypedResults.BadRequest("That file is too big to be a settings backup.");

        Dictionary<string, byte[]> entries = new();
        Manifest? manifest = null;
        try
        {
            using ZipArchive zip = new(new MemoryStream(bytes), ZipArchiveMode.Read);
            long total = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                total += entry.Length;
                if (total > MaxBytes)
                    return TypedResults.BadRequest("That backup unpacks to more than Tranga expects.");
                using Stream s = entry.Open();
                using MemoryStream copy = new();
                s.CopyTo(copy);
                if (entry.FullName == ManifestName)
                    manifest = JsonConvert.DeserializeObject<Manifest>(System.Text.Encoding.UTF8.GetString(copy.ToArray()));
                else if (Allowed(entry.FullName))
                    entries[entry.FullName.Replace('\\', '/')] = copy.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            return TypedResults.BadRequest("That isn't a .zip file.");
        }
        if (manifest is null)
            return TypedResults.BadRequest("That .zip isn't a Tranga settings backup (made in Settings → Maintenance).");
        if (entries.Count == 0)
            return TypedResults.BadRequest("That backup has no settings in it.");

        SettingsBackupInfoDto info = new(entries.Keys.OrderBy(k => k).ToList(), entries.Values.Sum(v => (long)v.Length), manifest.CreatedUtc);
        if (request.DryRun)
            return TypedResults.Ok(info);

        // Keep what's there now, in case the backup was the wrong one
        string before = Path.Join(Root, $"settings-before-restore-{DateTime.Now:yyyy-MM-dd_HHmmss}.zip");
        System.IO.File.WriteAllBytes(before, MakeZip(CurrentFiles()));

        // FMD2 module folders become exactly the backup's
        foreach (string folder in Folders)
        {
            string dir = Path.Join(Root, folder);
            if (Directory.Exists(dir))
                foreach (string path in Directory.GetFiles(dir))
                    System.IO.File.Delete(path);
        }
        foreach ((string name, byte[] content) in entries)
        {
            string path = Path.Join(Root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".restoring";
            System.IO.File.WriteAllBytes(tmp, content);
            System.IO.File.Move(tmp, path, true);
        }
        Log.Info($"Restored {entries.Count} settings files from a backup made {manifest.CreatedUtc:u}; previous files saved to {before}. Restarting.");

        // Settings are read at startup, so restart (Docker starts Tranga again: restart: unless-stopped)
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            lifetime.StopApplication();
        });
        return TypedResults.Ok(info);
    }
}
