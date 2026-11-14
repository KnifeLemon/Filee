// Records of a PowerPoint 97–2003 presentation ([MS-PPT] 2.3, 2.13.24): the same 8-byte header as OfficeArt records
// (recVer, recInstance, recType, recLen), so they are read with OfficeArtRecords and its bounds (a record is read only
// when it fits in what holds it). Containers have recVer 0xF. These are the record types the reader looks at.

using System.Buffers.Binary;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

internal static class PptRecords
{
    // Document.
    public const int Document = 0x03E8;
    public const int DocumentAtom = 0x03E9;
    public const int Slide = 0x03EE;
    public const int SlideAtom = 0x03EF;
    public const int Notes = 0x03F0;
    public const int NotesAtom = 0x03F1;
    public const int Environment = 0x03F2;
    public const int SlidePersistAtom = 0x03F3;
    public const int MainMaster = 0x03F8;
    public const int SlideShowSlideInfoAtom = 0x03F9;
    public const int DrawingGroup = 0x040B;
    public const int Drawing = 0x040C;
    public const int FontCollection = 0x07D5;
    public const int ColorSchemeAtom = 0x07F0;
    public const int PlaceholderAtom = 0x0BC3;
    public const int OutlineTextRefAtom = 0x0F9E;
    public const int TextHeaderAtom = 0x0F9F;
    public const int TextCharsAtom = 0x0FA0;
    public const int StyleTextPropAtom = 0x0FA1;
    public const int TextMasterStyleAtom = 0x0FA3;
    public const int TextRulerAtom = 0x0FA6;
    public const int TextBytesAtom = 0x0FA8;
    public const int FontEntityAtom = 0x0FB7;
    public const int SlideNumberMetaCharAtom = 0x0FD8;
    public const int SlideListWithText = 0x0FF0;
    public const int UserEditAtom = 0x0FF5;
    public const int CurrentUserAtom = 0x0FF6;
    public const int DateTimeMetaCharAtom = 0x0FF7;
    public const int PersistDirectoryAtom = 0x1772;
    public const int CryptSession10Container = 0x2F14;

    /// <summary>The record at <paramref name="at"/> of <paramref name="bytes"/>, when it fits in the bytes.</summary>
    public static OfficeArtRecord? At(byte[] bytes, long at) =>
        at is >= 0 and <= int.MaxValue ? OfficeArtRecords.At(bytes, (int)at, bytes.Length) : null;

    public static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

    public static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));

    public static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));

    public static short I16(byte[] bytes, int at) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at));
}
