// The slide feature matrix of the render quality harness: one deck written in code, one slide per feature of the
// slide renderer's scope, so each slide's SSIM against LibreOffice's rendering says how close that feature comes.

using static Filee.Engines.Tests.SlideDeckBuilder;

namespace Filee.Engines.Tests.RenderQuality;

internal static class SlideFeatures
{
    /// <summary>The features in slide order.</summary>
    public static readonly string[] Names =
        ["title", "shapes", "lines", "text", "vertical-text", "scripts", "pictures", "tables", "fills", "smartart-chart"];

    /// <summary>Text each feature's slide must show (read back from the PDF).</summary>
    public static readonly Dictionary<string, string[]> Text = new()
    {
        ["title"] = ["Quarterly review", "Filee draws slides itself"],
        ["shapes"] = ["Rounded", "Hexagon", "Star"],
        ["lines"] = ["Connectors and arrowheads"],
        ["text"] = ["Bullets and numbering", "First point", "Second level", "Shrunk to fit", "Left column", "filee.sh"],
        ["vertical-text"] = ["Down the side", "Up the side", "Turned 30°"],
        ["scripts"] = ["한국어 슬라이드", "日本語のスライド", "مرحبا بالعالم", "नमस्ते दुनिया"],
        ["pictures"] = ["Pictures"],
        ["tables"] = ["Region", "North", "Merged across", "Total"],
        ["fills"] = ["Gradients and patterns"],
        ["smartart-chart"] = ["Plan", "Build", "Ship"],
    };

