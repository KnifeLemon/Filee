// Paint.NET's own file (.pdn): "PDN3", a 24-bit header length, an XML header (size, layer count, a small thumbnail), the
// marker 00 01, the document as .NET binary serialization (NRBF: the layers' names, visibility, opacity and blend mode)
// and then each layer's BGRA pixels, in gzip-compressed chunks. The serialized part is read as plain data with
// System.Formats.Nrbf, which never creates the types it names. Layers are merged here with Paint.NET's own blend
// formulas, so the picture looks as it does in Paint.NET, or kept as PSD layers Photoshop, GIMP and Krita open.

using System.Buffers.Binary;
using System.Formats.Nrbf;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class PaintNet
{
    /// <summary>Paint.NET's layer blend modes, in the order of its LayerBlendMode enumeration.</summary>
    internal enum BlendMode
    {
        Normal, Multiply, Additive, ColorBurn, ColorDodge, Reflect, Glow, Overlay, Difference, Negation, Lighten, Darken,
        Screen, Xor,
    }

    /// <summary>One layer: its properties and, once read, its pixels (BGRA, not premultiplied, top row first).</summary>
    internal sealed record Layer(string Name, bool Visible, byte Opacity, BlendMode Blend, byte[] Bgra);

    /// <summary>Paint.NET canvases are at most 65,535 pixels on a side.</summary>
    private const int MaxSide = 65_535;

    /// <summary>More layers than Paint.NET users make; a file claiming more is damaged.</summary>
    private const int MaxLayers = 4_096;

    private static readonly InvalidDataException Damaged =
        new("This Paint.NET file is damaged or of an unknown kind. Open it in Paint.NET and save it again.");

    /// <summary>Width and height of the canvas, and its layers bottom first, each read as the caller takes it.</summary>
    /// <exception cref="InvalidDataException">The file is not a Paint.NET image Filee can read.</exception>
    public static (int Width, int Height, IEnumerable<Layer> Layers) Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            return Open(stream);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or InvalidCastException)
        {
            // A member missing, or of another type than Paint.NET writes.
            stream.Dispose();
            throw new InvalidDataException(Damaged.Message, ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static (int Width, int Height, IEnumerable<Layer> Layers) Open(FileStream stream)
    {
        var data = OpenDocument(stream, out var document);
        var width = document.GetInt32("width");
        var height = document.GetInt32("height");
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide)
            throw Damaged;
        if ((long)width * height * 4 > Array.MaxLength)
            throw new InvalidDataException("This Paint.NET picture is too large to convert.");
        var layers = Layers(document);
        return (width, height, ReadPixels(stream, data, layers, width, height));
    }

    /// <summary>The picture as Paint.NET shows it: every visible layer merged, bottom first.</summary>
    public static MagickImage ReadFlattened(string path)
    {
        var (width, height, layers) = Open(path);
        var canvas = new byte[width * height * 4];
        foreach (var layer in layers)
        {
            if (layer.Visible)
                Blend(canvas, layer.Bgra, layer.Opacity, layer.Blend);
        }
        return FromBgra(canvas, width, height);
    }

    /// <summary>
    /// True when a .pdn goes to PSD with its layers: the preset changes nothing about the picture. Otherwise it is
    /// converted from the merged picture, as for any image.
    /// </summary>
    public static bool KeepsLayers(ImageOptions options) =>
        !options.EditsPicture && !options.ReworksPixels && options.MaxSizeMb <= 0 && options.MinSizeMb <= 0;

    /// <summary>
    /// Writes the .pdn as a PSD (or PSB) with one layer per Paint.NET layer: its name, opacity, visibility and, where
    /// Photoshop has the same one, its blend mode. Reflect, Glow, Negation and Xor have none; those layers become Normal.
    /// The merged picture goes first, for programs that read only that.
    /// </summary>
    public static IReadOnlyList<string> SaveLayered(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var (width, height, layers) = Open(step.InputPath);
        using var images = new MagickImageCollection();
        var canvas = new byte[width * height * 4];
        var visible = new List<bool>();
        foreach (var layer in layers)
        {
            ct.ThrowIfCancellationRequested();
            if (layer.Visible)
                Blend(canvas, layer.Bgra, layer.Opacity, layer.Blend);
            images.Add(ToPsdLayer(layer, width, height));
            visible.Add(layer.Visible);
        }
        images.Insert(0, FromBgra(canvas, width, height));
        progress?.Report(0.7);

        var format = step.To == "psb" ? MagickFormat.Psb : MagickFormat.Psd;
        var path = step.Output.Allocate(step.To);
        if (path is null)
            return [];
        foreach (var image in images)
        {
            image.Settings.SetDefine(MagickFormat.Psd, "additional-info", "all");
            image.Settings.Compression = CompressionMethod.RLE;
            image.Page = new MagickGeometry(0, 0, (uint)width, (uint)height);
        }
        images.Write(path, format);
        SetLayerFlags(path, visible, format == MagickFormat.Psb);
        progress?.Report(1);
        return [path];
    }

    /// <summary>
    /// Checks the header and decodes the serialized document. Returns the stream the pixel data follows on: the file
    /// itself, or for files of old Paint.NET versions that compressed everything after the header, a gzip reader.
    /// </summary>
    private static Stream OpenDocument(Stream stream, out ClassRecord document)
    {
        Span<byte> header = stackalloc byte[7];
        if (stream.ReadAtLeast(header, 7, throwOnEndOfStream: false) < 7 || !header[..4].SequenceEqual("PDN3"u8))
            throw Damaged;
        var xmlLength = header[4] | header[5] << 8 | header[6] << 16;
        if (stream.Seek(xmlLength, SeekOrigin.Current) > stream.Length - 2)
            throw Damaged;
        Span<byte> marker = stackalloc byte[2];
        stream.ReadExactly(marker);
        Stream data = stream;
        if (marker[0] == 0x1F && marker[1] == 0x8B)
        {
            stream.Seek(-2, SeekOrigin.Current);
            data = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        }
        else if (marker[0] != 0x00 || marker[1] != 0x01)
        {
            throw Damaged;
        }

        try
        {
            document = NrbfDecoder.Decode(data, out _, new PayloadOptions(), leaveOpen: true) as ClassRecord
                       ?? throw Damaged;
        }
        catch (Exception ex) when (ex is System.Runtime.Serialization.SerializationException or NotSupportedException or EndOfStreamException
                                       or DecoderFallbackException or FormatException)
        {
            throw new InvalidDataException(Damaged.Message, ex);
        }
        if (document.TypeName.FullName != "PaintDotNet.Document")
            throw Damaged;
        return data;
    }

    /// <summary>The layers' properties, bottom first, from the serialized document.</summary>
    private static List<(string Name, bool Visible, byte Opacity, BlendMode Blend, long Length, int Stride)> Layers(
        ClassRecord document)
    {
        var list = document.GetClassRecord("layers") ?? throw Damaged;
        var count = Member<int>(list, "ArrayList+_size", "_size");
        if (count is < 0 or > MaxLayers
            || (list.GetSerializationRecord(MemberName(list, "ArrayList+_items", "_items"))
                    as SZArrayRecord<SerializationRecord>)?.GetArray() is not { } items
            || items.Length < count)
            throw Damaged;

        var layers = new List<(string, bool, byte, BlendMode, long, int)>(count);
        for (var i = 0; i < count; i++)
        {
            if (items[i] is not ClassRecord layer)
                throw Damaged;
            var properties = layer.GetClassRecord(MemberName(layer, "Layer+properties", "properties")) ?? throw Damaged;
            var surface = layer.GetClassRecord("surface") ?? throw Damaged;
            var memory = surface.GetClassRecord("scan0") ?? throw Damaged;
            layers.Add((properties.GetString("name") ?? "",
                properties.GetBoolean("visible"),
                properties.GetByte("opacity"),
                BlendOf(layer, properties),
                memory.GetInt64("length64"),
                surface.GetInt32("stride")));
        }
        return layers;
    }

    /// <summary>
    /// The layer's blend mode: the LayerBlendMode of Paint.NET 4 and later, or the blend operation's class name that
    /// Paint.NET 3 stored instead. Modes this reader doesn't know are drawn as Normal.
    /// </summary>
    private static BlendMode BlendOf(ClassRecord layer, ClassRecord properties)
    {
        if (properties.HasMember("blendMode"))
        {
            var value = properties.GetRawValue("blendMode") switch
            {
                ClassRecord mode when mode.HasMember("value__") => mode.GetInt32("value__"),
                int raw => raw,
                _ => 0,
            };
            return Enum.IsDefined((BlendMode)value) ? (BlendMode)value : BlendMode.Normal;
        }
        if (layer.HasMember("properties") && layer.GetClassRecord("properties") is { } bitmap
            && bitmap.HasMember("blendOp") && bitmap.GetClassRecord("blendOp") is { } op)
        {
            var name = op.TypeName.FullName;
            var plus = name.LastIndexOf('+');
            var className = plus < 0 ? name : name[(plus + 1)..];
            if (className.EndsWith("BlendOp", StringComparison.Ordinal)
                && Enum.TryParse<BlendMode>(className[..^"BlendOp".Length], out var mode))
                return mode;
        }
        return BlendMode.Normal;
    }

    /// <summary>
    /// The layers' pixels, which follow the document in its order: per layer a format byte (0 gzip, 1 stored), the
    /// chunk size, then every chunk as its number and length (big-endian) and its bytes. Chunks may come in any order.
    /// </summary>
    private static IEnumerable<Layer> ReadPixels(Stream file, Stream data,
        List<(string Name, bool Visible, byte Opacity, BlendMode Blend, long Length, int Stride)> layers, int width, int height)
    {
        using (file)
        using (data == file ? null : data)
        {
            foreach (var layer in layers)
            {
                if (layer.Length != (long)width * height * 4 || layer.Stride != width * 4)
                    throw Damaged;
                byte[] pixels;
                try
                {
                    pixels = ReadLayer(data, (int)layer.Length);
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException(Damaged.Message, ex);
                }
                yield return new Layer(layer.Name, layer.Visible, layer.Opacity, layer.Blend, pixels);
            }
        }
    }

    /// <summary>The <paramref name="length"/> bytes of one layer's pixels, from its chunks.</summary>
    private static byte[] ReadLayer(Stream data, int length)
    {
        var pixels = new byte[length];
        var compressed = data.ReadByte() switch
        {
            0 => true,
            1 => false,
            _ => throw Damaged,
        };
        var chunkSize = ReadUInt32(data);
        if (chunkSize == 0)
            throw Damaged;
        var chunks = (int)((length + (long)chunkSize - 1) / chunkSize);
        var seen = new bool[chunks];
        for (var i = 0; i < chunks; i++)
        {
            var chunk = ReadUInt32(data);
            var size = ReadUInt32(data);
            if (chunk >= chunks || seen[chunk])
                throw Damaged;
            seen[chunk] = true;
            var offset = (int)(chunk * chunkSize);
            var target = pixels.AsSpan(offset, (int)Math.Min(chunkSize, length - offset));
            // gzip never makes a chunk much larger than its pixels; a larger size is damage, not a reason to allocate it.
            if (size > target.Length + target.Length / 8 + 1024L)
                throw Damaged;
            ReadChunk(data, (int)size, compressed, target);
        }
        return pixels;
    }

    private static uint ReadUInt32(Stream data)
    {
        Span<byte> number = stackalloc byte[4];
        data.ReadExactly(number);
        return BinaryPrimitives.ReadUInt32BigEndian(number);
    }

    /// <summary>One chunk of a layer: <paramref name="size"/> bytes that fill <paramref name="target"/> exactly.</summary>
    private static void ReadChunk(Stream data, int size, bool compressed, Span<byte> target)
    {
        if (!compressed)
        {
            if (size != target.Length)
                throw Damaged;
            data.ReadExactly(target);
            return;
        }
        var packed = new byte[size];
        data.ReadExactly(packed);
        using var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        try
        {
            gzip.ReadExactly(target);
            // Nothing may be left over: a chunk that unpacks to more than its share is damaged (or a zip bomb).
            if (gzip.ReadByte() != -1)
                throw Damaged;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(Damaged.Message, ex);
        }
    }

    /// <summary>
    /// Paint.NET's blend of one layer onto the canvas below it (UserBlendOps): the layer's alpha times its opacity
    /// covers the canvas, and where both have colour the mode's function of the two takes its place.
    /// </summary>
    internal static void Blend(byte[] canvas, byte[] layer, byte opacity, BlendMode mode)
    {
        for (var i = 0; i < canvas.Length; i += 4)
        {
            var top = Scale(layer[i + 3], opacity);
            if (top == 0)
                continue;
            int bottom = canvas[i + 3];
            var both = Scale(bottom, top);
            var onlyBottom = bottom - both;
            var onlyTop = top - both;
            var total = onlyBottom + top;
            for (var c = 0; c < 3; c++)
            {
                int a = canvas[i + c], b = layer[i + c];
                var mixed = both == 0 ? 0 : Function(mode, a, b);
                canvas[i + c] = (byte)((a * onlyBottom + b * onlyTop + mixed * both + total / 2) / total);
            }
            canvas[i + 3] = (byte)total;
        }
    }

    /// <summary>a × b / 255, rounded.</summary>
    private static int Scale(int a, int b)
    {
        var product = a * b + 128;
        return (product + (product >> 8)) >> 8;
    }

    /// <summary>The colour a blend mode gives for bottom colour <paramref name="a"/> and top colour <paramref name="b"/>.</summary>
    private static int Function(BlendMode mode, int a, int b) => mode switch
    {
        BlendMode.Multiply => Scale(a, b),
        BlendMode.Additive => Math.Min(255, a + b),
        BlendMode.ColorBurn => b == 0 ? 0 : Math.Max(0, 255 - (255 - a) * 255 / b),
        BlendMode.ColorDodge => b == 255 ? 255 : Math.Min(255, a * 255 / (255 - b)),
        BlendMode.Reflect => b == 255 ? 255 : Math.Min(255, a * a / (255 - b)),
        BlendMode.Glow => a == 255 ? 255 : Math.Min(255, b * b / (255 - a)),
        BlendMode.Overlay => a < 128 ? Scale(2 * a, b) : 255 - Scale(2 * (255 - a), 255 - b),
        BlendMode.Difference => Math.Abs(a - b),
        BlendMode.Negation => 255 - Math.Abs(255 - a - b),
        BlendMode.Lighten => Math.Max(a, b),
        BlendMode.Darken => Math.Min(a, b),
        BlendMode.Screen => a + b - Scale(a, b),
        BlendMode.Xor => a ^ b,
        _ => b,
    };

    private static MagickImage FromBgra(byte[] bgra, int width, int height)
    {
        var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        return image;
    }

    /// <summary>
    /// A Paint.NET layer as ImageMagick's PSD writer takes it: the name in "label" (ASCII, as PSD's own field holds) and
    /// as Unicode ("luni", which Photoshop, GIMP and Krita show), the opacity in "psd:layer.opacity" with the alpha
    /// multiplied by it (the writer divides it out again). Visibility is set afterwards, see <see cref="SetLayerFlags"/>.
    /// </summary>
    private static MagickImage ToPsdLayer(Layer layer, int width, int height)
    {
        if (layer.Opacity < 255)
        {
            for (var i = 3; i < layer.Bgra.Length; i += 4)
                layer.Bgra[i] = (byte)Scale(layer.Bgra[i], layer.Opacity);
        }
        var image = FromBgra(layer.Bgra, width, height);
        image.Label = AsciiName(layer.Name);
        image.SetProfile(new ImageProfile("psd:additional-info", UnicodeName(layer.Name)));
        if (layer.Opacity < 255)
            image.SetArtifact("psd:layer.opacity", layer.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        image.Compose = layer.Blend switch
        {
            BlendMode.Multiply => CompositeOperator.Multiply,
            BlendMode.Additive => CompositeOperator.LinearDodge,
            BlendMode.ColorBurn => CompositeOperator.ColorBurn,
            BlendMode.ColorDodge => CompositeOperator.ColorDodge,
            BlendMode.Overlay => CompositeOperator.Overlay,
            BlendMode.Difference => CompositeOperator.Difference,
            BlendMode.Lighten => CompositeOperator.Lighten,
            BlendMode.Darken => CompositeOperator.Darken,
            BlendMode.Screen => CompositeOperator.Screen,
            _ => CompositeOperator.Over,
        };
        return image;
    }

    /// <summary>
    /// Sets each layer's flags in a PSD ImageMagick wrote: hidden where Paint.NET hides the layer, and nothing else.
    /// ImageMagick writes the flags byte as 1 for a visible layer and 0 for a hidden one, but the visibility bit is 2
    /// (set = hidden) and 1 locks the layer's transparent pixels: every layer would show, locked. Left as it is when the
    /// file is not laid out as expected.
    /// </summary>
    internal static void SetLayerFlags(string path, IReadOnlyList<bool> visible, bool psb)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        var records = PsdLayerRecords.Read(file, psb);
        if (records is null || records.Count != visible.Count)
            return;
        for (var i = 0; i < records.Count; i++)
        {
            file.Position = records[i].FlagsOffset;
            file.WriteByte(visible[i] ? (byte)0 : (byte)2);
        }
    }

    /// <summary>The layer name for PSD's Pascal string: accents dropped, other non-ASCII letters as '_', 250 at most.</summary>
    internal static string AsciiName(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(c is >= ' ' and <= '~' ? c : '_');
            if (builder.Length == 250)
                break;
        }
        return builder.Length == 0 ? "Layer" : builder.ToString();
    }

    /// <summary>PSD's "luni" block: the layer name as a length-prefixed UTF-16BE string, padded to four bytes.</summary>
    internal static byte[] UnicodeName(string name)
    {
        if (name.Length > 1_000)
            name = name[..1_000];
        var text = Encoding.BigEndianUnicode.GetBytes(name);
        var body = 4 + text.Length;
        var padded = (body + 3) & ~3;
        var block = new byte[12 + padded];
        "8BIMluni"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), (uint)padded);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(12), (uint)name.Length);
        text.CopyTo(block, 16);
        return block;
    }

    private static string MemberName(ClassRecord record, params string[] names) =>
        names.FirstOrDefault(record.HasMember) ?? throw Damaged;

    private static T Member<T>(ClassRecord record, params string[] names) where T : struct =>
        record.GetRawValue(MemberName(record, names)) is T value ? value : throw Damaged;
}
