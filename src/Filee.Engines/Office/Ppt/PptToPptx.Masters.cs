// Masters, layouts, themes, slides and notes of the translated package.
//
// PowerPoint 97–2003 has main masters (MainMasterContainer: text styles, colour scheme, drawing) and title masters (a
// SlideContainer in the master list whose SlideAtom names its main master). A slide names its master by slide id; it
// takes the master's colour scheme, objects and background unless its SlideAtom flags say it has its own.
//
// In the package a slide master stands for a main master in one colour scheme (a variant): its theme holds the scheme
// as the twelve theme colours (SchemeSlots), so the scheme indexes text and shapes use become theme colours, and a slide
// with a scheme of its own goes onto a copy of its master with that scheme. Each variant has a layout with the title
// and body placeholders, and a layout for each title master its slides use, with the title master's shapes, background
// and "show master objects" flag.

using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

internal sealed partial class PptToPptx
{
    /// <summary>Copies of masters for slides with colour schemes of their own, at most; more slides take their master's scheme.</summary>
    private const int MaxVariants = 64;

    // SlideAtom flags.
    private const int MasterObjects = 0x1;
    private const int MasterScheme = 0x2;
    private const int MasterBackground = 0x4;

    /// <summary>PowerPoint's default colour scheme: background, text, shadows, title, fills, accent, hyperlink, followed hyperlink.</summary>
    private static readonly uint[] DefaultScheme = [0xFFFFFF, 0x000000, 0x808080, 0x000000, 0x00CC99, 0x3333CC, 0xCCCCFF, 0xB2B2B2];

    private sealed class MainMaster(OfficeArtRecord? container, MasterStyles styles, uint[] scheme)
    {
        public OfficeArtRecord? Container { get; } = container;
        public MasterStyles Styles { get; } = styles;
        public uint[] Scheme { get; } = scheme;
    }

    private sealed class TitleMaster(OfficeArtRecord container, MainMaster main, int flags, uint[]? ownScheme)
    {
        public OfficeArtRecord Container { get; } = container;
        public MainMaster Main { get; } = main;
        public int Flags { get; } = flags;
        public uint[]? OwnScheme { get; } = ownScheme;
    }

    private sealed class Layout(int number, uint id, TitleMaster? title)
    {
        public int Number { get; } = number;
        public uint Id { get; } = id;
        public TitleMaster? Title { get; } = title;
        public string Part => $"ppt/slideLayouts/slideLayout{Number}.xml";
    }

    private sealed class Variant(MainMaster main, uint[] scheme, int number, uint id)
    {
        public MainMaster Main { get; } = main;
        public uint[] Scheme { get; } = scheme;
        public int Number { get; } = number;
        public uint Id { get; } = id;
        public List<Layout> Layouts { get; } = [];
        public Dictionary<TitleMaster, Layout> Titles { get; } = [];
        public string Part => $"ppt/slideMasters/slideMaster{Number}.xml";
    }

    private sealed record PlannedSlide(OfficeArtRecord Container, PptListEntry Entry, Variant Variant, Layout Layout, int Flags, bool Hidden,
        OfficeArtRecord? Notes);

    private readonly List<MainMaster> _mains = [];
    private readonly Dictionary<uint, object> _masters = [];
    private readonly List<Variant> _variants = [];
    private int _layouts;
    private uint _nextId = 2147483648;

    // ───────────────────────── Reading ─────────────────────────

