// Builds Paint.NET .pdn files for the tests as Paint.NET 4 and 5 lay them out: "PDN3", the XML header, the document in
// .NET binary serialization (written record by record here, as BinaryFormatter would) and every layer's BGRA pixels in
// gzip-compressed chunks, written in reverse order as Paint.NET's parallel writer may.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

internal static class PdnBuilder
{
    /// <summary>A layer of the test picture: its properties and its pixels (BGRA, top row first).</summary>
    public sealed record Layer(string Name, byte[] Bgra, bool Visible = true, byte Opacity = 255, int BlendMode = 0);

    private const string DataLibrary = "PaintDotNet.Data, Version=5.1.0.0, Culture=neutral, PublicKeyToken=null";
    private const string CoreLibrary = "PaintDotNet.Core, Version=5.1.0.0, Culture=neutral, PublicKeyToken=null";

    /// <summary>Pixels of one colour (BGRA order) over a whole width x height layer.</summary>
    public static byte[] Fill(int width, int height, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
        return pixels;
    }

    /// <summary>The bytes of a .pdn holding <paramref name="layers"/>, bottom first.</summary>
    /// <param name="chunkSize">Bytes of a layer per chunk; small ones make several chunks of a small picture.</param>
    /// <param name="pixelsWritten">False ends the file after the document: a header that claims pixels it lacks.</param>
    public static byte[] Build(int width, int height, IReadOnlyList<Layer> layers, int chunkSize = 100,
        bool pixelsWritten = true)
    {
        using var file = new MemoryStream();
        var xml = Encoding.UTF8.GetBytes(
            $"<pdnImage width=\"{width}\" height=\"{height}\" layers=\"{layers.Count}\" savedWithVersion=\"5.1\" />");
        file.Write("PDN3"u8);
        file.Write([(byte)xml.Length, (byte)(xml.Length >> 8), (byte)(xml.Length >> 16)]);
        file.Write(xml);
        file.Write([0x00, 0x01]);

        var nrbf = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
        var ids = 10;
        // SerializedStreamHeader: root object 1, header -1, version 1.0.
        nrbf.Write((byte)0);
        nrbf.Write(1);
        nrbf.Write(-1);
        nrbf.Write(1);
        nrbf.Write(0);
        Library(nrbf, 2, DataLibrary);
        Library(nrbf, 3, CoreLibrary);

        // Document { layers: LayerList, width, height }
        ClassHeader(nrbf, 1, "PaintDotNet.Document", 2,
            ("layers", Class("PaintDotNet.LayerList", 2)), ("width", Int32), ("height", Int32));
        // LayerList (an ArrayList) { _items, _size }: the array has room for one more, empty, as an ArrayList grows.
        ClassHeader(nrbf, 4, "PaintDotNet.LayerList", 2, ("ArrayList+_items", ObjectArray), ("ArrayList+_size", Int32));
        nrbf.Write((byte)16); // ArraySingleObject
        nrbf.Write(5);
        nrbf.Write(layers.Count + 1);
        foreach (var layer in layers)
        {
            ClassHeader(nrbf, ids++, "PaintDotNet.BitmapLayer", 2,
                ("surface", Class("PaintDotNet.Surface", 3)),
                ("Layer+properties", Class("PaintDotNet.Layer+LayerProperties", 2)));
            ClassHeader(nrbf, ids++, "PaintDotNet.Surface", 3,
                ("width", Int32), ("height", Int32), ("stride", Int32), ("scan0", Class("PaintDotNet.MemoryBlock", 3)));
            nrbf.Write(width);
            nrbf.Write(height);
            nrbf.Write(width * 4);
            ClassHeader(nrbf, ids++, "PaintDotNet.MemoryBlock", 3,
                ("length64", Int64), ("hasParent", Boolean), ("deferred", Boolean));
            nrbf.Write((long)width * height * 4);
            nrbf.Write(false);
            nrbf.Write(true);
            ClassHeader(nrbf, ids++, "PaintDotNet.Layer+LayerProperties", 2,
                ("name", String), ("visible", Boolean), ("isBackground", Boolean), ("opacity", Byte),
                ("blendMode", Class("PaintDotNet.LayerBlendMode", 2)));
            nrbf.Write((byte)6); // BinaryObjectString
            nrbf.Write(ids++);
            nrbf.Write(layer.Name);
            nrbf.Write(layer.Visible);
            nrbf.Write(ReferenceEquals(layer, layers[0]));
            nrbf.Write(layer.Opacity);
            ClassHeader(nrbf, ids++, "PaintDotNet.LayerBlendMode", 2, ("value__", Int32));
            nrbf.Write(layer.BlendMode);
        }
        nrbf.Write((byte)10); // ObjectNull: the array's unused room
        nrbf.Write(layers.Count); // ArrayList+_size
        nrbf.Write(width);
        nrbf.Write(height);
        nrbf.Write((byte)11); // MessageEnd
        nrbf.Flush();

        foreach (var layer in pixelsWritten ? layers : [])
        {
            file.WriteByte(0); // gzip
            WriteUInt32(file, (uint)chunkSize);
            var chunks = (layer.Bgra.Length + chunkSize - 1) / chunkSize;
            for (var chunk = chunks - 1; chunk >= 0; chunk--)
            {
                var offset = chunk * chunkSize;
                using var packed = new MemoryStream();
                using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(layer.Bgra, offset, Math.Min(chunkSize, layer.Bgra.Length - offset));
                WriteUInt32(file, (uint)chunk);
                WriteUInt32(file, (uint)packed.Length);
                packed.WriteTo(file);
            }
        }
        return file.ToArray();
    }

    // Member types: the BinaryTypeEnumeration and, for primitives and classes, what follows it in MemberTypeInfo.
    private static readonly (byte Kind, object? Info) Int32 = (0, (byte)8);
    private static readonly (byte Kind, object? Info) Int64 = (0, (byte)9);
    private static readonly (byte Kind, object? Info) Boolean = (0, (byte)1);
    private static readonly (byte Kind, object? Info) Byte = (0, (byte)2);
    private static readonly (byte Kind, object? Info) String = (1, null);
    private static readonly (byte Kind, object? Info) ObjectArray = (5, null);

    private static (byte Kind, object? Info) Class(string name, int library) => (4, (name, library));

    private static void Library(BinaryWriter writer, int id, string name)
    {
        writer.Write((byte)12); // BinaryLibrary
        writer.Write(id);
        writer.Write(name);
    }

    /// <summary>A ClassWithMembersAndTypes record up to its member values, which the caller writes next.</summary>
    private static void ClassHeader(BinaryWriter writer, int id, string name, int library,
        params (string Name, (byte Kind, object? Info) Type)[] members)
    {
        writer.Write((byte)5);
        writer.Write(id);
        writer.Write(name);
        writer.Write(members.Length);
        foreach (var member in members)
            writer.Write(member.Name);
        foreach (var member in members)
            writer.Write(member.Type.Kind);
        foreach (var member in members)
        {
            switch (member.Type.Info)
            {
                case byte primitive:
                    writer.Write(primitive);
                    break;
                case (string className, int classLibrary):
                    writer.Write(className);
                    writer.Write(classLibrary);
                    break;
            }
        }
        writer.Write(library);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
