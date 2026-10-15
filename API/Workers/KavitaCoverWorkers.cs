using System.Diagnostics.CodeAnalysis;
using API.KavitaCovers;
using API.Schema.LibraryContext;
using API.Schema.MangaContext;

namespace API.Workers;

/// <summary>
/// Sends covers of newly downloaded manga to Kavita, every 15 minutes (only those it hasn't sent yet, or whose cover changed)
/// </summary>
public class SyncKavitaCoversWorker(TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromMinutes(15);

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private LibraryContext LibraryContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
        LibraryContext = GetContext<LibraryContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        int set = await KavitaCoverSync.Run(MangaContext, LibraryContext, null, false,
            DateTime.UtcNow + Constants.WorkerTimeout - TimeSpan.FromMinutes(1), CancellationToken);
        if (set > 0)
            Log.InfoFormat("Set {0} series covers in Kavita.", set);
        return [];
    }
}

/// <summary>
/// Sends covers to Kavita now (one manga or all), e.g. from the website's button
/// </summary>
public class PushKavitaCoversWorker(string? mangaId, bool force, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    public string? MangaId { get; } = mangaId;

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private LibraryContext LibraryContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
        LibraryContext = GetContext<LibraryContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        int set = await KavitaCoverSync.Run(MangaContext, LibraryContext, MangaId, force,
            DateTime.UtcNow + Constants.WorkerTimeout - TimeSpan.FromMinutes(1), CancellationToken);
        Log.InfoFormat("Set {0} series covers in Kavita.", set);
        return [];
    }

    public override string ToString() => $"{base.ToString()} {MangaId ?? "all"}";
}
