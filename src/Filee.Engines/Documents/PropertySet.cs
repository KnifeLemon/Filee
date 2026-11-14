// The summary information of compound file documents (HWP's \005HwpSummaryInformation, Office's
// \005SummaryInformation): an OLE property set stream with a header, the offset of its first section, then property id /
// offset pairs. Only the title (property 2) is read, a UTF-16 string or one in the code page property 1 names.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Documents;

/// <summary>Reads the title of an OLE summary information property set.</summary>
internal static class PropertySet
{
    private const uint TitleId = 2;
    private const uint CodePageId = 1;

    /// <summary>The title in <paramref name="bytes"/>; null when there is none or the stream is damaged.</summary>
    public static string? Title(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 48)
            return null;
        var section = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44));
        if (section > bytes.Length - 8)
            return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)section + 4));
        uint? titleOffset = null;
        var codePage = 1252;
        for (var i = 0L; i < count && section + 8 + i * 8 + 8 <= bytes.Length; i++)
        {
            var entry = (int)(section + 8 + i * 8);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry));
            var offset = section + (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 4));
            if (offset > bytes.Length - 8)
                continue;
            if (id == TitleId)
                titleOffset = (uint)offset;
            else if (id == CodePageId && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset)) == 2)
                codePage = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + 4));
        }
        if (titleOffset is not { } at)
            return null;
        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)at));
        var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)at + 4));
        var start = (int)at + 8;
        var text = type switch
        {
            0x1F when length * 2 <= bytes.Length - start => Encoding.Unicode.GetString(bytes, start, (int)length * 2),
            0x1E when length <= bytes.Length - start => Decode(bytes.AsSpan(start, (int)length), codePage),
            _ => null,
        };
        text = text?.TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Decode(ReadOnlySpan<byte> bytes, int codePage)
    {
        if (codePage is 1200 or 65001)
            return codePage == 1200 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage).GetString(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