    private void ReadMasters()
    {
        var titles = new List<(uint SlideId, OfficeArtRecord Container)>();
        foreach (var entry in _file.Masters)
        {
            if (_file.Container(entry.PersistId, PptRecords.MainMaster) is { } container)
            {
                var main = new MainMaster(container, Styles(container), Scheme(container) ?? DefaultScheme);
                _mains.Add(main);
                _masters.TryAdd(entry.SlideId, main);
            }
            else if (_file.Container(entry.PersistId, PptRecords.Slide) is { } title)
            {
                titles.Add((entry.SlideId, title));
            }
        }
        if (_mains.Count == 0)
            _mains.Add(new MainMaster(null, DefaultStyles(), DefaultScheme));
        foreach (var (slideId, container) in titles)
        {
            var (masterId, _, flags) = SlideAtom(container);
            var main = _masters.GetValueOrDefault(masterId) as MainMaster ?? _mains[0];
            _masters.TryAdd(slideId, new TitleMaster(container, main, flags, (flags & MasterScheme) == 0 ? Scheme(container) : null));
        }
        VariantFor(_mains[0], _mains[0].Scheme);
    }

    /// <summary>A main master's text styles; text outside placeholders takes the Environment's when the master has none.</summary>
    private MasterStyles Styles(OfficeArtRecord master)
    {
        var styles = new MasterStyles();
        foreach (var child in OfficeArtRecords.Children(_file.Stream, master))
        {
            if (child.Type == PptRecords.TextMasterStyleAtom)
                styles.Add(child.Instance, ReadMasterStyle(child));
        }
        if (_file.DefaultTextStyle is { } environment)
            styles.Add(OtherText, ReadMasterStyle(environment));
        return styles;
    }

    private MasterStyles DefaultStyles()
    {
        var styles = new MasterStyles();
        if (_file.DefaultTextStyle is { } environment)
            styles.Add(OtherText, ReadMasterStyle(environment));
        return styles;
    }

    /// <summary>The colour scheme of a slide or master (its ColorSchemeAtom of instance 1) as RRGGBB values.</summary>
    private uint[]? Scheme(OfficeArtRecord container)
    {
        var bytes = _file.Stream;
        foreach (var child in OfficeArtRecords.Children(bytes, container))
        {
            if (child.Type != PptRecords.ColorSchemeAtom || child.Instance != 1 || child.Length < 32)
                continue;
            var scheme = new uint[8];
            for (var i = 0; i < 8; i++)
                scheme[i] = (uint)(bytes[child.Start + i * 4] << 16 | bytes[child.Start + i * 4 + 1] << 8 | bytes[child.Start + i * 4 + 2]);
            return scheme;
        }
        return null;
    }

    /// <summary>A SlideAtom's master id, notes id and flags; all objects, scheme and background of the master when it has none.</summary>
    private (uint Master, uint Notes, int Flags) SlideAtom(OfficeArtRecord container) =>
        OfficeArtRecords.Child(_file.Stream, container, PptRecords.SlideAtom) is { Length: >= 22 } atom
            ? (PptRecords.U32(_file.Stream, atom.Start + 12), PptRecords.U32(_file.Stream, atom.Start + 16), PptRecords.U16(_file.Stream, atom.Start + 20))
            : (0, 0, MasterObjects | MasterScheme | MasterBackground);

    private Variant VariantFor(MainMaster main, uint[] scheme)
    {
        foreach (var variant in _variants)
        {
            if (variant.Main == main && variant.Scheme.AsSpan().SequenceEqual(scheme))
                return variant;
        }
        if (_variants.Count >= MaxVariants && _variants.FirstOrDefault(v => v.Main == main) is { } existing)
            return existing;
        var created = new Variant(main, scheme, _variants.Count + 1, _nextId++);
        _variants.Add(created);
        var layout = new Layout(++_layouts, _nextId++, null);
        created.Layouts.Add(layout);
        return created;
    }

    private Layout LayoutFor(Variant variant, TitleMaster? title)
    {
        if (title is null)
            return variant.Layouts[0];
        if (variant.Titles.TryGetValue(title, out var layout))
            return layout;
        layout = new Layout(++_layouts, _nextId++, title);
        variant.Layouts.Add(layout);
        variant.Titles[title] = layout;
        return layout;
    }

