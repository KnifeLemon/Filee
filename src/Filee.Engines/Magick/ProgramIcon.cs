// The icon of a Windows program or library (.exe, .dll; issue #1188): the picture Explorer shows for the file. A PE file
// keeps its icons as resources: an RT_GROUP_ICON lists the sizes of one icon, and each size is an RT_ICON holding the
// picture as an .ico file does (a BMP without its file header, or a PNG). Filee takes the first group, the one Explorer
// shows, and puts it back together as an .ico in memory. The file is only read here, never loaded or run, and only the
// parts the headers point to are read, so a large installer costs no more than a small tool.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Microsoft.Win32.SafeHandles;

namespace Filee.Engines.Magick;

internal static class ProgramIcon
{
    private const uint GroupIconType = 14; // RT_GROUP_ICON
    private const uint IconType = 3;       // RT_ICON

    /// <summary>One size of an icon: a 1024 px 32-bit BMP is 4 MB, a PNG of that size much less.</summary>
    private const int MaxImageBytes = 16 * 1024 * 1024;

    /// <summary>All sizes of the icon together.</summary>
    private const long MaxIconBytes = 64L * 1024 * 1024;

    /// <summary>Entries of one resource directory. Real files have a few hundred at most.</summary>
    private const int MaxDirectoryEntries = 16_384;

    /// <summary>
    /// The program's icon as the bytes of an .ico file, with every size it has.
    /// </summary>
    /// <exception cref="ConversionRefusedException">
    /// <c>error.exe_unreadable</c> for a file that isn't a 32 or 64-bit Windows program or library, or is cut short;
    /// <c>error.exe_no_icon</c> for one without an icon of its own.
    /// </exception>
    public static byte[] Read(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return new Reader(handle).Icon();
    }

    /// <summary>
    /// True when a program's icon goes to ICO as it is, every size the program has: the preset changes nothing about
    /// the picture and asks for the usual sizes. Otherwise the icon is made again from its largest size, as for any
    /// picture.
    /// </summary>
    public static bool KeptAsItIs(ImageOptions options) =>
        !options.EditsPicture && !options.ReworksPixels && options.MaxSizeMb <= 0 && options.MinSizeMb <= 0
        && options.IcoSizes.ToHashSet().SetEquals(new ImageOptions().IcoSizes);

    /// <summary>Writes the program's icon unchanged to an .ico file.</summary>
    public static IReadOnlyList<string> Save(ConversionStep step, IProgress<double>? progress)
    {
        var icon = Read(step.InputPath);
        progress?.Report(0.5);
        var path = step.Output.Allocate("ico");
        if (path is null)
            return [];
        File.WriteAllBytes(path, icon);
        progress?.Report(1);
        return [path];
    }

    private sealed class Reader(SafeFileHandle handle)
    {
        private readonly long _length = RandomAccess.GetLength(handle);
        private readonly List<(uint Address, uint VirtualSize, uint RawSize, uint RawOffset)> _sections = [];
        private uint _resources;

        public byte[] Icon()
        {
            var resources = Headers();
            if (resources == 0)
                throw new ConversionRefusedException("error.exe_no_icon");
            _resources = resources;

            var types = Directory(0);
            var groups = Subdirectory(types, GroupIconType);
            var icons = Subdirectory(types, IconType);
            if (groups is null || icons is null)
                throw new ConversionRefusedException("error.exe_no_icon");
            var names = Directory(groups.Value);
            if (names.Count == 0)
                throw new ConversionRefusedException("error.exe_no_icon");

            // The first group is the icon Explorer shows: named groups come first, then numbered ones in order, as the
            // directory stores them.
            var group = Data(names[0]);
            var images = new Dictionary<uint, Entry>();
            foreach (var entry in Directory(icons.Value).Where(e => !e.IsName))
                images.TryAdd(entry.Id, entry);
            return Assemble(group, images);
        }

        /// <summary>Checks the MZ and PE headers, reads the section table and returns the resources' address (0: none).</summary>
        private uint Headers()
        {
            var dos = Read(0, 0x40);
            if (dos[0] != 'M' || dos[1] != 'Z')
                throw Unreadable();
            var pe = (long)BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(0x3c));
            var file = Read(pe, 24);
            if (!file.AsSpan(0, 4).SequenceEqual("PE\0\0"u8))
                throw Unreadable(); // an MS-DOS or 16-bit Windows program (NE) has no PE header
            var sections = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(20));
            if (sections == 0 || optionalSize < 2)
                throw Unreadable();