    public static string Write(string path)
    {
        var deck = new SlideDeckBuilder();
        var picture = deck.Media(TestPicture());
        var pictures = new Dictionary<string, string> { ["rIdPic"] = picture };

        // title: the title layout's placeholders, master background and bar.
        deck.Slide(deck.Placeholder("ctrTitle", Paragraph("Quarterly review")) +
                   deck.Placeholder("subTitle", Paragraph("Filee draws slides itself"), 1), Layout.Title);

        // shapes: presets with PowerPoint's default shape style (theme fill and line), labels in their text boxes.
        var shapes = new List<string>();
        string[] presets = ["roundRect", "ellipse", "triangle", "diamond", "hexagon", "star5", "rightArrow", "chevron",
            "can", "cube", "donut", "wedgeRectCallout", "flowChartDecision", "heart", "pie", "plus"];
        string[] labels = ["Rounded", "Ellipse", "Triangle", "Diamond", "Hexagon", "Star", "Arrow", "Chevron",
            "Can", "Cube", "Donut", "Callout", "Decision", "Heart", "Pie", "Plus"];
        for (var i = 0; i < presets.Length; i++)
        {
            var (x, y) = (40 + i % 8 * 112, 60 + i / 8 * 220);
            shapes.Add(deck.Shape(presets[i], x, y, 96, 96, paragraphs: Paragraph(labels[i], "<a:pPr algn=\"ctr\"/>", "<a:rPr lang=\"en-US\" sz=\"1200\"/>")));
            shapes.Add(deck.Shape(presets[i], x + 8, y + 120, 80, 56, Solid("accent" + (i % 6 + 1)), "<a:ln w=\"25400\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill></a:ln>",
                styled: false));
        }
        deck.Slide(string.Concat(shapes));

        // lines: straight lines with every arrowhead, dashes, a double line, connectors and custom geometry.
        var lines = new List<string> { deck.TextBox(40, 20, 880, 40, Paragraph("Connectors and arrowheads", "", "<a:rPr lang=\"en-US\" sz=\"2400\" b=\"1\"/>")) };
        string[] ends = ["triangle", "stealth", "diamond", "oval", "arrow"];
        string[] dashes = ["solid", "dash", "sysDot", "lgDashDot", "dashDot"];
        for (var i = 0; i < ends.Length; i++)
        {
            lines.Add(deck.Connector("straightConnector1", 60, 90 + i * 40, 360, 0,
                $"<a:ln w=\"{E(1.5 + i)}\"><a:solidFill><a:schemeClr val=\"accent{i + 1}\"/></a:solidFill><a:prstDash val=\"{dashes[i]}\"/>" +
                $"<a:headEnd type=\"{ends[(i + 2) % 5]}\" w=\"sm\" len=\"sm\"/><a:tailEnd type=\"{ends[i]}\" w=\"med\" len=\"med\"/></a:ln>"));
        }
        lines.Add(deck.Connector("line", 60, 300, 360, 0, "<a:ln w=\"76200\" cmpd=\"dbl\"><a:solidFill><a:srgbClr val=\"2E7D32\"/></a:solidFill></a:ln>"));
        lines.Add(deck.Connector("bentConnector3", 480, 90, 200, 120, "<a:ln w=\"28575\"><a:solidFill><a:schemeClr val=\"accent2\"/></a:solidFill><a:tailEnd type=\"triangle\"/></a:ln>"));
        lines.Add(deck.Connector("curvedConnector3", 720, 90, 180, 120, "<a:ln w=\"28575\"><a:solidFill><a:schemeClr val=\"accent5\"/></a:solidFill><a:tailEnd type=\"stealth\" w=\"lg\" len=\"lg\"/></a:ln>", flipV: true));
        lines.Add(deck.Connector("line", 480, 260, 200, 80, "<a:ln w=\"12700\" cap=\"rnd\"><a:solidFill><a:srgbClr val=\"6A1B9A\"/></a:solidFill><a:prstDash val=\"sysDash\"/></a:ln>", flipH: true));
        // A freeform: lines, an arc, a quadratic and a cubic curve, closed, with a fill.
        const string freeform =
            "<a:custGeom><a:avLst/><a:gdLst><a:gd name=\"half\" fmla=\"*/ w 1 2\"/></a:gdLst><a:ahLst/><a:cxnLst/><a:rect l=\"l\" t=\"t\" r=\"r\" b=\"b\"/>" +
            "<a:pathLst><a:path w=\"200\" h=\"120\"><a:moveTo><a:pt x=\"0\" y=\"120\"/></a:moveTo><a:lnTo><a:pt x=\"40\" y=\"20\"/></a:lnTo>" +
            "<a:arcTo wR=\"30\" hR=\"30\" stAng=\"10800000\" swAng=\"10800000\"/><a:quadBezTo><a:pt x=\"130\" y=\"110\"/><a:pt x=\"160\" y=\"40\"/></a:quadBezTo>" +
            "<a:cubicBezTo><a:pt x=\"180\" y=\"0\"/><a:pt x=\"200\" y=\"60\"/><a:pt x=\"200\" y=\"120\"/></a:cubicBezTo><a:close/></a:path></a:pathLst></a:custGeom>";
        lines.Add(deck.Shape(freeform, 720, 300, 200, 120, Solid("#80CBC4"), "<a:ln w=\"19050\"><a:solidFill><a:srgbClr val=\"00695C\"/></a:solidFill></a:ln>", styled: false));
        deck.Slide(string.Concat(lines));

        // text: the content layout's body with bullets, levels and numbers; autofit, columns, alignment, a link.
        var body = Paragraph("First point") + Paragraph("Second level", "<a:pPr lvl=\"1\"/>") + Paragraph("Back to the first level") +
                   Paragraph("Numbered one", "<a:pPr marL=\"457200\" indent=\"-457200\"><a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"arabicPeriod\"/></a:pPr>") +
                   Paragraph("Numbered two", "<a:pPr marL=\"457200\" indent=\"-457200\"><a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"arabicPeriod\"/></a:pPr>");
        var text = deck.Placeholder("title", Paragraph("Bullets and numbering")) +
                   deck.Placeholder("body", body, 1, Xfrm(66, 143.75, 420, 342.6)) +
                   deck.Shape("rect", 520, 150, 380, 90, Solid("#FFF3E0"), "<a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"EF6C00\"/></a:solidFill></a:ln>",
                       Paragraph("Shrunk to fit: PowerPoint stored the scale it applied to this long paragraph, so it fits the box.", "",
                           "<a:rPr lang=\"en-US\" sz=\"2800\"><a:solidFill><a:srgbClr val=\"BF360C\"/></a:solidFill></a:rPr>"),
                       "<a:bodyPr anchor=\"t\"><a:normAutofit fontScale=\"77500\" lnSpcReduction=\"20000\"/></a:bodyPr>", styled: false) +
                   deck.TextBox(520, 260, 380, 120,
                       Paragraph("Left column of text that runs on and on until it reaches the bottom of the box.") +
                       Paragraph("Right column, after the column break the box makes by itself."),
                       "<a:bodyPr wrap=\"square\" numCol=\"2\" spcCol=\"228600\" rtlCol=\"0\"><a:noAutofit/></a:bodyPr>") +
                   deck.TextBox(520, 400, 380, 60,
                       "<a:p><a:pPr algn=\"r\"/><a:r><a:rPr lang=\"en-US\" sz=\"1600\" i=\"1\"/><a:t>Right-aligned, with a link to </a:t></a:r>" +
                       "<a:r><a:rPr lang=\"en-US\" sz=\"1600\"><a:hlinkClick r:id=\"rIdLink\"/></a:rPr><a:t>filee.sh</a:t></a:r></a:p>");
        deck.Slide(text, Layout.Content, relationships: [("rIdLink", "hyperlink\" TargetMode=\"External", "https://filee.sh")]);

        // vertical-text: frames turned 90° and 270°, a turned shape with text, mirrored shapes.
        deck.Slide(
            deck.Shape("rect", 60, 40, 80, 440, Solid("#E3F2FD"), "", Paragraph("Down the side", "<a:pPr algn=\"ctr\"/>", "<a:rPr lang=\"en-US\" sz=\"2400\"><a:solidFill><a:srgbClr val=\"0D47A1\"/></a:solidFill></a:rPr>"),
                "<a:bodyPr vert=\"vert\" anchor=\"ctr\"/>", styled: false) +
            deck.Shape("rect", 180, 40, 80, 440, Solid("#FCE4EC"), "", Paragraph("Up the side", "<a:pPr algn=\"ctr\"/>", "<a:rPr lang=\"en-US\" sz=\"2400\"><a:solidFill><a:srgbClr val=\"880E4F\"/></a:solidFill></a:rPr>"),
                "<a:bodyPr vert=\"vert270\" anchor=\"ctr\"/>", styled: false) +
            deck.Shape("roundRect", 360, 160, 260, 120, paragraphs: Paragraph("Turned 30°", "<a:pPr algn=\"ctr\"/>", "<a:rPr lang=\"en-US\" sz=\"2400\"/>"), rotation: 30) +
            deck.Shape("rightArrow", 680, 80, 220, 100, Solid("accent6"), "", Paragraph("Mirrored", "<a:pPr algn=\"ctr\"/>", "<a:rPr lang=\"en-US\" sz=\"1800\"/>"), flipH: true) +
            deck.Shape("triangle", 700, 300, 180, 160, Solid("accent4"), "", Paragraph("Flipped", "<a:pPr algn=\"ctr\"/>", "<a:rPr lang=\"en-US\" sz=\"1800\"/>"), flipV: true));

        // scripts: Korean, Japanese, Arabic (right to left) and Devanagari through the theme's script fonts.
        deck.Slide(
            deck.TextBox(60, 60, 840, 70, Paragraph("한국어 슬라이드: 한글 문단이 줄을 바꿔 가며 상자 안에 들어갑니다.", "", "<a:rPr lang=\"ko-KR\" altLang=\"en-US\" sz=\"2400\"/>")) +
            deck.TextBox(60, 160, 840, 70, Paragraph("日本語のスライド：文字を正しく並べます。", "", "<a:rPr lang=\"ja-JP\" altLang=\"en-US\" sz=\"2400\"/>")) +
            deck.TextBox(60, 260, 840, 70, Paragraph("مرحبا بالعالم من فيلي", "<a:pPr algn=\"r\" rtl=\"1\"/>", "<a:rPr lang=\"ar-SA\" sz=\"2400\"><a:cs typeface=\"Arial\"/></a:rPr>")) +
            deck.TextBox(60, 360, 840, 70, Paragraph("नमस्ते दुनिया, यह स्लाइड है।", "", "<a:rPr lang=\"hi-IN\" sz=\"2400\"><a:cs typeface=\"Nirmala UI\"/></a:rPr>")));

        // pictures: as they are, cropped, turned, in an ellipse, grey, half transparent, and as a tiled shape fill.
        deck.Slide(
            deck.TextBox(40, 16, 880, 40, Paragraph("Pictures", "", "<a:rPr lang=\"en-US\" sz=\"2400\" b=\"1\"/>")) +
            deck.Picture("rIdPic", 40, 70, 240, 150) +
            deck.Picture("rIdPic", 320, 70, 240, 150, "<a:srcRect l=\"25000\" t=\"10000\" r=\"5000\" b=\"20000\"/>") +
            deck.Picture("rIdPic", 620, 80, 240, 150, rotation: 15, line: "<a:ln w=\"38100\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:ln>") +
            deck.Picture("rIdPic", 40, 280, 200, 200, geometry: "ellipse") +
            deck.Picture("rIdPic", 280, 300, 240, 150, blipEffects: "<a:grayscl/>") +
            deck.Picture("rIdPic", 560, 300, 160, 100, blipEffects: "<a:alphaModFix amt=\"50000\"/>") +
            deck.Shape("roundRect", 760, 280, 160, 200, "<a:blipFill dpi=\"0\" rotWithShape=\"1\"><a:blip r:embed=\"rIdPic\"/><a:srcRect/><a:tile tx=\"0\" ty=\"0\" sx=\"25000\" sy=\"25000\" flip=\"none\" algn=\"tl\"/></a:blipFill>",
                "<a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill></a:ln>", styled: false),
            pictures: pictures);

        // tables: PowerPoint's default style (header, banded rows, a merged cell) and a table with its own lines and fills.
        string[][] data = [["Region", "Q1", "Q2", "Q3"], ["North", "120", "135", "150"], ["South", "98", "110", "121"], ["East", "143", "150", "149"]];
        var styled = data.Select((row, r) => string.Concat(row.Select(cell => Cell(cell, r > 0 && cell != row[0] ? "<a:tcPr anchor=\"ctr\"/>" : "<a:tcPr/>")))).ToList();
        styled.Add(Cell("Merged across", attributes: " gridSpan=\"3\"") + Cell("", attributes: " hMerge=\"1\"") + Cell("", attributes: " hMerge=\"1\"") + Cell("412"));
        const string thin = "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"37474F\"/></a:solidFill></a:ln>";
        string Own(string fill) => $"<a:tcPr>{thin.Replace("a:ln", "a:lnL")}{thin.Replace("a:ln", "a:lnR")}{thin.Replace("a:ln", "a:lnT")}{thin.Replace("a:ln", "a:lnB")}{Solid(fill)}</a:tcPr>";
        var plain = new[]
        {
            Cell("Item", Own("#CFD8DC")) + Cell("Count", Own("#CFD8DC")),
            Cell("Apples", Own("#FFFFFF")) + Cell("12", Own("#FFFFFF")),
            Cell("Total", Own("#ECEFF1")) + Cell("12", Own("#ECEFF1")),
        };
        deck.Slide(deck.Table(40, 60, [180, 120, 120, 120], 40, styled) +
                   deck.Table(660, 60, [160, 100], 36, plain, "<a:tblPr/>"));

        // fills: linear (angle, scaled), circle and rectangle gradients, patterns, transparency, a turned group, a gradient background.
        string Gradient(string path, string colors) => $"<a:gradFill rotWithShape=\"1\"><a:gsLst>{colors}</a:gsLst>{path}</a:gradFill>";
        const string twoStops = "<a:gs pos=\"0\"><a:srgbClr val=\"FFFFFF\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"1565C0\"/></a:gs>";
        const string threeStops = "<a:gs pos=\"0\"><a:srgbClr val=\"F44336\"/></a:gs><a:gs pos=\"50000\"><a:srgbClr val=\"FFEB3B\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"4CAF50\"/></a:gs>";
        string Pattern(string preset) => $"<a:pattFill prst=\"{preset}\"><a:fgClr><a:srgbClr val=\"5D4037\"/></a:fgClr><a:bgClr><a:srgbClr val=\"FFF8E1\"/></a:bgClr></a:pattFill>";
        var fills = deck.TextBox(40, 16, 880, 40, Paragraph("Gradients and patterns", "", "<a:rPr lang=\"en-US\" sz=\"2400\" b=\"1\"/>")) +
                    deck.Shape("rect", 40, 70, 200, 120, Gradient("<a:lin ang=\"0\" scaled=\"0\"/>", twoStops), styled: false) +
                    deck.Shape("rect", 260, 70, 200, 120, Gradient("<a:lin ang=\"2700000\" scaled=\"1\"/>", threeStops), styled: false) +
                    deck.Shape("ellipse", 480, 70, 200, 120, Gradient("<a:path path=\"circle\"><a:fillToRect l=\"50000\" t=\"50000\" r=\"50000\" b=\"50000\"/></a:path>", twoStops), styled: false) +
                    deck.Shape("rect", 700, 70, 200, 120, Gradient("<a:path path=\"rect\"><a:fillToRect l=\"50000\" t=\"50000\" r=\"50000\" b=\"50000\"/></a:path>", threeStops), styled: false) +
                    string.Concat(new[] { "pct20", "ltUpDiag", "smGrid", "lgCheck", "horz", "diagCross" }.Select((p, i) =>
                        deck.Shape("rect", 40 + i * 145, 220, 125, 80, Pattern(p), "<a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"5D4037\"/></a:solidFill></a:ln>", styled: false))) +
                    deck.Shape("ellipse", 60, 340, 160, 160, "<a:solidFill><a:srgbClr val=\"E91E63\"><a:alpha val=\"60000\"/></a:srgbClr></a:solidFill>", styled: false) +
                    deck.Shape("ellipse", 140, 340, 160, 160, "<a:solidFill><a:srgbClr val=\"3F51B5\"><a:alpha val=\"60000\"/></a:srgbClr></a:solidFill>", styled: false) +
                    deck.Group(420, 330, 300, 170, 0.5,
                        deck.Shape("rect", 0, 0, 280, 160, Solid("accent1"), styled: false) +
                        deck.Shape("ellipse", 320, 0, 280, 160, Solid("accent2"), styled: false) +
                        deck.Shape("triangle", 160, 180, 280, 160, Solid("accent6"), styled: false), rotation: -10) +
                    deck.Shape("star5", 760, 340, 160, 160, "<a:solidFill><a:schemeClr val=\"accent4\"><a:lumMod val=\"75000\"/></a:schemeClr></a:solidFill>",
                        "<a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"accent4\"><a:lumMod val=\"50000\"/></a:schemeClr></a:solidFill></a:ln>", styled: false);
        deck.Slide(fills, background: $"<p:bgPr>{Gradient("<a:lin ang=\"5400000\" scaled=\"0\"/>", "<a:gs pos=\"0\"><a:srgbClr val=\"FFFFFF\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"E0F2F1\"/></a:gs>")}<a:effectLst/></p:bgPr>");

