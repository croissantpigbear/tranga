using System.Net;
using System.Security.Cryptography;
using System.Text;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace API.LuaModules;

/// <summary>
/// Compares uploaded FMD2 Lua files with the current ones in the FMD2 repository on GitHub and updates them.
/// One GitHub API request lists every file with its content hash (git blob SHA), so checking is cheap; the last
/// change date and message are looked up only for files that differ. Before a file is replaced, the old version is
/// kept in lua/backups (last 3 per file) and restored automatically if the new one fails to load.
/// State and settings in lua/updates.json.
/// </summary>
public static class LuaUpdateChecker
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LuaUpdateChecker));
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lock StateLock = new();
    private static string StatePath => Path.Join(LuaModuleStore.Root, "updates.json");
    private static string BackupFolder => Path.Join(LuaModuleStore.Root, "backups");
    private const int MaxCommitLookups = 15;
    private const int BackupsPerFile = 3;

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tranga/2 (self-hosted; FMD2 module updates)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public class FileStatus
    {
        /// <summary>module, template or helper</summary>
        public string Kind { get; set; } = "";
        public string FileName { get; set; } = "";
        /// <summary>upToDate, updateAvailable, notInFmd2</summary>
        public string Status { get; set; } = "";
        /// <summary>Path in the FMD2 repository</summary>
        public string? RemotePath { get; set; }
        public string? RemoteSha { get; set; }
        public string? LocalSha { get; set; }
        /// <summary>When the file last changed in FMD2, and the change's description</summary>
        public DateTime? RemoteChangedUtc { get; set; }
        public string? RemoteChangeMessage { get; set; }
        /// <summary>An older version is kept (for Undo)</summary>
        public bool HasBackup { get; set; }
        public DateTime? UpdatedUtc { get; set; }
    }

    public class State
    {
        public string Repository { get; set; } = "dazedcat19/FMD2";
        public string Branch { get; set; } = "master";
        /// <summary>Install updates automatically after the daily check</summary>
        public bool AutoUpdate { get; set; }
        public DateTime? LastCheckUtc { get; set; }
        public string? LastError { get; set; }
        public List<FileStatus> Files { get; set; } = [];
        /// <summary>Remote versions already notified about ("kind/file@sha")</summary>
        public HashSet<string> Notified { get; set; } = [];
    }

    public static State Load()
    {
        lock (StateLock)
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonConvert.DeserializeObject<State>(File.ReadAllText(StatePath)) ?? new State();
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {StatePath}: {e.Message}");
            }
            return new State();
        }
    }

    private static void Save(State state)
    {
        lock (StateLock)
        {
            Directory.CreateDirectory(LuaModuleStore.Root);
            File.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
        }
    }

    public static void SetSettings(bool? autoUpdate, string? repository, string? branch)
    {
        State state = Load();
        if (autoUpdate is { } a)
            state.AutoUpdate = a;
        if (!string.IsNullOrWhiteSpace(repository))
            state.Repository = repository.Trim().Trim('/');
        if (!string.IsNullOrWhiteSpace(branch))
            state.Branch = branch.Trim();
        Save(state);
    }

    internal static string KindName(LuaFileKind kind) => kind switch
    {
        LuaFileKind.Module => "module",
        LuaFileKind.Template => "template",
        _ => "helper"
    };

    private static string RepoFolder(LuaFileKind kind) => kind switch
    {
        LuaFileKind.Module => "lua/modules",
        LuaFileKind.Template => "lua/templates",
        _ => "lua/utils"
    };

    /// <summary>Git blob SHA-1 of a file's content (line endings normalised like the repository stores them)</summary>
    internal static string GitSha(string content)
    {
        byte[] body = Encoding.UTF8.GetBytes(LuaModuleRunner.StripBom(content).Replace("\r\n", "\n"));
        byte[] header = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
        return Convert.ToHexStringLower(SHA1.HashData(header.Concat(body).ToArray()));
    }

    private static async Task<JToken?> GitHub(string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub's limit for checks was reached (60 an hour); try again later");
        response.EnsureSuccessStatusCode();
        return JToken.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Compares every uploaded Lua file with FMD2's current version</summary>
    /// <returns>The new state (LastError set if GitHub couldn't be reached)</returns>
    public static async Task<State> Check(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            State state = Load();
            Dictionary<string, FileStatus> previous = state.Files.ToDictionary(f => $"{f.Kind}/{f.FileName}");
            Dictionary<string, string> remote; // path → sha
            try
            {
                JToken? tree = await GitHub($"https://api.github.com/repos/{state.Repository}/git/trees/{Uri.EscapeDataString(state.Branch)}?recursive=1", ct);
                if (tree?["tree"] is not JArray entries)
                    throw new InvalidOperationException($"{state.Repository} ({state.Branch}) wasn't found on GitHub");
                remote = entries
                    .Where(e => e["type"]?.Value<string>() == "blob" && (e["path"]?.Value<string>() ?? "").StartsWith("lua/"))
                    .ToDictionary(e => e["path"]!.Value<string>()!, e => e["sha"]!.Value<string>()!);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                state.LastError = e.Message;
                state.LastCheckUtc = DateTime.UtcNow;
                Save(state);
                Log.Warn($"FMD2 update check failed: {e.Message}");
                return state;
            }

            List<FileStatus> files = [];
            int lookups = 0;
            foreach (LuaFileKind kind in Enum.GetValues<LuaFileKind>())
            {
                foreach (string fileName in LuaModuleStore.List(kind))
                {
                    string local = GitSha(File.ReadAllText(Path.Join(LuaModuleStore.Folder(kind), fileName)));
                    // Usually in the matching folder; otherwise anywhere under lua/ with the same name
                    string? path = remote.ContainsKey($"{RepoFolder(kind)}/{fileName}") ? $"{RepoFolder(kind)}/{fileName}"
                        : remote.Keys.FirstOrDefault(p => p.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase));
                    previous.TryGetValue($"{KindName(kind)}/{fileName}", out FileStatus? before);
                    FileStatus status = new()
                    {
                        Kind = KindName(kind),
                        FileName = fileName,
                        RemotePath = path,
                        RemoteSha = path is null ? null : remote[path],
                        LocalSha = local,
                        HasBackup = Backups(fileName).Count > 0,
                        UpdatedUtc = before?.UpdatedUtc,
                    };
                    status.Status = path is null ? "notInFmd2" : status.RemoteSha == local ? "upToDate" : "updateAvailable";
                    if (status.Status == "updateAvailable")
                    {
                        if (before?.RemoteSha == status.RemoteSha && before.RemoteChangedUtc is not null)
                        {
                            status.RemoteChangedUtc = before.RemoteChangedUtc;
                            status.RemoteChangeMessage = before.RemoteChangeMessage;
                        }
                        else if (lookups++ < MaxCommitLookups)
                        {
                            try
                            {
                                JToken? commits = await GitHub(
                                    $"https://api.github.com/repos/{state.Repository}/commits?path={Uri.EscapeDataString(path!)}&sha={Uri.EscapeDataString(state.Branch)}&per_page=1", ct);
                                if (commits is JArray { Count: > 0 } list)
                                {
                                    status.RemoteChangedUtc = list[0]["commit"]?["committer"]?["date"]?.Value<DateTime?>()?.ToUniversalTime();
                                    status.RemoteChangeMessage = list[0]["commit"]?["message"]?.Value<string>()?.Split('\n')[0].Trim();
                                }
                            }
                            catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
                            {
                                Log.Debug($"No change details for {path}: {e.Message}");
                            }
                        }
                    }
                    files.Add(status);
                }
            }
            state.Files = files;
            state.LastCheckUtc = DateTime.UtcNow;
            state.LastError = null;
            Save(state);
            Log.Info($"FMD2 update check: {files.Count(f => f.Status == "updateAvailable")} of {files.Count} files have a newer version");
            return state;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Result of updating one file</summary>
    public record UpdateResult(string FileName, bool Ok, string Message);

    /// <summary>
    /// Downloads FMD2's current version of a file and installs it (old version kept for Undo). If the new version of a
    /// module doesn't load, the old one is put back.
    /// </summary>
    public static async Task<UpdateResult> Update(LuaFileKind kind, string fileName, CancellationToken ct)
    {
        State state = Load();
        FileStatus? status = state.Files.FirstOrDefault(f => f.Kind == KindName(kind) && f.FileName == fileName);
        if (status?.RemotePath is null)
            return new UpdateResult(fileName, false, "Not found in FMD2; check for updates first");
        string localPath = Path.Join(LuaModuleStore.Folder(kind), fileName);
        if (!File.Exists(localPath))
            return new UpdateResult(fileName, false, "The file isn't uploaded any more");

        string content;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                $"https://raw.githubusercontent.com/{state.Repository}/{state.Branch}/{status.RemotePath}", ct);
            response.EnsureSuccessStatusCode();
            content = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new UpdateResult(fileName, false, $"Couldn't download it from GitHub: {e.Message}");
        }

        string old = await File.ReadAllTextAsync(localPath, ct);
        Backup(fileName, old);
        if (LuaModuleStore.Save(fileName, content, kind) is { } error)
        {
            await File.WriteAllTextAsync(localPath, old, ct);
            LuaModuleStore.LoadAll();
            return new UpdateResult(fileName, false, error);
        }
        if (kind == LuaFileKind.Module && LuaModuleStore.Files.FirstOrDefault(f => f.FileName == fileName)?.Error is { } loadError)
        {
            LuaModuleStore.Save(fileName, old, kind);
            return new UpdateResult(fileName, false, $"The new version doesn't load in Tranga ({loadError}); kept the old one");
        }

        lock (StateLock)
        {
            State current = Load();
            if (current.Files.FirstOrDefault(f => f.Kind == KindName(kind) && f.FileName == fileName) is { } s)
            {
                s.Status = "upToDate";
                s.LocalSha = GitSha(content);
                s.HasBackup = true;
                s.UpdatedUtc = DateTime.UtcNow;
            }
            Save(current);
        }
        Log.Info($"Updated FMD2 {KindName(kind)} {fileName} from {state.Repository}");
        return new UpdateResult(fileName, true, "Updated");
    }

    /// <summary>Updates every file with a newer version (templates and helpers first, as modules use them)</summary>
    public static async Task<List<UpdateResult>> UpdateAll(CancellationToken ct)
    {
        List<UpdateResult> results = [];
        foreach (FileStatus f in Load().Files.Where(f => f.Status == "updateAvailable").OrderBy(f => f.Kind == "module" ? 1 : 0))
        {
            LuaFileKind kind = f.Kind switch { "module" => LuaFileKind.Module, "template" => LuaFileKind.Template, _ => LuaFileKind.Helper };
            results.Add(await Update(kind, f.FileName, ct));
        }
        return results;
    }

    /// <summary>Puts back the version from before the last update</summary>
    public static UpdateResult Undo(LuaFileKind kind, string fileName)
    {
        List<string> backups = Backups(fileName);
        if (backups.Count == 0)
            return new UpdateResult(fileName, false, "No older version is kept");
        string latest = backups[0];
        if (LuaModuleStore.Save(fileName, File.ReadAllText(latest), kind) is { } error)
            return new UpdateResult(fileName, false, error);
        File.Delete(latest);
        lock (StateLock)
        {
            State current = Load();
            if (current.Files.FirstOrDefault(f => f.Kind == KindName(kind) && f.FileName == fileName) is { } s)
            {
                s.LocalSha = GitSha(File.ReadAllText(Path.Join(LuaModuleStore.Folder(kind), fileName)));
                s.Status = s.RemoteSha is null ? "notInFmd2" : s.RemoteSha == s.LocalSha ? "upToDate" : "updateAvailable";
                s.HasBackup = backups.Count > 1;
            }
            Save(current);
        }
        return new UpdateResult(fileName, true, "Previous version restored");
    }

    private static string BackupPrefix(string fileName) => Path.GetFileNameWithoutExtension(fileName) + ".";

    /// <summary>Backups of a file, newest first</summary>
    private static List<string> Backups(string fileName)
    {
        if (!Directory.Exists(BackupFolder))
            return [];
        System.Text.RegularExpressions.Regex mine = new(
            "^" + System.Text.RegularExpressions.Regex.Escape(BackupPrefix(fileName)) + @"\d{8}-\d{6}\.lua\.bak$");
        return Directory.EnumerateFiles(BackupFolder, BackupPrefix(fileName) + "*.lua.bak")
            .Where(p => mine.IsMatch(Path.GetFileName(p)))
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static void Backup(string fileName, string content)
    {
        Directory.CreateDirectory(BackupFolder);
        File.WriteAllText(Path.Join(BackupFolder, $"{BackupPrefix(fileName)}{DateTime.UtcNow:yyyyMMdd-HHmmss}.lua.bak"), content);
        foreach (string extra in Backups(fileName).Skip(BackupsPerFile))
            File.Delete(extra);
    }

    /// <summary>Files with a newer version that haven't been notified about yet (marks them notified)</summary>
    internal static List<FileStatus> TakeNewForNotification()
    {
        lock (StateLock)
        {
            State state = Load();
            List<FileStatus> fresh = state.Files
                .Where(f => f.Status == "updateAvailable" && state.Notified.Add($"{f.Kind}/{f.FileName}@{f.RemoteSha}"))
                .ToList();
            if (fresh.Count > 0)
                Save(state);
            return fresh;
        }
    }
}
