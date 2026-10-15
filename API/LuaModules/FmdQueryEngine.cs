using System.Globalization;
using System.Text;
using System.Xml.Linq;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wmhelp.XPath2;

namespace API.LuaModules;

/// <summary>A parsed document (FMD2's CreateTXQuery / ParseHTML)</summary>
internal sealed class QueryDocument(string source)
{
    public string Source { get; } = source;
    private XDocument? _xml;
    public XDocument Xml => _xml ??= HtmlToXml.Convert(Source);
}

/// <summary>
/// Evaluates the queries FMD2 modules run on pages: XPath 2.0 on HTML (via XPath2.Net), plus FMD2's JSON syntax
/// (json(*).data().title, sources()[1].images(), …). Results are kept as numbered items so Lua can refer to them.
/// One instance per module run (not thread-safe).
/// </summary>
internal sealed class FmdQueryEngine
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FmdQueryEngine));

    private readonly List<QueryDocument> _documents = [];
    private readonly List<object> _items = [];
    private readonly Dictionary<string, XPath2Expression?> _compiled = new();
    private const int MaxItems = 500_000;

    public int NewDocument(string text)
    {
        _documents.Add(new QueryDocument(text ?? ""));
        return _documents.Count;
    }

    private QueryDocument? Document(int id) => id >= 1 && id <= _documents.Count ? _documents[id - 1] : null;
    public object? Item(int id) => id >= 1 && id <= _items.Count ? _items[id - 1] : null;

    private int Register(object item)
    {
        if (_items.Count >= MaxItems)
            throw new InvalidOperationException("Too many query results");
        _items.Add(item);
        return _items.Count;
    }

    /// <summary>Runs a query; returns the ids of the results ("3,4,5")</summary>
    public string Query(int documentId, int contextId, string expression)
    {
        QueryDocument doc = Document(documentId) ?? new QueryDocument("");
        object? context = contextId > 0 ? Item(contextId) : null;
        List<object> results;
        try
        {
            results = Evaluate(doc, context, expression ?? "");
        }
        catch (Exception e)
        {
            Log.Debug($"Query failed: {expression} — {e.Message}");
            return "";
        }
        StringBuilder sb = new();
        foreach (object r in results)
        {
            if (sb.Length > 0)
                sb.Append(',');
            sb.Append(Register(r));
        }
        return sb.ToString();
    }

    internal List<object> Evaluate(QueryDocument doc, object? context, string expression)
    {
        string expr = expression.Trim();
        if (expr.Length == 0)
            return [];
        if (context is JToken json)
            return JsonQuery.EvaluateInContext(expr, json, this, doc);
        if (JsonQuery.TryEvaluate(expr, doc, context, this) is { } jsonResults)
            return jsonResults;
        return XPath(doc, context, expr);
    }

    private List<object> XPath(QueryDocument doc, object? context, string expr)
    {
        if (!_compiled.TryGetValue(expr, out XPath2Expression? compiled))
        {
            try
            {
                compiled = XPath2Expression.Compile(expr);
            }
            catch (Exception e)
            {
                Log.Debug($"Unsupported XPath: {expr} — {e.Message}");
                compiled = null;
            }
            _compiled[expr] = compiled;
        }
        if (compiled is null)
            return [];

        XNode node = context switch
        {
            XNode n => n,
            XAttribute a when a.Parent is not null => a.Parent,
            _ => doc.Xml
        };
        List<object> results = [];
        foreach (object? o in node.XPath2Select(compiled, null!))
        {
            if (o is null)
                continue;
            results.Add(o is XObject ? o : FormatAtomic(o));
        }
        return results;
    }

    /// <summary>Strings of the results of a query (used inside json(…) arguments)</summary>
    internal IEnumerable<string> EvaluateStrings(QueryDocument doc, object? context, string expression) =>
        Evaluate(doc, context, expression).Select(StringOf);

    internal static string FormatAtomic(object o) => o switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        double d => double.IsNaN(d) ? "NaN" : d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>String value of a result, like Xidel's toString</summary>
    internal static string StringOf(object? item) => item switch
    {
        null => "",
        string s => s,
        XElement e => e.Value,
        XAttribute a => a.Value,
        XText t => t.Value,
        XDocument d => d.Root?.Value ?? "",
        JToken j => JsonQuery.StringOf(j),
        _ => FormatAtomic(item)
    };

    public string ItemString(int id) => StringOf(Item(id));

    public string ItemAttribute(int id, string name)
    {
        switch (Item(id))
        {
            case XElement e:
                string? safe = HtmlToXml.SafeName(name);
                return safe is null ? "" : e.Attribute(safe)?.Value ?? "";
            case XAttribute a:
                return a.Name.LocalName == HtmlToXml.SafeName(name) ? a.Value : "";
            case JObject o:
                return o[name] is { } v ? JsonQuery.StringOf(v) : "";
            default:
                return "";
        }
    }

    /// <summary>JSON property of a result (FMD2's GetProperty); 0 if there is none</summary>
    public int ItemProperty(int id, string name)
    {
        object? item = Item(id);
        if (item is JObject o && o[name] is { } v)
            return Register(v);
        if (item is XElement e && HtmlToXml.SafeName(name) is { } safe && e.Attribute(safe) is { } attr)
            return Register(attr);
        return 0;
    }

    /// <summary>0 = inner HTML, 1 = outer HTML, 2 = inner text</summary>
    public string ItemHtml(int id, int mode)
    {
        object? item = Item(id);
        if (item is not XElement e)
            return StringOf(item);
        return mode switch
        {
            1 => e.ToString(SaveOptions.DisableFormatting),
            2 => e.Value,
            _ => string.Concat(e.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)))
        };
    }
}

