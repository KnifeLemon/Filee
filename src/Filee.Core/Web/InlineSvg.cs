using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Core.Web;

/// <summary>A complete standalone SVG selection, never HTML extraction or entity decoding.</summary>
public static partial class InlineSvg
{
    public const int MaxCharacters = 4 * 1024 * 1024;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    public static byte[]? Read(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > MaxCharacters)
            return null;
        source = source.Trim();
        if (!source.StartsWith("<svg", StringComparison.Ordinal))
            return null;
        try
        {
            using var input = new StringReader(source);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxCharacters,
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (document.Root is not { } root || root.IsEmpty || root.Name.LocalName != "svg"
                || document.Nodes().Any(node => node != root && (node is not XText text || !string.IsNullOrWhiteSpace(text.Value)))
                || (root.Name.NamespaceName.Length != 0 && root.Name.NamespaceName != SvgNamespace))
                return null;
            // HTML selections may omit xmlns; standalone converters need the SVG namespace.
            if (root.Name.NamespaceName.Length == 0)
            {
                foreach (var element in root.DescendantsAndSelf().Where(e => e.Name.NamespaceName.Length == 0))
                    element.Name = XName.Get(element.Name.LocalName, SvgNamespace);
                root.SetAttributeValue("xmlns", SvgNamespace);
            }
            return Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Selections have no base folder or page stylesheet; missing resources become job errors.</summary>
    public static void EnsureSelfContained(byte[] svg)
    {
        var root = XElement.Parse(Encoding.UTF8.GetString(svg));
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName == "href" && element.Name.LocalName != "a"
                    || attribute.Name == XNamespace.Xml + "base")
                    CheckReference(attribute.Value);
                CheckCss(attribute.Value);
            }
            if (element.Name.LocalName == "style")
                CheckCss(element.Value);
        }
        if (root.DescendantNodes().OfType<XProcessingInstruction>().Any(p => p.Target == "xml-stylesheet"))
            throw new InvalidDataException("The selected SVG requires an external stylesheet. Include its styles in the SVG.");
    }

    private static void CheckReference(string reference)
    {
        reference = reference.Trim();
        if (reference.Length == 0 || reference.StartsWith('#') || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;
        throw new InvalidDataException("The selected SVG contains an unresolved external image, font or stylesheet. Embed the referenced resource in the SVG.");
    }

    private static void CheckCss(string css)
    {
        try
        {
            css = CssEscapes().Replace(css, match => match.Groups[1].Success
                ? new Rune(Convert.ToInt32(match.Groups[1].Value, 16)).ToString()
                : match.Groups[2].Value);
            css = CssComments().Replace(css, "");
            if (css.Contains("@import", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected SVG requires an external stylesheet. Include its styles in the SVG.");
            foreach (Match match in CssUrls().Matches(css))
                CheckReference(match.Groups[1].Value.Trim().Trim('\'', '"'));
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The selected SVG's resource references could not be checked.", ex);
        }
    }

    [GeneratedRegex(@"\\([0-9a-fA-F]{1,6})\s?|\\([^\r\n])", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CssEscapes();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CssComments();
    [GeneratedRegex(@"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CssUrls();
}
