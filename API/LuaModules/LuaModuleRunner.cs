using System.Reflection;
using System.Text;
using NLua;
using NLua.Exceptions;

namespace API.LuaModules;

/// <summary>
/// Runs one FMD2 module in a fresh, sandboxed Lua 5.4 state: loads the FMD2 compatibility prelude, then the module,
/// then calls one of the prelude's entry points (which return JSON). Create one per operation and dispose it.
/// </summary>
internal sealed class LuaModuleRunner : IDisposable
{
    private static readonly Lazy<string> Prelude = new(() =>
    {
        using Stream stream = typeof(LuaModuleRunner).Assembly.GetManifestResourceStream("fmd_prelude.lua")
                              ?? throw new InvalidOperationException("fmd_prelude.lua resource missing");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    private readonly Lua _lua;
    public LuaHost Host { get; }

    private LuaModuleRunner(Lua lua, LuaHost host)
    {
        _lua = lua;
        Host = host;
    }

    /// <summary>Loads a module file</summary>
    /// <exception cref="LuaModuleException">The module couldn't be loaded</exception>
    public static LuaModuleRunner Load(string moduleSource, string fileName, TimeSpan budget, CancellationToken ct)
    {
        Lua lua = new();
        LuaHost host = new(budget, ct, fileName);
        try
        {
            // Byte-exact strings (binary data from modules survives); text is converted in LuaText
            lua.State.Encoding = LuaText.StateEncoding;
            foreach ((string luaName, string method) in LuaHost.Functions)
            {
                MethodInfo info = typeof(LuaHost).GetMethod(method) ?? throw new InvalidOperationException($"LuaHost.{method} missing");
                lua.RegisterFunction(luaName, host, info);
            }
            lua.DoString(LuaText.ToLua(Prelude.Value), "@fmd_prelude.lua");
            lua.DoString(LuaText.ToLua(StripBom(moduleSource)), "@" + fileName);
            return new LuaModuleRunner(lua, host);
        }
        catch (LuaException e)
        {
            lua.Dispose();
            throw new LuaModuleException(Clean(e.Message), host);
        }
        catch (Exception)
        {
            lua.Dispose();
            throw;
        }
    }

    /// <summary>Calls a prelude entry point (__fmd_info, __fmd_pages, …) and returns its JSON</summary>
    /// <exception cref="LuaModuleException">The module failed</exception>
    public string Call(string function, params object[] args)
    {
        try
        {
            LuaFunction f = _lua.GetFunction(function) ?? throw new LuaModuleException($"{function} missing", Host);
            object[] luaArgs = args.Select(a => a is string s ? LuaText.ToLua(s) : a).ToArray();
            object[]? result = f.Call(luaArgs);
            if (result is not { Length: > 0 } || result[0] is not string json)
                throw new LuaModuleException($"{function} returned nothing", Host);
            return LuaText.FromLua(json);
        }
        catch (LuaException e)
        {
            throw new LuaModuleException(Clean(e.Message), Host);
        }
    }

    internal static string StripBom(string s) => s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;

    /// <summary>Lua error text without the internal chunk names</summary>
    private static string Clean(string message) => LuaText.FromLua(message).Replace("[string \"", "").Replace("\"]", "");

    public void Dispose() => _lua.Dispose();
}

/// <summary>An FMD2 module failed (message is shown to the user)</summary>
public class LuaModuleException(string message, LuaHost? host = null) : Exception(message)
{
    public LuaHost? Host { get; } = host;
}