            var optional = Read(pe + 24, optionalSize);
            // PE32 and PE32+ differ in where the data directories begin; the resource table is the third of them.
            var (countAt, directoriesAt) = BinaryPrimitives.ReadUInt16LittleEndian(optional) switch
            {
                0x10b => (92, 96),
                0x20b => (108, 112),
                _ => throw Unreadable(),
            };
            var table = Read(pe + 24 + optionalSize, sections * 40);
            for (var i = 0; i < sections; i++)
            {
                var section = table.AsSpan(i * 40);
                _sections.Add((BinaryPrimitives.ReadUInt32LittleEndian(section[12..]), BinaryPrimitives.ReadUInt32LittleEndian(section[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(section[16..]), BinaryPrimitives.ReadUInt32LittleEndian(section[20..])));
            }
            if (optionalSize < directoriesAt + 3 * 8 || BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(countAt)) < 3)
                return 0;
            return BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoriesAt + 2 * 8));
        }

        /// <summary>The entries of the resource directory at <paramref name="offset"/> into the resources.</summary>
        private List<Entry> Directory(uint offset)
        {
            var header = Resource(offset, 16);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
            if (count > MaxDirectoryEntries)
                throw Unreadable();
            var raw = Resource(offset + 16, count * 8);
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                var name = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 8));
                var target = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 8 + 4));
                entries.Add(new Entry((name & 0x8000_0000) != 0, name & 0x7fff_ffff, (target & 0x8000_0000) != 0, target & 0x7fff_ffff));
            }
            return entries;
        }

        /// <summary>The subdirectory of the resource type <paramref name="type"/>, or null when the file has none.</summary>
        private static uint? Subdirectory(List<Entry> types, uint type) =>
            types.FirstOrDefault(e => !e.IsName && e.Id == type && e.IsDirectory) is { IsDirectory: true } entry ? entry.Target : null;

        /// <summary>
        /// The data of a resource: below its name is a directory of languages, and the first language is taken (a program
        /// rarely has its icon in more than one).
        /// </summary>
        private byte[] Data(Entry entry)
        {
            if (entry.IsDirectory)
            {
                var languages = Directory(entry.Target);
                // Two levels below a type is the data; a directory there would lead further and is no resource file's.
                entry = languages.FirstOrDefault(e => !e.IsDirectory) ?? throw Unreadable();
            }
            var description = Resource(entry.Target, 16);
            var address = BinaryPrimitives.ReadUInt32LittleEndian(description);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(description.AsSpan(4));
            if (size > MaxImageBytes)
                throw Unreadable();
            return Read(Offset(address, size), (int)size);
        }

        /// <summary>
        /// An .ico file from a group: the same header and size list, with each size's place in the file instead of its
        /// resource number, then the pictures.
        /// </summary>
        private byte[] Assemble(byte[] group, Dictionary<uint, Entry> images)
        {
            if (group.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(2)) != 1)
                throw Unreadable();
            var count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
            if (group.Length < 6 + count * 14)
                throw Unreadable();

            var sizes = new List<(byte[] Entry, byte[] Picture)>();
            long total = 0;
            for (var i = 0; i < count; i++)
            {
                var entry = group.AsSpan(6 + i * 14, 14);
                // A size whose picture is missing is left out, as Windows does when it draws the icon.
                if (!images.TryGetValue(BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]), out var image))
                    continue;
                var picture = Data(image);
                if (picture.Length == 0)
                    continue;
                total += picture.Length;
                if (total > MaxIconBytes)
                    throw Unreadable();
                sizes.Add((entry[..12].ToArray(), picture));
            }
            if (sizes.Count == 0)
                throw new ConversionRefusedException("error.exe_no_icon");

            var ico = new byte[6 + sizes.Count * 16 + total];
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), (ushort)sizes.Count);
            var at = 6 + sizes.Count * 16;
            for (var i = 0; i < sizes.Count; i++)
            {
                var (entry, picture) = sizes[i];
                var target = ico.AsSpan(6 + i * 16, 16);
                // Width, height, colours, reserved, planes and bits per pixel are the same as in the group.
                entry.AsSpan(0, 8).CopyTo(target);
                BinaryPrimitives.WriteUInt32LittleEndian(target[8..], (uint)picture.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(target[12..], (uint)at);
                picture.CopyTo(ico.AsSpan(at));
                at += picture.Length;
            }
            return ico;
        }

        /// <summary><paramref name="count"/> bytes at <paramref name="offset"/> into the resource directory.</summary>
        private byte[] Resource(uint offset, int count) => Read(Offset(_resources + (long)offset, (uint)count), count);

        /// <summary>Where in the file the bytes at a virtual address are, when all of them are stored there.</summary>
        private long Offset(long address, uint count)
        {
            foreach (var (start, virtualSize, rawSize, rawOffset) in _sections)
            {
                if (address < start || address >= start + (long)Math.Max(virtualSize, rawSize))
                    continue;
                var into = address - start;
                // Past the stored part a section is zeros in memory only: such data isn't in the file.
                if (into + count > rawSize)
                    throw Unreadable();
                return rawOffset + into;
            }
            throw Unreadable();
        }

        private byte[] Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > _length)
                throw Unreadable();
            var data = new byte[count];
            for (var read = 0; read < count;)
            {
                var got = RandomAccess.Read(handle, data.AsSpan(read), offset + read);
                if (got == 0)
                    throw Unreadable();
                read += got;
            }
            return data;
        }

        private static ConversionRefusedException Unreadable() => new("error.exe_unreadable");
    }

    /// <summary>One entry of a resource directory: a type, name or language, and what it leads to.</summary>
    private sealed record Entry(bool IsName, uint Id, bool IsDirectory, uint Target);
}
