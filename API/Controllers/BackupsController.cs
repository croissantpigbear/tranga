using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Backups made by the tranga-backup container (see docker-compose.yaml): database dumps and copies of the settings
/// folder. The API only reads the folder (mounted at /backups, or TRANGA_BACKUP_DIR) to show what's there.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class BackupsController : ControllerBase
{
    /// <summary>A backup file</summary>
    /// <param name="Kind">database or settings</param>
    /// <param name="FileName">File name</param>
    /// <param name="SizeBytes">Size</param>
    /// <param name="CreatedUtc">When it was made</param>
    public record BackupFileDto(string Kind, string FileName, long SizeBytes, DateTime CreatedUtc);

    /// <summary>Backups found</summary>
    /// <param name="FolderFound">The backup folder is mounted into the API container</param>
    /// <param name="Folder">Where the API looks</param>
    /// <param name="Files">Newest first</param>
    public record BackupsDto(bool FolderFound, string Folder, List<BackupFileDto> Files);

    private static string Folder => Environment.GetEnvironmentVariable("TRANGA_BACKUP_DIR") is { Length: > 0 } dir ? dir : "/backups";

    /// <summary>
    /// Lists the backups the backup container has made
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<BackupsDto>(Status200OK, "application/json")]
    public Ok<BackupsDto> List()
    {
        if (!Directory.Exists(Folder))
            return TypedResults.Ok(new BackupsDto(false, Folder, []));
        List<BackupFileDto> files = Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Where(f => f.Name.StartsWith("tranga-db-") || f.Name.StartsWith("tranga-settings-"))
            .Select(f => new BackupFileDto(f.Name.StartsWith("tranga-db-") ? "database" : "settings", f.Name, f.Length, f.LastWriteTimeUtc))
            .OrderByDescending(f => f.CreatedUtc)
            .ToList();
        return TypedResults.Ok(new BackupsDto(true, Folder, files));
    }
}
