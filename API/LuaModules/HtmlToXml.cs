using System.Text;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace API.LuaModules;

/// <summary>
/// Turns (possibly messy) HTML into a well-formed XDocument, so XPath 2.0 can run on it the way FMD2's HTML engine does:
/// element and attribute names lower-cased, entities decoded, comments dropped, everything under a single html root.
/// </summary>
internal static class HtmlToXml
{
    public static XDocument Convert(string html)
    {
        HtmlDocument doc = new()
        {
            OptionFixNestedTags = true,
            OptionCheckSyntax = false
        };
        doc.LoadHtml(html ?? "");

        XElement root = new("html");
        HtmlNode[] topElements = doc.DocumentNode.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).ToArray();
        if (topElements.Length == 1 && topElements[0].Name.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            CopyAttributes(topElements[0], root);
            AddChildren(topElements[0], root, 0);
        }
        else
        {
            AddChildren(doc.DocumentNode, root, 0);
        }
        return new XDocument(root);
    }

    private const int MaxDepth = 400;

    private static void AddChildren(HtmlNode source, XElement target, int depth)
    {
        if (depth > MaxDepth)
            return;
        bool rawText = source.Name is "script" or "style" or "textarea";
        foreach (HtmlNode child in source.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Element:
                    string? name = SafeName(child.Name);
                    if (name is null)
                    {
                        // Unusable tag name: keep its content
                        AddChildren(child, target, depth + 1);
                        break;
                    }
                    XElement element = new(name);
                    CopyAttributes(child, element);
                    AddChildren(child, element, depth + 1);
                    target.Add(element);
                    break;
                case HtmlNodeType.Text:
                    string text = ((HtmlTextNode)child).Text ?? "";
                    if (!rawText)
                        text = HtmlEntity.DeEntitize(text) ?? "";
                    text = CleanText(text);
                    if (text.Length > 0)
                        target.Add(new XText(text));
                    break;
                // Comments and the doctype are dropped
            }
        }
    }

    private static void CopyAttributes(HtmlNode source, XElement target)
    {
        foreach (HtmlAttribute attribute in source.Attributes)
        {
            string? name = SafeName(attribute.Name);
            if (name is null || name == "xmlns" || name.StartsWith("xmlns", StringComparison.Ordinal))
                continue;
            if (target.Attribute(name) is not null)
                continue;
            string value = CleanText(HtmlEntity.DeEntitize(attribute.Value ?? "") ?? "");
            target.SetAttributeValue(name, value);
        }
    }

    /// <summary>Lower-case XML-safe name, or null if nothing usable is left</summary>
    internal static string? SafeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string name = raw.Trim().ToLowerInvariant();
        if (name.StartsWith('#') || name.StartsWith('!') || name.StartsWith('?'))
            return null;
        try
        {
            name = XmlConvert.EncodeLocalName(name) ?? "";
            if (name.Length == 0)
                return null;
            XmlConvert.VerifyNCName(name);
            return name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Removes characters that can't appear in XML</summary>
    internal static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        bool clean = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (XmlConvert.IsXmlChar(c))
                continue;
            if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
            {
                i++;
                continue;
            }
            clean = false;
            break;
        }
        if (clean)
            return text;

        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (XmlConvert.IsXmlChar(c))
                sb.Append(c);
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
            {
                sb.Append(c).Append(text[i + 1]);
                i++;
            }
        }
        return sb.ToString();
    }
}