    /// <summary>The slides in order, each with its master copy, layout and notes.</summary>
    private List<PlannedSlide> PlanSlides()
    {
        var notesById = new Dictionary<uint, OfficeArtRecord>();
        foreach (var entry in _file.NotesPages)
        {
            if (_file.Container(entry.PersistId, PptRecords.Notes) is { } notes)
                notesById.TryAdd(entry.SlideId, notes);
        }
        var slides = new List<PlannedSlide>();
        foreach (var entry in _file.Slides)
        {
            if (_file.Container(entry.PersistId, PptRecords.Slide) is not { } container)
                continue;
            var (masterId, notesId, flags) = SlideAtom(container);
            var master = _masters.GetValueOrDefault(masterId);
            var title = master as TitleMaster;
            var main = title?.Main ?? master as MainMaster ?? _mains[0];
            var scheme = ((flags & MasterScheme) == 0 ? Scheme(container) : null) ?? title?.OwnScheme ?? main.Scheme;
            var variant = VariantFor(main, scheme);
            slides.Add(new PlannedSlide(container, entry, variant, LayoutFor(variant, title), flags, Hidden(container),
                notesId != 0 ? notesById.GetValueOrDefault(notesId) : null));
        }
        return slides;
    }

    /// <summary>The slide is hidden in the slide show (SlideShowSlideInfoAtom fHidden).</summary>
    private bool Hidden(OfficeArtRecord container) =>
        OfficeArtRecords.Child(_file.Stream, container, PptRecords.SlideShowSlideInfoAtom) is { Length: >= 12 } info
        && (PptRecords.U16(_file.Stream, info.Start + 10) & 0x4) != 0;

    // ───────────────────────── Writing ─────────────────────────