        // smartart-chart: SmartArt through the drawing PowerPoint keeps with it, a chart through its fallback picture.
        const string dsp = "xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        string Step(int i, string label) =>
            $"<dsp:sp modelId=\"{{0000000{i}-0000-0000-0000-000000000000}}\"><dsp:nvSpPr><dsp:cNvPr id=\"0\" name=\"\"/><dsp:cNvSpPr/></dsp:nvSpPr>" +
            $"<dsp:spPr>{Xfrm(i * 140, 20, 120, 80)}<a:prstGeom prst=\"chevron\"><a:avLst/></a:prstGeom><a:solidFill><a:schemeClr val=\"accent{i + 1}\"/></a:solidFill>" +
            "<a:ln><a:noFill/></a:ln></dsp:spPr><dsp:style><a:lnRef idx=\"0\"><a:scrgbClr r=\"0\" g=\"0\" b=\"0\"/></a:lnRef><a:fillRef idx=\"1\"><a:scrgbClr r=\"0\" g=\"0\" b=\"0\"/></a:fillRef>" +
            "<a:effectRef idx=\"0\"><a:scrgbClr r=\"0\" g=\"0\" b=\"0\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"lt1\"/></a:fontRef></dsp:style>" +
            $"<dsp:txBody><a:bodyPr anchor=\"ctr\"/><a:lstStyle/><a:p><a:pPr algn=\"ctr\"/><a:r><a:rPr lang=\"en-US\" sz=\"2000\"/><a:t>{label}</a:t></a:r></a:p></dsp:txBody>" +
            $"{Xfrm(i * 140 + 40, 20, 60, 80, element: "dsp:txXfrm")}</dsp:sp>";
        deck.Part("ppt/diagrams/drawing1.xml", $"<dsp:drawing {dsp}><dsp:spTree><dsp:nvGrpSpPr><dsp:cNvPr id=\"0\" name=\"\"/><dsp:cNvGrpSpPr/></dsp:nvGrpSpPr><dsp:grpSpPr/>" +
                                               Step(0, "Plan") + Step(1, "Build") + Step(2, "Ship") + "</dsp:spTree></dsp:drawing>", "drawingml.diagramDrawing");
        deck.Part("ppt/diagrams/data1.xml", "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                                            "<dgm:ptLst><dgm:pt modelId=\"{00000000-0000-0000-0000-0000000000AA}\" type=\"doc\"/></dgm:ptLst><dgm:cxnLst/><dgm:bg/><dgm:whole/>" +
                                            "<dgm:extLst><a:ext uri=\"http://schemas.microsoft.com/office/drawing/2008/diagram\"><dsp:dataModelExt xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\" relId=\"rIdDrawing\" minVer=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/></a:ext></dgm:extLst></dgm:dataModel>",
            "drawingml.diagramData");
        var smartArt =
            $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{deck.NextId()}\" name=\"Diagram\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>{Xfrm(60, 60, 420, 120, element: "p:xfrm")}" +
            "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"><dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rIdData\" r:lo=\"rIdData\" r:qs=\"rIdData\" r:cs=\"rIdData\"/></a:graphicData></a:graphic></p:graphicFrame>";
        var chart =
            "<mc:AlternateContent><mc:Choice xmlns:c14=\"http://schemas.microsoft.com/office/drawing/2007/8/2/chart\" Requires=\"c14\">" +
            $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{deck.NextId()}\" name=\"Chart\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>{Xfrm(520, 220, 380, 260, element: "p:xfrm")}" +
            "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rIdChart\"/></a:graphicData></a:graphic></p:graphicFrame>" +
            "</mc:Choice><mc:Fallback>" + deck.Picture("rIdPic", 520, 220, 380, 260) + "</mc:Fallback></mc:AlternateContent>";
        deck.Slide(smartArt + chart, pictures: pictures, relationships: [("rIdData", "diagramData", "../diagrams/data1.xml"),
            ("rIdDrawing", "diagramDrawing", "../diagrams/drawing1.xml")]);

        return deck.Save(path);
    }
}
