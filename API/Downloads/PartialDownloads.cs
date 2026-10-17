using log4net;

namespace API.Downloads;

/// <summary>
/// Pages of chapters still being downloaded, one file per page, in partial-downloads/&lt;chapter&gt; next to settings.json.
/// If a chapter fails or Tranga stops, the next attempt only fetches the pages that are missing. The folder is
/// deleted once the chapter's .cbz is written; folders untouched for 14 days are cleaned up.
/// </summary>
public static class PartialDownloads
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(PartialDownloads));
    private static string Root => Path.Join(TrangaSettings.WorkingDirectory, "partial-downloads");
    private static DateTime _lastCleanup = DateTime.MinValue;

    /// <summary>
    /// The page folder for a chapter. Pages kept from an earlier attempt are only reused if they came from the same
    /// website and the chapter had the same number of pages; otherwise the folder starts empty.
    /// </summary>
    public static string Prepare(string chapterKey, string website, int pageCount)
    {
        string dir = Path.Join(Root, chapterKey);
        string marker = Path.Join(dir, "chapter.txt");
        string expected = $"{website}|{pageCount}";
        if (Directory.Exists(dir) && (!File.Exists(marker) || File.ReadAllText(marker).Trim() != expected))
        {
            Log.Info($"Pages kept for chapter {chapterKey} don't match this attempt ({expected}); starting again");
            Directory.Delete(dir, true);
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(marker, expected);
        return dir;
    }

    public static string PagePath(string dir, int index) => Path.Join(dir, $"{index:D4}.img");

    public static bool HasPage(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    public static void Delete(string chapterKey)
    {
        try
        {
            string dir = Path.Join(Root, chapterKey);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't remove the page folder of chapter {chapterKey}: {e.Message}");
        }
    }

    /// <summary>Removes page folders untouched for 14 days (runs at most once an hour)</summary>
    public static void CleanupOld()
    {
        if (DateTime.UtcNow - _lastCleanup < TimeSpan.FromHours(1) || !Directory.Exists(Root))
            return;
        _lastCleanup = DateTime.UtcNow;
        foreach (string dir in Directory.EnumerateDirectories(Root))
        {
            try
            {
                DateTime last = Directory.EnumerateFiles(dir).Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir)).Max();
                if (DateTime.UtcNow - last > TimeSpan.FromDays(14))
                {
                    Directory.Delete(dir, true);
                    Log.Debug($"Removed old page folder {dir}");
                }
            }
            catch (Exception e)
            {
                Log.Debug($"Couldn't check {dir}: {e.Message}");
            }
        }
    }
}