    private void WriteMaster(Variant variant)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var main = variant.Main;
        var sheet = new Sheet(variant.Part, main.Styles) { Scheme = variant.Scheme };
        var tree = new StringBuilder(PresentationPackage.TreeStart);
        var background = main.Container is { } container ? WriteDrawing(tree, container, sheet) : null;
        var xml = new StringBuilder($"<p:sldMaster {Namespaces}><p:cSld>");
        xml.Append(Background(background, sheet) ?? "<p:bg><p:bgPr><a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>");
        xml.Append(tree).Append("</p:spTree></p:cSld>").Append(PresentationPackage.ColorMap).Append("<p:sldLayoutIdLst>");
        foreach (var layout in variant.Layouts)
            xml.Append($"<p:sldLayoutId id=\"{layout.Id}\" r:id=\"{sheet.Relationship("slideLayout", layout.Part)}\"/>");
        xml.Append("</p:sldLayoutIdLst><p:txStyles><p:titleStyle>");
        WriteLevels(xml, main.Styles.Resolved(TitleText), full: true);
        xml.Append("</p:titleStyle><p:bodyStyle>");
        WriteLevels(xml, main.Styles.Resolved(BodyText), full: true);
        xml.Append("</p:bodyStyle><p:otherStyle>");
        WriteLevels(xml, main.Styles.Resolved(OtherText), full: true);
        xml.Append("</p:otherStyle></p:txStyles></p:sldMaster>");
        var theme = $"ppt/theme/theme{variant.Number}.xml";
        sheet.Relationship("theme", theme);
        Write(variant.Part, xml.ToString(), PresentationTypes + "slideMaster+xml");
        Write(sheet.RelsPart, PresentationPackage.Rels([.. sheet.Rels]));
        Write(theme, Theme(variant), PresentationPackage.ThemeType);
        foreach (var layout in variant.Layouts)
            WriteLayout(variant, layout);
    }

    private void WriteLayout(Variant variant, Layout layout)
    {
        var sheet = new Sheet(layout.Part, variant.Main.Styles) { Scheme = variant.Scheme };
        var tree = new StringBuilder(PresentationPackage.TreeStart);
        var xml = new StringBuilder($"<p:sldLayout {Namespaces}");
        if (layout.Title is { } title)
        {
            xml.Append(" type=\"title\" preserve=\"1\"");
            if ((title.Flags & MasterObjects) == 0)
                xml.Append(" showMasterSp=\"0\"");
            var background = WriteDrawing(tree, title.Container, sheet);
            xml.Append("><p:cSld name=\"Title Slide\">");
            if ((title.Flags & MasterBackground) == 0 && Background(background, sheet) is { } own)
                xml.Append(own);
        }
        else
        {
            // The master's placeholders, inherited as they are.
            tree.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>")
                .Append("<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Content Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>");
            xml.Append(" type=\"obj\" preserve=\"1\"><p:cSld name=\"Title and Content\">");
        }
        xml.Append(tree).Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        sheet.Relationship("slideMaster", variant.Part);
        Write(layout.Part, xml.ToString(), PresentationTypes + "slideLayout+xml");
        Write(sheet.RelsPart, PresentationPackage.Rels([.. sheet.Rels]));
    }

    private void WriteSlide(PlannedSlide slide, int number)
    {
        var sheet = new Sheet($"ppt/slides/slide{number}.xml", slide.Variant.Main.Styles) { Outline = slide.Entry.Texts, Scheme = slide.Variant.Scheme };
        sheet.Relationship("slideLayout", slide.Layout.Part);
        var tree = new StringBuilder(PresentationPackage.TreeStart);
        var background = WriteDrawing(tree, slide.Container, sheet);
        var xml = new StringBuilder($"<p:sld {Namespaces}");
        if (slide.Hidden)
            xml.Append(" show=\"0\"");
        if ((slide.Flags & MasterObjects) == 0)
            xml.Append(" showMasterSp=\"0\"");
        xml.Append("><p:cSld>");
        if ((slide.Flags & MasterBackground) == 0 && Background(background, sheet) is { } own)
            xml.Append(own);
        xml.Append(tree).Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        if (slide.Notes is { } notes)
        {
            WriteNotesSlide(notes, number);
            sheet.Relationship("notesSlide", $"ppt/notesSlides/notesSlide{number}.xml");
        }
        Write(sheet.Part, xml.ToString(), PresentationTypes + "slide+xml");
        Write(sheet.RelsPart, PresentationPackage.Rels([.. sheet.Rels]));
    }

    /// <summary>A background from the drawing's background shape; null when it has none or no fill.</summary>
    private string? Background(OfficeArtShape? shape, Sheet sheet)
    {
        if (shape is null)
            return null;
        var fill = FillXml(Properties(shape, defaults: true), sheet, shape);
        return fill is null or "<a:noFill/>" ? null : $"<p:bg><p:bgPr>{fill}<a:effectLst/></p:bgPr></p:bg>";
    }

    /// <summary>The theme of a master copy: its colour scheme, and the title and body styles' first fonts as the theme's fonts.</summary>
    private string Theme(Variant variant)
    {
        var scheme = variant.Scheme;
        string Color(string name, int index) => $"<a:{name}><a:srgbClr val=\"{scheme[index]:X6}\"/></a:{name}>";
        string Font(int type) =>
            Escape(variant.Main.Styles.Resolved(type)[0]?.Character.Font is { } font && FontName(font) is { } face ? face : FontName(0) ?? "Arial");
        const string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        var lines = string.Concat(new[] { "9525", "25400", "38100" }.Select(w => $"<a:ln w=\"{w}\">{fill}</a:ln>"));
        return "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office Theme\"><a:themeElements>" +
               "<a:clrScheme name=\"PowerPoint\">" +
               Color("dk1", 1) + Color("lt1", 0) + Color("dk2", 3) + Color("lt2", 2) +
               Color("accent1", 4) + Color("accent2", 5) + Color("accent3", 6) + Color("accent4", 7) + Color("accent5", 4) + Color("accent6", 5) +
               Color("hlink", 6) + Color("folHlink", 7) + "</a:clrScheme>" +
               $"<a:fontScheme name=\"PowerPoint\"><a:majorFont><a:latin typeface=\"{Font(TitleText)}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
               $"<a:minorFont><a:latin typeface=\"{Font(BodyText)}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
               "<a:fmtScheme name=\"PowerPoint\">" +
               $"<a:fillStyleLst>{fill}{fill}{fill}</a:fillStyleLst><a:lnStyleLst>{lines}</a:lnStyleLst>" +
               "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
               $"<a:bgFillStyleLst>{fill}{fill}{fill}</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }

    // ───────────────────────── Notes ─────────────────────────

    /// <summary>The notes master: the slide's picture in the upper half of the page, the notes below, and a theme of its own.</summary>
    private void WriteNotesMaster(int theme)
    {
        Write("ppt/notesMasters/notesMaster1.xml",
            $"<p:notesMaster {Namespaces}><p:cSld>{PresentationPackage.TreeStart}" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"685800\"/><a:ext cx=\"5486400\" cy=\"3429000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"body\" sz=\"quarter\" idx=\"1\"/></p:nvPr></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"4343400\"/><a:ext cx=\"5486400\" cy=\"4114800\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
            "<p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>" +
            $"</p:spTree></p:cSld>{PresentationPackage.ColorMap}" +
            "<p:notesStyle><a:lvl1pPr marL=\"0\" algn=\"l\"><a:defRPr sz=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
            "<a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:notesStyle></p:notesMaster>", PresentationTypes + "notesMaster+xml");
        Write("ppt/notesMasters/_rels/notesMaster1.xml.rels", PresentationPackage.Rels(("rId1", "theme", $"../theme/theme{theme}.xml")));
        Write($"ppt/theme/theme{theme}.xml", PresentationPackage.Theme(), PresentationPackage.ThemeType);
    }

    /// <summary>The notes slide of slide <paramref name="number"/>: the text of its notes placeholder, a paragraph a line.</summary>
    private void WriteNotesSlide(OfficeArtRecord notes, int number)
    {
        var body = new StringBuilder();
        foreach (var text in NotesText(notes))
        {
            foreach (var line in text.Characters.Split('\r'))
            {
                _budget.Paragraph();
                var content = Escape(line.Replace('\v', '\n'));
                body.Append(content.Length == 0 ? "<a:p/>" : $"<a:p><a:r><a:rPr lang=\"en-US\" dirty=\"0\"/><a:t>{content}</a:t></a:r></a:p>");
            }
        }
        if (body.Length == 0)
            body.Append("<a:p/>");
        Write($"ppt/notesSlides/notesSlide{number}.xml",
            $"<p:notes {Namespaces}><p:cSld>{PresentationPackage.TreeStart}" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
            $"<p:txBody><a:bodyPr/><a:lstStyle/>{body}</p:txBody></p:sp>" +
            "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>", PresentationTypes + "notesSlide+xml");
        Write($"ppt/notesSlides/_rels/notesSlide{number}.xml.rels", PresentationPackage.Rels(
            ("rId1", "notesMaster", "../notesMasters/notesMaster1.xml"),
            ("rId2", "slide", $"../slides/slide{number}.xml")));
    }

    /// <summary>The text of a notes page's notes placeholder (or of its notes-type text).</summary>
    private IEnumerable<ShapeText> NotesText(OfficeArtRecord notes)
    {
        var bytes = _file.Stream;
        if (OfficeArtRecords.Child(bytes, notes, PptRecords.Drawing) is not { IsContainer: true } drawing
            || OfficeArtRecords.Child(bytes, drawing, OfficeArtRecords.DgContainer) is not { IsContainer: true } dg
            || OfficeArtRecords.Child(bytes, dg, OfficeArtRecords.SpgrContainer) is not { IsContainer: true } group)
            yield break;
        foreach (var child in OfficeArtRecords.Children(bytes, group))
        {
            if (child.Type != OfficeArtRecords.SpContainer)
                continue;
            _budget.Object();
            var shape = OfficeArtShape.Read(bytes, child);
            if (TextOf(shape, null) is { Type: NotesTextType } text && (Placeholder(shape)?.Placement is null or 12 or 6))
                yield return text;
        }
    }
}
