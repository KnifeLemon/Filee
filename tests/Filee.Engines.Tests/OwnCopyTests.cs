// A copy of an optional engine the user already has (Settings → Engines → "Use my copy…") is found from the program
// they picked, wins over Filee's download, and is refused when its folder lacks a program the engine needs.

using Filee.Engines.Ebooks;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // OwnCopies is process-wide
public sealed class OwnCopyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-own-").FullName;

    public void Dispose()
    {
        EngineEnvironment.OwnCopies = new Dictionary<string, string>();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string Program(string relative)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    [Fact]
    public void The_users_own_copy_wins_over_filees_download()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var ffmpeg = Program(@"ffmpeg\bin\ffmpeg.exe");
        var ffprobe = Program(@"ffmpeg\bin\ffprobe.exe");
        var pandoc = Program(@"pandoc\pandoc.exe");
        var calibre = Program(@"Calibre2\calibre.exe");
        var convert = Program(@"Calibre2\ebook-convert.exe");

        EngineEnvironment.OwnCopies = new Dictionary<string, string>
        {
            ["ffmpeg"] = Path.Combine(_folder, "ffmpeg"), // the folder of an FFmpeg build: its bin\ is found
            ["pandoc"] = pandoc,
            ["calibre"] = calibre, // calibre.exe picked: ebook-convert.exe next to it is used
        };

        Assert.Equal((ffmpeg, ffprobe), FfmpegConverter.Locate());
        Assert.Equal(pandoc, PandocConverter.Locate());
        Assert.Equal(convert, CalibreConverter.Locate());
    }

    [Fact]
    public void A_folder_without_every_program_is_not_used()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var ffmpeg = Program(@"tools\ffmpeg.exe"); // no ffprobe.exe next to it

        Assert.Null(EngineEnvironment.FolderWithPrograms("ffmpeg", ffmpeg));
        EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["ffmpeg"] = ffmpeg };
        Assert.Null(EngineEnvironment.OwnCopyFolder("ffmpeg"));
        Assert.NotEqual(ffmpeg, FfmpegConverter.Locate()?.Ffmpeg);
    }
}

/// <summary>Runs alone: while OwnCopies points at fake programs, no other engine test may convert.</summary>
[CollectionDefinition("EngineEnvironment", DisableParallelization = true)]
public sealed class EngineEnvironmentCollection;
