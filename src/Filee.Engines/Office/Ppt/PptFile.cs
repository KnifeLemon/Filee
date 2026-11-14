// A PowerPoint 97–2003 presentation (.ppt, .pps, .pot) opened from its compound file ([MS-PPT], [MS-CFB]): the
// Current User stream names the last save, whose persist directory (PptPersist) leads to the DocumentContainer in the
// PowerPoint Document stream. From it: the slide size (DocumentAtom), the fonts (Environment → FontCollection), the
// drawing group (default shape properties and the BLIP store, whose pictures are in the Pictures stream), and the
// lists of masters, slides and notes (SlideListWithText): a SlidePersistAtom for each, which names its container by
// persist id, followed on the slide list by the text of the slide's placeholders (the outline).
//
// What Filee can't read is refused with a message before anything else is read: presentations protected with a
// password (nothing is ever decrypted), PowerPoint 95 and older files (no PowerPoint 97 Current User atom), and
// compound files that hold no presentation. Unsupported names the old versions for the route planner, which then takes
// LibreOffice when it is installed.
//
// Everything points into a stream that comes from anywhere: records are read only where they fit in what holds them,
// persist ids only when the directory lists them, and damage that leaves no document is InvalidDataException.

using Filee.Engines.Documents;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

/// <summary>A slide, master or notes page as the document lists it, with the outline text a slide's placeholders use.</summary>
internal sealed record PptListEntry(uint PersistId, uint SlideId, List<List<OfficeArtRecord>> Texts);

internal sealed class PptFile : IDisposable
{
    private const uint HeaderToken = 0xE391C05F;
    private const uint EncryptedToken = 0xF3D1C4DF;
    private const ushort DocFileVersion = 0x03F4;

    private const string Encrypted =
        "The presentation is protected with a password (encrypted). Open it in PowerPoint, remove the password (File → Info → Protect Presentation), save it and convert it again.";

    private const string Old =
        "The file is a PowerPoint 95 or older presentation, which Filee reads only through LibreOffice. Install LibreOffice in Settings → Engines, or open it in PowerPoint and save it as a PowerPoint presentation, and convert it again.";

    private readonly CompoundFile _cfb;

    private PptFile(CompoundFile cfb, byte[] stream, PptPersist persist, OfficeArtRecord document)
    {
        _cfb = cfb;
        Stream = stream;
        Persist = persist;
        DocumentContainer = document;
    }

    public DocumentLimits Limits => _cfb.Limits;

    /// <summary>Counts bytes unpacked from the presentation (inflated pictures) against its total.</summary>
    public void Count(long bytes) => _cfb.Count(bytes);

    /// <summary>The PowerPoint Document stream.</summary>
    public byte[] Stream { get; }

    /// <summary>The Pictures stream (where the BLIP store's pictures are); empty when there is none.</summary>
    public byte[] Pictures { get; private set; } = [];

    public PptPersist Persist { get; }

    public OfficeArtRecord DocumentContainer { get; }

    /// <summary>Slide size in master units (576 per inch).</summary>
    public (int Width, int Height) SlideSize { get; private set; } = (5760, 4320);

    /// <summary>Font faces by their index in the font collection.</summary>
    public Dictionary<int, string> Fonts { get; } = [];

    /// <summary>The properties every shape starts with (the drawing group's default property tables).</summary>
    public Dictionary<int, uint> DefaultProperties { get; } = [];

    /// <summary>The BLIP store's FBSEs, in order: a picture's pib is its 1-based index here.</summary>
    public List<OfficeArtRecord> Blips { get; } = [];

    public List<PptListEntry> Masters { get; } = [];
    public List<PptListEntry> Slides { get; } = [];
    public List<PptListEntry> NotesPages { get; } = [];

    /// <summary>The Environment's text master style for text outside placeholders (Tx_TYPE_OTHER), when there is one.</summary>
    public OfficeArtRecord? DefaultTextStyle { get; private set; }

    // ───────────────────────── Opening ─────────────────────────

