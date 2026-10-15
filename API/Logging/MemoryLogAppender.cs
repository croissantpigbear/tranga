using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

namespace API.Logging;

/// <summary>
/// Keeps the most recent log lines in memory, so the website can show them (Activity → Logs).
/// The full log still goes to the console and the log file as configured in Log4Net.config.xml.
/// </summary>
public class MemoryLogAppender : AppenderSkeleton
{
    public const int Capacity = 5000;

    /// <param name="Id">Increasing number; ask for entries after the last one you have</param>
    public record LogEntry(long Id, DateTime TimeUtc, string Level, string Logger, string Message, string? Exception);

    public static MemoryLogAppender Instance { get; } = new() { Name = "MemoryLogAppender", Threshold = Level.Debug };

    private readonly LinkedList<LogEntry> _entries = new();
    private readonly Lock _lock = new();
    private long _nextId = 1;

    /// <summary>Adds the appender to the root logger (call after log4net is configured, and again after a reconfigure)</summary>
    public static void Attach()
    {
        if (log4net.LogManager.GetRepository() is not Hierarchy hierarchy)
            return;
        if (hierarchy.Root.GetAppender(Instance.Name) is null)
            hierarchy.Root.AddAppender(Instance);
        hierarchy.Configured = true;
    }

    protected override void Append(LoggingEvent e)
    {
        string logger = e.LoggerName ?? "";
        int dot = logger.LastIndexOf('.');
        if (dot >= 0 && dot < logger.Length - 1)
            logger = logger[(dot + 1)..]; // "API.Workers.Foo" → "Foo"
        string message = e.RenderedMessage ?? "";
        if (message.Length > 4000)
            message = message[..4000] + "…";
        string? exception = e.ExceptionObject?.ToString();
        if (exception is { Length: > 8000 })
            exception = exception[..8000] + "…";

        lock (_lock)
        {
            _entries.AddLast(new LogEntry(_nextId++, e.TimeStampUtc, e.Level?.Name ?? "INFO", logger, message, exception));
            while (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
    }

    private static int Rank(string level) => level switch
    {
        "DEBUG" => 0,
        "INFO" => 1,
        "WARN" => 2,
        "ERROR" => 3,
        "FATAL" => 4,
        _ => 1
    };

    /// <summary>Entries matching the filter, oldest first, at most <paramref name="limit"/> (the newest ones)</summary>
    public List<LogEntry> Query(long afterId, string? minLevel, string? search, int limit)
    {
        int min = Rank((minLevel ?? "DEBUG").ToUpperInvariant());
        List<LogEntry> snapshot;
        lock (_lock)
            snapshot = _entries.Where(e => e.Id > afterId).ToList();
        IEnumerable<LogEntry> q = snapshot.Where(e => Rank(e.Level) >= min);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(e => e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                             || e.Logger.Contains(search, StringComparison.OrdinalIgnoreCase)
                             || (e.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        List<LogEntry> list = q.ToList();
        return list.Count > limit ? list[^limit..] : list;
    }

    /// <summary>Counts of warnings and errors since <paramref name="sinceUtc"/></summary>
    public (int warnings, int errors) Problems(DateTime sinceUtc)
    {
        lock (_lock)
            return (_entries.Count(e => e.TimeUtc >= sinceUtc && e.Level == "WARN"),
                _entries.Count(e => e.TimeUtc >= sinceUtc && Rank(e.Level) >= 3));
    }

    public long LastId
    {
        get { lock (_lock) return _entries.Last?.Value.Id ?? 0; }
    }

    public DateTime? OldestUtc
    {
        get { lock (_lock) return _entries.First?.Value.TimeUtc; }
    }
}
