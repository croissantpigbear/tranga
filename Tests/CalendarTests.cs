using API.Controllers;
using API.Downloads;

namespace Tests;

/// <summary>Release calendar: what counts as a release, and when the next one is expected</summary>
public class CalendarTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Predict_WeeklySeries_ExpectsAWeekAfterTheLast()
    {
        DateTime[] times = [Start, Start.AddDays(7), Start.AddDays(14), Start.AddDays(21).AddHours(3)];
        var p = CalendarController.Predict(times);
        Assert.NotNull(p);
        Assert.Equal(7, p.Value.EveryDays);
        Assert.Equal(Start.AddDays(28).AddHours(3), p.Value.Next);
    }

    [Fact]
    public void Predict_ChaptersFoundTheSameDayAreOneRelease()
    {
        DateTime[] times = [Start, Start.AddHours(1), Start.AddDays(7), Start.AddDays(7).AddHours(2)];
        Assert.Null(CalendarController.Predict(times)); // only 2 releases
    }

    [Fact]
    public void Predict_RareSeries_NoEstimate()
    {
        DateTime[] times = [Start, Start.AddDays(200), Start.AddDays(400)];
        Assert.Null(CalendarController.Predict(times));
    }

    [Fact]
    public void Releases_SkipsChaptersDownloadedWhenTheSeriesWasAdded()
    {
        Dictionary<string, DateTime> first = new() { ["m"] = Start };
        List<(string, string, DateTime)> downloads =
        [
            ("m", "c1", Start), ("m", "c2", Start.AddHours(1)), // added
            ("m", "c3", Start.AddDays(7)),                       // a release
        ];
        var releases = CalendarController.Releases([], downloads, first);
        Assert.Equal("c3", Assert.Single(releases).ChapterId);
    }

    [Fact]
    public void Releases_SkipsBatches_AndPrefersTheReleaseLog()
    {
        Dictionary<string, DateTime> first = new() { ["m"] = Start };
        List<(string, string, DateTime)> downloads = Enumerable.Range(1, ReleaseLog.MaxPerCheck + 1)
            .Select(i => ("m", $"b{i}", Start.AddDays(10))).ToList();
        downloads.Add(("m", "c9", Start.AddDays(20)));
        ReleaseLog.Entry[] log = [new("m", "c9", Start.AddDays(19))];

        var releases = CalendarController.Releases(log, downloads, first);
        Assert.Equal(Start.AddDays(19), Assert.Single(releases).WhenUtc);
    }
}
