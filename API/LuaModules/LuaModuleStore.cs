using System.Text.RegularExpressions;
using API.MangaConnectors;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace API.LuaModules;

/// <summary>What an FMD2 module registered in Init()</summary>
internal sealed record LuaModuleMeta(string Id, string Name, string RootUrl, string Category, string OnGetInfo, string OnGetPageNumber,
    string OnGetNameAndLink, string OnBeforeDownloadImage, bool AccountSupport);

/// <summary>Kind of uploaded Lua file</summary>
public enum LuaFileKind { Module, Template, Helper }

/// <summary>
/// Uploaded FMD2 Lua files (in /usr/share/tranga-api/lua/{modules,templates,utils}) and the websites they add.
/// Sites keep their name once assigned (it's stored with every manga/chapter link), so renaming a module doesn't
/// orphan downloads. Must be loaded before Tranga's startup clean-up, which removes links to unknown sites.
/// </summary>
public static class LuaModuleStore
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LuaModuleStore));
    private static readonly Lock StateLock = new();

    public static string Root => Path.Join(TrangaSettings.WorkingDirectory, "lua");
    private static string StatePath => Path.Join(Root, "state.json");
    internal static string Folder(LuaFileKind kind) => Path.Join(Root, kind switch
    {
        LuaFileKind.Module => "modules",
        LuaFileKind.Template => "templates",
        _ => "utils"
    });

    public const int MaxFileBytes = 1024 * 1024;
    private static readonly Regex SafeFileName = new(@"^[A-Za-z0-9][A-Za-z0-9_.\- ]{0,100}\.lua$", RegexOptions.Compiled);

    /// <summary>A loaded module file</summary>
    public sealed record ModuleFile(string FileName, List<string> SiteNames, string? Error, List<string> Warnings);

    private static volatile LuaMangaConnector[] _connectors = [];
    private static volatile List<ModuleFile> _files = [];

    /// <summary>Websites added by modules (enabled or not)</summary>
    public static LuaMangaConnector[] Connectors => _connectors;
    public static IReadOnlyList<ModuleFile> Files => _files;

    private sealed class State
    {
        /// <summary>file name → (module id → site name)</summary>
        public Dictionary<string, Dictionary<string, string>> Names { get; set; } = new();
        /// <summary>Site names that are turned off</summary>
        public HashSet<string> Disabled { get; set; } = [];
    }

    private static State LoadState()
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

    private static void SaveState(State state) => File.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));

    /// <summary>
    /// Names of every site an uploaded module file has had (also if it failed to load now), so their manga links are kept
    /// </summary>
    public static IEnumerable<string> KnownSiteNames()
    {
        try
        {
            return LoadState().Names.Values.SelectMany(n => n.Values).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Loads every module file and (re)builds the list of sites</summary>
    public static void LoadAll()
    {
        lock (StateLock)
        {
            foreach (LuaFileKind kind in Enum.GetValues<LuaFileKind>())
                Directory.CreateDirectory(Folder(kind));
            State state = LoadState();
            HashSet<string> builtIn = Tranga.BuiltInMangaConnectors.Select(c => c.Name)
                .Concat(Tranga.RetiredMangaConnectorNames).Select(n => n.ToLowerInvariant()).ToHashSet();
            HashSet<string> taken = [.. builtIn];
            List<LuaMangaConnector> connectors = [];
            List<ModuleFile> files = [];

            foreach (string path in Directory.GetFiles(Folder(LuaFileKind.Module), "*.lua").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(path);
                string source = File.ReadAllText(path);
                List<string> warnings = Warnings(source);
                try
                {
                    List<LuaModuleMeta> metas = Init(source, fileName);
                    if (metas.Count == 0)
                        throw new LuaModuleException("Init() didn't register a website (is this a module, not a template?)");
                    Dictionary<string, string> names = state.Names.TryGetValue(fileName, out Dictionary<string, string>? n) ? n : new();
                    List<string> siteNames = [];
                    foreach (LuaModuleMeta meta in metas)
                    {
                        if (!names.TryGetValue(meta.Id, out string? siteName) || taken.Contains(siteName.ToLowerInvariant()))
                            siteName = UniqueName(meta.Name, taken);
                        names[meta.Id] = siteName;
                        taken.Add(siteName.ToLowerInvariant());
                        LuaMangaConnector connector = new(siteName, fileName, meta) { Enabled = !state.Disabled.Contains(siteName) };
                        connectors.Add(connector);
                        siteNames.Add(siteName);
                        if (meta.AccountSupport)
                            warnings.Add($"{meta.Name} supports logging in to the site; Tranga can't log in, so members-only chapters won't work.");
                    }
                    state.Names[fileName] = names;
                    files.Add(new ModuleFile(fileName, siteNames, null, warnings));
                }
                catch (Exception e)
                {
                    string message = e is LuaModuleException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                    Log.Warn($"FMD2 module {fileName} failed to load: {message}");
                    files.Add(new ModuleFile(fileName, [], message, warnings));
                }
            }

            // Forget names of deleted files
            foreach (string gone in state.Names.Keys.Where(k => files.All(f => f.FileName != k)).ToList())
                state.Names.Remove(gone);
            SaveState(state);
            _connectors = connectors.ToArray();
            _files = files;
            Log.Info($"FMD2 modules: {connectors.Count} websites from {files.Count} files.");
        }
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        string baseName = Regex.Replace(name.Trim(), @"\s+", " ");
        if (baseName.Length == 0)
            baseName = "Site";
        if (baseName.Length > 28)
            baseName = baseName[..28].Trim();
        string candidate = baseName;
        for (int i = 2; taken.Contains(candidate.ToLowerInvariant()); i++)
            candidate = $"{baseName} {i}";
        if (taken.Contains(baseName.ToLowerInvariant()) && baseName.Length <= 26)
        {
            candidate = $"{baseName} FMD";
            for (int i = 2; taken.Contains(candidate.ToLowerInvariant()); i++)
                candidate = $"{baseName} FMD{i}";
        }
        return candidate.Length > 32 ? candidate[..32] : candidate;
    }

    private static List<LuaModuleMeta> Init(string source, string fileName)
    {
        using LuaModuleRunner runner = LuaModuleRunner.Load(source, fileName, TimeSpan.FromSeconds(20), CancellationToken.None);
        JArray metas = JArray.Parse(runner.Call("__fmd_init"));
        List<LuaModuleMeta> result = [];
        foreach (JToken m in metas)
        {
            string id = m["id"]?.ToString() ?? "";
            string rootUrl = m["rootUrl"]?.ToString() ?? "";
            if (id.Length == 0 || !Uri.TryCreate(rootUrl, UriKind.Absolute, out _))
                continue;
            result.Add(new LuaModuleMeta(id, m["name"]?.ToString() ?? id, rootUrl, m["category"]?.ToString() ?? "",
                m["onGetInfo"]?.ToString() ?? "", m["onGetPageNumber"]?.ToString() ?? "", m["onGetNameAndLink"]?.ToString() ?? "",
                m["onBeforeDownloadImage"]?.ToString() ?? "", m["accountSupport"]?.Value<bool>() ?? false));
        }
        return result;
    }

    /// <summary>Things in a module (or its templates) that Tranga can't do</summary>
    internal static List<string> Warnings(string source)
    {
        List<string> warnings = [];
        void Check(string needle, string message)
        {
            if (source.Contains(needle, StringComparison.Ordinal) && !warnings.Contains(message))
                warnings.Add(message);
        }
        Check("fmd.duktape", "Runs JavaScript (fmd.duktape), which Tranga can't. Parts that need it won't work.");
        Check("utils.nodejs", "Needs Node.js/a browser (utils.nodejs), which Tranga can't run. Parts that need it won't work.");
        Check("fmd.subprocess", "Runs programs (fmd.subprocess), which Tranga doesn't allow.");
        Check("mangafoxwatermark", "Uses FMD2's watermark remover, which Tranga doesn't have.");
        Check("imagepuzzle", "Unscrambles images (fmd.imagepuzzle), which Tranga can't. Pages may come out scrambled.");
        Check("OnDownloadImage", "Downloads images in a custom way (OnDownloadImage); Tranga downloads them normally, which may fail.");
        Check("AESDecrypt", "Decrypts data with AES, which Tranga's FMD2 support doesn't include.");
        foreach (Match m in Regex.Matches(source, @"require\s*\(?\s*['""]((?:templates|utils)\.[A-Za-z0-9_]+)['""]"))
        {
            string name = m.Groups[1].Value;
            if (name == "utils.json")
                continue;
            if (ReadLibrary(name) is null)
            {
                string folder = name.StartsWith("templates.") ? "templates" : "utils";
                warnings.Add($"Needs {name[(name.IndexOf('.') + 1)..]}.lua from FMD2's lua/{folder} folder. Upload it as a {(folder == "templates" ? "template" : "helper")}.");
            }
        }
        return warnings;
    }

    /// <summary>Source of a module file, or null</summary>
    internal static string? ReadModule(string fileName)
    {
        if (!SafeFileName.IsMatch(fileName))
            return null;
        string path = Path.Join(Folder(LuaFileKind.Module), fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Source for require 'templates.X' / 'utils.X', or null</summary>
    internal static string? ReadLibrary(string name)
    {
        Match m = Regex.Match(name, @"^(templates|utils)\.([A-Za-z0-9_]+)$");
        if (!m.Success)
            return null;
        string path = Path.Join(Folder(m.Groups[1].Value == "templates" ? LuaFileKind.Template : LuaFileKind.Helper), m.Groups[2].Value + ".lua");
        return File.Exists(path) ? LuaModuleRunner.StripBom(File.ReadAllText(path)) : null;
    }

    public static List<string> List(LuaFileKind kind)
    {
        string folder = Folder(kind);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.lua").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    /// <summary>Guesses what kind of file this is</summary>
    public static LuaFileKind Detect(string source)
    {
        if (Regex.IsMatch(source, @"function\s+Init\s*\(") && source.Contains("NewWebsiteModule", StringComparison.Ordinal))
            return LuaFileKind.Module;
        if (Regex.IsMatch(source, @"local\s+_M\s*=\s*\{\s*\}") && Regex.IsMatch(source, @"function\s+_M\.(GetInfo|GetPageNumber|GetNameAndLink)"))
            return LuaFileKind.Template;
        return LuaFileKind.Helper;
    }

    /// <summary>Saves an uploaded file and reloads the modules</summary>
    /// <returns>Error message, or null</returns>
    public static string? Save(string fileName, string content, LuaFileKind kind)
    {
        fileName = Path.GetFileName(fileName ?? "").Trim();
        if (!SafeFileName.IsMatch(fileName))
            return "File names may only contain letters, numbers, spaces, '_', '-' and '.', and must end in .lua";
        if (string.IsNullOrWhiteSpace(content))
            return "The file is empty";
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
            return "The file is larger than 1 MB";
        if (content.Contains('\0'))
            return "This doesn't look like a text file";
        Directory.CreateDirectory(Folder(kind));
        // A file name can only be one kind
        foreach (LuaFileKind other in Enum.GetValues<LuaFileKind>().Where(k => k != kind))
        {
            string otherPath = Path.Join(Folder(other), fileName);
            if (File.Exists(otherPath))
                File.Delete(otherPath);
        }
        File.WriteAllText(Path.Join(Folder(kind), fileName), LuaModuleRunner.StripBom(content));
        LoadAll();
        return null;
    }

    public static bool Delete(string fileName, LuaFileKind kind)
    {
        if (!SafeFileName.IsMatch(fileName ?? ""))
            return false;
        string path = Path.Join(Folder(kind), fileName);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        if (kind == LuaFileKind.Module)
            LuaTitleList.DeleteFor(Connectors.Where(c => c.FileName == fileName).Select(c => c.Name));
        LoadAll();
        return true;
    }

    public static bool SetEnabled(string siteName, bool enabled)
    {
        lock (StateLock)
        {
            if (Connectors.FirstOrDefault(c => c.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase)) is not { } connector)
                return false;
            State state = LoadState();
            if (enabled)
                state.Disabled.Remove(connector.Name);
            else
                state.Disabled.Add(connector.Name);
            SaveState(state);
            connector.Enabled = enabled;
            return true;
        }
    }
}
