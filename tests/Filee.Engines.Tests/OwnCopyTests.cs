// A copy of an optional engine the user already has (Settings → Engines → "Use my copy…") is found from the program
// they picked, wins over Filee's download, and is refused when its folder lacks a program the engine needs.

using Filee.Engines.Ebooks;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using Filee.Engines.Metadata;
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
        var ffmpeg = Program(Path.Combine("ffmpeg", "bin", EngineEnvironment.ProgramName("ffmpeg.exe")));
        var ffprobe = Program(Path.Combine("ffmpeg", "bin", EngineEnvironment.ProgramName("ffprobe.exe")));
        var pandoc = Program(Path.Combine("pandoc", EngineEnvironment.ProgramName("pandoc.exe")));
        var calibre = Program(Path.Combine("Calibre2", EngineEnvironment.ProgramName("calibre.exe")));
        var convert = Program(Path.Combine("Calibre2", EngineEnvironment.ProgramName("ebook-convert.exe")));

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
        var ffmpeg = Program(Path.Combine("tools", EngineEnvironment.ProgramName("ffmpeg.exe")));

        Assert.Null(EngineEnvironment.FolderWithPrograms("ffmpeg", ffmpeg));
        EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["ffmpeg"] = ffmpeg };
        Assert.Null(EngineEnvironment.OwnCopyFolder("ffmpeg"));
        Assert.NotEqual(ffmpeg, FfmpegConverter.Locate()?.Ffmpeg);
    }

    [Fact]
    public void An_exiftool_script_with_nothing_to_run_it_reports_the_missing_perl()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "ExifTool is a program on Windows.");
        var script = Path.Combine(_folder, "exiftool");
        File.WriteAllText(script, "#!/no/such/perl\nprint 1;\n");
        EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["exiftool"] = script };

        Assert.Equal("engine.reason.needs_perl", new ExifToolConverter().GetStatus().ReasonKey);
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
        File.WriteAllBytes(Path.Combine(_folder, EngineEnvironment.ProgramName("ffmpeg.exe")), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(_folder, EngineEnvironment.ProgramName("ffprobe.exe")), [0x4D, 0x5A]);
        Environment.SetEnvironmentVariable("PATH", _folder + Path.PathSeparator + _path);
        EngineEnvironment.ForgetSystemCopies();

        Assert.Equal(_folder, EngineEnvironment.SystemCopyFolder("ffmpeg"));
        Assert.Equal(Path.Combine(_folder, EngineEnvironment.ProgramName("ffmpeg.exe")), EngineEnvironment.SystemProgram("ffmpeg", "ffmpeg.exe"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // skips elsewhere
    public void LibreOffice_is_found_in_the_folder_its_installer_recorded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows registry.");
        const string key = @"Software\FileeTests\LibreOffice\UNO\InstallPath";
        File.WriteAllBytes(Path.Combine(_folder, "soffice.exe"), [0x4D, 0x5A]);
        try
        {
            using (var written = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
                written.SetValue(null, $"\"{_folder}\"");

            var folders = EngineEnvironment.RegisteredFolders(key);

            Assert.Contains(_folder, folders);
            Assert.Equal(_folder, EngineEnvironment.FolderWithPrograms("libreoffice", _folder));
            Assert.Empty(EngineEnvironment.RegisteredFolders(@"Software\FileeTests\Missing"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FileeTests", throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData("ffmpeg", "ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright (c) 2000-2025", "7.1")]
    [InlineData("ffmpeg", "ffmpeg version n8.0 Copyright (c) 2000-2025", "8.0")]
    [InlineData("ffmpeg", "ffmpeg version 2025-06-02-git-688f3944ce-full_build", null)]
    [InlineData("pandoc", "pandoc.exe 3.6.4\nFeatures: +server +lua", "3.6")]
    [InlineData("calibre", "ebook-convert.exe (calibre 8.5.0)\nCreated by: Kovid Goyal", "8.5")]
    [InlineData("weasyprint", "WeasyPrint version 70.0", "70.0")]
    [InlineData("ghostscript", "10.04.0\r\n", "10.4")]
    [InlineData("tesseract", "tesseract 5.5.3\n leptonica-1.87.0\n  libgif 5.2.2 : libjpeg 8d", "5.5")]
    [InlineData("tesseract", "tesseract v5.4.0.20240606\r\n leptonica-1.84.1\r\n", "5.4")]
    [InlineData("tesseract", " leptonica-1.87.0\n  libpng 1.6.58 : zlib 1.2.12\n", null)]
    [InlineData("exiftool", "13.60\n", "13.60")]
    [InlineData("ghostscript", "99999999999.1\n", null)]
    [InlineData("libreoffice", "[Version]\nAllLanguages=en-US\nMsiProductVersion=25.2.4.3\n", "25.2")]
    [InlineData("libreoffice", "LibreOffice 26.2.6.2 98d5f8\n", "26.2")]
    // LibreOffice 26.8's version.ini has no MsiProductVersion; Installed adds soffice.exe's product version to it.
    [InlineData("libreoffice", "[Version]\nUpdateID=LibreOffice_26_en-US_de\nVendor=The Document Foundation\n", null)]
    [InlineData("libreoffice", "[Version]\nUpdateID=LibreOffice_26_en-US_de\nVendor=The Document Foundation\nLibreOffice 26.8.1.1", "26.8")]
    public void Versions_are_read_from_each_program(string id, string output, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), CopyVersions.ParseVersion(id, output));

    [Fact]
    public void A_libreoffice_without_msi_version_is_told_by_its_program()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "version.ini is read on Windows only");
        // Any program with a version resource stands in for soffice.exe.
        var program = Environment.ProcessPath!;
        var folder = Directory.CreateTempSubdirectory("filee-lo-version-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "version.ini"), "[Version]\nVendor=The Document Foundation\n");
            File.Copy(program, Path.Combine(folder, "soffice.exe"));
            var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(program);

            Assert.Equal(new Version(product.ProductMajorPart, product.ProductMinorPart), CopyVersions.Installed("libreoffice", folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_copy_on_the_pc_hidden_by_filees_download_is_reported_but_not_used()
    {
        File.WriteAllBytes(Path.Combine(_folder, EngineEnvironment.ProgramName("ffmpeg.exe")), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(_folder, EngineEnvironment.ProgramName("ffprobe.exe")), [0x4D, 0x5A]);
        Environment.SetEnvironmentVariable("PATH", _folder + Path.PathSeparator + _path);
        EngineEnvironment.ForgetSystemCopies();
        var root = Directory.CreateTempSubdirectory("filee-download-").FullName;
        try
        {
            var component = new EngineComponent("ffmpeg", "8.0", "https://example.test/ffmpeg.zip", new string('a', 64), 1, "zip");
            var installer = new EngineInstaller(new HttpClient(), root, new Dictionary<string, EngineComponent> { ["ffmpeg"] = component });
            var package = new EnginePackage("ffmpeg", ["ffmpeg"], 1000, ["ffmpeg"]);

            // Without Filee's download the copy on the PC is the one used.
            Assert.Equal(new EngineCopy(EngineCopyStatus.OwnCopy, _folder, true, false), installer.StatusOf(package));

            // With it, the download comes first and the copy on the PC is only reported.
            Directory.CreateDirectory(Path.Combine(root, "ffmpeg"));
            File.WriteAllText(Path.Combine(root, "ffmpeg", ".filee-component"), component.Sha256);
            Assert.Equal(new EngineCopy(EngineCopyStatus.Installed, null, false, false, _folder), installer.StatusOf(package));

            // The user's own copy comes first of all: nothing else is reported.
            var own = Directory.CreateTempSubdirectory("filee-own-").FullName;
            File.WriteAllBytes(Path.Combine(own, EngineEnvironment.ProgramName("ffmpeg.exe")), [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(own, EngineEnvironment.ProgramName("ffprobe.exe")), [0x4D, 0x5A]);
            EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["ffmpeg"] = own };
            try
            {
                Assert.Equal(new EngineCopy(EngineCopyStatus.OwnCopy, own, false, false), installer.StatusOf(package));
            }
            finally
            {
                EngineEnvironment.OwnCopies = new Dictionary<string, string>();
                Directory.Delete(own, true);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Filees_download_reached_by_another_path_is_not_reported_as_another_copy()
    {
        var root = Directory.CreateTempSubdirectory("filee-download-").FullName;
        try
        {
            var component = new EngineComponent("ffmpeg", "8.0", "https://example.test/ffmpeg.zip", new string('a', 64), 1, "zip");
            var installer = new EngineInstaller(new HttpClient(), root, new Dictionary<string, EngineComponent> { ["ffmpeg"] = component });
            var package = new EnginePackage("ffmpeg", ["ffmpeg"], 1000, ["ffmpeg"]);
            var download = Directory.CreateDirectory(Path.Combine(root, "ffmpeg")).FullName;
            File.WriteAllText(Path.Combine(download, ".filee-component"), component.Sha256);
            var bin = Directory.CreateDirectory(Path.Combine(download, "bin")).FullName;
            var programs = new[] { "ffmpeg.exe", "ffprobe.exe" }.Select(EngineEnvironment.ProgramName).ToList();
            File.WriteAllBytes(Path.Combine(bin, programs[0]), [0x4D, 0x5A, 1]);
            File.WriteAllBytes(Path.Combine(bin, programs[1]), [0x4D, 0x5A, 2]);

            // A link to the download, a link to its bin folder (also written with separators at the end, as PATH
            // entries may be), and links to its programs in a folder of their own (a user putting Filee's FFmpeg on the
            // PATH).
            var toDownload = Path.Combine(_folder, "to-download");
            var toBin = Path.Combine(_folder, "to-bin");
            var programLinks = Directory.CreateDirectory(Path.Combine(_folder, "program-links")).FullName;
            try
            {
                Directory.CreateSymbolicLink(toDownload, download);
                Directory.CreateSymbolicLink(toBin, bin);
                foreach (var program in programs)
                    File.CreateSymbolicLink(Path.Combine(programLinks, program), Path.Combine(bin, program));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Making a link needs Developer Mode or an administrator here.");
            }
            // And a link to a folder above the programs, as a user linking Filee's LibreOffice.app into /Applications.
            var app = Directory.CreateDirectory(Path.Combine(download, "app", "Contents", "MacOS")).FullName;
            foreach (var program in programs)
                File.Copy(Path.Combine(bin, program), Path.Combine(app, program));
            Directory.CreateSymbolicLink(Path.Combine(_folder, "app-link"), Path.Combine(download, "app"));
            var throughApp = Path.Combine(_folder, "app-link", "Contents", "MacOS");
            var sep = Path.DirectorySeparatorChar;
            foreach (var entry in new[] { toDownload, toBin, $"{toBin}{sep}", $"{toBin}{sep}{sep}", programLinks, throughApp })
            {
                Environment.SetEnvironmentVariable("PATH", entry + Path.PathSeparator + _path);
                EngineEnvironment.ForgetSystemCopies();
                Assert.NotNull(EngineEnvironment.SystemCopyFolder("ffmpeg"));
                // Removing "Filee's download" to use this copy would remove the copy itself.
                Assert.Equal(new EngineCopy(EngineCopyStatus.Installed, null, false, false), installer.StatusOf(package));
            }

            // A copy of its own is reported, even one unpacked from the same archive (the same files and times).
            var other = Directory.CreateDirectory(Path.Combine(_folder, "other")).FullName;
            foreach (var program in programs)
            {
                File.Copy(Path.Combine(bin, program), Path.Combine(other, program));
                File.SetLastWriteTimeUtc(Path.Combine(other, program), File.GetLastWriteTimeUtc(Path.Combine(bin, program)));
            }
            Environment.SetEnvironmentVariable("PATH", other + Path.PathSeparator + _path);
            EngineEnvironment.ForgetSystemCopies();
            Assert.Equal(new EngineCopy(EngineCopyStatus.Installed, null, false, false, other), installer.StatusOf(package));
        }
        finally
        {
            foreach (var link in new[] { "to-download", "to-bin", "app-link" }.Select(l => Path.Combine(_folder, l)))
                if (Directory.Exists(link))
                    Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // skips there
    public void A_copy_runs_when_it_answers_and_a_start_that_failed_is_tried_again()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in programs are shell scripts.");
        var program = Path.Combine(_folder, "tesseract");
        File.WriteAllText(program, "#!/bin/sh\necho 'tesseract 5.5.3'\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.True(CopyVersions.Runs("tesseract", _folder));
        Assert.Equal(new Version(5, 5), CopyVersions.Installed("tesseract", _folder));

        // Answers without a version Filee can read: it still runs.
        var convert = Path.Combine(_folder, "ebook-convert");
        File.WriteAllText(convert, "#!/bin/sh\necho 'Usage: ebook-convert input_file output_file'\n");
        File.SetUnixFileMode(convert, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.True(CopyVersions.Runs("calibre", _folder));
        Assert.Null(CopyVersions.Installed("calibre", _folder));

        // ExifTool's --version prints its manual; only -ver prints the number.
        var exiftool = Path.Combine(_folder, "exiftool");
        File.WriteAllText(exiftool, "#!/bin/sh\nif [ \"$1\" = -ver ]; then echo 13.60; else echo NAME; echo '    exiftool 12.5 - Read and write meta information'; fi\n");
        File.SetUnixFileMode(exiftool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(new Version(13, 60), CopyVersions.Installed("exiftool", _folder));

        // It stopped working (a library it needs was removed): the same file, but it fails.
        var time = File.GetLastWriteTimeUtc(program);
        File.WriteAllText(program, "#!/bin/sh\nexit 134\n");
        File.SetLastWriteTimeUtc(program, time);
        Assert.False(CopyVersions.Runs("tesseract", _folder));

        // Not startable at first (no execute permission), then fixed with the same file time: the version is read then.
        var pandoc = Path.Combine(_folder, "pandoc");
        File.WriteAllText(pandoc, "#!/bin/sh\necho 'pandoc 3.6.4'\n");
        File.SetUnixFileMode(pandoc, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(CopyVersions.Installed("pandoc", _folder));
        Assert.False(CopyVersions.Runs("pandoc", _folder));
        File.SetUnixFileMode(pandoc, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(new Version(3, 6), CopyVersions.Installed("pandoc", _folder));

        // Failing at first (a library missing), then repaired with the same file: the version is read then too.
        var gs = Path.Combine(_folder, "gs");
        File.WriteAllText(gs, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(gs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var written = File.GetLastWriteTimeUtc(gs);
        Assert.Null(CopyVersions.Installed("ghostscript", _folder));
        File.WriteAllText(gs, "#!/bin/sh\necho 10.04.0\n");
        File.SetLastWriteTimeUtc(gs, written);
        Assert.Equal(new Version(10, 4), CopyVersions.Installed("ghostscript", _folder));
    }

    [Fact]
    public void Forgetting_one_package_searches_again_for_it_only()
    {
        File.WriteAllBytes(Path.Combine(_folder, EngineEnvironment.ProgramName("ffmpeg.exe")), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(_folder, EngineEnvironment.ProgramName("ffprobe.exe")), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(_folder, EngineEnvironment.ProgramName("pandoc.exe")), [0x4D, 0x5A]);
        Environment.SetEnvironmentVariable("PATH", _folder + Path.PathSeparator + _path);
        EngineEnvironment.ForgetSystemCopies();
        Assert.Equal(_folder, EngineEnvironment.SystemCopyFolder("ffmpeg"));
        Assert.Equal(_folder, EngineEnvironment.SystemCopyFolder("pandoc"));

        // Both move; only Pandoc's download was removed, so only Pandoc is searched again.
        var moved = Directory.CreateTempSubdirectory("filee-path-moved-").FullName;
        try
        {
            foreach (var program in new[] { "ffmpeg.exe", "ffprobe.exe", "pandoc.exe" })
                File.Move(Path.Combine(_folder, EngineEnvironment.ProgramName(program)), Path.Combine(moved, EngineEnvironment.ProgramName(program)));
            Environment.SetEnvironmentVariable("PATH", moved + Path.PathSeparator + _path);
            EngineEnvironment.ForgetSystemCopy("pandoc");
            Assert.Equal(moved, EngineEnvironment.SystemCopyFolder("pandoc"));
            Assert.Equal(_folder, EngineEnvironment.SystemCopyFolder("ffmpeg"));
        }
        finally
        {
            Directory.Delete(moved, true);
        }
    }

    [Fact]
    public void Expected_versions_come_from_engines_json() =>
        Assert.NotNull(CopyVersions.Expected("ffmpeg"));
}
