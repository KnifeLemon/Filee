// Real images verify automatic and custom labels through both readers and raw volume descriptors.
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Archives;

namespace Filee.Engines.Tests;

public sealed class IsoVolumeLabelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-label-").FullName;

    [Theory]
    [InlineData(IsoFileSystem.Joliet, "my photos", "", "my photos", "MY_PHOTOS")]
    [InlineData(IsoFileSystem.Joliet, "photos.jpg", "", "photos.jpg", "PHOTOS_JPG")]
    [InlineData(IsoFileSystem.Joliet, "Filee-ISO-Label-1492-Test1-win", "", "Filee-ISO-Label-", "FILEE_ISO_LABEL_")]
    [InlineData(IsoFileSystem.Joliet, "照片", "", "照片", "__")]
    [InlineData(IsoFileSystem.Joliet, "😀", "", "FILEE", "FILEE")]
    [InlineData(IsoFileSystem.Joliet, "source", "ABCDEFGHIJKLMNOP", "ABCDEFGHIJKLMNOP", "ABCDEFGHIJKLMNOP")]
    [InlineData(IsoFileSystem.Udf201, "我的照片", "", "我的照片", null)]
    [InlineData(IsoFileSystem.Udf201, "abcdefghijklmn text字", "", "abcdefghijklmn", null)]
    [InlineData(IsoFileSystem.Udf201, "source", "123456789012345678901234567890", "123456789012345678901234567890", null)]
    [InlineData(IsoFileSystem.Udf201, "source", "一二三四五六七八九十一二三四五", "一二三四五六七八九十一二三四五", null)]
    public async Task Folder_labels_are_not_silently_truncated(IsoFileSystem filesystem, string folderName, string custom, string expected, string? primary)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, folderName)).FullName;
        File.WriteAllText(Path.Combine(folder, "data.txt"), "contents");
        var output = Path.Combine(_root, "image.iso");
        var preset = new Preset { TargetFormat = "iso", Archive = { IsoFileSystem = filesystem, IsoVolumeLabel = custom } };
        await new FolderIsoConverter().ConvertAsync(new ConversionStep(folder, "folder", "iso", preset, new Output(output), _root), null, TestContext.Current.CancellationToken);
        Assert.Equal(expected, ReadLabel(output, filesystem));
        if (filesystem == IsoFileSystem.Joliet)
        {
            var bytes = File.ReadAllBytes(output);
            Assert.Equal(primary, Encoding.ASCII.GetString(bytes, 16 * 2048 + 40, 32).TrimEnd());
            Assert.Equal(expected, Encoding.BigEndianUnicode.GetString(bytes, 17 * 2048 + 40, 32).TrimEnd());
        }
    }

    [Theory]
    [InlineData(IsoFileSystem.Joliet, false, "", "holiday")]
    [InlineData(IsoFileSystem.Joliet, true, "", "holiday")]
    [InlineData(IsoFileSystem.Udf201, false, "", "holiday")]
    [InlineData(IsoFileSystem.Udf201, true, "", "holiday")]
    [InlineData(IsoFileSystem.Udf201, true, "Custom disc", "Custom disc")]
    public async Task Archive_labels_use_the_original_name_and_remove_compound_extensions(IsoFileSystem filesystem, bool tar, string custom, string expected)
    {
        var source = Path.Combine(_root, "holiday" + (tar ? ".tar.gz" : ".zip"));
        if (tar)
        {
            using var output = File.Create(source);
            using var gzip = new GZipStream(output, CompressionMode.Compress);
            using var writer = new TarWriter(gzip);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data.txt") { DataStream = new MemoryStream("contents"u8.ToArray()) });
        }
        else
        {
            using var zip = ZipFile.Open(source, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("data.txt").Open());
            writer.Write("contents");
        }
        var image = Path.Combine(_root, "image.iso");
        var preset = new Preset { TargetFormat = "iso", Archive = { IsoFileSystem = filesystem, IsoVolumeLabel = custom } };
        await new ArchiveConverter().ConvertAsync(new ConversionStep(source, tar ? "tgz" : "zip", "iso", preset, new Output(image), _root), null, TestContext.Current.CancellationToken);
        Assert.Equal(expected, ReadLabel(image, filesystem));
        Assert.Equal(custom, preset.Archive.IsoVolumeLabel);
    }

    [Theory]
    [InlineData(IsoFileSystem.Joliet, "a/b")]
    [InlineData(IsoFileSystem.Udf201, "1234567890123456789012345678901")]
    public async Task Invalid_custom_labels_preserve_existing_destinations(IsoFileSystem filesystem, string label)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var output = Path.Combine(_root, "image.iso");
        File.WriteAllText(output, "keep");
        var preset = new Preset { TargetFormat = "iso", Archive = { IsoFileSystem = filesystem, IsoVolumeLabel = label } };
        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() => new FolderIsoConverter().ConvertAsync(new ConversionStep(folder, "folder", "iso", preset, new Output(output), _root), null, TestContext.Current.CancellationToken));
        Assert.Equal("error.iso_label", error.ErrorKey);
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, ".filee-iso-*.tmp"));
    }

    private static string ReadLabel(string path, IsoFileSystem filesystem)
    {
        using var stream = File.OpenRead(path);
        using DiscUtils.DiscFileSystem fs = filesystem == IsoFileSystem.Joliet ? new CDReader(stream, true) : new UdfReader(stream);
        return fs.VolumeLabel;
    }

    private sealed class Output(string path) : IOutputAllocator { public string? Allocate(string extension, string? suffix = null) => path; }
    public void Dispose() => Directory.Delete(_root, true);
}
