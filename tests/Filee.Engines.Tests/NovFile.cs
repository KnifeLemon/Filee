// Wisenet WAVE / Nx Witness exports (.nov) as Network Optix's client writes them (nx_open, layout_file_info.h and
// layout_storage_resource.cpp): named streams (a NUL-terminated UTF-8 name, then the data) and their index, at the end
// of the file in version 2 or at its start in version 1. Nothing writes .nov but that client, so the tests do.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Tests;

internal static class NovFile
{
    /// <summary>An export of <paramref name="streams"/>, protected (crypto info in the index) when asked.</summary>
    public static byte[] Write(IReadOnlyList<(string Name, byte[] Data)> streams, int version = 2, bool encrypted = false)
    {
        var output = new MemoryStream();
        if (version == 1)
            output.Write(new byte[16 + 256 * 16 + (encrypted ? 256 : 0)]);
        var offsets = new List<long>();
        foreach (var (name, data) in streams)
        {
            offsets.Add(output.Position);
            output.Write(Encoding.UTF8.GetBytes(name));
            output.WriteByte(0);
            output.Write(data);
        }
        var buffer = new byte[16];
        if (version == 1)
        {
            var end = output.Position;
            output.Position = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, encrypted ? 0xfed8260da9eebc03 : 0xfed8260da9eebc04);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), (uint)offsets.Count);
            output.Write(buffer);
            WriteEntries(output, offsets);
            output.Position = end;
            return output.ToArray();
        }
        var headerAt = output.Position;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, encrypted ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)offsets.Count);
        output.Write(buffer.AsSpan(0, 8));
        if (encrypted)
            output.Write(new byte[256]); // Password salt and hash.
        WriteEntries(output, offsets);
        var footer = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(footer, 0xdea5489c3f44ca16);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(8), 2);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(12), headerAt);
        output.Write(footer);
        return output.ToArray();
    }

    /// <summary>One index entry per stream: its offset, the hash of its name (unused by Filee, left at 0), reserved.</summary>
    private static void WriteEntries(MemoryStream output, List<long> offsets)
    {
        var entry = new byte[16];
        foreach (var offset in offsets)
        {
            BinaryPrimitives.WriteInt64LittleEndian(entry, offset);
            output.Write(entry);
        }
    }
}
