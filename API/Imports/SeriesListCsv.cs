using System.Globalization;
using System.Text;

namespace API.Imports;

/// <summary>
/// Reading and writing series lists as CSV. Export: one row per series per website. Import accepts that file, or a
/// simpler one: any CSV with a header naming some of the columns below (in any order, extra columns ignored), or no
/// header at all, just one link (or title) per line.
/// </summary>
public static class SeriesListCsv
{
    /// <summary>A series (on one website) to add</summary>
    /// <param name="Line">Line number in the file (1 = first line)</param>
    /// <param name="Name">Title (used to search when there's no link, and to group the rows of one series)</param>
    /// <param name="Url">Link to the series on a website</param>
    /// <param name="Website">Website name (searched when there's no link)</param>
    /// <param name="Library">Library name or folder (empty: the one chosen when importing)</param>
    /// <param name="StartFrom">Start from chapter (null: keep / every chapter)</param>
    /// <param name="Download">Download from this website (default yes)</param>
    /// <param name="Preferred">This website is the series' preferred one</param>
    /// <param name="Problem">Why the row can't be used (null if it can)</param>
    public record Row(int Line, string? Name, string? Url, string? Website, string? Library, float? StartFrom, bool Download, bool Preferred, string? Problem);

    /// <summary>Column names, as written by Export</summary>
    public static readonly string[] Columns =
    [
        "name", "website", "url", "downloading", "preferred_website", "library", "start_from_chapter",
        "release_status", "year", "chapters_downloaded", "latest_chapter"
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "name", ["title"] = "name", ["series"] = "name", ["manga"] = "name",
        ["url"] = "url", ["link"] = "url", ["website_url"] = "url", ["address"] = "url",
        ["website"] = "website", ["site"] = "website", ["source"] = "website", ["connector"] = "website",
        ["library"] = "library", ["download_folder"] = "library",
        ["start_from_chapter"] = "start", ["start_from"] = "start", ["start"] = "start", ["ignore_chapters_before"] = "start",
        ["downloading"] = "download", ["download"] = "download",
        ["preferred_website"] = "preferred", ["preferred"] = "preferred"
    };

    /// <summary>Splits CSV text into rows of cells (quotes, doubled quotes and line breaks inside quotes; comma, semicolon or tab)</summary>
    internal static List<(int Line, List<string> Cells)> Split(string text)
    {
        text = text.TrimStart('﻿');
        string firstLine = text.Split('\n')[0];
        char sep = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        if (!firstLine.Contains(sep))
            sep = ',';

        List<(int, List<string>)> rows = [];
        List<string> cells = [];
        StringBuilder cell = new();
        bool quoted = false;
        int line = 1, rowStart = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else
                {
                    if (c == '\n') line++;
                    cell.Append(c);
                }
                continue;
            }
            if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == sep) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                cells.Add(cell.ToString()); cell.Clear();
                rows.Add((rowStart, cells));
                cells = [];
                line++;
                rowStart = line;
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            rows.Add((rowStart, cells));
        }
        // Drop empty lines
        return rows.Where(r => r.Item2.Any(x => x.Trim().Length > 0)).Select(r => (r.Item1, r.Item2.Select(x => x.Trim()).ToList())).ToList();
    }

    internal static bool? ReadBool(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" or "on" or "x" or "✓" => true,
        "no" or "n" or "false" or "0" or "off" => false,
        _ => null
    };

    private static bool LooksLikeUrl(string s) =>
        Uri.TryCreate(s.Trim(), UriKind.Absolute, out Uri? u) && u.Scheme is "http" or "https";

    /// <summary>Reads a series list (see the class summary for what's accepted)</summary>
    public static List<Row> Parse(string text)
    {
        List<(int Line, List<string> Cells)> rows = Split(text);
        if (rows.Count == 0)
            return [];

        // Header: the first row, if any of its cells is a known column name
        Dictionary<string, int> col = new();
        List<string> first = rows[0].Cells;
        for (int i = 0; i < first.Count; i++)
            if (Aliases.TryGetValue(first[i].Trim().Replace(' ', '_'), out string? key) && !col.ContainsKey(key))
                col[key] = i;
        IEnumerable<(int Line, List<string> Cells)> data = rows;
        if (col.Count > 0)
            data = rows.Skip(1);
        else
            // No header: one link or title per line (first column)
            col["first"] = 0;

        string? Get(List<string> cells, string key) =>
            col.TryGetValue(key, out int i) && i < cells.Count && cells[i].Trim().Length > 0 ? cells[i].Trim() : null;

        List<Row> result = [];
        foreach ((int line, List<string> cells) in data)
        {
            string? name = Get(cells, "name"), url = Get(cells, "url");
            if (col.ContainsKey("first") && Get(cells, "first") is { } only)
            {
                if (LooksLikeUrl(only)) url = only;
                else name = only;
            }
            // A link put in the name column (or the other way round) still works
            if (url is null && name is not null && LooksLikeUrl(name)) (url, name) = (name, null);
            if (url is not null && !LooksLikeUrl(url))
            {
                result.Add(new Row(line, name, url, null, null, null, true, false, $"\"{url}\" isn't a web link"));
                continue;
            }

            float? start = null;
            string? problem = null;
            if (Get(cells, "start") is { } s)
            {
                if (float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f >= 0)
                    start = f;
                else
                    problem = $"\"{s}\" isn't a chapter number";
            }
            if (url is null && name is null)
                problem ??= "No link or title";

            result.Add(new Row(line, name, url, Get(cells, "website"), Get(cells, "library"), start,
                ReadBool(Get(cells, "download")) ?? true, ReadBool(Get(cells, "preferred")) ?? false, problem));
        }
        return result;
    }

    private static string Quote(string? s)
    {
        s ??= "";
        // Formulas aren't run when the file is opened in a spreadsheet
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        bool needsQuotes = s.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0 || s != s.Trim();
        return needsQuotes ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>CSV text (with a byte-order mark, so spreadsheet apps read accents and non-Latin titles correctly)</summary>
    public static string Write(IEnumerable<IReadOnlyList<string?>> rows)
    {
        StringBuilder sb = new("﻿");
        sb.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (IReadOnlyList<string?> r in rows)
            sb.Append(string.Join(',', r.Select(Quote))).Append("\r\n");
        return sb.ToString();
    }
}