/// <summary>
/// FMD2's JSON query syntax (Xidel/JSONiq style), the subset modules use:
/// json(*) / json(xpath) / parse-json(.), then .name, /name, ("name"), (), (n), [n], [last()], [condition], ?name, ?*,
/// /* and trailing /string() or /normalize-space(.). Also string-join(…, sep) and count(…) around them.
/// </summary>
internal static class JsonQuery
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(JsonQuery));

    /// <summary>Null if the expression isn't a JSON query</summary>
    public static List<object>? TryEvaluate(string expr, QueryDocument doc, object? context, FmdQueryEngine engine)
    {
        // Wrappers around JSON queries: string-join(json(*).a(), ", "), count(json(*).items())
        foreach (string fn in Wrappers)
        {
            if (!expr.StartsWith(fn, StringComparison.Ordinal) || MatchingClose(expr, fn.Length - 1) != expr.Length - 1)
                continue;
            List<string> args = SplitTopLevel(expr[fn.Length..^1], ',');
            if (args.Count == 0 || TryEvaluate(args[0], doc, context, engine) is not { } items)
                continue;
            return Wrap(fn, items, args.Count > 1 ? Unquote(args[1].Trim()) : "");
        }
        // Sequence of JSON queries: (json(*).a, json(*).b)
        if (expr.Length > 0 && expr[0] == '(' && MatchingClose(expr, 0) == expr.Length - 1)
        {
            List<string> parts = SplitTopLevel(expr[1..^1], ',');
            if (parts.Count > 0 && parts.All(StartsWithJson))
                return parts.SelectMany(p => TryEvaluate(p, doc, context, engine) ?? []).ToList();
        }

        if (!StartsWithJson(expr))
            return null;
        int open = expr.IndexOf('(');
        int end = MatchingClose(expr, open);
        if (end < 0)
            return [];
        string argument = expr[(open + 1)..end].Trim();
        string rest = expr[(end + 1)..];

        List<JToken> roots = [];
        if (argument is "*" or "." or "" or "/" or "//*")
        {
            // The document as JSON: its source, or (if a module HTML-encoded it first) its decoded text
            if (Parse(ContextText(doc, context)) is { } token)
                roots.Add(token);
            else if (context is null && Parse(doc.Xml.Root?.Value ?? "") is { } decoded)
                roots.Add(decoded);
        }
        else
        {
            foreach (string text in engine.EvaluateStrings(doc, context, argument))
                if (Parse(text) is { } token)
                    roots.Add(token);
        }
        return Apply(rest, roots, engine, doc).ToList();
    }

    private static readonly string[] Wrappers = ["string-join(", "count(", "string(", "normalize-space(", "distinct-values("];

    /// <summary>
    /// A query relative to a JSON value: "title", "authors?*?title", "string-join(altTitles?*, ', ')",
    /// "(genres?*?title, themes?*?title)"
    /// </summary>
    internal static List<object> EvaluateInContext(string expr, JToken context, FmdQueryEngine engine, QueryDocument doc)
    {
        expr = expr.Trim();
        if (expr.Length == 0)
            return [];
        // Sequence: (a, b, c)
        if (expr[0] == '(' && MatchingClose(expr, 0) == expr.Length - 1)
        {
            List<object> all = [];
            foreach (string part in SplitTopLevel(expr[1..^1], ','))
                all.AddRange(EvaluateInContext(part, context, engine, doc));
            return all;
        }
        foreach (string fn in Wrappers)
        {
            if (!expr.StartsWith(fn, StringComparison.Ordinal) || MatchingClose(expr, fn.Length - 1) != expr.Length - 1)
                continue;
            List<string> args = SplitTopLevel(expr[fn.Length..^1], ',');
            List<object> items = args.Count > 0 ? EvaluateInContext(args[0], context, engine, doc) : [];
            string separator = args.Count > 1 ? Unquote(args[1].Trim()) : "";
            return Wrap(fn, items, separator);
        }
        // Document-level JSON inside a JSON context ("json(*).x") still works
        if (StartsWithJson(expr) && TryEvaluate(expr, doc, null, engine) is { } fromDocument)
            return fromDocument;
        return Apply(expr, [context], engine, doc).ToList();
    }

    private static List<object> Wrap(string fn, List<object> items, string separator) => fn switch
    {
        "string-join(" => [string.Join(separator, items.Select(FmdQueryEngine.StringOf))],
        "count(" => [items.Count.ToString(CultureInfo.InvariantCulture)],
        "normalize-space(" => [NormalizeSpace(items.Count > 0 ? FmdQueryEngine.StringOf(items[0]) : "")],
        "distinct-values(" => items.Select(FmdQueryEngine.StringOf).Distinct().Cast<object>().ToList(),
        _ => [items.Count > 0 ? FmdQueryEngine.StringOf(items[0]) : ""]
    };

    private static List<string> SplitTopLevel(string s, char separator)
    {
        List<string> parts = [];
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
                quote = ch;
            else if (ch is '(' or '[' or '{')
                depth++;
            else if (ch is ')' or ']' or '}')
                depth--;
            else if (ch == separator && depth == 0)
            {
                parts.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(s[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static bool StartsWithJson(string expr) =>
        expr.StartsWith("json(", StringComparison.Ordinal) || expr.StartsWith("parse-json(", StringComparison.Ordinal);

    private static string ContextText(QueryDocument doc, object? context) =>
        context is null ? doc.Source : FmdQueryEngine.StringOf(context);

    internal static JToken? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        try
        {
            using JsonTextReader reader = new(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            return JToken.ReadFrom(reader);
        }
        catch (Exception e)
        {
            Log.Debug($"Not JSON: {e.Message}");
            return null;
        }
    }

    internal static string StringOf(JToken token) => token switch
    {
        JValue { Type: JTokenType.Null or JTokenType.Undefined } => "",
        JValue { Value: bool b } => b ? "true" : "false",
        JValue { Value: double d } => d.ToString("R", CultureInfo.InvariantCulture),
        JValue v => System.Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "",
        _ => token.ToString(Formatting.None)
    };

    /// <summary>Applies a chain of steps (".data().title") to a sequence of JSON values</summary>
    internal static IEnumerable<object> Apply(string steps, List<JToken> current, FmdQueryEngine engine, QueryDocument doc)
    {
        List<object> seq = current.Cast<object>().ToList();
        int i = 0;
        bool first = true;
        while (i < steps.Length)
        {
            char c = steps[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (first && (char.IsLetter(c) || c == '_' || c == '@' || c == '$'))
            {
                // Relative name with a JSON context item: "title", "data/title", "attributes.title"
                string name = ReadName(steps, ref i);
                seq = Member(seq, name.TrimStart('@'));
            }
            else if (c == '.' && i + 1 < steps.Length && steps[i + 1] == '/')
            {
                i += 2; // "./" = from here
            }
            else if (c == '.' || c == '/' || c == '!')
            {
                i++;
                if (i < steps.Length && steps[i] == '/')
                    return []; // descendant search isn't supported
                if (i >= steps.Length)
                    break;
                if (steps[i] == '*')
                {
                    i++;
                    seq = AllValues(seq);
                    continue;
                }
                if (steps[i] == '(')
                {
                    // .("key")
                    int close = MatchingClose(steps, i);
                    if (close < 0)
                        return [];
                    seq = Member(seq, Unquote(steps[(i + 1)..close].Trim()));
                    i = close + 1;
                    continue;
                }
                string name = ReadName(steps, ref i);
                if (name.Length == 0)
                    return [];
                if (i < steps.Length && steps[i] == '(' && c != '.')
                {
                    // /string(), /normalize-space(.), !string() …: turn into strings
                    int close = MatchingClose(steps, i);
                    if (close < 0)
                        return [];
                    seq = ApplyFunction(name, seq);
                    i = close + 1;
                    continue;
                }
                seq = Member(seq, name.TrimStart('@'));
            }
            else if (c == '(')
            {
                int close = MatchingClose(steps, i);
                if (close < 0)
                    return [];
                string inside = steps[(i + 1)..close].Trim();
                i = close + 1;
                if (inside.Length == 0)
                    seq = Unbox(seq);
                else if (int.TryParse(inside, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    seq = Index(seq, n);
                else
                    seq = Member(seq, Unquote(inside));
            }
            else if (c == '[')
            {
                int close = MatchingClose(steps, i);
                if (close < 0)
                    return [];
                string inside = steps[(i + 1)..close].Trim();
                i = close + 1;
                if (int.TryParse(inside, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    seq = n >= 1 && n <= seq.Count ? [seq[n - 1]] : [];
                else if (inside == "last()")
                    seq = seq.Count > 0 ? [seq[^1]] : [];
                else
                    seq = seq.Where(item => Predicate(inside, item, engine, doc)).ToList();
            }
            else if (c == '?')
            {
                i++;
                if (i < steps.Length && steps[i] == '*')
                {
                    i++;
                    seq = AllValues(seq);
                    continue;
                }
                int start = i;
                while (i < steps.Length && char.IsDigit(steps[i]))
                    i++;
                if (i > start)
                {
                    seq = Index(seq, int.Parse(steps[start..i], CultureInfo.InvariantCulture));
                    continue;
                }
                string name = ReadName(steps, ref i);
                if (name.Length == 0)
                    return [];
                seq = Member(seq, name);
            }
            else
            {
                Log.Debug($"Unsupported JSON query step at '{steps[i..]}'");
                return [];
            }
            first = false;
        }
        return seq;
    }

    private static string ReadName(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-' or '$' or '@' or ':'))
            i++;
        return s[start..i];
    }

    private static List<object> Member(List<object> seq, string name)
    {
        List<object> result = [];
        foreach (object item in seq)
        {
            switch (item)
            {
                case JObject o when o[name] is { } v:
                    result.Add(v);
                    break;
                case JArray a:
                    // Lenient: look into the array's objects
                    foreach (JToken t in a)
                        if (t is JObject ao && ao[name] is { } av)
                            result.Add(av);
                    break;
            }
        }
        return result;
    }

    private static List<object> Unbox(List<object> seq)
    {
        List<object> result = [];
        foreach (object item in seq)
        {
            if (item is JArray a)
                result.AddRange(a);
            else
                result.Add(item);
        }
        return result;
    }

    private static List<object> AllValues(List<object> seq)
    {
        List<object> result = [];
        foreach (object item in seq)
        {
            if (item is JArray a)
                result.AddRange(a);
            else if (item is JObject o)
                result.AddRange(o.Properties().Select(p => p.Value));
        }
        return result;
    }

    private static List<object> Index(List<object> seq, int n)
    {
        List<object> result = [];
        foreach (object item in seq)
            if (item is JArray a && n >= 1 && n <= a.Count)
                result.Add(a[n - 1]);
        return result;
    }

    private static List<object> ApplyFunction(string name, List<object> seq) => name switch
    {
        "normalize-space" => seq.Select(x => (object)NormalizeSpace(FmdQueryEngine.StringOf(x))).ToList(),
        "lower-case" => seq.Select(x => (object)FmdQueryEngine.StringOf(x).ToLowerInvariant()).ToList(),
        "upper-case" => seq.Select(x => (object)FmdQueryEngine.StringOf(x).ToUpperInvariant()).ToList(),
        _ => seq.Select(x => (object)FmdQueryEngine.StringOf(x)).ToList() // string(), text(), data(), number()…
    };

    internal static string NormalizeSpace(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Condition inside [ ]: "a.b = 'x'", "type != 'chapter'", "not(x)", "a and b", "a or b", "contains(name, 'x')", or a path (true if it has a value)
    /// </summary>
    private static bool Predicate(string condition, object item, FmdQueryEngine engine, QueryDocument doc)
    {
        condition = condition.Trim();
        if (TrySplitTopLevel(condition, " or ", out string left, out string right))
            return Predicate(left, item, engine, doc) || Predicate(right, item, engine, doc);
        if (TrySplitTopLevel(condition, " and ", out left, out right))
            return Predicate(left, item, engine, doc) && Predicate(right, item, engine, doc);
        if (condition.StartsWith("not(", StringComparison.Ordinal) && MatchingClose(condition, 3) == condition.Length - 1)
            return !Predicate(condition[4..^1], item, engine, doc);
        if (condition.StartsWith("contains(", StringComparison.Ordinal) && MatchingClose(condition, 8) == condition.Length - 1)
        {
            (string a, string? b) = SplitTopLevelComma(condition[9..^1]);
            return Operand(a, item, engine, doc).Any(v => v.Contains(Operand(b ?? "", item, engine, doc).FirstOrDefault() ?? "", StringComparison.Ordinal));
        }
        foreach (string op in new[] { "!=", ">=", "<=", "=", ">", "<", " eq ", " ne " })
        {
            if (!TrySplitTopLevel(condition, op, out left, out right))
                continue;
            List<string> l = Operand(left, item, engine, doc);
            List<string> r = Operand(right, item, engine, doc);
            return op.Trim() switch
            {
                "=" or "eq" => l.Any(a => r.Any(b => Equal(a, b))),
                "!=" or "ne" => l.Any(a => r.Any(b => !Equal(a, b))),
                _ => l.Any(a => r.Any(b => CompareNumbers(a, b, op.Trim())))
            };
        }
        List<object> values = Apply(condition, [item is JToken t ? t : JValue.CreateNull()], engine, doc).ToList();
        if (values.Count == 0)
            return false;
        if (values.Count == 1 && values[0] is JValue jv)
            return jv.Type switch
            {
                JTokenType.Boolean => (bool)jv.Value!,
                JTokenType.Null => false,
                JTokenType.String => ((string?)jv.Value ?? "") != "",
                JTokenType.Integer or JTokenType.Float => System.Convert.ToDouble(jv.Value, CultureInfo.InvariantCulture) != 0,
                _ => true
            };
        return true;
    }

    private static List<string> Operand(string text, object item, FmdQueryEngine engine, QueryDocument doc)
    {
        text = text.Trim();
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
            return [text[1..^1]];
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return [text];
        if (text is "true()" or "false()")
            return [text[..^2]];
        return Apply(text, [item is JToken t ? t : JValue.CreateNull()], engine, doc).Select(FmdQueryEngine.StringOf).ToList();
    }

    private static bool Equal(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return x.Equals(y);
        return a == b;
    }

    private static bool CompareNumbers(string a, string b, string op)
    {
        if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return false;
        return op switch { ">" => x > y, "<" => x < y, ">=" => x >= y, "<=" => x <= y, _ => false };
    }

    /// <summary>Index of the bracket closing the one at <paramref name="open"/> (quotes respected), or -1</summary>
    internal static int MatchingClose(string s, int open)
    {
        if (open < 0 || open >= s.Length)
            return -1;
        char o = s[open];
        char c = o switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
        if (c == '\0')
            return -1;
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < s.Length; i++)
        {
            char ch = s[i];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
                quote = ch;
            else if (ch == '(' || ch == '[' || ch == '{')
                depth++;
            else if (ch == ')' || ch == ']' || ch == '}')
            {
                depth--;
                if (depth == 0)
                    return ch == c ? i : -1;
            }
        }
        return -1;
    }

    private static bool TrySplitTopLevel(string s, string separator, out string left, out string right)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i <= s.Length - separator.Length; i++)
        {
            char ch = s[i];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }
            if (ch is '(' or '[' or '{')
                depth++;
            else if (ch is ')' or ']' or '}')
                depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, separator, 0, separator.Length) == 0)
            {
                // Don't split "!=" / ">=" / "<=" as "="
                if (separator == "=" && i > 0 && s[i - 1] is '!' or '<' or '>')
                    continue;
                if (separator is ">" or "<" && i + 1 < s.Length && s[i + 1] == '=')
                    continue;
                left = s[..i];
                right = s[(i + separator.Length)..];
                return true;
            }
        }
        left = right = "";
        return false;
    }

    private static (string first, string? second) SplitTopLevelComma(string s) =>
        TrySplitTopLevel(s, ",", out string a, out string b) ? (a, b) : (s, null);

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
            return s[1..^1];
        return s;
    }
}
