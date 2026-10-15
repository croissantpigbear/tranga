using API.LuaModules;
using API.MangaConnectors;

namespace API.Workers;

/// <summary>
/// Reads the full title list of a website added by an FMD2 module, so it can be searched
/// </summary>
public class BuildLuaTitleListWorker(string siteName, IEnumerable<BaseWorker>? dependsOn = null) : BaseWorker(dependsOn)
{
    public string SiteName { get; } = siteName;

    /// <summary>True if a title list for this site is already being built or queued</summary>
    public static bool IsQueued(string siteName) =>
        Tranga.GetKnownWorkers().Concat(Tranga.GetRunningWorkers())
            .OfType<BuildLuaTitleListWorker>()
            .Any(w => w.State is >= WorkerExecutionState.Created and < WorkerExecutionState.Completed
                      && w.SiteName.Equals(siteName, StringComparison.OrdinalIgnoreCase));

    protected override Task<BaseWorker[]> DoWorkInternal()
    {
        if (LuaModuleStore.Connectors.FirstOrDefault(c => c.Name.Equals(SiteName, StringComparison.OrdinalIgnoreCase)) is not { } connector)
            return Task.FromResult<BaseWorker[]>([]);
        LuaTitleList.Build(connector, CancellationToken);
        return Task.FromResult<BaseWorker[]>([]);
    }

    public override string ToString() => $"{base.ToString()} {SiteName}";
}
