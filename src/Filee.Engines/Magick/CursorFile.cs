// Cursors (.cur) as Filee writes them. ImageMagick's CUR writer writes an icon: the header says type 1, and each size's
// directory entry holds colour planes and bits per pixel where a cursor holds its hotspot (the point that clicks). Windows
// reads those fields as the hotspot, so a "CUR" straight from ImageMagick clicks at (1, 32). Every .cur Filee writes is
// made a real cursor here (type 2), with its hotspot at the top left corner, or, from a cursor (CUR, ANI), where the
// source's hotspot is once the preset resized, turned or mirrored the picture.

using System.Buffers.Binary;
using Filee.Core.Presets;

namespace Filee.Engines.Magick;

internal static class CursorFile
{
    /// <summary>
    /// Makes the .ico or .cur at <paramref name="path"/> a cursor: type 2, every size with its hotspot. The hotspot is
    /// that of a <paramref name="hotspot"/>.Width × Height source turned and scaled as <see cref="Moved"/> says, or the
    /// top left corner when there is none. A file that isn't an icon or a cursor is left alone.
    /// </summary>
    public static void Write(string path, (int X, int Y, uint Width, uint Height)? hotspot = null, ImageOptions? options = null)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 22 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) is not (1 or 2))
            return;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        for (var i = 0; i < count && 6 + i * 16 + 16 <= data.Length; i++)
        {
            var entry = data.AsSpan(6 + i * 16);
            var (x, y) = hotspot is { } known ? Moved(known, options ?? new ImageOptions(), Side(entry[0]), Side(entry[1])) : (0, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)x);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], (ushort)y);
        }
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// The hotspot of a .cur file's largest size (the one converted, see <see cref="ImageEncoder.SelectFrames"/>) and
    /// that size; null for a file that isn't a cursor (an .ico has no hotspot).
    /// </summary>
    public static (int X, int Y, uint Width, uint Height)? Hotspot(string path)
    {
        Span<byte> header = stackalloc byte[6];
        using var stream = File.OpenRead(path);
        if (stream.ReadAtLeast(header, 6, throwOnEndOfStream: false) < 6 || BinaryPrimitives.ReadUInt16LittleEndian(header) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) != 2)
            return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var entries = new byte[count * 16];
        if (count == 0 || stream.ReadAtLeast(entries, entries.Length, throwOnEndOfStream: false) < entries.Length)
            return null;
        var largest = Enumerable.Range(0, count).MaxBy(i => (long)Side(entries[i * 16]) * Side(entries[i * 16 + 1]));
        var entry = entries.AsSpan(largest * 16);
        return (BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]), BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
            Side(entry[0]), Side(entry[1]));
    }

    /// <summary>The hotspot the preset can carry over: not when the picture is trimmed or set on a background.</summary>
    public static bool HotspotFollows(ImageOptions options) => !options.TrimTransparent && !options.AddsBackdrop;

    /// <summary>A side as a directory entry stores it: 0 means 256.</summary>
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
}