    /// <summary>Opens <paramref name="path"/>, refusing what Filee doesn't read.</summary>
    public static PptFile Open(string path, DocumentLimits limits)
    {
        if (!CompoundFile.HasSignature(path))
            throw new InvalidDataException("The file is not a PowerPoint 97–2003 presentation (it is not a compound file). It may be damaged or another program's file with a .ppt name.");
        var cfb = CompoundFile.Open(path, limits);
        try
        {
            var (stream, currentEdit) = Refuse(cfb);
            var persist = PptPersist.Read(stream, currentEdit);
            if (persist.Encrypted)
                throw new InvalidDataException(Encrypted);
            if (persist.Record(persist.DocumentRef) is not { Type: PptRecords.Document, IsContainer: true } document)
                throw new InvalidDataException("The presentation is damaged: its document record is missing.");
            var file = new PptFile(cfb, stream, persist, document);
            file.Load();
            return file;
        }
        catch
        {
            cfb.Dispose();
            throw;
        }
    }

    public void Dispose() => _cfb.Dispose();

    /// <summary>
    /// What Filee doesn't read in a compound file, in a few words ("a PowerPoint 95 or older presentation"); null when
    /// it is a PowerPoint 97–2003 presentation (or is damaged or encrypted, which the reader then says). For the route
    /// planner: such a file takes another engine (LibreOffice) when there is one.
    /// </summary>
    public static string? Unsupported(string path)
    {
        try
        {
            if (!CompoundFile.HasSignature(path))
                return null;
            using var cfb = CompoundFile.Open(path);
            if (cfb.Exists("EncryptedPackage") || cfb.Exists("EncryptionInfo"))
                return null;
            if (!cfb.Exists("PowerPoint Document"))
                return cfb.Exists("PP40") ? "a PowerPoint 4.0 presentation" : "a compound file without a PowerPoint presentation";
            return CurrentEdit(cfb.Read("Current User")) is null ? "a PowerPoint 95 or older presentation" : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or Core.Conversion.ConversionRefusedException)
        {
            return null; // the reader says what is wrong
        }
    }

    /// <summary>The PowerPoint Document stream and the offset of its last edit, or a refusal with the reason.</summary>
    private static (byte[] Stream, uint CurrentEdit) Refuse(CompoundFile cfb)
    {
        if (cfb.Exists("EncryptedPackage") || cfb.Exists("EncryptionInfo"))
            throw new InvalidDataException(Encrypted);
        if (!cfb.Exists("PowerPoint Document"))
        {
            if (cfb.Exists("PP40"))
                throw new InvalidDataException(Old);
            throw new InvalidDataException("The file is not a PowerPoint presentation (it has no PowerPoint Document stream). It may be another program's file with a .ppt name.");
        }
        var current = cfb.Read("Current User");
        if (CurrentEdit(current) is not { } edit)
            throw new InvalidDataException(Old);
        if (PptRecords.U32(current!, 12) == EncryptedToken)
            throw new InvalidDataException(Encrypted);
        return (cfb.Read("PowerPoint Document")!, edit);
    }

    /// <summary>
    /// The offset of the last edit from a PowerPoint 97 or later CurrentUserAtom (header token, file version 0x03F4,
    /// major version 3); null for anything else.
    /// </summary>
    private static uint? CurrentEdit(byte[]? current)
    {
        if (current is null || PptRecords.At(current, 0) is not { Type: PptRecords.CurrentUserAtom, Length: >= 20 })
            return null;
        var token = PptRecords.U32(current, 12);
        if (token is not (HeaderToken or EncryptedToken) || PptRecords.U16(current, 22) != DocFileVersion || current[24] < 3)
            return null;
        return PptRecords.U32(current, 16);
    }

    // ───────────────────────── Document ─────────────────────────

    private void Load()
    {
        var bytes = Stream;
        foreach (var child in OfficeArtRecords.Children(bytes, DocumentContainer))
        {
            switch (child.Type)
            {
                case PptRecords.DocumentAtom when child.Length >= 8:
                    SlideSize = (PptRecords.I32(bytes, child.Start), PptRecords.I32(bytes, child.Start + 4));
                    break;
                case PptRecords.Environment when child.IsContainer:
                    ReadEnvironment(child);
                    break;
                case PptRecords.DrawingGroup when child.IsContainer:
                    ReadDrawingGroup(child);
                    break;
                case PptRecords.SlideListWithText when child.IsContainer:
                    var list = child.Instance switch
                    {
                        0 => Slides,
                        1 => Masters,
                        2 => NotesPages,
                        _ => null,
                    };
                    // One list of each kind; a second one would list the same pages again.
                    if (list is { Count: 0 })
                        ReadList(child, list);
                    break;
            }
        }
        if (_cfb.Exists("Pictures"))
            Pictures = _cfb.Read("Pictures") ?? [];
    }

