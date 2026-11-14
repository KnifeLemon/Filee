// Builds Visio drawings (.vsdx, [MS-VSDX]) in code for the Visio reader's tests and fuzz seeds: the document part with
// its colours, face names and style sheets, pages (with page sheets, background pages and layers), masters, and
// pictures, from XML strings of shapes. Nothing is read from disk.

using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class VsdxBuilder
{
    public const string Namespace = "http://schemas.microsoft.com/office/visio/2012/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string RelationshipTypes = "http://schemas.microsoft.com/visio/2010/relationships";

    private sealed record PageEntry(int Id, string Shapes, double Width, double Height, string PageSheet, bool Background, int? BackPage,
        List<(string Id, string Target, string Type)> Relationships);

    private readonly List<PageEntry> _pages = [];
    private readonly List<(int Id, string Shapes)> _masters = [];
    private readonly Dictionary<string, byte[]> _media = [];

    /// <summary>Style sheets (StyleSheet elements); the default is "No Style" (ID 0): black hairline-ish line, white fill, 12 pt Calibri.</summary>
    public string StyleSheets { get; set; } =
        "<StyleSheet ID='0' NameU='No Style' Name='No Style'>" +
        Cell("LineWeight", 0.01) + Cell("LineColor", "#000000") + Cell("LinePattern", 1) + Cell("FillForegnd", "#FFFFFF") +
        Cell("FillBkgnd", "#FFFFFF") + Cell("FillPattern", 1) + Cell("LeftMargin", 0) + Cell("RightMargin", 0) + Cell("TopMargin", 0) +
        Cell("BottomMargin", 0) + Cell("VerticalAlign", 1) +
        "<Section N='Character'><Row IX='0'>" + Cell("Font", "Calibri") + Cell("Color", "#000000") + Cell("Size", 1.0 / 6) + Cell("Style", 0) + "</Row></Section>" +
        "<Section N='Paragraph'><Row IX='0'>" + Cell("HorzAlign", 1) + "</Row></Section>" +
        "</StyleSheet>";

    /// <summary>The document's colour table (ColorEntry elements).</summary>
    public string Colors { get; set; } = "<ColorEntry IX='0' RGB='#000000'/><ColorEntry IX='1' RGB='#FFFFFF'/><ColorEntry IX='2' RGB='#FF0000'/>";

    public static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Cell(string name, double value) => $"<Cell N='{name}' V='{N(value)}'/>";

    public static string Cell(string name, string value) => $"<Cell N='{name}' V='{value}'/>";

    /// <summary>A shape: its transform cells, then <paramref name="body"/> (more cells, sections, text, sub-shapes).</summary>
    /// <param name="locPinX">The local pin; null = the box's centre.</param>
    public static string Shape(int id, double pinX, double pinY, double width, double height, string body = "", string attributes = "Type='Shape'",
        double? locPinX = null, double? locPinY = null) =>
        $"<Shape ID='{id}' {attributes}>" + Cell("PinX", pinX) + Cell("PinY", pinY) + Cell("Width", width) + Cell("Height", height) +
        Cell("LocPinX", locPinX ?? width / 2) + Cell("LocPinY", locPinY ?? height / 2) + body + "</Shape>";

    /// <summary>A Geometry section of rows (Row elements).</summary>
    public static string Geometry(string rows, int index = 0, string cells = "") => $"<Section N='Geometry' IX='{index}'>{cells}{rows}</Section>";

    public static string Row(string type, int index, params (string Name, double Value)[] cells) =>
        $"<Row T='{type}' IX='{index}'>" + string.Concat(cells.Select(c => Cell(c.Name, c.Value))) + "</Row>";

    /// <summary>A closed rectangle over the shape's box.</summary>
    public static string Rectangle(double width, double height) => Geometry(
        Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("LineTo", 2, ("X", width), ("Y", 0)) + Row("LineTo", 3, ("X", width), ("Y", height)) +
        Row("LineTo", 4, ("X", 0), ("Y", height)) + Row("LineTo", 5, ("X", 0), ("Y", 0)));

    /// <summary>A solid fill and no line.</summary>
    public static string Filled(string color) => Cell("FillForegnd", color) + Cell("FillPattern", 1) + Cell("LinePattern", 0);

    /// <summary>A text element (the characters with cp / pp markers).</summary>
    public static string TextBlock(string content) => $"<Text>{content}</Text>";

    /// <summary>Adds a page of <paramref name="shapes"/>; pictures are named by relationship id in <paramref name="pictures"/>.</summary>
    public int Page(string shapes, double width = 8.5, double height = 11, string pageSheet = "", bool background = false, int? backPage = null,
        Dictionary<string, string>? pictures = null, Dictionary<string, int>? masters = null)
    {
        var id = _pages.Count;
        var relationships = new List<(string, string, string)>();
        foreach (var (rid, media) in pictures ?? [])
            relationships.Add((rid, $"../{media}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"));
        foreach (var (rid, master) in masters ?? [])
            relationships.Add((rid, $"../masters/master{master}.xml", $"{RelationshipTypes}/master"));
        _pages.Add(new PageEntry(id, shapes, width, height, pageSheet, background, backPage, relationships));
        return id;
    }

    /// <summary>Adds a master of <paramref name="shapes"/>; returns its ID.</summary>
    public int Master(string shapes)
    {
        var id = _masters.Count + 1;
        _masters.Add((id, shapes));
        return id;
    }

    /// <summary>Adds a picture part; pages refer to it by the returned name.</summary>
    public string Media(byte[] bytes, string extension = "png")
    {
        var name = $"media/image{_media.Count + 1}.{extension}";
        _media[$"visio/{name}"] = bytes;
        return name;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            static string Relationships(IEnumerable<(string Id, string Target, string Type)> items) =>
                "<?xml version='1.0' encoding='UTF-8'?><Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" +
                string.Concat(items.Select(r => $"<Relationship Id='{r.Id}' Type='{r.Type}' Target='{r.Target}'/>")) + "</Relationships>";

            Write("[Content_Types].xml",
                "<?xml version='1.0' encoding='UTF-8'?><Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>" +
                "<Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>" +
                "<Default Extension='xml' ContentType='application/xml'/><Default Extension='png' ContentType='image/png'/>" +
                "<Override PartName='/visio/document.xml' ContentType='application/vnd.ms-visio.drawing.main+xml'/></Types>");
            Write("_rels/.rels", Relationships([("rId1", "visio/document.xml", $"{RelationshipTypes}/document")]));
            Write("visio/document.xml",
                $"<?xml version='1.0' encoding='UTF-8'?><VisioDocument xmlns='{Namespace}' xmlns:r='{R}'><DocumentSettings/>" +
                $"<Colors>{Colors}</Colors><FaceNames><FaceName NameU='Calibri'/></FaceNames><StyleSheets>{StyleSheets}</StyleSheets></VisioDocument>");
            Write("visio/_rels/document.xml.rels", Relationships([("rId1", "pages/pages.xml", $"{RelationshipTypes}/pages"),
                ("rId2", "masters/masters.xml", $"{RelationshipTypes}/masters")]));

            var pages = new StringBuilder($"<?xml version='1.0' encoding='UTF-8'?><Pages xmlns='{Namespace}' xmlns:r='{R}'>");
            foreach (var page in _pages)
            {
                pages.Append($"<Page ID='{page.Id}' NameU='Page-{page.Id + 1}' Name='Page-{page.Id + 1}'")
                    .Append(page.Background ? " Background='1'" : "")
                    .Append(page.BackPage is { } back ? $" BackPage='{back}'" : "")
                    .Append("><PageSheet>").Append(Cell("PageWidth", page.Width)).Append(Cell("PageHeight", page.Height)).Append(page.PageSheet)
                    .Append($"</PageSheet><Rel r:id='rId{page.Id + 1}'/></Page>");
                Write($"visio/pages/page{page.Id + 1}.xml",
                    $"<?xml version='1.0' encoding='UTF-8'?><PageContents xmlns='{Namespace}' xmlns:r='{R}'><Shapes>{page.Shapes}</Shapes></PageContents>");
                if (page.Relationships.Count > 0)
                    Write($"visio/pages/_rels/page{page.Id + 1}.xml.rels", Relationships(page.Relationships));
            }
            pages.Append("</Pages>");
            Write("visio/pages/pages.xml", pages.ToString());
            Write("visio/pages/_rels/pages.xml.rels", Relationships(_pages.Select(p => ($"rId{p.Id + 1}", $"page{p.Id + 1}.xml", $"{RelationshipTypes}/page"))));

            var masters = new StringBuilder($"<?xml version='1.0' encoding='UTF-8'?><Masters xmlns='{Namespace}' xmlns:r='{R}'>");
            foreach (var (id, shapes) in _masters)
            {
                masters.Append($"<Master ID='{id}' NameU='Master {id}'><PageSheet/><Rel r:id='rId{id}'/></Master>");
                Write($"visio/masters/master{id}.xml",
                    $"<?xml version='1.0' encoding='UTF-8'?><MasterContents xmlns='{Namespace}' xmlns:r='{R}'><Shapes>{shapes}</Shapes></MasterContents>");
            }
            masters.Append("</Masters>");
            Write("visio/masters/masters.xml", masters.ToString());
            Write("visio/masters/_rels/masters.xml.rels", Relationships(_masters.Select(m => ($"rId{m.Id}", $"master{m.Id}.xml", $"{RelationshipTypes}/master"))));

            foreach (var (name, bytes) in _media)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    public string Save(string path)
    {
        File.WriteAllBytes(path, ToBytes());
        return path;
    }
}
