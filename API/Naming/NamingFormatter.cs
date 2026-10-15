using System.Text;
using System.Text.RegularExpressions;
using API.Schema.MangaContext;
using log4net;
using Newtonsoft.Json;

namespace API.Naming;

/// <summary>
/// Formats file/folder names from a scheme, the same way Tranga formats chapter archive names:
/// "%X" inserts a value, "?X(...)" keeps the bracketed text only if %X has a value; the result is cleaned with CleanNameForWindows.
/// </summary>
public static class NamingFormatter
{
    private static readonly Regex NullableRex = new(@"\?([a-zA-Z])\(([^\)]*)\)|(.+?)");
    private static readonly Regex ReplaceRexx = new(@"%([a-zA-Z])|(.+?)");

    private static string Format(string scheme, Func<char, bool> isNull, Func<char, string?> value)
    {
        StringBuilder sb = new();
        foreach (Match nullable in NullableRex.Matches(scheme))
        {
            if (nullable.Groups[3].Success)
            {
                sb.Append(nullable.Groups[3].Value);
                continue;
            }
            if (!isNull(nullable.Groups[1].Value[0]))
                sb.Append(nullable.Groups[2].Value);
        }
        string checkedString = sb.ToString();
        sb = new StringBuilder();
        foreach (Match replace in ReplaceRexx.Matches(checkedString))
        {
            if (replace.Groups[2].Success)
            {
                sb.Append(replace.Groups[2].Value);
                continue;
            }
            sb.Append(value(replace.Groups[1].Value[0]));
        }
        return sb.ToString();
    }

    /// <summary>Chapter archive name for <paramref name="scheme"/> (mirrors Chapter.GetArchiveFileName)</summary>
    public static string ChapterFileName(string scheme, Chapter chapter, Manga manga)
    {
        string? Value(char p) => p switch
        {
            'M' => manga.Name,
            'V' => chapter.VolumeNumber?.ToString() ?? (Constants.ZeroVolumeInFilenameIfNull ? "0" : null),
            'C' => chapter.ChapterNumber,
            'T' => chapter.Title,
            'A' => manga.Authors?.FirstOrDefault()?.AuthorName,
            'Y' => manga.Year?.ToString(),
            _ => null
        };
        bool IsNull(char p) => p switch
        {
            'V' => chapter.VolumeNumber is null,
            _ => Value(p) is null
        };
        return (Format(scheme, IsNull, Value) + ".cbz").CleanNameForWindows();
    }

    /// <summary>English title from the alternative titles, if any (differs from the main name)</summary>
    public static string? EnglishTitle(Manga manga) =>
        manga.AltTitles?
            .Where(t => t.Language.Equals("en", StringComparison.InvariantCultureIgnoreCase)
                        || t.Language.StartsWith("en-", StringComparison.InvariantCultureIgnoreCase)
                        || t.Language.Equals("English", StringComparison.InvariantCultureIgnoreCase))
            .Select(t => t.Title.Trim())
            .FirstOrDefault(t => t.Length > 0 && !t.Equals(manga.Name, StringComparison.InvariantCultureIgnoreCase));

    /// <summary>
    /// Series folder name for <paramref name="scheme"/>. Placeholders: %M name, %E English title (falls back to the name),
    /// %Y year, %A first author. Falls back to the plain name if the result would be empty.
    /// </summary>
    public static string SeriesFolderName(string scheme, Manga manga)
    {
        string? Value(char p) => p switch
        {
            'M' => manga.Name,
            'E' => EnglishTitle(manga) ?? manga.Name,
            'Y' => manga.Year?.ToString(),
            'A' => manga.Authors?.FirstOrDefault()?.AuthorName,
            _ => null
        };
        bool IsNull(char p) => p switch
        {
            'E' => false,
            _ => Value(p) is null
        };
        string name = Format(scheme, IsNull, Value).CleanNameForWindows().Trim().TrimEnd('.');
        return name.Length > 0 ? name : manga.Name.CleanNameForWindows();
    }
}

/// <summary>
/// Series folder naming setting, kept in its own file (naming.json next to settings.json).
/// </summary>
public static class SeriesNamingSettings
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SeriesNamingSettings));
    private static string FilePath => Path.Join(TrangaSettings.WorkingDirectory, "naming.json");
    public const string DefaultScheme = "%M";

    private class Stored
    {
        public string SeriesFolderScheme { get; set; } = DefaultScheme;
    }

    private static readonly Lock FileLock = new();
    private static string? _scheme;

    public static string SeriesFolderScheme
    {
        get
        {
            if (_scheme is not null)
                return _scheme;
            lock (FileLock)
            {
                try
                {
                    _scheme = File.Exists(FilePath)
                        ? JsonConvert.DeserializeObject<Stored>(File.ReadAllText(FilePath))?.SeriesFolderScheme ?? DefaultScheme
                        : DefaultScheme;
                }
                catch (Exception e)
                {
                    Log.Error("Could not read naming.json, using default.", e);
                    _scheme = DefaultScheme;
                }
                return _scheme;
            }
        }
        set
        {
            lock (FileLock)
            {
                _scheme = string.IsNullOrWhiteSpace(value) ? DefaultScheme : value;
                Directory.CreateDirectory(TrangaSettings.WorkingDirectory);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(new Stored { SeriesFolderScheme = _scheme }, Formatting.Indented));
            }
        }
    }
}
