// The icon of a Windows program or library (.exe, .dll; issue #1188). The programs are built here as a linker lays them
// out, with only what Windows needs to find their icons: the MZ and PE headers, one .rsrc section and the resource tree
// in it. Their icons come from ICO files ImageMagick writes, so a program's icon taken out as ICO can be compared with
// the icon that went in.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class ProgramIconTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const uint IconType = 3;       // RT_ICON
    private const uint GroupIconType = 14; // RT_GROUP_ICON
    private const uint VersionType = 16;   // RT_VERSION
    private const uint SectionAddress = 0x1000;
    private const int SectionOffset = 0x200;

    /// <summary>
    /// An .ico with one square picture per size, each in its own colour: a BMP below 256 px (as ImageMagick encodes it in
    /// an icon), a PNG at 256, as in most programs. ImageMagick writes one size per file here, so the sizes are put
    /// together by hand.
    /// </summary>
    private static byte[] Ico(params (uint Size, IMagickColor<byte> Colour)[] sizes)
    {
        var entries = new List<(byte[] Entry, byte[] Picture)>();
        foreach (var (size, colour) in sizes)
        {
            using var image = new MagickImage(colour, size, size);
            if (size >= 256)
            {
                // Width and height 0 stand for 256; one plane, 32 bits per pixel.
                entries.Add(([0, 0, 0, 0, 1, 0, 32, 0, 0, 0, 0, 0], image.ToByteArray(MagickFormat.Png32)));
                continue;
            }
            var single = image.ToByteArray(MagickFormat.Icon);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(single.AsSpan(6 + 8));
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(single.AsSpan(6 + 12));
            entries.Add((single[6..(6 + 12)], single[offset..(offset + length)]));
        }
        var ico = new byte[6 + entries.Count * 16 + entries.Sum(e => e.Picture.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), (ushort)entries.Count);
        var at = 6 + entries.Count * 16;
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Entry.CopyTo(ico.AsSpan(6 + i * 16));
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + i * 16 + 8), (uint)entries[i].Picture.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + i * 16 + 12), (uint)at);
            entries[i].Picture.CopyTo(ico.AsSpan(at));
            at += entries[i].Picture.Length;
        }
        return ico;
    }

    /// <summary>The 16, 32 and 256 px icon most programs have: red, green and blue.</summary>
    private static byte[] UsualIcon() => Ico((16, MagickColors.Red), (32, MagickColors.Lime), (256, MagickColors.Blue));

    /// <summary>
    /// The resources of a program with these icons: each .ico becomes an icon group (numbered 1, 2, … in order) and its
    /// sizes icons (numbered on from 1 across the groups), as the resource compiler does with ICON statements.
    /// </summary>
    private static List<(uint Type, List<(uint Id, byte[] Data)> Items)> IconResources(params byte[][] icons)
    {
        var images = new List<(uint, byte[])>();
        var groups = new List<(uint, byte[])>();
        foreach (var ico in icons)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
            var group = new byte[6 + count * 14];
            ico.AsSpan(0, 6).CopyTo(group);
            for (var i = 0; i < count; i++)
            {
                var entry = ico.AsSpan(6 + i * 16, 16);
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                var id = (uint)images.Count + 1;
                images.Add((id, ico.AsSpan(offset, size).ToArray()));
                entry[..12].CopyTo(group.AsSpan(6 + i * 14));
                BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(6 + i * 14 + 12), (ushort)id);
            }
            groups.Add(((uint)groups.Count + 1, group));
        }
        return [(IconType, images), (GroupIconType, groups)];
    }

    /// <summary>
    /// A resource section: the type directory, a directory of names per type and of languages (US English) per name,
    /// the data descriptions and the data, at the addresses the section is loaded at.
    /// </summary>
    private static byte[] ResourceSection(List<(uint Type, List<(uint Id, byte[] Data)> Items)> types)
    {
        static int Directory(int entries) => 16 + 8 * entries;
        var items = types.SelectMany(t => t.Items).ToList();
        var nameDirectories = Directory(types.Count);
        var languageDirectories = nameDirectories + types.Sum(t => Directory(t.Items.Count));
        var descriptions = languageDirectories + items.Count * Directory(1);
        var data = descriptions + items.Count * 16;
        var section = new byte[data + items.Sum(i => (i.Data.Length + 3) & ~3)];

        void WriteDirectory(int at, IReadOnlyList<(uint Id, int Target, bool IsDirectory)> entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(at + 14), (ushort)entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(at + 16 + i * 8), entries[i].Id);
                BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(at + 20 + i * 8),
                    (uint)entries[i].Target | (entries[i].IsDirectory ? 0x8000_0000 : 0));
            }
        }

        var typeEntries = new List<(uint, int, bool)>();
        var nameAt = nameDirectories;
        var item = 0;
        foreach (var (type, list) in types)
        {
            typeEntries.Add((type, nameAt, true));
            var nameEntries = new List<(uint, int, bool)>();
            foreach (var (id, bytes) in list)
            {
                var languageAt = languageDirectories + item * Directory(1);
                var descriptionAt = descriptions + item * 16;
                nameEntries.Add((id, languageAt, true));
                WriteDirectory(languageAt, [(1033u, descriptionAt, false)]);
                BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(descriptionAt), SectionAddress + (uint)data);
                BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(descriptionAt + 4), (uint)bytes.Length);
                bytes.CopyTo(section.AsSpan(data));
                data += (bytes.Length + 3) & ~3;
                item++;
            }
            WriteDirectory(nameAt, nameEntries);
            nameAt += Directory(list.Count);
        }
        WriteDirectory(0, typeEntries);
        return section;
    }

    /// <summary>A program (or with <paramref name="library"/> a DLL) whose only section holds these resources.</summary>
    private static byte[] Program(List<(uint Type, List<(uint Id, byte[] Data)> Items)>? resources, bool pe32 = false,
        bool library = false)
    {
        var section = resources is null ? new byte[16] : ResourceSection(resources);
        var rawSize = (section.Length + 0x1ff) & ~0x1ff;
        var file = new byte[SectionOffset + rawSize];
        "MZ"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x3c), 0x40);
        "PE\0\0"u8.CopyTo(file.AsSpan(0x40));
        var optionalSize = pe32 ? 224 : 240;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x44), pe32 ? (ushort)0x14c : (ushort)0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x46), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x54), (ushort)optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x56), library ? (ushort)0x2022 : (ushort)0x22);
        var optional = file.AsSpan(0x58, optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(optional, pe32 ? (ushort)0x10b : (ushort)0x20b);
        var directories = pe32 ? 96 : 112;
        BinaryPrimitives.WriteUInt32LittleEndian(optional[(directories - 4)..], 16);
        if (resources is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(optional[(directories + 16)..], SectionAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(optional[(directories + 20)..], (uint)section.Length);
        }
        var header = file.AsSpan(0x58 + optionalSize, 40);
        ".rsrc"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)section.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], SectionAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], SectionOffset);
        section.CopyTo(file.AsSpan(SectionOffset));
        return file;
    }

    private string Save(byte[] file, string name = "tool.exe")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, file);
        return path;
    }

    private static string Failures(ConversionJob job) => string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}"));

    private static IMagickColor<byte>? Colour(string path)
    {
        using var image = new MagickImage(path);
        using var pixels = image.GetPixels();
        return pixels.GetPixel((int)image.Width / 2, (int)image.Height / 2)!.ToColor();
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData("SHELL32.DLL")]
    public void Programs_and_libraries_are_read_as_their_icon_and_never_written(string name)
    {
        var format = FormatRegistry.Detect(name)!;
        Assert.Equal(FormatRegistry.Program, format.Id);
        Assert.Equal(FormatCategory.Image, format.Category);
        Assert.False(format.Writable);
        Assert.True(FormatRegistry.IsProgram(name));
        Assert.False(FormatRegistry.IsProgram("tool.ico"));
    }

    [Theory]
    [InlineData(false, "tool.exe")]
    [InlineData(true, "tool.exe")]
    [InlineData(false, "shell.dll")]
    public async Task A_program_becomes_a_png_of_its_largest_icon(bool pe32, string name)
    {
        var program = Save(Program(IconResources(UsualIcon()), pe32, library: name.EndsWith(".dll")), name);

        var job = await fx.ConvertAsync([program], new Preset { TargetFormat = "png" });

        Assert.True(job.State == JobState.Completed, Failures(job));
        var output = Assert.Single(job.Outputs);
        Assert.Equal(Path.ChangeExtension(name, "png"), Path.GetFileName(output));
        using var png = new MagickImage(output);
        Assert.Equal((256u, 256u), (png.Width, png.Height));
        Assert.Equal(MagickColors.Blue, Colour(output));
    }

    [Fact]
    public async Task A_program_becomes_its_own_icon_as_ico_with_every_size()
    {
        var icon = UsualIcon();
        var program = Save(Program(IconResources(icon)));

        var job = await fx.ConvertAsync([program], new Preset { TargetFormat = "ico" });

        Assert.True(job.State == JobState.Completed, Failures(job));
        // The same file that went into the program: no size is made again from another.
        var output = Assert.Single(job.Outputs);
        Assert.Equal(icon, File.ReadAllBytes(output));
        using var sizes = new MagickImageCollection(output);
        Assert.Equal([16u, 32u, 256u], sizes.Select(i => i.Width).Order());
        // The small sizes stay BMPs and the large one a PNG.
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        var pngs = Enumerable.Range(0, 3)
            .Count(i => icon.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(icon.AsSpan(6 + i * 16 + 12))).StartsWith(png));
        Assert.Equal(1, pngs);
    }

    [Fact]
    public async Task Other_icon_sizes_make_the_icon_again_from_its_largest_size()
    {
        var program = Save(Program(IconResources(UsualIcon())));
        var preset = new Preset { TargetFormat = "ico" };
        preset.Image.IcoSizes = [48, 24];

        var job = await fx.ConvertAsync([program], preset);

        Assert.True(job.State == JobState.Completed, Failures(job));
        using var ico = new MagickImageCollection(Assert.Single(job.Outputs));
        Assert.Equal([48u, 24u], ico.Select(i => i.Width).OrderDescending());
        // Both from the 256 px picture, not the 32 px one.
        Assert.All(ico, i => Assert.Equal(MagickColors.Blue, i.GetPixels().GetPixel((int)i.Width / 2, (int)i.Height / 2)!.ToColor()));
    }

    [Fact]
    public async Task The_first_icon_group_is_taken_as_explorer_shows_it()
    {
        var shown = Ico((32, MagickColors.Red), (48, MagickColors.Red));
        var other = Ico((64, MagickColors.Lime));
        var program = Save(Program(IconResources(shown, other)));

        var job = await fx.ConvertAsync([program], new Preset { TargetFormat = "png" });

        Assert.True(job.State == JobState.Completed, Failures(job));
        var output = Assert.Single(job.Outputs);
        using var png = new MagickImage(output);
        Assert.Equal(48u, png.Width);
        Assert.Equal(MagickColors.Red, Colour(output));
    }

    [Fact]
    public async Task A_size_whose_picture_is_missing_is_left_out()
    {
        var resources = IconResources(UsualIcon());
        resources[0].Items.RemoveAt(2); // the 256 px picture

        var job = await fx.ConvertAsync([Save(Program(resources))], new Preset { TargetFormat = "png" });

        Assert.True(job.State == JobState.Completed, Failures(job));
        var output = Assert.Single(job.Outputs);
        Assert.Equal(MagickColors.Lime, Colour(output));
    }

    public static TheoryData<string> NoIcon => new() { "no resources", "other resources" };

    [Theory]
    [MemberData(nameof(NoIcon))]
    public async Task A_program_without_an_icon_of_its_own_says_so(string kind)
    {
        var resources = kind == "no resources" ? null : new List<(uint, List<(uint, byte[])>)> { (VersionType, [(1, new byte[64])]) };
        var job = await fx.ConvertAsync([Save(Program(resources))], new Preset { TargetFormat = "png" });

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.exe_no_icon", file.ErrorKey);
    }

    public static TheoryData<string> Unreadable => new() { "cut short", "not a program", "16-bit", "too short for a header" };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task A_file_that_isnt_a_readable_program_says_so(string kind)
    {
        var program = Program(IconResources(UsualIcon()));
        var file = kind switch
        {
            // The icons run past the end of the file.
            "cut short" => program[..(SectionOffset + 300)],
            "not a program" => "This is a text file renamed to .exe"u8.ToArray(),
            "16-bit" => [.. program[..0x40], .. "NE"u8.ToArray(), .. new byte[0x200]],
            _ => "MZ"u8.ToArray(),
        };

        var job = await fx.ConvertAsync([Save(file)], new Preset { TargetFormat = "png" });

        var result = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, result.State);
        Assert.Equal("error.exe_unreadable", result.ErrorKey);
    }
}