    private void ReadEnvironment(OfficeArtRecord environment)
    {
        foreach (var child in OfficeArtRecords.Children(Stream, environment))
        {
            if (child.Type == PptRecords.FontCollection && child.IsContainer)
            {
                foreach (var font in OfficeArtRecords.Children(Stream, child))
                {
                    if (font.Type == PptRecords.FontEntityAtom && font.Length >= 64)
                        Fonts.TryAdd(font.Instance, FaceName(Stream.AsSpan(font.Start, 64)));
                }
            }
            else if (child.Type == PptRecords.TextMasterStyleAtom)
            {
                DefaultTextStyle ??= child;
            }
        }
    }

    /// <summary>A LOGFONT face name: up to 32 UTF-16 characters, ended by a NUL.</summary>
    private static string FaceName(ReadOnlySpan<byte> bytes)
    {
        var name = System.Text.Encoding.Unicode.GetString(bytes);
        var end = name.IndexOf('\0');
        return (end >= 0 ? name[..end] : name).Trim();
    }

    private void ReadDrawingGroup(OfficeArtRecord group)
    {
        if (OfficeArtRecords.Child(Stream, group, OfficeArtRecords.DggContainer) is not { IsContainer: true } dgg)
            return;
        foreach (var child in OfficeArtRecords.Children(Stream, dgg))
        {
            switch (child.Type)
            {
                case OfficeArtRecords.Fopt or OfficeArtRecords.SecondaryFopt:
                    OfficeArtProperties.Read(Stream, child, DefaultProperties);
                    break;
                case OfficeArtRecords.BStoreContainer when child.IsContainer:
                    foreach (var fbse in OfficeArtRecords.Children(Stream, child))
                    {
                        if (Blips.Count >= Limits.MaxObjects)
                            throw DocumentLimits.Exceeded($"The presentation has more than {DocumentLimits.Count(Limits.MaxObjects)} pictures.");
                        Blips.Add(fbse);
                    }
                    break;
            }
        }
    }

    /// <summary>The SlidePersistAtoms of a list, each with the text records that follow it, grouped by text header.</summary>
    private void ReadList(OfficeArtRecord container, List<PptListEntry> list)
    {
        PptListEntry? current = null;
        foreach (var child in OfficeArtRecords.Children(Stream, container))
        {
            if (child.Type == PptRecords.SlidePersistAtom && child.Length >= 16)
            {
                if (list.Count >= Limits.MaxObjects)
                    throw DocumentLimits.Exceeded($"The presentation has more than {DocumentLimits.Count(Limits.MaxObjects)} slides.");
                current = new PptListEntry(PptRecords.U32(Stream, child.Start), PptRecords.U32(Stream, child.Start + 12), []);
                list.Add(current);
            }
            else if (current is not null && child.Type == PptRecords.TextHeaderAtom)
            {
                current.Texts.Add([child]);
            }
            else if (current is { Texts: [.., var text] })
            {
                text.Add(child);
            }
        }
    }

    // ───────────────────────── Lookups ─────────────────────────

    /// <summary>The container persist id <paramref name="id"/> stands for, when it is a container of <paramref name="type"/>.</summary>
    public OfficeArtRecord? Container(uint id, params int[] type) =>
        Persist.Record(id) is { IsContainer: true } record && type.Contains(record.Type) ? record : null;

    /// <summary>The picture of the BLIP store's entry <paramref name="pib"/> (1-based); null when there is none or it can't be read.</summary>
    public OfficeArtPicture? Picture(uint pib)
    {
        if (pib == 0 || pib > Blips.Count)
            return null;
        if (OfficeArtBlips.FbseBlip(Stream, Blips[(int)pib - 1], Pictures, out var delayed) is not { } blip)
            return null;
        return OfficeArtBlips.Read(delayed ? Pictures : Stream, blip, Limits, Count);
    }
}
