// Writes Paint.NET's own file (.pdn) as Paint.NET 4.2 does, which every later Paint.NET opens: "PDN3", the XML header
// with a thumbnail, the document in .NET binary serialization (NRBF) and each layer's BGRA pixels in gzip-compressed
// 256 KB chunks. The serialization is written record by record here, in the order and shape BinaryFormatter gives
// Paint.NET's own classes (class metadata once per type, objects queued and referenced by id, enums inline), so the
// file matches one Paint.NET saved itself; see PaintNet for the reader.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class PaintNetWriter
{
    /// <summary>A layer to write: full canvas size, BGRA (not premultiplied), top row first.</summary>
    internal sealed record Layer(string Name, byte[] Bgra, bool Visible = true, byte Opacity = 255,
        PaintNet.BlendMode Blend = PaintNet.BlendMode.Normal);

    /// <summary>The Paint.NET version the file says it was saved with; its assemblies are named after it.</summary>
    private const string SavedWith = "4.21.6589.7045";

    private const string DataLibrary = "PaintDotNet.Data, Version=" + SavedWith + ", Culture=neutral, PublicKeyToken=null";
    private const string CoreLibrary = "PaintDotNet.Core, Version=" + SavedWith + ", Culture=neutral, PublicKeyToken=null";

    private const string StringPairs =
        "System.Collections.Generic.KeyValuePair`2[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, " +
        "PublicKeyToken=b77a5c561934e089],[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, " +
        "PublicKeyToken=b77a5c561934e089]]";

    /// <summary>Paint.NET writes its layers' pixels in chunks of this many bytes.</summary>
    private const int ChunkSize = 256 * 1024;

    /// <summary>Longest side of the thumbnail in the header, as Paint.NET makes it.</summary>
    private const int ThumbnailSide = 256;

    /// <summary>The largest canvas Paint.NET opens.</summary>
    public const int MaxSide = 65_535;

    /// <summary>One picture as a .pdn of a single layer named "Background".</summary>
    public static void Write(IMagickImage<byte> image, string path) =>
        Write(path, (int)image.Width, (int)image.Height, [new Layer("Background", ToBgra(image))]);

    /// <summary>
    /// Writes a PSD (or PSB) as a .pdn with its layers: each pixel layer at its place on the canvas, with its name, opacity,
    /// visibility and, where Paint.NET has the same one, its blend mode (others are drawn Normal). Groups and adjustment
    /// layers have no pixels of their own in Paint.NET's terms; a PSD without layers becomes one layer.
    /// </summary>
    public static IReadOnlyList<string> SaveLayered(Core.Conversion.ConversionStep step, IProgress<double>? progress,
        CancellationToken ct)
    {
        var (width, height, psdLayers) = ReadPsdLayers(step, ct);
        var layers = psdLayers.Select(l => new Layer(l.Name, l.Bgra, l.Visible, l.Opacity, BlendOf(l.Compose))).ToList();
        progress?.Report(0.6);

        var path = step.Output.Allocate("pdn");
        if (path is null)
            return [];
        Write(path, width, height, layers);
        progress?.Report(1);
        return [path];
    }

    /// <summary>
    /// The pixel layers of a PSD (or PSB), bottom first, each on a canvas of its own: name (Unicode when the file has it),
    /// pixels, visibility, opacity and Photoshop blend mode. A PSD without layers gives its picture as one layer.
    /// </summary>
    internal static (int Width, int Height, List<PaintNet.PsdLayer> Layers) ReadPsdLayers(Core.Conversion.ConversionStep step,
        CancellationToken ct)
    {
        using var psd = new MagickImageCollection();
        var settings = new MagickReadSettings { Format = step.From == "psb" ? MagickFormat.Psb : MagickFormat.Psd };
        psd.Read(step.InputPath, settings);
        var width = (int)psd[0].Width;
        var height = (int)psd[0].Height;
        // The names as written (ImageMagick damages any that is not ASCII), when every record became a layer here.
        List<PsdLayerRecords.Record>? records;
        using (var file = File.OpenRead(step.InputPath))
            records = PsdLayerRecords.Read(file, step.From == "psb");
        if (records?.Count != psd.Count - 1)
            records = null;
        var layers = new List<PaintNet.PsdLayer>();
        for (var i = 1; i < psd.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            layers.Add(FromPsdLayer(psd[i], width, height, records?[i - 1].Name));
        }
        if (layers.Count == 0)
            layers.Add(new PaintNet.PsdLayer("Background", ToBgra(psd[0]), true, 255, CompositeOperator.Over));
        return (width, height, layers);
    }

    /// <summary>
    /// One layer ImageMagick read from a PSD, on a canvas of its own: its alpha back without the layer's opacity (the
    /// reader multiplies it in), the opacity, visibility (hidden layers come back composed as "No") and blend mode.
    /// </summary>
    private static PaintNet.PsdLayer FromPsdLayer(IMagickImage<byte> image, int width, int height, string? name)
    {
        var opacity = byte.TryParse(image.GetArtifact("psd:layer.opacity"), out var value) ? value : (byte)255;
        var pixels = ToBgra(image);
        if (opacity is > 0 and < 255)
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = (byte)Math.Min(255, (pixels[i] * 255 + opacity / 2) / opacity);
        }

        // The layer's place: its offset on the canvas, the part of it inside the canvas copied row by row.
        var canvas = new byte[width * height * 4];
        int layerWidth = (int)image.Width, layerHeight = (int)image.Height;
        int left = image.Page.X, top = image.Page.Y;
        var from = Math.Max(0, -left);
        var to = Math.Min(layerWidth, width - left);
        if (to > from)
        {
            for (var y = Math.Max(0, -top); y < layerHeight && top + y < height; y++)
                Array.Copy(pixels, (y * layerWidth + from) * 4, canvas, ((top + y) * width + left + from) * 4, (to - from) * 4);
        }

        var visible = image.Compose != CompositeOperator.No;
        return new PaintNet.PsdLayer(name ?? PsdLayerName(image), canvas, visible, opacity,
            visible ? image.Compose : CompositeOperator.Over);
    }

    /// <summary>The Paint.NET blend mode of a Photoshop one (as ImageMagick names it); Normal where Paint.NET has none.</summary>
    private static PaintNet.BlendMode BlendOf(CompositeOperator compose) => compose switch
    {
        CompositeOperator.Multiply => PaintNet.BlendMode.Multiply,
        CompositeOperator.LinearDodge => PaintNet.BlendMode.Additive,
        CompositeOperator.ColorBurn => PaintNet.BlendMode.ColorBurn,
        CompositeOperator.ColorDodge => PaintNet.BlendMode.ColorDodge,
        CompositeOperator.Overlay => PaintNet.BlendMode.Overlay,
        CompositeOperator.Difference => PaintNet.BlendMode.Difference,
        CompositeOperator.Lighten => PaintNet.BlendMode.Lighten,
        CompositeOperator.Darken => PaintNet.BlendMode.Darken,
        CompositeOperator.Screen => PaintNet.BlendMode.Screen,
        _ => PaintNet.BlendMode.Normal,
    };

    /// <summary>
    /// The layer's name when the file's own records could not be matched to the layers: its Unicode name ("luni" in the
    /// layer's additional information, which ImageMagick keeps as a profile but reads as ASCII only), else its label.
    /// </summary>
    internal static string PsdLayerName(IMagickImage<byte> image)
    {
        var info = image.GetProfile("psd:additional-info")?.ToByteArray();
        for (var p = 0; info is not null && p + 12 <= info.Length;)
        {
            var key = Encoding.ASCII.GetString(info, p + 4, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(p + 8)), int.MaxValue);
            p += 12;
            if (size > info.Length - p)
                break;
            if (key == "luni" && size >= 4)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(p));
                if (length <= (size - 4) / 2)
                    return Encoding.BigEndianUnicode.GetString(info, p + 4, (int)length * 2).TrimEnd('\0');
            }
            p += size;
        }
        return string.IsNullOrEmpty(image.Label) ? "Layer" : image.Label;
    }

    /// <summary>The pixels of <paramref name="image"/> as BGRA bytes, with an alpha channel and in sRGB.</summary>
    public static byte[] ToBgra(IMagickImage<byte> image)
    {
        using var copy = image.Clone();
        if (copy.ColorSpace != ColorSpace.sRGB)
            copy.TransformColorSpace(ColorProfiles.SRGB);
        copy.ColorType = ColorType.TrueColorAlpha;
        using var pixels = copy.GetPixelsUnsafe();
        return pixels.ToByteArray(PixelMapping.BGRA)!;
    }

    /// <summary>Writes a .pdn of <paramref name="layers"/>, bottom first, each <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static void Write(string path, int width, int height, IReadOnlyList<Layer> layers)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide)
            throw new InvalidOperationException(
                $"Paint.NET opens pictures of at most {MaxSide:N0} pixels on a side; this one is {width:N0} × {height:N0}.");
        if (layers.Count == 0 || layers.Any(l => l.Bgra.Length != width * height * 4))
            throw new ArgumentException("Every layer must cover the canvas.", nameof(layers));

        using var file = File.Create(path);
        var xml = Encoding.UTF8.GetBytes(
            $"<pdnImage width=\"{width}\" height=\"{height}\" layers=\"{layers.Count}\" savedWithVersion=\"{SavedWith}\">" +
            $"<custom><thumb png=\"{Thumbnail(width, height, layers)}\" /></custom></pdnImage>");
        file.Write("PDN3"u8);
        file.Write([(byte)xml.Length, (byte)(xml.Length >> 8), (byte)(xml.Length >> 16)]);
        file.Write(xml);
        file.Write([0x00, 0x01]);

        new Serializer(file).Write(Document(width, height, layers));

        foreach (var layer in layers)
            WritePixels(file, layer.Bgra);
    }

    /// <summary>The picture as Paint.NET shows it, at most 256 pixels on a side, as base64 PNG.</summary>
    private static string Thumbnail(int width, int height, IReadOnlyList<Layer> layers)
    {
        var canvas = new byte[width * height * 4];
        foreach (var layer in layers.Where(l => l.Visible))
            PaintNet.Blend(canvas, layer.Bgra, layer.Opacity, layer.Blend);
        using var image = new MagickImage();
        image.ReadPixels(canvas, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        if (Math.Max(width, height) > ThumbnailSide)
            image.Thumbnail(new MagickGeometry(ThumbnailSide, ThumbnailSide));
        return Convert.ToBase64String(image.ToByteArray(MagickFormat.Png32));
    }

    /// <summary>One layer's pixels: format 0 (gzip), the chunk size, then each chunk's number, length and bytes.</summary>
    private static void WritePixels(Stream file, byte[] bgra)
    {
        file.WriteByte(0);
        WriteUInt32(file, ChunkSize);
        using var packed = new MemoryStream();
        for (var chunk = 0; chunk * (long)ChunkSize < bgra.Length; chunk++)
        {
            var offset = chunk * ChunkSize;
            packed.SetLength(0);
            using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(bgra, offset, Math.Min(ChunkSize, bgra.Length - offset));
            WriteUInt32(file, (uint)chunk);
            WriteUInt32(file, (uint)packed.Length);
            packed.WriteTo(file);
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    /// <summary>Paint.NET's Document and everything under it, as the object graph BinaryFormatter walks.</summary>
    private static Obj Document(int width, int height, IReadOnlyList<Layer> layers)
    {
        // One empty metadata list shared by the layers, as in Paint.NET's own files; the document has its own.
        var noMetadata = new EmptyArray(StringPairs);
        var document = new Obj("PaintDotNet.Document", DataLibrary);
        var list = new Obj("PaintDotNet.LayerList", DataLibrary);
        var items = new ObjectArray([.. layers.Select((layer, i) => (object)BitmapLayer(layer, i == 0, width, height, noMetadata))]);
        list.Members.AddRange(
        [
            new("parent", new Ref(document)),
            new("ArrayList+_items", new Ref(items)),
            new("ArrayList+_size", layers.Count),
            new("ArrayList+_version", layers.Count),
        ]);
        var version = new Obj("System.Version", null);
        var parts = SavedWith.Split('.').Select(int.Parse).ToArray();
        version.Members.AddRange(
        [
            new("_Major", parts[0]), new("_Minor", parts[1]), new("_Build", parts[2]), new("_Revision", parts[3]),
        ]);
        document.Members.AddRange(
        [
            new("isDisposed", false),
            new("layers", new Ref(list)),
            new("width", width),
            new("height", height),
            new("savedWith", new Ref(version)),
            new("userMetadataItems", new Ref(new EmptyArray(StringPairs))),
        ]);
        return document;
    }

    private static Obj BitmapLayer(Layer layer, bool background, int width, int height, EmptyArray noMetadata)
    {
        var blendOp = new Obj($"PaintDotNet.UserBlendOps+{layer.Blend}BlendOp", DataLibrary);
        var bitmapProperties = new Obj("PaintDotNet.BitmapLayer+BitmapLayerProperties", DataLibrary);
        bitmapProperties.Members.Add(new("blendOp", new Ref(blendOp)));

        var memory = new Obj("PaintDotNet.MemoryBlock", CoreLibrary);
        memory.Members.AddRange([new("length64", (long)width * height * 4), new("hasParent", false), new("deferred", true)]);
        var surface = new Obj("PaintDotNet.Surface", CoreLibrary);
        surface.Members.AddRange(
            [new("width", width), new("height", height), new("stride", width * 4), new("scan0", new Ref(memory))]);

        var mode = new Obj("PaintDotNet.LayerBlendMode", DataLibrary);
        mode.Members.Add(new("value__", (int)layer.Blend));
        var properties = new Obj("PaintDotNet.Layer+LayerProperties", DataLibrary);
        properties.Members.AddRange(
        [
            new("name", layer.Name),
            new("userMetadataItems", new Ref(noMetadata)),
            new("visible", layer.Visible),
            new("isBackground", background),
            new("opacity", layer.Opacity),
            new("blendMode", mode), // an enum: written inline
        ]);

        var bitmapLayer = new Obj("PaintDotNet.BitmapLayer", DataLibrary);
        bitmapLayer.Members.AddRange(
        [
            new("properties", new Ref(bitmapProperties)),
            new("surface", new Ref(surface)),
            new("Layer+isDisposed", false),
            new("Layer+width", width),
            new("Layer+height", height),
            new("Layer+properties", new Ref(properties)),
        ]);
        return bitmapLayer;
    }

    /// <summary>An object of a class: its type, its assembly (null for mscorlib) and its fields in order.</summary>
    private sealed class Obj(string type, string? library)
    {
        public string Type { get; } = type;
        public string? Library { get; } = library;
        public List<Member> Members { get; } = [];
    }

    /// <summary>A field: a primitive, a string, an inline value (an enum's <see cref="Obj"/>) or a <see cref="Ref"/>.</summary>
    private sealed record Member(string Name, object Value);

    /// <summary>A reference to an object written later, as BinaryFormatter queues them.</summary>
    private sealed record Ref(object Target);

    /// <summary>An object[] (an ArrayList's items).</summary>
    private sealed record ObjectArray(object[] Items);

    /// <summary>An empty array of a system type (Paint.NET's metadata lists).</summary>
    private sealed record EmptyArray(string ElementType);

    /// <summary>NRBF writer for the object graph above (MS-NRBF records).</summary>
    private sealed class Serializer(Stream stream)
    {
        private readonly BinaryWriter _writer = new(stream, Encoding.UTF8, leaveOpen: true);
        private readonly Queue<object> _queue = new();
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _libraries = [];
        private readonly Dictionary<string, int> _metadata = [];
        private int _next = 1;

        public void Write(Obj root)
        {
            Id(root);
            _writer.Write((byte)0); // SerializedStreamHeader
            _writer.Write(1);
            _writer.Write(-1);
            _writer.Write(1);
            _writer.Write(0);
            _queue.Enqueue(root);
            while (_queue.TryDequeue(out var next))
            {
                switch (next)
                {
                    case Obj obj:
                        WriteObject(obj, _ids[obj]);
                        break;
                    case ObjectArray array:
                        _writer.Write((byte)16); // ArraySingleObject
                        _writer.Write(_ids[array]);
                        _writer.Write(array.Items.Length);
                        foreach (var item in array.Items)
                            WriteReference(item);
                        break;
                    case EmptyArray empty:
                        _writer.Write((byte)7); // BinaryArray
                        _writer.Write(_ids[empty]);
                        _writer.Write((byte)0); // single-dimensional
                        _writer.Write(1);       // rank
                        _writer.Write(0);       // length
                        _writer.Write((byte)3); // SystemClass
                        _writer.Write(empty.ElementType);
                        break;
                }
            }
            _writer.Write((byte)11); // MessageEnd
            _writer.Flush();
        }

        private int Id(object target)
        {
            if (!_ids.TryGetValue(target, out var id))
                _ids[target] = id = _next++;
            return id;
        }

        private void WriteReference(object target)
        {
            if (!_ids.ContainsKey(target))
            {
                Id(target);
                _queue.Enqueue(target);
            }
            _writer.Write((byte)9); // MemberReference
            _writer.Write(_ids[target]);
        }

        private int Library(string name)
        {
            if (_libraries.TryGetValue(name, out var id))
                return id;
            id = _libraries[name] = _next++;
            _writer.Write((byte)12); // BinaryLibrary
            _writer.Write(id);
            _writer.Write(name);
            return id;
        }

        /// <summary>
        /// A class record: the full metadata the first time a type is written with these member types, after that
        /// ClassWithId pointing at it. Values follow; objects inside are queued, enums written inline.
        /// </summary>
        private void WriteObject(Obj obj, int id)
        {
            var types = obj.Members.Select(m => MemberType(m.Value)).ToList();
            foreach (var library in types.Select(t => t.Library).Append(obj.Library).OfType<string>())
                Library(library);
            var signature = obj.Type + "|" + string.Join("|", types.Select(t => $"{t.Kind}:{t.Info}:{t.Library}"));
            if (_metadata.TryGetValue(signature, out var metadata))
            {
                _writer.Write((byte)1); // ClassWithId
                _writer.Write(id);
                _writer.Write(metadata);
            }
            else
            {
                _metadata[signature] = id;
                _writer.Write(obj.Library is null ? (byte)4 : (byte)5); // (System)ClassWithMembersAndTypes
                _writer.Write(id);
                _writer.Write(obj.Type);
                _writer.Write(obj.Members.Count);
                foreach (var member in obj.Members)
                    _writer.Write(member.Name);
                foreach (var type in types)
                    _writer.Write(type.Kind);
                foreach (var type in types)
                {
                    switch (type.Kind)
                    {
                        case 0:
                            _writer.Write((byte)type.Info!);
                            break;
                        case 3:
                            _writer.Write((string)type.Info!);
                            break;
                        case 4:
                            _writer.Write((string)type.Info!);
                            _writer.Write(_libraries[type.Library!]);
                            break;
                    }
                }
                if (obj.Library is not null)
                    _writer.Write(_libraries[obj.Library]);
            }

            foreach (var member in obj.Members)
            {
                switch (member.Value)
                {
                    case bool flag:
                        _writer.Write(flag);
                        break;
                    case byte number:
                        _writer.Write(number);
                        break;
                    case int number:
                        _writer.Write(number);
                        break;
                    case long number:
                        _writer.Write(number);
                        break;
                    case string text:
                        _writer.Write((byte)6); // BinaryObjectString
                        _writer.Write(_next++);
                        _writer.Write(text);
                        break;
                    case Obj inline:
                        // A value type (an enum) is written in place, under an id of its own made negative.
                        WriteObject(inline, -_next++);
                        break;
                    case Ref reference:
                        WriteReference(reference.Target);
                        break;
                }
            }
        }

        /// <summary>BinaryTypeEnumeration and what follows it in MemberTypeInfo, with the library of a class.</summary>
        private static (byte Kind, object? Info, string? Library) MemberType(object value) => value switch
        {
            bool => (0, (byte)1, null),
            byte => (0, (byte)2, null),
            int => (0, (byte)8, null),
            long => (0, (byte)9, null),
            string => (1, null, null),
            Obj { Library: null } inline => (3, inline.Type, null),
            Obj inline => (4, inline.Type, inline.Library),
            Ref { Target: Obj { Library: null } target } => (3, target.Type, null),
            Ref { Target: Obj target } => (4, target.Type, target.Library),
            Ref { Target: ObjectArray } => (5, null, null),
            Ref { Target: EmptyArray empty } => (3, empty.ElementType + "[]", null),
            _ => throw new NotSupportedException(value.GetType().Name),
        };
    }
}
