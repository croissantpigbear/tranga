using System.Diagnostics.CodeAnalysis;
using API.LuaModules;
using API.Schema.NotificationsContext;

namespace API.Workers;

/// <summary>
/// Once a day: checks uploaded FMD2 Lua files against FMD2 on GitHub. Installs the updates if automatic updates are on,
/// otherwise sends a notification (once per new version).
/// </summary>
public class CheckLuaUpdatesWorker(TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromHours(24);

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private NotificationsContext NotificationsContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        NotificationsContext = GetContext<NotificationsContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        if (Enum.GetValues<LuaFileKind>().All(k => LuaModuleStore.List(k).Count == 0))
            return [];
        LuaUpdateChecker.State state = await LuaUpdateChecker.Check(CancellationToken);
        if (state.LastError is not null)
            return [];

        string message;
        if (state.AutoUpdate)
        {
            List<LuaUpdateChecker.UpdateResult> results = await LuaUpdateChecker.UpdateAll(CancellationToken);
            if (results.Count == 0)
                return [];
            List<string> updated = results.Where(r => r.Ok).Select(r => r.FileName).ToList();
            List<string> failed = results.Where(r => !r.Ok).Select(r => $"{r.FileName} ({r.Message})").ToList();
            message = (updated.Count > 0 ? $"Updated: {string.Join(", ", updated)}. " : "")
                      + (failed.Count > 0 ? $"Not updated: {string.Join(", ", failed)}." : "");
            LuaUpdateChecker.TakeNewForNotification(); // nothing left to announce for these versions
        }
        else
        {
            List<LuaUpdateChecker.FileStatus> fresh = LuaUpdateChecker.TakeNewForNotification();
            if (fresh.Count == 0)
                return [];
            message = $"Newer versions on FMD2: {string.Join(", ", fresh.Select(f => f.FileName))}. Update them on the FMD2 websites page.";
        }

        await NotificationsContext.Notifications.AddAsync(new Notification("FMD2 website modules", message.Trim()), CancellationToken);
        if (await NotificationsContext.Sync(CancellationToken, GetType(), "FMD2 module updates") is { success: false } e)
            Log.Error($"Failed to save notification: {e.exceptionMessage}");
        return [];
    }
}
