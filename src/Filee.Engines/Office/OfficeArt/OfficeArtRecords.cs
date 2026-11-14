// OfficeArt records ([MS-ODRAW] 2.1.1): the drawing layer Word (DOC), PowerPoint (PPT) and Excel (XLS) binary files
// share. Every record has an 8-byte header (recVer in the low 4 bits and recInstance in the high 12 bits of the first
// 16-bit word, then recType and recLen) and a body; a container's body is its child records one after the other.
//
// Lengths come from the file: a record is read only when its header and body fit inside what holds it (its parent, or
// the stream), so a damaged length ends the walk instead of reading past the parent. Readers walk containers with
// Children and keep their own bounds on how deep and how many (DocumentLimits.MaxObjects, MaxNestingDepth).

using System.Buffers.Binary;

namespace Filee.Engines.Office.OfficeArt;

/// <summary>An OfficeArt record: its header fields and where its body is (<see cref="Start"/> is after the header).</summary>
internal readonly record struct OfficeArtRecord(int Version, int Instance, int Type, int Start, int Length)
{
    /// <summary>Where the body ends (and the next record starts).</summary>
    public int End => Start + Length;

    /// <summary>Where the record's header starts.</summary>
    public int HeaderStart => Start - OfficeArtRecords.HeaderSize;

    /// <summary>A container (recVer 0xF): its body is records.</summary>
    public bool IsContainer => Version == 0xF;
}

/// <summary>Reading OfficeArt records and their record types.</summary>
internal static class OfficeArtRecords
{
    public const int HeaderSize = 8;

    // Containers.
    public const int DggContainer = 0xF000;
    public const int BStoreContainer = 0xF001;
    public const int DgContainer = 0xF002;
    public const int SpgrContainer = 0xF003;
    public const int SpContainer = 0xF004;

    // Atoms.
    public const int Fdgg = 0xF006;
    public const int Fbse = 0xF007;
    public const int Fdg = 0xF008;
    public const int Fspgr = 0xF009;
    public const int Fsp = 0xF00A;
    public const int Fopt = 0xF00B;
    public const int ClientTextbox = 0xF00D;
    public const int ChildAnchor = 0xF00F;
    public const int ClientAnchor = 0xF010;
    public const int ClientData = 0xF011;
    public const int SecondaryFopt = 0xF121;
    public const int TertiaryFopt = 0xF122;

    // BLIPs (pictures).
    public const int BlipEmf = 0xF01A;
    public const int BlipWmf = 0xF01B;
    public const int BlipPict = 0xF01C;
    public const int BlipJpeg = 0xF01D;
    public const int BlipPng = 0xF01E;
    public const int BlipDib = 0xF01F;
    public const int BlipTiff = 0xF029;
    public const int BlipCmykJpeg = 0xF02A;

    /// <summary>
    /// The record at <paramref name="at"/> of <paramref name="bytes"/>, when its header and body fit within
    /// <paramref name="end"/> (and <paramref name="end"/> within the bytes); otherwise null.
    /// </summary>
    public static OfficeArtRecord? At(ReadOnlySpan<byte> bytes, int at, int end)
    {
        if (at < 0 || at + HeaderSize > end || end > bytes.Length)
            return null;
        var verInstance = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 2)..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
        if (length > end - at - HeaderSize)
            return null;
        return new OfficeArtRecord(verInstance & 0xF, verInstance >> 4, type, at + HeaderSize, (int)length);
    }

    /// <summary>The records of a container's body, up to the first one that doesn't fit.</summary>
    public static List<OfficeArtRecord> Children(ReadOnlySpan<byte> bytes, OfficeArtRecord container)
    {
        var children = new List<OfficeArtRecord>();
        var at = container.Start;
        while (At(bytes, at, container.End) is { } child)
        {
            children.Add(child);
            at = child.End;
        }
        return children;
    }

    /// <summary>The first child of <paramref name="container"/> of type <paramref name="type"/>, or null.</summary>
    public static OfficeArtRecord? Child(ReadOnlySpan<byte> bytes, OfficeArtRecord container, int type)
    {
        var at = container.Start;
        while (At(bytes, at, container.End) is { } child)
        {
            if (child.Type == type)
                return child;
            at = child.End;
        }
        return null;
    }
}
