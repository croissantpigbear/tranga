using System.Collections.Concurrent;
using log4net;
using Newtonsoft.Json;

namespace API.LuaModules;

/// <summary>
/// FMD2's MODULE.Storage: values a module keeps between calls (e.g. MangaFire remembers in GetInfo which chapter list
/// it used, and needs it again when the pages are fetched). Kept per module id and saved to lua/storage.json so it
/// survives restarts.
/// </summary>
internal static class LuaModuleStorage
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LuaModuleStorage));
    private const int MaxKeysPerModule = 200, MaxKeyLength = 128, MaxValueLength = 64 * 1024;

    private static string FilePath => Path.Join(LuaModuleStore.Root, "storage.json");
    private static readonly Lock FileLock = new();
    private static ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? _values;

    private static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Values
    {
        get
        {
            if (_values is not null)
                return _values;
            lock (FileLock)
            {
                if (_values is not null)
                    return _values;
                ConcurrentDictionary<string, ConcurrentDictionary<string, string>> loaded = new();
                try
                {
                    if (File.Exists(FilePath)
                        && JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(FilePath)) is { } saved)
                        foreach ((string module, Dictionary<string, string> values) in saved)
                            loaded[module] = new ConcurrentDictionary<string, string>(values);
                }
                catch (Exception e)
                {
                    Log.Warn($"Couldn't read {FilePath}: {e.Message}");
                }
                return _values = loaded;
            }
        }
    }

    public static string Get(string moduleId, string key) =>
        Values.TryGetValue(moduleId, out ConcurrentDictionary<string, string>? values) && values.TryGetValue(key, out string? value)
            ? value
            : "";

    public static void Set(string moduleId, string key, string value)
    {
        if (moduleId.Length == 0 || key.Length is 0 or > MaxKeyLength || value.Length > MaxValueLength)
            return;
        ConcurrentDictionary<string, string> values = Values.GetOrAdd(moduleId, _ => new ConcurrentDictionary<string, string>());
        if (values.TryGetValue(key, out string? old) && old == value)
            return;
        if (value.Length == 0)
            values.TryRemove(key, out _);
        else if (values.ContainsKey(key) || values.Count < MaxKeysPerModule)
            values[key] = value;
        else
            return;
        Save();
    }

    private static void Save()
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(LuaModuleStore.Root);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(Values, Formatting.Indented));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't save {FilePath}: {e.Message}");
            }
        }
    }
}
