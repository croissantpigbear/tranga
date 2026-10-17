using API.Downloads;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.MangaContext;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Release calendar: new chapters by day, and when each series' next chapter is likely (from how often it has released).
/// Dates are when Tranga found a chapter (checks run every few hours), or for older chapters when it downloaded it;
/// chapters downloaded when a series was first added aren't counted as releases.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class CalendarController(MangaContext mangaContext, ActionsContext actionsContext) : ControllerBase
{
    /// <summary>New chapters of one series on one day</summary>
    /// <param name="MangaId">Manga.Key</param>
    /// <param name="MangaName">Series name</param>
    /// <param name="Chapters">Chapter numbers, lowest first</param>
    /// <param name="FirstUtc">Earliest of them</param>
    public record CalendarReleaseDto(string MangaId, string MangaName, List<string> Chapters, DateTime FirstUtc);

    /// <summary>One day of the calendar</summary>
    /// <param name="Date">yyyy-MM-dd in the server's time zone</param>
    public record CalendarDayDto(string Date, List<CalendarReleaseDto> Releases);

    /// <summary>When a series' next chapter is likely</summary>
    /// <param name="EveryDays">Usual gap between releases</param>
    /// <param name="LastUtc">Last release</param>
    /// <param name="NextUtc">Expected next release (last + usual gap)</param>
    /// <param name="Releases">Releases the estimate is based on</param>
    public record CalendarExpectedDto(string MangaId, string MangaName, double EveryDays, DateTime LastUtc, DateTime NextUtc, int Releases);

    /// <param name="Days">Days with releases, in order</param>
    /// <param name="Expected">Series expected in this period, soonest first</param>
    public record CalendarDto(List<CalendarDayDto> Days, List<CalendarExpectedDto> Expected);

    /// <summary>Chapters downloaded within this time of a series' first download were part of adding it, not releases</summary>
    private static readonly TimeSpan AddingWindow = TimeSpan.FromDays(2);
    private static readonly TimeSpan History = TimeSpan.FromDays(366);

    internal record Release(string MangaId, string ChapterId, DateTime WhenUtc);

    /// <summary>
    /// Releases from the release log, plus older ones from download history: a download counts if it came after the
    /// series' first few days and wasn't part of a batch (more than <see cref="ReleaseLog.MaxPerCheck"/> chapters of
    /// the series that day).
    /// </summary>
    internal static List<Release> Releases(IEnumerable<ReleaseLog.Entry> log, IEnumerable<(string MangaId, string ChapterId, DateTime WhenUtc)> downloads,
        IReadOnlyDictionary<string, DateTime> firstDownloadOfManga)
    {
        Dictionary<string, Release> byChapter = new();
        foreach (ReleaseLog.Entry e in log)
            byChapter.TryAdd(e.ChapterId, new Release(e.MangaId, e.ChapterId, e.FoundUtc));

        List<(string MangaId, string ChapterId, DateTime WhenUtc)> later = downloads
            .Where(d => !byChapter.ContainsKey(d.ChapterId))
            .Where(d => !firstDownloadOfManga.TryGetValue(d.MangaId, out DateTime first) || d.WhenUtc > first + AddingWindow)
            .GroupBy(d => d.ChapterId).Select(g => g.MinBy(d => d.WhenUtc))
            .ToList();
        foreach (var batch in later.GroupBy(d => (d.MangaId, d.WhenUtc.Date)))
        {
            if (batch.Count() > ReleaseLog.MaxPerCheck)
                continue;
            foreach ((string mangaId, string chapterId, DateTime when) in batch)
                byChapter.TryAdd(chapterId, new Release(mangaId, chapterId, when));
        }
        return byChapter.Values.ToList();
    }

    /// <summary>Usual gap and next release from release times (needs 3 release days; none if it releases less than every 4 months)</summary>
    internal static (double EveryDays, DateTime Last, DateTime Next)? Predict(IEnumerable<DateTime> releaseTimesUtc)
    {
        // Chapters found within a day of each other are one release
        List<DateTime> releases = [];
        foreach (DateTime t in releaseTimesUtc.OrderBy(t => t))
            if (releases.Count == 0 || t - releases[^1] > TimeSpan.FromHours(20))
                releases.Add(t);
        if (releases.Count < 3)
            return null;
        List<double> gaps = releases.Zip(releases.Skip(1), (a, b) => (b - a).TotalDays).OrderBy(g => g).ToList();
        double median = gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2;
        if (median > 120)
            return null;
        median = Math.Max(1, Math.Round(median));
        return (median, releases[^1], releases[^1].AddDays(median));
    }

    /// <summary>
    /// Returns the releases of one month and the series expected in it
    /// </summary>
    /// <param name="year">Year (default: this month)</param>
    /// <param name="month">Month 1–12</param>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<CalendarDto>(Status200OK, "application/json")]
    public async Task<Ok<CalendarDto>> Get([FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        DateTime nowLocal = DateTime.Now;
        int y = year is >= 2000 and <= 2100 ? year.Value : nowLocal.Year;
        int m = month is >= 1 and <= 12 ? month.Value : nowLocal.Month;
        DateTime fromLocal = new(y, m, 1, 0, 0, 0, DateTimeKind.Local);
        DateTime toLocal = fromLocal.AddMonths(1);
        CancellationToken ct = HttpContext.RequestAborted;

        DateTime since = DateTime.UtcNow - History;
        List<(string MangaId, string ChapterId, DateTime WhenUtc)> downloads = (await actionsContext.Actions
                .OfType<ChapterDownloadedActionRecord>()
                .Where(a => a.PerformedAt >= since)
                .Select(a => new { a.MangaId, a.ChapterId, a.PerformedAt })
                .ToListAsync(ct))
            .Select(a => (a.MangaId, a.ChapterId, a.PerformedAt)).ToList();
        Dictionary<string, DateTime> firstDownload = await actionsContext.Actions
            .OfType<ChapterDownloadedActionRecord>()
            .GroupBy(a => a.MangaId)
            .Select(g => new { MangaId = g.Key, First = g.Min(a => a.PerformedAt) })
            .ToDictionaryAsync(x => x.MangaId, x => x.First, ct);

        List<Release> releases = Releases(ReleaseLog.All(), downloads, firstDownload);

        // Names and chapter numbers of series still in Tranga
        Dictionary<string, string> names = await mangaContext.Mangas
            .Select(mg => new { mg.Key, mg.Name })
            .ToDictionaryAsync(x => x.Key, x => x.Name, ct);
        releases.RemoveAll(r => !names.ContainsKey(r.MangaId));

        DateTime fromUtc = fromLocal.ToUniversalTime(), toUtc = toLocal.ToUniversalTime();
        List<Release> inMonth = releases.Where(r => r.WhenUtc >= fromUtc && r.WhenUtc < toUtc).ToList();
        List<string> chapterIds = inMonth.Select(r => r.ChapterId).Distinct().ToList();
        Dictionary<string, string> numbers = await mangaContext.Chapters
            .Where(c => chapterIds.Contains(c.Key))
            .Select(c => new { c.Key, c.ChapterNumber })
            .ToDictionaryAsync(x => x.Key, x => x.ChapterNumber, ct);

        List<CalendarDayDto> days = inMonth
            .GroupBy(r => DateTime.SpecifyKind(r.WhenUtc, DateTimeKind.Utc).ToLocalTime().Date)
            .OrderBy(g => g.Key)
            .Select(day => new CalendarDayDto(day.Key.ToString("yyyy-MM-dd"), day
                .GroupBy(r => r.MangaId)
                .Select(s => new CalendarReleaseDto(s.Key, names[s.Key],
                    s.Select(r => numbers.GetValueOrDefault(r.ChapterId)).OfType<string>()
                        .OrderBy(n => Chapter.NumberValue(n)).ToList(),
                    s.Min(r => r.WhenUtc)))
                .OrderBy(r => r.MangaName, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .ToList();

        // Expected releases: series whose next release falls in this month (or is overdue, for the current month)
        bool thisMonth = y == nowLocal.Year && m == nowLocal.Month;
        List<CalendarExpectedDto> expected = releases
            .GroupBy(r => r.MangaId)
            .Select(g => (MangaId: g.Key, Count: g.Count(), Prediction: Predict(g.Select(r => r.WhenUtc))))
            .Where(p => p.Prediction is not null)
            .Select(p => new CalendarExpectedDto(p.MangaId, names[p.MangaId], p.Prediction!.Value.EveryDays,
                p.Prediction.Value.Last, p.Prediction.Value.Next, p.Count))
            .Where(e => (e.NextUtc >= fromUtc && e.NextUtc < toUtc)
                        || (thisMonth && e.NextUtc < fromUtc && DateTime.UtcNow - e.NextUtc < TimeSpan.FromDays(e.EveryDays * 2)))
            .OrderBy(e => e.NextUtc)
            .ToList();

        return TypedResults.Ok(new CalendarDto(days, expected));
    }
}
