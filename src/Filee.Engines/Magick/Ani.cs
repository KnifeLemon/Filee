// A Windows animated cursor (.ani, issue #1365): a RIFF file of type ACON. Its "anih" header gives the number of frames
// and steps and the default time of a step, in jiffies (1/60 s). The frames are in a "LIST fram", one "icon" chunk each,
// and each is a whole .cur or .ico file, which may hold several sizes. An optional "seq " chunk says which frame each step
// shows (a frame can come back), an optional "rate" chunk how long each step lasts. ImageMagick doesn't read ANI but
// reads CUR and ICO: Filee takes the frames out here, has ImageMagick decode each (its largest size) and builds the
// animation step by step. Files come from the Internet: everything is read from a bounded copy in memory, sizes and
// counts are checked, and a damaged file is refused with a clear message.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class Ani
{
    /// <summary>Real animated cursors are a few kilobytes to a few megabytes.</summary>
    private const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>Frames in the "LIST fram". Real cursors have up to a few hundred.</summary>
    private const int MaxFrames = 1024;

    /// <summary>Steps of the animation (a frame can be shown by several steps).</summary>
    private const int MaxSteps = 4096;

    /// <summary>The widest or tallest picture a frame may hold. A cursor stores at most 256 px.</summary>
    private const int MaxSide = 1024;

    /// <summary>All steps together: 1,024 steps of a 256 px cursor.</summary>
    private const long MaxAnimationPixels = 1024L * 256 * 256;

    /// <summary>The longest step a GIF stores (65,535 hundredths of a second).</summary>
    private const uint MaxDelay = ushort.MaxValue;

    private const uint IconFlag = 1; // AF_ICON: the frames are .ico/.cur files, not raw bitmaps

    /// <summary>One frame: a whole .cur or .ico file.</summary>
    private sealed record Frame(byte[] Data, bool IsCursor);

    /// <summary>A step of the animation: the frame it shows and for how long, in jiffies (1/60 s).</summary>
    private readonly record struct Step(int Frame, uint Jiffies);

    /// <summary>One size of a frame as its directory lists it: the cursor's hotspot (0, 0 in an .ico).</summary>
    private readonly record struct Entry(int HotspotX, int HotspotY);

    /// <summary>
    /// The animation: one image per step, each as large as the largest frame (smaller frames at the top left), with the
    /// step's time and an endless loop, as Windows plays it.
    /// </summary>
    /// <exception cref="InvalidDataException">The file isn't an animated cursor Filee can read, or it is damaged.</exception>
    public static MagickImageCollection Read(string path)
    {
        var (frames, steps) = Parse(path);
        var pictures = Decode(frames);
        try
        {
            var (width, height) = Canvas(pictures);
            if ((long)steps.Count * width * height > MaxAnimationPixels)
                throw new InvalidDataException("This animated cursor has too many steps or too large pictures to convert.");
            var images = new MagickImageCollection();
            try
            {
                foreach (var step in steps)
                {
                    var image = (MagickImage)pictures[step.Frame].Image.Clone();
                    if (image.Width != width || image.Height != height)
                    {
                        image.BackgroundColor = MagickColors.Transparent;
                        image.Extent(width, height, Gravity.Northwest);
                    }
                    image.Page = new MagickGeometry(0, 0, width, height);
                    image.AnimationTicksPerSecond = 100;
                    image.AnimationDelay = Delay(step.Jiffies);
                    image.AnimationIterations = 0;
                    image.GifDisposeMethod = GifDisposeMethod.Background;
                    images.Add(image);
                }
                return images;
            }
            catch
            {
                images.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var picture in pictures)
                picture.Image.Dispose();
        }
    }

    /// <summary>A step's time in hundredths of a second, as GIF, WebP and APNG frames carry it.</summary>
    internal static uint Delay(uint jiffies) => (uint)Math.Min(MaxDelay, Math.Round(jiffies * 100.0 / 60));

    /// <summary>
    /// The hotspot of the animation's first picture (the point of the cursor that clicks), and the size of the pictures
    /// <see cref="Read"/> gives. Null when the first frame is an .ico, which has none.
    /// </summary>
    public static (int X, int Y, uint Width, uint Height)? Hotspot(string path)
    {
        var (frames, steps) = Parse(path);
        var pictures = Decode(frames);
        try
        {
            var (width, height) = Canvas(pictures);
            var first = pictures[steps[0].Frame];
            return frames[steps[0].Frame].IsCursor ? (first.Hotspot.HotspotX, first.Hotspot.HotspotY, width, height) : null;
        }
        finally
        {
            foreach (var picture in pictures)
                picture.Image.Dispose();
        }
    }

    /// <summary>
    /// True when an ANI goes to CUR as its first frame is: the preset changes nothing about the picture, so the .cur
    /// stored in the file is written as it is, every size and the hotspot with it.
    /// </summary>
    public static bool KeptAsItIs(ImageOptions options) =>
        !options.EditsPicture && !options.ReworksPixels && options.MaxSizeMb <= 0 && options.MinSizeMb <= 0;

    /// <summary>
    /// The .cur of the first step written unchanged; null when that frame is an .ico (it has no hotspot), which is then
    /// converted as any picture.
    /// </summary>
    public static IReadOnlyList<string>? SaveFirstCursor(ConversionStep step, IProgress<double>? progress)
    {
        var (frames, steps) = Parse(step.InputPath);
        var frame = frames[steps[0].Frame];
        if (!frame.IsCursor)
            return null;
        Entries(frame.Data); // checked as Read would
        progress?.Report(0.5);
        var path = step.Output.Allocate("cur");
        if (path is null)
            return [];
        File.WriteAllBytes(path, frame.Data);
        progress?.Report(1);
        return [path];
    }

    /// <summary>
    /// Makes the .cur ImageMagick wrote from the ANI's first picture a cursor with that picture's hotspot, where it is
    /// after the preset turned, mirrored or resized the picture. ImageMagick's CUR writer writes an icon (type 1), whose
    /// directory holds colour planes and bits per pixel where a cursor's holds its hotspot. When the hotspot isn't known
    /// (the frame is an .ico, or the picture was trimmed or set on a background) it is the top left corner.
    /// </summary>
    public static void KeepHotspot(string sourcePath, IEnumerable<string> written, ImageOptions options)
    {
        var hotspot = options.TrimTransparent || options.AddsBackdrop ? null : Hotspot(sourcePath);
        foreach (var path in written)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 22 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) is not (1 or 2))
                continue;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
            for (var i = 0; i < count && 6 + i * 16 + 16 <= data.Length; i++)
            {
                var entry = data.AsSpan(6 + i * 16);
                var (x, y) = hotspot is { } known ? Moved(known, options, Side(entry[0]), Side(entry[1])) : (0, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)x);
                BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], (ushort)y);
            }
            File.WriteAllBytes(path, data);
        }
    }

    private static uint Side(byte stored) => stored == 0 ? 256u : stored;

    /// <summary>
    /// Where the hotspot of a <paramref name="hotspot"/>.Width × Height picture is once it is turned and mirrored as
    /// <paramref name="options"/> say (<see cref="ImageEncoder.Turn"/>) and scaled to <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    internal static (int X, int Y) Moved((int X, int Y, uint Width, uint Height) hotspot, ImageOptions options, uint width, uint height)
    {
        var (x, y, w, h) = (hotspot.X, hotspot.Y, (int)hotspot.Width, (int)hotspot.Height);
        (x, y, w, h) = options.Rotation switch
        {
            ImageRotation.Right90 => (h - 1 - y, x, h, w),
            ImageRotation.Left90 => (y, w - 1 - x, h, w),
            ImageRotation.Rotate180 => (w - 1 - x, h - 1 - y, w, h),
            _ => (x, y, w, h),
        };
        if (options.Flip == ImageFlip.Horizontal)
            x = w - 1 - x;
        else if (options.Flip == ImageFlip.Vertical)
            y = h - 1 - y;
        // The centre of the hotspot's pixel is scaled, so it stays on the same part of the picture.
        var scaledX = (int)Math.Floor((x + 0.5) * width / w);
        var scaledY = (int)Math.Floor((y + 0.5) * height / h);
        return (Math.Clamp(scaledX, 0, (int)width - 1), Math.Clamp(scaledY, 0, (int)height - 1));
    }

    private static (uint Width, uint Height) Canvas(List<(MagickImage Image, Entry Hotspot)> pictures) =>
        (pictures.Max(p => p.Image.Width), pictures.Max(p => p.Image.Height));

    /// <summary>Each frame's largest size, decoded by ImageMagick, with its hotspot.</summary>
    private static List<(MagickImage Image, Entry Hotspot)> Decode(List<Frame> frames)
    {
        var pictures = new List<(MagickImage, Entry)>(frames.Count);
        try
        {
            foreach (var frame in frames)
            {
                var entries = Entries(frame.Data);
                using var sizes = new MagickImageCollection();
                try
                {
                    sizes.Read(frame.Data, new MagickReadSettings { Format = frame.IsCursor ? MagickFormat.Cur : MagickFormat.Icon });
                }
                catch (MagickException ex)
                {
                    throw new InvalidDataException("A frame of this animated cursor is damaged. (" + ex.Message + ")", ex);
                }
                if (sizes.Count == 0)
                    throw Damaged();
                // ImageMagick returns the sizes in the directory's order.
                var largest = Enumerable.Range(0, sizes.Count).MaxBy(i => (long)sizes[i].Width * sizes[i].Height);
                if (sizes[largest].Width > MaxSide || sizes[largest].Height > MaxSide)
                    throw new InvalidDataException("A frame of this animated cursor is too large to be a cursor.");
                pictures.Add(((MagickImage)sizes[largest].Clone(), largest < entries.Count ? entries[largest] : default));
            }
            return pictures;
        }
        catch
        {
            foreach (var (image, _) in pictures)
                image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The directory of a frame (a .cur or .ico file): each size's hotspot, after checking that the sizes it points to
    /// are in the frame and are not larger than a cursor can be.
    /// </summary>
    private static List<Entry> Entries(byte[] icon)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        if (count == 0 || 6 + count * 16 > icon.Length)
            throw Damaged();
        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = icon.AsSpan(6 + i * 16, 16);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (size == 0 || offset > icon.Length || size > icon.Length - offset)
                throw Damaged();
            var picture = icon.AsSpan((int)offset, (int)size);
            // A size is a PNG (its IHDR gives the size) or a BMP without its file header (BITMAPINFOHEADER; its height
            // counts the picture and its mask).
            var (width, height) = picture.Length >= 24 && picture[..8].SequenceEqual(PngSignature)
                ? ((long)BinaryPrimitives.ReadUInt32BigEndian(picture[16..]), (long)BinaryPrimitives.ReadUInt32BigEndian(picture[20..]))
                : picture.Length >= 12
                    ? (Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(picture[4..])), Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(picture[8..])) / 2)
                    : throw Damaged();
            if (width > MaxSide || height > MaxSide)
                throw new InvalidDataException("A frame of this animated cursor is too large to be a cursor.");
            entries.Add(new Entry(BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]), BinaryPrimitives.ReadUInt16LittleEndian(entry[6..])));
        }
        return entries;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>The frames and steps of the file, checked.</summary>
    private static (List<Frame> Frames, List<Step> Steps) Parse(string path)
    {
        byte[] data;
        using (var stream = File.OpenRead(path))
        {
            if (stream.Length > MaxFileBytes)
                throw new InvalidDataException("This animated cursor is too large to be one (more than 64 MB).");
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("ACON"u8))
            throw new InvalidDataException("This is not a Windows animated cursor (.ani), or the file is damaged.");
        // The RIFF size counts what follows it; a file can end with padding after it, never stop short of it.
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (riffSize > data.Length - 8L)
            throw CutShort();
        var end = 8 + (int)riffSize;

        uint? frameCount = null, stepCount = null, defaultJiffies = null, flags = null;
        uint[]? rates = null, sequence = null;
        var frames = new List<Frame>();
        // "LIST INFO" (the cursor's title and author) and any other chunk are skipped.
        foreach (var (id, body) in Chunks(data, 12, end))
        {
            switch (id)
            {
                case "anih" when body.Length >= 36:
                    frameCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body.Start + 4));
                    stepCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body.Start + 8));
                    defaultJiffies = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body.Start + 28));
                    flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body.Start + 32));
                    break;
                case "anih":
                    throw Damaged();
                case "rate":
                    rates = Numbers(data, body);
                    break;
                case "seq ":
                    sequence = Numbers(data, body);
                    break;
                case "LIST" when body.Length >= 4 && data.AsSpan(body.Start, 4).SequenceEqual("fram"u8):
                    foreach (var (frameId, frame) in Chunks(data, body.Start + 4, body.End))
                    {
                        if (frameId != "icon")
                            continue;
                        if (frames.Count == MaxFrames)
                            throw new InvalidDataException($"This animated cursor has more than {MaxFrames:N0} frames.");
                        frames.Add(ReadFrame(data[frame.Start..frame.End]));
                    }
                    break;
            }
        }

        if (flags is null || frameCount is null || stepCount is null || defaultJiffies is null)
            throw new InvalidDataException("This animated cursor has no header (anih chunk): the file is damaged.");
        if ((flags & IconFlag) == 0)
            throw new InvalidDataException(
                "This animated cursor stores its frames as raw bitmaps, which Filee can't read. Open it in a cursor " +
                "editor and save it again, or save its frames as .cur files.");
        if (frames.Count == 0)
            throw new InvalidDataException("This animated cursor has no frames (icon chunks): the file is damaged.");

        // Without "seq " the steps show the frames in order; with it, each step names its frame.
        var count = sequence?.Length ?? frames.Count;
        if (count == 0)
            throw Damaged();
        if (count > MaxSteps)
            throw new InvalidDataException($"This animated cursor has more than {MaxSteps:N0} steps.");
        var steps = new List<Step>(count);
        for (var i = 0; i < count; i++)
        {
            var frame = sequence is null ? (uint)i : sequence[i];
            if (frame >= frames.Count)
                throw Damaged();
            steps.Add(new Step((int)frame, rates is not null && i < rates.Length ? rates[i] : defaultJiffies.Value));
        }
        return (frames, steps);
    }

    /// <summary>Where a chunk's body is in the file.</summary>
    private readonly record struct Part(int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <summary>The chunks between <paramref name="start"/> and <paramref name="end"/>: id, and where the body is.</summary>
    private static IEnumerable<(string Id, Part Body)> Chunks(byte[] data, int start, int end)
    {
        var at = start;
        while (at + 8 <= end)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            if (size > end - (at + 8L))
                throw CutShort();
            var bodyStart = at + 8;
            yield return (id, new Part(bodyStart, bodyStart + (int)size));
            // Chunks start on an even byte: an odd-sized body is followed by a pad byte.
            at = bodyStart + (int)size + (int)(size & 1);
        }
    }

    private static uint[] Numbers(byte[] data, Part body)
    {
        var bytes = data.AsSpan(body.Start, body.Length);
        if (bytes.Length / 4 > MaxSteps)
            throw new InvalidDataException($"This animated cursor has more than {MaxSteps:N0} steps.");
        var numbers = new uint[bytes.Length / 4];
        for (var i = 0; i < numbers.Length; i++)
            numbers[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
        return numbers;
    }

    /// <summary>A frame: a .cur (type 2) or .ico (type 1) file.</summary>
    private static Frame ReadFrame(byte[] icon)
    {
        if (icon.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(icon) != 0)
            throw Damaged();
        return BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(2)) switch
        {
            1 => new Frame(icon, false),
            2 => new Frame(icon, true),
            _ => throw Damaged(),
        };
    }

    private static InvalidDataException Damaged() =>
        new("This animated cursor is damaged: a frame or the order of its steps can't be read.");

    private static InvalidDataException CutShort() =>
        new("This animated cursor is cut short or damaged: a part of it runs past the end of the file.");
}
