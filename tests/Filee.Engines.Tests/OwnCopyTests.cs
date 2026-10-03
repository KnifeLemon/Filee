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

[Collection("EngineEnvironment")] // changes PATH and the system-copy cache
public sealed class SystemCopyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-path-").FullName;
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        EngineEnvironment.ForgetSystemCopies();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void A_tool_on_the_path_is_found_when_filee_has_no_copy()
    {
        if (!OperatingSystem.IsWindows())
            return;
        // Like Scoop's shims folder: ffmpeg.exe and ffprobe.exe side by side, on the PATH.
        File.WriteAllBytes(Path.Combine(_folder, "ffmpeg.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(_folder, "ffprobe.exe"), [0x4D, 0x5A]);
        Environment.SetEnvironmentVariable("PATH", _folder + ";" + _path);
        EngineEnvironment.ForgetSystemCopies();

        Assert.Equal(_folder, EngineEnvironment.SystemCopyFolder("ffmpeg"));
        Assert.Equal(Path.Combine(_folder, "ffmpeg.exe"), EngineEnvironment.SystemProgram("ffmpeg", "ffmpeg.exe"));
    }

    [Theory]
    [InlineData("ffmpeg", "ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright (c) 2000-2025", "7.1")]
    [InlineData("ffmpeg", "ffmpeg version n8.0 Copyright (c) 2000-2025", "8.0")]
    [InlineData("ffmpeg", "ffmpeg version 2025-06-02-git-688f3944ce-full_build", null)]
    [InlineData("pandoc", "pandoc.exe 3.6.4\nFeatures: +server +lua", "3.6")]
    [InlineData("calibre", "ebook-convert.exe (calibre 8.5.0)\nCreated by: Kovid Goyal", "8.5")]
    [InlineData("ghostscript", "10.04.0\r\n", "10.4")]
    [InlineData("libreoffice", "[Version]\nAllLanguages=en-US\nMsiProductVersion=25.2.4.3\n", "25.2")]
    public void Versions_are_read_from_each_program(string id, string output, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), CopyVersions.ParseVersion(id, output));

    [Fact]
    public void Expected_versions_come_from_engines_json() =>
        Assert.NotNull(CopyVersions.Expected("ffmpeg"));
}
