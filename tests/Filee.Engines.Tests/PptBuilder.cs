// PowerPoint 97–2003 (PPT) files built in code for the PPT reader's tests: the PowerPoint Document stream with its
// DocumentContainer (DocumentAtom, Environment with fonts, drawing group with default shape properties and the BLIP
// store, the master, slide and notes lists), a main master (text master styles, colour scheme, drawing), an optional
// title master, slides and notes pages with OfficeArt drawings, the persist directory and user edit; the Current User
// stream; pictures in the Pictures stream. Packed as a compound file with the HWP builder's writer. Flags make files the
// reader refuses or must survive (encrypted, PowerPoint 95, a user edit that points to itself).

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class PptBuilder
{
    /// <summary>A slide, master or notes page: its shapes, background, colour scheme, SlideAtom flags and outline text.</summary>
    public sealed class Sheet
    {
        public List<byte[]> Shapes { get; } = [];

        /// <summary>The background shape's property table entries (fill), or null for none.</summary>
        public (int Pid, uint Value)[]? Background { get; set; }

        /// <summary>A colour scheme of its own (eight RRGGBB values), or null.</summary>
        public uint[]? Scheme { get; set; }

        /// <summary>SlideAtom flags: 1 master objects, 2 master scheme, 4 master background.</summary>
        public ushort Flags { get; set; } = 7;

        /// <summary>Text records of the slide list after this slide's SlidePersistAtom (OutlineText).</summary>
        public List<byte[]> Outline { get; } = [];

        public bool OnTitleMaster { get; set; }

        public bool Hidden { get; set; }

        /// <summary>The notes page's text, or null for none.</summary>
        public string? Notes { get; set; }
    }

    public static readonly uint[] DefaultScheme = [0xFFFFFF, 0x000000, 0x808080, 0x003399, 0x00CC99, 0x3333CC, 0xCCCCFF, 0xB2B2B2];

    public (int Width, int Height) SlideSize { get; set; } = (5760, 4320);

    public List<string> Fonts { get; } = ["Arial", "Wingdings"];

    public uint[] Scheme { get; set; } = DefaultScheme;

    public Sheet Master { get; } = new();

    /// <summary>TextMasterStyleAtoms of the main master (whole records); the default ones when left empty.</summary>
    public List<byte[]> MasterStyles { get; } = [];

    public Sheet? TitleMaster { get; set; }

    public List<Sheet> Slides { get; } = [];

    /// <summary>PNG pictures of the BLIP store, in the Pictures stream; a shape's pib is the 1-based index.</summary>
    public List<byte[]> Pictures { get; } = [];

    /// <summary>Raw BLIP records of the store (instead of PNGs), stored in the Pictures stream.</summary>
    public List<byte[]> Blips { get; } = [];

    public bool Encrypted { get; set; }

    /// <summary>A PowerPoint 95 file: the Current User atom names another file version.</summary>
    public bool PowerPoint95 { get; set; }

    /// <summary>The user edit's previous edit is itself.</summary>
    public bool EditLoop { get; set; }

    // ───────────────────────── Records ─────────────────────────

    public static byte[] Atom(int type, byte[] body, int instance = 0, int version = 0)
    {
        var record = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)(version | instance << 4));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), body.Length);
        body.CopyTo(record, 8);
        return record;
    }

    public static byte[] Container(int type, int instance, params byte[][] children) => Atom(type, [.. children.SelectMany(c => c)], instance, 0xF);

    public static byte[] Bytes(params object[] values)
    {
        var stream = new MemoryStream();
        foreach (var value in values)
        {
            switch (value)
            {
                case byte b:
                    stream.WriteByte(b);
                    break;
                case short s:
                    stream.Write(BitConverter.GetBytes(s));
                    break;
                case ushort u:
                    stream.Write(BitConverter.GetBytes(u));
                    break;
                case int i:
                    stream.Write(BitConverter.GetBytes(i));
                    break;
                case uint u:
                    stream.Write(BitConverter.GetBytes(u));
                    break;
                case byte[] bytes:
                    stream.Write(bytes);
                    break;
                default:
                    throw new ArgumentException(value.GetType().Name);
            }
        }
        return stream.ToArray();
    }

    // ───────────────────────── Shapes ─────────────────────────

    /// <summary>A property table (OfficeArtFOPT) of simple properties.</summary>
    public static byte[] Fopt(params (int Pid, uint Value)[] properties) =>
        Atom(0xF00B, [.. properties.SelectMany(p => Bytes((ushort)p.Pid, p.Value))], properties.Length, 3);

    /// <summary>
    /// A shape (OfficeArtSpContainer): MSOSPT <paramref name="type"/>, anchored at (left, top, right, bottom) in master
    /// units; a child anchor when it is in a group.
    /// </summary>
    public static byte[] Shape(int spid, int type, (int Left, int Top, int Right, int Bottom) anchor, (int Pid, uint Value)[]? properties = null,
        byte[]? clientData = null, byte[]? text = null, bool child = false, uint flags = 0)
    {
        var parts = new List<byte[]>
        {
            Atom(0xF00A, Bytes(spid, 0x200u | 0x800u | (child ? 0x2u : 0) | flags), type, 2),
        };
        if (properties is { Length: > 0 })
            parts.Add(Fopt(properties));
        parts.Add(child
            ? Atom(0xF00F, Bytes(anchor.Left, anchor.Top, anchor.Right, anchor.Bottom))
            : Atom(0xF010, Bytes((short)anchor.Top, (short)anchor.Left, (short)anchor.Right, (short)anchor.Bottom)));
        if (clientData is not null)
            parts.Add(clientData);
        if (text is not null)
            parts.Add(text);
        return Container(0xF004, 0, [.. parts]);
    }

    /// <summary>A group (OfficeArtSpgrContainer): its group shape with the child coordinate space, then its shapes.</summary>
    public static byte[] Group(int spid, (int Left, int Top, int Right, int Bottom) anchor, (int Left, int Top, int Right, int Bottom) space,
        bool child = false, params byte[][] shapes)
    {
        var groupShape = Container(0xF004, 0,
            Atom(0xF009, Bytes(space.Left, space.Top, space.Right, space.Bottom), version: 1),
            Atom(0xF00A, Bytes(spid, 0x1u | 0x200u | (child ? 0x2u : 0)), 0, 2),
            child
                ? Atom(0xF00F, Bytes(anchor.Left, anchor.Top, anchor.Right, anchor.Bottom))
                : Atom(0xF010, Bytes((short)anchor.Top, (short)anchor.Left, (short)anchor.Right, (short)anchor.Bottom)));
        return Container(0xF003, 0, [groupShape, .. shapes]);
    }

    /// <summary>Client data with an OEPlaceholderAtom (PlaceholderEnum, position).</summary>
    public static byte[] Placeholder(int placement, int position = 0) =>
        Container(0xF011, 0, Atom(0x0BC3, Bytes(position, (byte)placement, (byte)0, (ushort)0)));

    /// <summary>A client text box with its own text: header, characters, styles.</summary>
    public static byte[] TextBox(int textType, string text, byte[]? styles = null, bool bytes = false) =>
        Container(0xF00D, 0, [.. TextRecords(textType, text, styles, bytes)]);

    /// <summary>A client text box that refers to the slide list's outline text <paramref name="index"/>.</summary>
    public static byte[] OutlineRef(int index) => Container(0xF00D, 0, Atom(0x0F9E, Bytes(index)));

    /// <summary>The text records of a text: TextHeaderAtom, TextCharsAtom (or TextBytesAtom), StyleTextPropAtom.</summary>
    public static List<byte[]> TextRecords(int textType, string text, byte[]? styles = null, bool bytes = false)
    {
        var records = new List<byte[]>
        {
            Atom(0x0F9F, Bytes(textType)),
            bytes ? Atom(0x0FA8, Encoding.Latin1.GetBytes(text)) : Atom(0x0FA0, Encoding.Unicode.GetBytes(text)),
        };
        if (styles is not null)
            records.Add(styles);
        return records;
    }

    // ───────────────────────── Text formats ─────────────────────────

    /// <summary>
    /// A TextPFException with the fields given (in the order the format keeps them). <paramref name="full"/> writes every
    /// field, the ones not given with PowerPoint's defaults, as PowerPoint saves the first level of a master's text
    /// styles: LibreOffice reads that level's fields by older mask bits, so only a full set lines up with the format.
    /// </summary>
    public static byte[] Paragraph(bool? bullet = null, char? bulletChar = null, int? bulletFont = null, int? align = null,
        short? lineSpacing = null, short? spaceBefore = null, short? leftMargin = null, short? indent = null, bool full = false)
    {
        if (full)
        {
            // Masks 0x3FFDFF: bullet flags, font, colour, size, character, left margin, indent, alignment, line spacing,
            // space before / after, default tab, font alignment, wrap flags, tab stops, text direction. The bullet colour is
            // scheme colour 1 (text), unused while the flags leave it off.
            var flags = (ushort)((bullet == true ? 1 : 0) | (bulletFont is null ? 0 : 2));
            return Bytes(0x3FFDFFu, flags, (ushort)(bulletChar ?? '•'), (ushort)(bulletFont ?? 0), (short)100, 0x01000000u,
                (ushort)(align ?? 0), lineSpacing ?? (short)100, spaceBefore ?? (short)0, (short)0, leftMargin ?? (short)0, indent ?? (short)0,
                (short)576, (ushort)0, (ushort)0, (ushort)0x2, (ushort)0);
        }
        uint masks = 0;
        var fields = new List<object>();
        if (bullet is { } on)
        {
            masks |= 0x1 | (bulletFont is null ? 0 : 0x2u);
            fields.Add((ushort)((on ? 1 : 0) | (bulletFont is null ? 0 : 2)));
        }
        if (bulletChar is { } c)
        {
            masks |= 1u << 7;
            fields.Add((ushort)c);
        }
        if (bulletFont is { } font)
        {
            masks |= 1u << 4;
            fields.Add((ushort)font);
        }
        if (align is { } a)
        {
            masks |= 1u << 11;
            fields.Add((ushort)a);
        }
        if (lineSpacing is { } l)
        {
            masks |= 1u << 12;
            fields.Add(l);
        }
        if (spaceBefore is { } sb)
        {
            masks |= 1u << 13;
            fields.Add(sb);
        }
        if (leftMargin is { } lm)
        {
            masks |= 1u << 8;
            fields.Add(lm);
        }
        if (indent is { } i)
        {
            masks |= 1u << 10;
            fields.Add(i);
        }
        return Bytes([masks, .. fields]);
    }

    /// <summary>A TextCFException with the fields given; a colour is RRGGBB, or a scheme index below 8.</summary>
    public static byte[] Character(bool? bold = null, bool? italic = null, int? font = null, short? size = null, uint? color = null)
    {
        uint masks = 0;
        ushort style = 0;
        if (bold is { } b)
        {
            masks |= 0x1;
            style |= (ushort)(b ? 1 : 0);
        }
        if (italic is { } i)
        {
            masks |= 0x2;
            style |= (ushort)(i ? 2 : 0);
        }
        var fields = new List<object> { masks };
        if ((masks & 0x3) != 0)
            fields.Add(style);
        if (font is { } f)
        {
            masks |= 1u << 16;
            fields.Add((ushort)f);
        }
        if (size is { } s)
        {
            masks |= 1u << 17;
            fields.Add(s);
        }
        if (color is { } c)
        {
            masks |= 1u << 18;
            fields.Add(c < 8 ? Bytes((byte)0, (byte)0, (byte)0, (byte)c) : Bytes((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)0xFE));
        }
        fields[0] = masks;
        return Bytes([.. fields]);
    }

    /// <summary>A StyleTextPropAtom: paragraph runs (characters, indent level, format), then character runs.</summary>
    public static byte[] Styles((int Count, int Level, byte[] Format)[] paragraphs, (int Count, byte[] Format)[] runs) =>
        Atom(0x0FA1, Bytes([
            .. paragraphs.SelectMany(p => new object[] { p.Count, (ushort)p.Level, p.Format }),
            .. runs.SelectMany(r => new object[] { r.Count, r.Format }),
        ]));

    /// <summary>A TextMasterStyleAtom of text type <paramref name="type"/> with its levels' paragraph and character formats.</summary>
    public static byte[] MasterStyle(int type, params (byte[] Paragraph, byte[] Character)[] levels) =>
        Atom(0x0FA3, Bytes([
            (ushort)levels.Length,
            .. levels.SelectMany((l, i) => type >= 5 ? new object[] { (ushort)i, l.Paragraph, l.Character } : [l.Paragraph, l.Character]),
        ]), type);

    /// <summary>
    /// The default master styles: a 44 pt centred title in the title colour, a bulleted body in five levels, other text
    /// 18 pt. The first level of the title, body and other text has every paragraph field, as PowerPoint writes it.
    /// </summary>
    public static List<byte[]> DefaultMasterStyles() =>
    [
        MasterStyle(0, (Paragraph(bullet: false, align: 1, full: true), Character(bold: false, font: 0, size: 44, color: 3))),
        MasterStyle(1,
            (Paragraph(bullet: true, bulletChar: '•', align: 0, leftMargin: 216, indent: 0, full: true), Character(font: 0, size: 32, color: 1)),
            (Paragraph(bullet: true, bulletChar: '–', align: 0, leftMargin: 576, indent: 360), Character(font: 0, size: 28, color: 1)),
            (Paragraph(bullet: true, bulletChar: '•', align: 0, leftMargin: 936, indent: 720), Character(font: 0, size: 24, color: 1)),
            (Paragraph(bullet: true, bulletChar: '–', align: 0, leftMargin: 1296, indent: 1080), Character(font: 0, size: 20, color: 1)),
            (Paragraph(bullet: true, bulletChar: '»', align: 0, leftMargin: 1656, indent: 1440), Character(font: 0, size: 20, color: 1))),
        MasterStyle(4, (Paragraph(bullet: false, align: 0, full: true), Character(font: 0, size: 18, color: 1))),
        MasterStyle(6, (Paragraph(align: 1), Character(size: 40))),
        MasterStyle(5, (Paragraph(bullet: false, align: 1, leftMargin: 0, indent: 0), Character(size: 28))),
    ];

    // ───────────────────────── File ─────────────────────────

    public byte[] Build()
    {
        var stream = new MemoryStream();
        var persist = new List<(int Id, int Offset)>();
        int Add(int id, byte[] record)
        {
            persist.Add((id, (int)stream.Position));
            stream.Write(record);
            return id;
        }

        const uint mainMasterId = 0x80000000, titleMasterId = 0x80000001;
        var nextPersist = 2;
        var masterPersist = nextPersist++;
        var titlePersist = TitleMaster is null ? 0 : nextPersist++;
        var slidePersists = Slides.Select(_ => nextPersist++).ToList();
        var notesPersists = Slides.Select(s => s.Notes is null ? 0 : nextPersist++).ToList();

        var spid = 1024;
        int Spid() => spid++;

        // The masters, slides and notes first; the document after them.
        Add(masterPersist, Container(0x03F8, 0, [
            SlideAtom(0, 0, 0),
            .. (MasterStyles.Count > 0 ? MasterStyles : DefaultMasterStyles()),
            Drawing(Master, Spid),
            SchemeAtom(Scheme),
        ]));
        if (TitleMaster is { } title)
        {
            Add(titlePersist, Container(0x03EE, 0, [
                SlideAtom(mainMasterId, 0, title.Flags),
                Drawing(title, Spid),
                .. title.Scheme is { } scheme ? [SchemeAtom(scheme)] : Array.Empty<byte[]>(),
            ]));
        }
        for (var i = 0; i < Slides.Count; i++)
        {
            var slide = Slides[i];
            var children = new List<byte[]>
            {
                SlideAtom(slide.OnTitleMaster && TitleMaster is not null ? titleMasterId : mainMasterId, slide.Notes is null ? 0 : (uint)(0x100 + i), slide.Flags),
            };
            if (slide.Hidden)
                children.Add(Atom(0x03F9, Bytes(0, 0u, (byte)0, (byte)0, (ushort)0x4, (byte)0, (byte)0, (byte)0, (byte)0), version: 0));
            children.Add(Drawing(slide, Spid));
            if (slide.Scheme is { } scheme)
                children.Add(SchemeAtom(scheme));
            Add(slidePersists[i], Container(0x03EE, 0, [.. children]));
            if (slide.Notes is { } notes)
            {
                var notesSheet = new Sheet();
                notesSheet.Shapes.Add(Shape(Spid(), 1, (288, 2880, 5472, 5400), [(0x01BF, 0x00100000), (0x01FF, 0x00080000)], Placeholder(12, 1), TextBox(2, notes)));
                Add(notesPersists[i], Container(0x03F0, 0, Atom(0x03F1, Bytes((uint)(256 + i), (ushort)0, (ushort)0), version: 1), Drawing(notesSheet, Spid)));
            }
        }

        // Pictures: the BLIP store's FBSEs point into the Pictures stream.
        var pictures = new MemoryStream();
        var fbses = new List<byte[]>();
        foreach (var blip in Pictures.Select(png => Atom(0xF01E, [.. new byte[16], 0xFF, .. png], 0x6E0)).Concat(Blips))
        {
            var offset = (uint)pictures.Position;
            pictures.Write(blip);
            fbses.Add(Atom(0xF007, Bytes((byte)6, (byte)6, new byte[16], (ushort)0xFF, (uint)blip.Length, 1u, offset, (byte)0, (byte)0, (byte)0, (byte)0), 6, 2));
        }

        var fonts = Fonts.Select((face, index) =>
        {
            var name = new byte[64];
            Encoding.Unicode.GetBytes(face).AsSpan(0, Math.Min(62, face.Length * 2)).CopyTo(name);
            return Atom(0x0FB7, Bytes(name, (byte)0, (byte)0, (byte)0, (byte)0x22), index);
        }).ToArray();
        var masterList = new List<byte[]> { PersistAtom(masterPersist, mainMasterId) };
        if (TitleMaster is not null)
            masterList.Add(PersistAtom(titlePersist, titleMasterId));
        var slideList = new List<byte[]>();
        for (var i = 0; i < Slides.Count; i++)
        {
            // The outline's TextHeaderAtoms are numbered by their record instance, as PowerPoint saves them: an
            // OutlineTextRefAtom names that number (LibreOffice finds the text by it). Left as they are when a test
            // numbered any of them itself. cTexts counts them.
            static bool IsHeader(byte[] record) => record.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(2)) == 0x0F9F;
            var numbered = Slides[i].Outline.Any(r => IsHeader(r) && BinaryPrimitives.ReadUInt16LittleEndian(r) >> 4 != 0);
            var texts = 0;
            var outline = new List<byte[]>();
            foreach (var record in Slides[i].Outline)
            {
                var copy = record;
                if (IsHeader(record))
                {
                    var header = BinaryPrimitives.ReadUInt16LittleEndian(record);
                    if (!numbered)
                    {
                        copy = (byte[])record.Clone();
                        BinaryPrimitives.WriteUInt16LittleEndian(copy, (ushort)(header & 0xF | texts << 4));
                    }
                    texts++;
                }
                outline.Add(copy);
            }
            slideList.Add(PersistAtom(slidePersists[i], (uint)(256 + i), texts));
            slideList.AddRange(outline);
        }
        var notesList = Slides.Select((s, i) => s.Notes is null ? null : PersistAtom(notesPersists[i], (uint)(0x100 + i))).OfType<byte[]>().ToArray();
        var document = Container(0x03E8, 0, [
            Atom(0x03E9, Bytes(SlideSize.Width, SlideSize.Height, 4320, 5760, 1, 2, 0u, 0u, (ushort)1, (ushort)0, (byte)0, (byte)0, (byte)0, (byte)0), version: 1),
            Container(0x03F2, 0, Container(0x07D5, 0, fonts)),
            Container(0x040B, 0, Container(0xF000, 0,
                Atom(0xF006, Bytes((uint)spid, 1u, (uint)(spid - 1024), 1u)),
                Fopt((0x0181, 0x08000004), (0x01BF, 0x00100010), (0x01C0, 0x08000001), (0x01FF, 0x00080008)),
                Container(0xF001, fbses.Count, [.. fbses]))),
            Container(0x0FF0, 1, [.. masterList]),
            Container(0x0FF0, 0, [.. slideList]),
            .. notesList.Length > 0 ? [Container(0x0FF0, 2, notesList)] : Array.Empty<byte[]>(),
            Atom(0x03EA, []),
        ]);
        Add(1, document);

        // The persist directory: ids 1..n, each with its offset.
        var ordered = persist.OrderBy(p => p.Id).ToList();
        var directory = (int)stream.Position;
        var entries = new List<object>();
        foreach (var (id, offset) in ordered)
        {
            entries.Add((uint)(id | 1 << 20));
            entries.Add(offset);
        }
        stream.Write(Atom(0x1772, Bytes([.. entries])));
        var edit = (int)stream.Position;
        stream.Write(Atom(0x0FF5, Encrypted
            ? Bytes(256u, (ushort)0, (byte)0, (byte)3, EditLoop ? (uint)edit : 0u, (uint)directory, 1u, (uint)nextPersist, (ushort)1, (ushort)0, 99u)
            : Bytes(256u, (ushort)0, (byte)0, (byte)3, EditLoop ? (uint)edit : 0u, (uint)directory, 1u, (uint)nextPersist, (ushort)1, (ushort)0)));

        var user = "filee"u8.ToArray();
        var current = Atom(0x0FF6, Bytes(0x14u, Encrypted ? 0xF3D1C4DFu : 0xE391C05Fu, (uint)edit, (ushort)user.Length,
            (ushort)(PowerPoint95 ? 0x03F3 : 0x03F4), (byte)3, (byte)0, (ushort)0, user, 8u));

        var streams = new List<(string, byte[])>
        {
            ("Current User", current),
            ("PowerPoint Document", stream.ToArray()),
        };
        if (pictures.Length > 0)
            streams.Add(("Pictures", pictures.ToArray()));
        return HwpBuilder.Cfb(streams, standard: true);
    }

    private static byte[] SlideAtom(uint master, uint notes, ushort flags) =>
        Atom(0x03EF, Bytes(0u, new byte[8], master, notes, flags, (ushort)0), version: 2);

    private static byte[] SchemeAtom(uint[] scheme) =>
        Atom(0x07F0, Bytes([.. scheme.Select(c => Bytes((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)0))]), 1);

    private static byte[] PersistAtom(int persist, uint slideId, int texts = 0) => Atom(0x03F3, Bytes((uint)persist, 0u, texts, slideId, 0u));

    /// <summary>A DrawingContainer: the patriarch group with the sheet's shapes, and the background shape.</summary>
    private static byte[] Drawing(Sheet sheet, Func<int> spid)
    {
        var patriarch = Container(0xF004, 0, Atom(0xF009, new byte[16], version: 1), Atom(0xF00A, Bytes(spid(), 0x5u), 0, 2));
        var children = new List<byte[]>
        {
            Atom(0xF008, Bytes((uint)sheet.Shapes.Count + 1, 0u), 1),
            Container(0xF003, 0, [patriarch, .. sheet.Shapes]),
        };
        if (sheet.Background is { } fill)
            children.Add(Container(0xF004, 0, Atom(0xF00A, Bytes(spid(), 0x400u | 0x800u), 1, 2), Fopt(fill)));
        return Container(0x040C, 0, Container(0xF002, 1, [.. children]));
    }
}
