// XML from files Filee did not write: no DTDs, no resolver (so nothing outside the file is ever read and no entity
// expands into gigabytes), and limits on depth, element count and size. The tree is built here from the reader rather
// than by XDocument.Load, which takes time quadratic in the depth (a 1.4 MB file nested 100,000 deep kept it busy for
// 48 seconds; the reader alone needs a quarter of a second) and so must never see such a file. Shared by the
// geospatial formats (KML, GPX) and the document readers (HWPX, DOCX, OFD).

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Infrastructure;

/// <summary>What <see cref="SafeXml.Load(Stream, XmlLimits, Func{string, Exception}?)"/> accepts.</summary>
/// <param name="MaxDepth">Levels of elements (the root is level 1).</param>
/// <param name="MaxElements">Elements in the file.</param>
/// <param name="MaxCharacters">Characters in the file.</param>
/// <param name="PreserveWhitespace">Keep white space between elements (like <see cref="LoadOptions.PreserveWhitespace"/>).</param>
/// <param name="KeepNamespaceDeclarations">Keep xmlns attributes, so prefixes can be looked up as after XDocument.Load.</param>
/// <param name="CheckCharacters">Refuse characters XML 1.0 does not allow.</param>
/// <param name="ElementsBefore">Elements read before this file that count against <paramref name="MaxElements"/> (other parts of one package).</param>
internal readonly record struct XmlLimits(
    int MaxDepth,
    int MaxElements,
    long MaxCharacters,
    bool PreserveWhitespace = true,
    bool KeepNamespaceDeclarations = true,
    bool CheckCharacters = true,
    int ElementsBefore = 0);

/// <summary>Parses untrusted XML into an <see cref="XDocument"/> within <see cref="XmlLimits"/>.</summary>
internal static class SafeXml
{
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>
    /// The document in <paramref name="stream"/>. Comments and processing instructions are left out. Throws
    /// <see cref="XmlException"/> for XML that is not well formed or has a DTD.
    /// </summary>
    /// <param name="limitExceeded">Makes the exception for a limit that was passed from a message saying which
    /// (<see cref="InvalidDataException"/> when null).</param>
    public static XDocument Load(Stream stream, XmlLimits limits, Func<string, Exception>? limitExceeded = null) =>
        Load(stream, limits, limitExceeded, out _);

    /// <param name="elements">The number of elements read.</param>
    public static XDocument Load(Stream stream, XmlLimits limits, Func<string, Exception>? limitExceeded, out int elements)
    {
        limitExceeded ??= message => new InvalidDataException(message);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = limits.MaxCharacters,
            CheckCharacters = limits.CheckCharacters,
            IgnoreWhitespace = !limits.PreserveWhitespace,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(stream, settings);
        var document = new XDocument();
        XContainer current = document;
        var depth = 0;
        elements = 0;
        // Text the reader hands over in pieces (it splits text around a skipped comment or processing instruction) is
        // gathered here until the next element, end tag or CDATA, so text in a million pieces costs no more to join
        // than text in one.
        var text = new StringBuilder();
        try
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        AddText();
                        if (++depth > limits.MaxDepth)
                            throw limitExceeded($"The file nests elements more than {limits.MaxDepth} levels deep.");
                        if (++elements > limits.MaxElements - limits.ElementsBefore)
                        {
                            throw limitExceeded((limits.ElementsBefore > 0 ? "The package has more than " : "The file has more than ")
                                                + limits.MaxElements.ToString("N0", CultureInfo.InvariantCulture) + " elements.");
                        }
                        var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                        var empty = reader.IsEmptyElement;
                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                if (reader.NamespaceURI == XmlnsNamespace)
                                {
                                    if (limits.KeepNamespaceDeclarations)
                                        element.Add(new XAttribute(reader.Prefix.Length == 0 ? XName.Get("xmlns") : XNamespace.Xmlns + reader.LocalName, reader.Value));
                                }
                                else
                                {
                                    element.SetAttributeValue(XName.Get(reader.LocalName, reader.NamespaceURI), reader.Value);
                                }
                            }
                            reader.MoveToElement();
                        }
                        current.Add(element);
                        if (empty)
                            depth--;
                        else
                            current = element;
                        break;
                    case XmlNodeType.EndElement:
                        AddText();
                        current = current.Parent ?? (XContainer)document;
                        depth--;
                        break;
                    case XmlNodeType.CDATA:
                        AddText();
                        if (current is XElement)
                            current.Add(new XCData(reader.Value));
                        break;
                    case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        // Text outside the root element (white space between declarations) is no content. Text next
                        // to text becomes one node, as XDocument.Load makes it.
                        if (current is XElement)
                            text.Append(reader.Value);
                        break;
                }
            }
            AddText();
        }
        catch (XmlException ex) when (ex.Message.Contains("MaxCharactersInDocument", StringComparison.Ordinal))
        {
            throw limitExceeded("The file has more than " + limits.MaxCharacters.ToString("N0", CultureInfo.InvariantCulture) + " characters.");
        }
        return document;

        void AddText()
        {
            if (text.Length == 0)
                return;
            current.Add(new XText(text.ToString()));
            text.Clear();
        }
    }
}
