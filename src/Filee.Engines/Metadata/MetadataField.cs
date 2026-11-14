// The metadata of a file as the Metadata tool shows it: every field, the group it lives in (where in the file: EXIF's
// IFD0, GPS, IPTC, XMP-dc, a PDF's document info …), whether it holds a list (keywords, creators), and whether it may
// be changed, and whether ExifTool read all of it.

namespace Filee.Engines.Metadata;

/// <summary>One field of a file's metadata.</summary>
/// <param name="Family">
/// Where it comes from, broadly: EXIF, IPTC, XMP, PDF, File, MakerNotes … (ExifTool's family 0).
/// </param>
/// <param name="Group">Where it lives exactly: IFD0, ExifIFD, GPS, XMP-dc … (ExifTool's family 1).</param>
/// <param name="Tag">The field's name, as ExifTool writes it ("DateTimeOriginal").</param>
/// <param name="Items">
/// Its value as ExifTool prints it (and takes it back): "Rotate 90 CW", "1/120". One item, or one per entry of a list
/// (keywords, creators), each whole: a comma inside one is part of it.
/// </param>
/// <param name="IsList">The field holds a list, of any length.</param>
/// <param name="Editable">
/// False for what the file's structure decides (sizes, thumbnails, maker notes, file facts).
/// </param>
public sealed record MetadataField(string Family, string Group, string Tag, IReadOnlyList<string> Items, bool IsList, bool Editable)
{
    /// <summary>A field of one value.</summary>
    public MetadataField(string family, string group, string tag, string value, bool editable)
        : this(family, group, tag, [value], false, editable)
    {
    }

    /// <summary>
    /// The XMP list ExifTool stopped reading at 1,000 entries that this field is part of ("XMP-xmpMM:History"), or
    /// null. Its value is only what was read, so writing it back would drop the rest; removing it takes all of it.
    /// </summary>
    public string? CutShortList { get; init; }

    /// <summary>"IFD0:Make": how ExifTool names the field when writing it.</summary>
    public string Key => $"{Group}:{Tag}";

    /// <summary>The value as one text: the items of a list one per line.</summary>
    public string Value => string.Join("\n", Items);
}
