using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using API.Schema.MangaContext;
using log4net;
using Newtonsoft.Json;

namespace API.Imports;

/// <summary>
/// Importing series folders that are already in a library (from before Tranga, another downloader, or a lost database):
/// a folder is linked to a series, and its chapter files are recognised as downloaded (by the chapter number in the
/// file's ComicInfo.xml, or in its name) instead of being downloaded again. Folders waiting for their chapters to be
/// recognised are kept in library-import.json next to settings.json.
/// </summary>
public static class LibraryImport
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LibraryImport));
    private static readonly Lock StoreLock = new();
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "library-import.json");
    private static readonly string[] Extensions = [".cbz", ".zip", ".cbr", ".cb7", ".pdf", ".epub"];

    private class Pending
    {
        public string Folder { get; set; } = "";
        public DateTime SinceUtc { get; set; }
    }

    private static Dictionary<string, Pending>? _pending;

    private static Dictionary<string, Pending> Data
    {
        get
        {
            if (_pending is not null)
                return _pending;
            try
            {
                if (File.Exists(FilePath))
                    _pending = JsonConvert.DeserializeObject<Dictionary<string, Pending>>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't read {FilePath}: {e.Message}");
            }
            return _pending ??= new Dictionary<string, Pending>();
        }
    }

    private static void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(Data, Formatting.Indented));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't save {FilePath}: {e.Message}");
        }
    }

    /// <summary>Chapter files of a series folder are recognised the next times its chapters are checked (for a week)</summary>
    public static void MarkPending(string mangaId, string folder)
    {
        lock (StoreLock)
        {
            Data[mangaId] = new Pending { Folder = folder, SinceUtc = DateTime.UtcNow };
            foreach (string old in Data.Where(kv => DateTime.UtcNow - kv.Value.SinceUtc > TimeSpan.FromDays(7)).Select(kv => kv.Key).ToList())
                Data.Remove(old);
            Save();
        }
    }

    public static bool IsPending(string mangaId)
    {
        lock (StoreLock)
            return Data.TryGetValue(mangaId, out Pending? p) && DateTime.UtcNow - p.SinceUtc < TimeSpan.FromDays(7);
    }

    public static void Done(string mangaId)
    {
        lock (StoreLock)
            if (Data.Remove(mangaId))
                Save();
    }

    public static bool IsChapterFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>"7", "007", "10,5" and "10.50" → "7", "7", "10.5", "10.5" (like Tranga's chapter numbers)</summary>
    internal static string? Normalise(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return null;
        Match m = Regex.Match(number.Trim().Replace(',', '.'), @"^(\d+)(?:\.(\d+))?");
        if (!m.Success)
            return null;
        string whole = m.Groups[1].Value.TrimStart('0');
        if (whole.Length == 0)
            whole = "0";
        string part = m.Groups[2].Success ? m.Groups[2].Value.TrimEnd('0') : "";
        return part.Length > 0 ? $"{whole}.{part}" : whole;
    }

    private static readonly Regex ChapterMarker = new(
        @"(?:^|[\s_\-\[(.])(?:ch(?:apter|ap)?|c|ep(?:isode)?|#|第)\s*\.?\s*(\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VolumeMarker = new(@"(?:^|[\s_\-\[(.])(?:vol(?:ume)?|v)\s*\.?\s*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The chapter number in a file name: after "Ch."/"Chapter"/"c"/"#", otherwise the last number once the series name
    /// and volume are taken out ("Series 12.cbz"). Null for files that only name a volume.
    /// </summary>
    internal static string? NumberFromFileName(string fileName, string seriesName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(seriesName) && name.StartsWith(seriesName, StringComparison.OrdinalIgnoreCase))
            name = name[seriesName.Length..];
        Match marked = ChapterMarker.Match(name);
        if (marked.Success)
            return Normalise(marked.Groups[1].Value);
        string rest = VolumeMarker.Replace(name, " ");
        rest = Regex.Replace(rest, @"[\[(][^\])]*[\])]", " "); // [group], (2019), (Digital)
        MatchCollection numbers = Regex.Matches(rest, @"\d+(?:[.,]\d+)?");
        if (numbers.Count == 0)
            return null;
        return Normalise(numbers[^1].Value);
    }

    /// <summary>The chapter number in a .cbz's ComicInfo.xml</summary>
    internal static string? NumberFromComicInfo(string path)
    {
        try
        {
            if (!path.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return null;
            using ZipArchive zip = ZipFile.OpenRead(path);
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return null;
            using Stream s = entry.Open();
            XDocument doc = XDocument.Load(s);
            return Normalise(doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Number")?.Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Chapter files in a folder with the chapter number each one is (null if it can't tell)</summary>
    public static List<(string FileName, string? Number)> ChapterFiles(string folder, string seriesName, bool readComicInfo)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder)
            .Where(IsChapterFile)
            .Select(path => (Path.GetFileName(path),
                (readComicInfo ? NumberFromComicInfo(path) : null) ?? NumberFromFileName(Path.GetFileName(path), seriesName)))
            .ToList();
    }

    /// <summary>
    /// Marks chapters of a series whose files are already in its folder as downloaded (each file is used once).
    /// Returns how many were found.
    /// </summary>
    public static int MatchExisting(Manga manga, IEnumerable<Chapter> chapters)
    {
        if (manga.Library is null)
            return 0;
        string folder = Path.Join(manga.Library.BasePath, manga.DirectoryName);
        List<(string FileName, string? Number)> files = ChapterFiles(folder, manga.DirectoryName, true);
        HashSet<string> usedFiles = chapters.Where(c => c.FileName is not null).Select(c => c.FileName!).ToHashSet();
        Dictionary<string, string> byNumber = files
            .Where(f => f.Number is not null && !usedFiles.Contains(f.FileName))
            .GroupBy(f => f.Number!)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.FileName).First().FileName);
        int found = 0;
        foreach (Chapter chapter in chapters.Where(c => !c.Downloaded))
        {
            if (Normalise(chapter.ChapterNumber) is not { } number || !byNumber.Remove(number, out string? file))
                continue;
            chapter.FileName = file;
            chapter.Downloaded = true;
            found++;
        }
        if (found > 0)
            Log.Info($"Recognised {found} chapter files already in {folder} for {manga.Name}.");
        return found;
    }
}
