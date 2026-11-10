// Optional engine downloads without the network: a fake HTTP handler serves generated archives.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using SharpCompress.Compressors.ZStandard;

namespace Filee.Engines.Tests;

public sealed class EngineInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-installer-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Where the user keeps engine files they brought (#447), outside the engines folder.</summary>
    private readonly string _files = Path.Combine(Path.GetTempPath(), "filee-installer-files-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _files })
            try { EngineInstaller.DeleteFolder(folder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_separate_data_folder_keeps_its_own_engines()
    {
        Assert.Equal(Path.Combine(_root, "engines"), EngineEnvironment.DefaultDownloadRoot(_root));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Filee", "engines"),
            EngineEnvironment.DefaultDownloadRoot(null));
    }

    /// <summary>Serves byte arrays by URL; a string value is a redirect target.</summary>
    private sealed class FakeServer(Dictionary<string, object> content) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = content.GetValueOrDefault(url) switch
            {
                byte[] bytes => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
                string location => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] TarGzip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }
        return buffer.ToArray();
    }

    /// <summary>A conda package: a zip with metadata and the files as a zstd-compressed tarball.</summary>
    private static byte[] Conda(params (string Path, string Text)[] files)
    {
        using var tarball = new MemoryStream();
        using (var zstd = new CompressionStream(tarball, level: 3, leaveOpen: true))
        using (var tar = new TarWriter(zstd, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("metadata.json").Open()))
                writer.Write("{\"conda_pkg_format_version\": 2}");
            using (var entry = zip.CreateEntry("pkg-tool-1.0-0.tar.zst").Open())
                entry.Write(tarball.ToArray());
        }
        return buffer.ToArray();
    }

    private static EngineComponent Component(string id, string kind, byte[] bytes, string? sha256 = null) =>
        new(id, "1.0", $"https://example.test/{id}.{kind}", sha256 ?? Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, kind);

    private EngineInstaller Installer(FakeServer server, params EngineComponent[] components) =>
        new(new HttpClient(server), _root, components.ToDictionary(c => c.Id));

    [Fact]
    public async Task Zip_engines_are_verified_unpacked_without_their_top_folder_and_reported()
    {
        var archive = Zip(("tool-1.0/tool.exe", "binary"), ("tool-1.0/readme.txt", "hello"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new() { [component.Url] = archive });
        var installer = Installer(server, component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        var reports = new List<EngineInstallProgress>();

        Assert.False(installer.IsInstalled(package));
        await installer.InstallAsync(package, new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
        Assert.True(installer.IsInstalled(package));
        Assert.True(installer.IsUpToDate(package));
        Assert.Contains(reports, r => r.Stage == EngineInstallStage.Unpacking);
        Assert.Equal(1.0, reports.Where(r => r.Stage == EngineInstallStage.Downloading).Max(r => r.Fraction), 3);
        // Byte counts drive the speed and time-left display.
        var last = reports.Last(r => r.Stage == EngineInstallStage.Downloading);
        Assert.Equal((archive.Length, archive.Length), ((int)last.BytesDone, (int)last.BytesTotal));
        // Nothing is left over from downloading or unpacking.
        Assert.Equal(["tool"], Directory.GetDirectories(_root).Select(Path.GetFileName).Where(n => !n!.StartsWith(".downloads", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));

        // An installed, up-to-date package is not downloaded again.
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.Single(server.Requests);

        installer.Uninstall(package);
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
    }

    [Fact]
    public async Task A_package_another_Filee_is_installing_is_neither_downloaded_nor_removed()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new() { [component.Url] = archive });
        var installer = Installer(server, component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        var update = component with { Version = "2.0", Sha256 = new string('1', 64) };
        var updating = Installer(server, update);

        var locks = Path.Combine(_root, ".downloads", "locks");
        using (new FileStream(Path.Combine(locks, "tool.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = await Assert.ThrowsAsync<EngineBusyException>(() => updating.InstallAsync(package, null, TestContext.Current.CancellationToken));
            Assert.Equal("tool", busy.PackageId);
            Assert.Contains("another Filee", busy.Message);
            Assert.Throws<EngineBusyException>(() => installer.Uninstall(package));
        }

        Assert.Single(server.Requests);
        Assert.True(installer.IsUpToDate(package));
        installer.Uninstall(package);
        Assert.False(installer.IsInstalled(package));
    }

    [Fact]
    public async Task The_copy_in_use_is_Filees_download_until_it_is_removed()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var installer = Installer(new FakeServer(new() { [component.Url] = archive }), component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        Assert.Equal(new EngineCopy(EngineCopyStatus.NotInstalled, null, false, false), installer.StatusOf(package));

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.Equal(new EngineCopy(EngineCopyStatus.Installed, null, false, false), installer.StatusOf(package));

        var newer = Installer(new FakeServer(new()), component with { Version = "2.0", Sha256 = new string('1', 64) });
        Assert.Equal(new EngineCopy(EngineCopyStatus.Installed, null, false, true), newer.StatusOf(package));

        installer.Uninstall(package);
        Assert.Equal(EngineCopyStatus.NotInstalled, installer.StatusOf(package).Status);
    }

    [Fact]
    public void Transfer_rate_averages_over_the_last_seconds_and_estimates_the_time_left()
    {
        var rate = new TransferRate(TimeSpan.FromSeconds(5));
        Assert.Null(rate.Remaining(0, 1000));

        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromSeconds(0.2), 100); // too short to tell
        Assert.Equal(0, rate.BytesPerSecond);

        for (var second = 1; second <= 10; second++)
            rate.Add(TimeSpan.FromSeconds(second), second * 1_000_000L);
        Assert.Equal(1_000_000, rate.BytesPerSecond, 0);
        Assert.Equal(TimeSpan.FromSeconds(30), rate.Remaining(10_000_000, 40_000_000));

        // The speed follows a slower connection within the window instead of keeping the old average.
        for (var second = 11; second <= 20; second++)
            rate.Add(TimeSpan.FromSeconds(second), 10_000_000L + (second - 10) * 100_000L);
        Assert.Equal(100_000, rate.BytesPerSecond, 0);
    }

    /// <summary>Serves <paramref name="bytes"/> but breaks off after half of them, as a dropped connection does.</summary>
    private sealed class BrokenServer(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(bytes)) });

        private sealed class BreakingStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override int Read(byte[] buffer, int offset, int count) =>
                Position >= Length / 2 ? throw new IOException("The connection was reset.") : base.Read(buffer, offset, Math.Min(count, 64));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                Position >= Length / 2 ? throw new IOException("The connection was reset.") : base.ReadAsync(buffer[..Math.Min(buffer.Length, 64)], cancellationToken);
        }
    }

    [Fact]
    public async Task A_broken_off_download_leaves_no_partial_file()
    {
        // #963: the .part file of an interrupted download stayed (up to ~420 MB for LibreOffice).
        var archive = Zip(("tool.exe", new string('x', 4096)));
        var component = Component("tool", "zip", archive);
        var installer = new EngineInstaller(new HttpClient(new BrokenServer(archive)), _root, new Dictionary<string, EngineComponent> { ["tool"] = component });
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);

        await Assert.ThrowsAnyAsync<IOException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.False(installer.IsInstalled(package));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Fact]
    public async Task Removing_a_package_with_a_file_in_use_leaves_it_whole_and_installed()
    {
        // #963: the delete went in name order, so the marker went first and a half-deleted engine looked not installed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to move a folder with an open file.");
        var first = Zip(("a.exe", "first"));
        var second = Zip(("b.exe", "second"));
        var a = Component("a", "zip", first);
        var b = Component("b", "zip", second);
        var installer = Installer(new FakeServer(new() { [a.Url] = first, [b.Url] = second }), a, b);
        var package = new EnginePackage("pair", ["a", "b"], 1000, ["pair"]);
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        using (new FileStream(Path.Combine(_root, "b", "b.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => installer.Uninstall(package));

        Assert.True(installer.IsInstalled(package));
        Assert.True(File.Exists(Path.Combine(_root, "a", "a.exe")));
        Assert.Empty(Directory.GetDirectories(_root, "~*"));

        installer.Uninstall(package);
        Assert.False(installer.IsInstalled(package));
        Assert.Equal([".downloads"], Directory.GetDirectories(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task What_a_cut_short_removal_left_aside_goes_with_the_next_install()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var installer = Installer(new FakeServer(new() { [component.Url] = archive }), component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        var left = Directory.CreateDirectory(Path.Combine(_root, "~removed-tool-0badf00d")).FullName;
        File.WriteAllText(Path.Combine(left, "soffice.exe"), "leftover");
        var other = Directory.CreateDirectory(Path.Combine(_root, "~removed-other-0badf00d")).FullName;

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(left));
        Assert.True(Directory.Exists(other)); // another package's leftovers wait for that package's lock
        Assert.True(installer.IsInstalled(package));
    }

    /// <summary>Installs version 1.0 of a package and leaves its files read-only, as the calibre MSI does.</summary>
    private async Task<(EnginePackage Package, string Folder)> InstallReadOnly(string id)
    {
        var archive = Zip(($"{id}-1.0/old.exe", "old"), ($"{id}-1.0/app/bin/amatch.dll", "old"));
        var component = Component(id, "zip", archive);
        var package = new EnginePackage(id, [id], 1, [id]);
        await Installer(new FakeServer(new() { [component.Url] = archive }), component).InstallAsync(package, null, TestContext.Current.CancellationToken);
        var folder = Path.Combine(_root, id);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != ".filee-component"))
            File.SetAttributes(file, FileAttributes.ReadOnly);
        return (package, folder);
    }

    [Fact]
    public async Task An_update_replaces_a_copy_with_read_only_files()
    {
        // #1084, #1092, #1109: every file of the calibre MSI is read-only, so its first update failed on 'amatch.dll' for
        // good: the delete took the marker first, the card went back to Install and each retry stopped at the same file.
        var (package, folder) = await InstallReadOnly("tool");
        var update = Zip(("tool-2.0/new.exe", "new"));
        var component = Component("tool", "zip", update) with { Version = "2.0" };
        var installer = Installer(new FakeServer(new() { [component.Url] = update }), component);

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        Assert.True(installer.IsUpToDate(package));
        Assert.Equal("new", File.ReadAllText(Path.Combine(folder, "new.exe")));
        Assert.False(File.Exists(Path.Combine(folder, "old.exe")));
        Assert.Empty(Directory.GetDirectories(_root, "~*"));
    }

    [Fact]
    public async Task A_LibreOffice_extension_update_replaces_read_only_files()
    {
        var extensions = Path.Combine(_root, "libreoffice", "share", "extensions");
        var old = Directory.CreateDirectory(Path.Combine(extensions, "H2Orestart")).FullName;
        var jar = Path.Combine(old, "old.jar");
        File.WriteAllText(jar, "old");
        File.SetAttributes(jar, FileAttributes.ReadOnly);
        var oxt = Zip(("new.jar", "new"));
        var extension = Component("h2orestart", "oxt", oxt);
        var installer = Installer(new FakeServer(new() { [extension.Url] = oxt }), extension);

        await installer.InstallAsync(new EnginePackage("filter", ["h2orestart"], 1, []), null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(old, "new.jar")));
        Assert.False(File.Exists(jar));
        Assert.Empty(Directory.GetDirectories(_root, "~*"));
    }

    [Fact]
    public async Task Removing_a_package_with_read_only_files_leaves_nothing_aside()
    {
        var (package, _) = await InstallReadOnly("tool");

        Installer(new FakeServer([])).Uninstall(package);

        Assert.Equal([".downloads"], Directory.GetDirectories(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task An_update_while_the_engine_runs_leaves_the_working_copy_and_its_marker()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to move a folder with an open file.");
        var (package, folder) = await InstallReadOnly("tool");
        var marker = File.ReadAllText(Path.Combine(folder, ".filee-component"));
        var update = Zip(("tool-2.0/new.exe", "new"));
        var component = Component("tool", "zip", update) with { Version = "2.0" };
        var installer = Installer(new FakeServer(new() { [component.Url] = update }), component);

        using (new FileStream(Path.Combine(folder, "old.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Equal(marker, File.ReadAllText(Path.Combine(folder, ".filee-component")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(folder, "old.exe")));
        Assert.Empty(Directory.GetDirectories(_root, "~*"));

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.True(installer.IsUpToDate(package));
    }

    [Fact]
    public async Task The_error_of_a_failed_unpack_is_not_replaced_by_its_cleanup_and_read_only_files_go()
    {
        // #1092: the delete in 'finally' threw on a read-only file and hid what had gone wrong in the try block.
        var installer = Installer(new FakeServer([]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InStagingAsync(staging =>
        {
            var bin = Directory.CreateDirectory(Path.Combine(staging, "app", "bin")).FullName;
            File.WriteAllText(Path.Combine(bin, "amatch.dll"), "unpacked");
            File.SetAttributes(Path.Combine(bin, "amatch.dll"), FileAttributes.ReadOnly);
            throw new InvalidDataException("the real error");
        }));

        Assert.Equal("the real error", error.Message);
        Assert.Empty(Directory.GetDirectories(_root, "~*"));
    }

    [Fact]
    public async Task The_error_of_a_failed_unpack_is_not_replaced_by_a_file_that_stays_locked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete an open file.");
        var installer = Installer(new FakeServer([]));
        FileStream? open = null;
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InStagingAsync(staging =>
            {
                Directory.CreateDirectory(staging);
                open = new FileStream(Path.Combine(staging, "held.dll"), FileMode.Create, FileAccess.Write, FileShare.None);
                throw new InvalidDataException("the real error");
            }));

            Assert.Equal("the real error", error.Message);
        }
        finally
        {
            open?.Dispose();
        }
    }

    [Fact]
    public void A_file_held_briefly_doesnt_stop_a_folder_being_deleted()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        var file = Path.Combine(folder, "scanned.dll");
        File.WriteAllText(file, "bytes");
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        // On its own thread: under load the pool can get to a release only as the retries below give up.
        var release = new Thread(() =>
        {
            Thread.Sleep(100);
            held.Dispose();
        })
        { IsBackground = true };
        release.Start();

        EngineInstaller.DeleteFolder(folder);
        release.Join();

        Assert.False(Directory.Exists(folder));
    }

    /// <summary>A junction on Windows, a symbolic link elsewhere.</summary>
    private static void LinkFolder(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("This machine doesn't let the test make a symbolic link.");
            }
            return;
        }
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.SkipUnless(mklink.ExitCode == 0, "This drive can't hold a junction.");
    }

    /// <summary>A folder elsewhere with a read-only file, which must stay as it is.</summary>
    private string ForeignFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "mine");
        File.SetAttributes(Path.Combine(folder, "keep.txt"), FileAttributes.ReadOnly);
        return folder;
    }

    private static void AssertUntouched(string foreign)
    {
        var kept = Path.Combine(foreign, "keep.txt");
        Assert.Equal("mine", File.ReadAllText(kept));
        Assert.True(File.GetAttributes(kept).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Deleting_a_folder_with_a_link_in_it_leaves_what_the_link_leads_to()
    {
        var foreign = ForeignFolder();
        var folder = Directory.CreateDirectory(Path.Combine(_root, "engine")).FullName;
        File.WriteAllText(Path.Combine(folder, "tool.exe"), "binary");
        File.SetAttributes(Path.Combine(folder, "tool.exe"), FileAttributes.ReadOnly);
        LinkFolder(Path.Combine(folder, "link"), foreign);

        EngineInstaller.DeleteFolder(folder);

        Assert.False(Directory.Exists(folder));
        AssertUntouched(foreign);
    }

    [Fact]
    public void Deleting_a_folder_that_is_a_link_removes_only_the_link()
    {
        var foreign = ForeignFolder();
        Directory.CreateDirectory(_root);
        var link = Path.Combine(_root, "engines");
        LinkFolder(link, foreign);

        EngineInstaller.DeleteFolder(link);

        Assert.False(Directory.Exists(link));
        AssertUntouched(foreign);
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_rejected_and_nothing_is_installed()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive, sha256: new string('0', 64));
        var installer = Installer(new FakeServer(new() { [component.Url] = archive }), component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message);
        Assert.False(installer.IsInstalled(package));
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    public async Task An_incomplete_engine_update_preserves_the_working_install_and_its_marker(string kind)
    {
        var programs = EngineEnvironment.OwnCopyPrograms["ffmpeg"];
        byte[] Archive(params (string Path, string Text)[] files) => kind == "zip" ? Zip(files) : TarGzip(files);
        var previousArchive = Archive(($"ffmpeg-1.0/bin/{programs[0]}", "working encoder"),
            ($"ffmpeg-1.0/bin/{programs[1]}", "working probe"));
        var previousComponent = Component("ffmpeg", kind, previousArchive);
        var package = new EnginePackage("ffmpeg", ["ffmpeg"], 1, ["ffmpeg"]);
        var previousInstaller = Installer(new FakeServer(new() { [previousComponent.Url] = previousArchive }), previousComponent);
        await previousInstaller.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var incompleteArchive = Archive(($"ffmpeg-2.0/bin/{programs[0]}", "new encoder without probe"));
        var incompleteComponent = Component("ffmpeg", kind, incompleteArchive) with { Version = "2.0" };
        var installer = Installer(new FakeServer(new() { [incompleteComponent.Url] = incompleteArchive }), incompleteComponent);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Contains("ffmpeg", error.Message);
        var folder = Path.Combine(_root, "ffmpeg");
        Assert.Equal("working encoder", File.ReadAllText(Path.Combine(folder, "bin", programs[0])));
        Assert.Equal("working probe", File.ReadAllText(Path.Combine(folder, "bin", programs[1])));
        Assert.Equal(previousComponent.Sha256, File.ReadAllText(Path.Combine(folder, ".filee-component")));
        Assert.True(previousInstaller.IsUpToDate(package));
        Assert.True(installer.IsInstalled(package));
        Assert.False(installer.IsUpToDate(package));
        Assert.Empty(Directory.GetDirectories(_root, "~*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Theory]
    [InlineData("libreoffice", "LibreOffice.app", "soffice")]
    [InlineData("calibre", "calibre.app", "ebook-convert")]
    public void A_Mac_application_bundle_requires_its_conversion_program(string id, string app, string program)
    {
        var folder = Path.Combine(_root, app, "Contents", "MacOS");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README"), "not an executable");

        Assert.Throws<InvalidDataException>(() => EngineInstaller.RequiredProgramFolder(id, _root, windows: false));

        File.WriteAllText(Path.Combine(folder, program), "binary");
        Assert.Equal(folder, EngineInstaller.RequiredProgramFolder(id, _root, windows: false));
    }

    [Fact]
    public async Task Redirects_to_plain_http_mirrors_are_followed_because_the_hash_is_checked()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new()
        {
            [component.Url] = "https://example.test/mirror-list",
            ["https://example.test/mirror-list"] = "http://mirror.example.test/tool.zip",
            ["http://mirror.example.test/tool.zip"] = archive,
        });
        var installer = Installer(server, component);

        await installer.InstallAsync(new EnginePackage("tool", ["tool"], 1, ["tool"]), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, server.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
    }

    [Fact]
    public async Task Conda_packages_unpack_their_windows_binaries_and_get_the_runtime_next_to_them()
    {
        var gs = Conda(("Library/bin/gswin64c.exe", "exe"), ("Library/bin/gsdll64.dll", "dll"), ("Library/share/doc.txt", "doc"), ("info/index.json", "{}"));
        var runtime = Conda(("Library/bin/vcruntime140.dll", "runtime"), ("Library/bin/msvcp140.dll", "c++"));
        var ghostscript = Component("ghostscript", "conda", gs);
        var vcruntime = Component("vcruntime", "conda", runtime);
        var installer = Installer(new FakeServer(new() { [ghostscript.Url] = gs, [vcruntime.Url] = runtime }), ghostscript, vcruntime);
        var package = new EnginePackage("ghostscript", ["ghostscript", "vcruntime"], 1, ["ghostscript"]);

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var folder = Path.Combine(_root, "ghostscript");
        Assert.Equal(["gsdll64.dll", "gswin64c.exe", "msvcp140.dll", "vcruntime140.dll"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => !n!.StartsWith('.')).Order(StringComparer.Ordinal));
        Assert.Equal("runtime", File.ReadAllText(Path.Combine(folder, "vcruntime140.dll")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public void Conda_packages_cannot_write_outside_the_engine_folder()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "evil.conda");
        File.WriteAllBytes(file, Conda(("Library/bin/../../../evil.txt", "evil")));

        Assert.Throws<InvalidDataException>(() => EngineInstaller.UnpackConda(file, Path.Combine(_root, "target"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task LibreOffice_extensions_go_into_the_installed_LibreOffice_and_need_it()
    {
        var oxt = Zip(("description.xml", "<description/>"), ("H2Orestart.jar", "jar"));
        var extension = Component("h2orestart", "oxt", oxt);
        var server = new FakeServer(new() { [extension.Url] = oxt });
        var installer = Installer(server, extension);
        var package = new EnginePackage("filter", ["h2orestart"], 1, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Directory.CreateDirectory(Path.Combine(_root, "libreoffice", "share", "extensions"));
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, "libreoffice", "share", "extensions", "H2Orestart", "H2Orestart.jar")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public void LibreOffice_is_trimmed_to_what_headless_conversion_needs()
    {
        var root = Path.Combine(_root, "lo");
        foreach (var folder in new[] { "help", "readmes", "program", "share/gallery", "share/extensions/dict-en", "share/extensions/dict-ko", "share/extensions/dict-fr", "share/extensions/H2Orestart" })
            Directory.CreateDirectory(Path.Combine(root, folder));
        File.WriteAllText(Path.Combine(root, "LibreOffice.msi"), "");

        EngineInstaller.TrimLibreOffice(root);

        var remaining = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["program", "share", "share/extensions", "share/extensions/H2Orestart", "share/extensions/dict-en", "share/extensions/dict-ko"], remaining);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void Every_package_component_is_pinned_in_the_manifest()
    {
        foreach (var package in EngineDownloads.Packages.Where(EngineDownloads.CanDownload))
        {
            foreach (var id in package.Components)
            {
                var component = EngineDownloads.Components[id];
                Assert.StartsWith("https://", component.Url);
                Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
                Assert.True(component.Size > 0);
            }
            Assert.True(EngineDownloads.DownloadSize(package) > 0);
        }
        // rhwp is bundled with the installer, but its download is pinned in the same list.
        Assert.Contains("rhwp", EngineDownloads.Components.Keys);
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    [Fact]
    public async Task A_tesseract_language_is_its_file_in_a_folder_of_its_own()
    {
        var data = Encoding.UTF8.GetBytes("lstm");
        var german = Component("deu", "traineddata", data);
        var installer = Installer(new FakeServer(new() { [german.Url] = data }), german);
        var package = new EnginePackage("ocr-deu", ["deu"], data.Length, ["tesseract"]);

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        Assert.Equal("lstm", File.ReadAllText(Path.Combine(_root, "deu", "deu.traineddata")));
        Assert.True(installer.IsUpToDate(package));
    }

    [Fact]
    public void Tesseract_keeps_only_what_ocr_needs()
    {
        var root = Path.Combine(_root, "tesseract");
        foreach (var file in new[] { "tesseract.exe", "libtesseract-5.dll", "text2image.exe", "tesseract.1.html", "tesseract-uninstall.exe",
                     "doc/LICENSE", "tessdata/eng.traineddata", "$PLUGINSDIR/System.dll" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
            File.WriteAllText(Path.Combine(root, file), "x");
        }

        EngineInstaller.TrimTesseract(root);

        Assert.Equal(["doc/LICENSE", "libtesseract-5.dll", "tesseract.exe"],
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Model_components_are_copied_and_marked_installed()
    {
        var modelBytes = "ggml-model-bytes"u8.ToArray();
        var component = Component("whisper-model", "model", modelBytes);
        var server = new FakeServer(new() { [component.Url] = modelBytes });
        var installer = Installer(server, component);
        var package = new EnginePackage("whisper", ["whisper-model"], modelBytes.Length, ["whisper"]);

        Assert.False(installer.IsInstalled(package));
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "whisper-model", "whisper-model.model")));
        Assert.True(File.Exists(Path.Combine(_root, "whisper-model", ".filee-component")));
        Assert.True(installer.IsInstalled(package));
        Assert.True(installer.IsUpToDate(package));
    }

    [Fact]
    public async Task Ncnn_components_write_custom_marker_and_are_reported_installed()
    {
        var paramBytes = "ncnn-param-bytes"u8.ToArray();
        var component = Component("model-param", "ncnn-param", paramBytes);
        var server = new FakeServer(new() { [component.Url] = paramBytes });
        var installer = Installer(server, component);
        var package = new EnginePackage("ncnn-test", ["model-param"], paramBytes.Length, ["upscaler"]);

        Assert.False(installer.IsInstalled(package));
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "model", ".model-param")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public async Task Uninstalling_a_package_leaves_components_shared_with_another_installed_package_intact()
    {
        var archiveA = Zip(("toolA.exe", "binaryA"));
        var compA = Component("toolA", "zip", archiveA);
        var archiveB = Zip(("toolB.exe", "binaryB"));
        var compB = Component("toolB", "zip", archiveB);
        var archiveShared = Zip(("shared.dll", "sharedDll"));
        var compShared = Component("shared", "zip", archiveShared);

        var server = new FakeServer(new()
        {
            [compA.Url] = archiveA,
            [compB.Url] = archiveB,
            [compShared.Url] = archiveShared,
        });

        var packageA = new EnginePackage("pkgA", ["toolA", "shared"], 1000, ["toolA"]);
        var packageB = new EnginePackage("pkgB", ["toolB", "shared"], 1000, ["toolB"]);

        var installer = new EngineInstaller(new HttpClient(server), _root,
            new Dictionary<string, EngineComponent>
            {
                ["toolA"] = compA,
                ["toolB"] = compB,
                ["shared"] = compShared,
            },
            packages: [packageA, packageB]);

        await installer.InstallAsync(packageA, null, TestContext.Current.CancellationToken);
        await installer.InstallAsync(packageB, null, TestContext.Current.CancellationToken);

        Assert.True(installer.IsInstalled(packageA));
        Assert.True(installer.IsInstalled(packageB));
        Assert.True(Directory.Exists(Path.Combine(_root, "shared")));

        // Uninstall pkgA: shared component is still used by pkgB, so shared must stay intact.
        installer.Uninstall(packageA);

        Assert.False(installer.IsInstalled(packageA));
        Assert.True(installer.IsInstalled(packageB));
        Assert.False(Directory.Exists(Path.Combine(_root, "toolA")));
        Assert.True(Directory.Exists(Path.Combine(_root, "toolB")));
        Assert.True(Directory.Exists(Path.Combine(_root, "shared")));

        // Uninstall pkgB: now shared is no longer used by any installed package, so it is removed.
        installer.Uninstall(packageB);

        Assert.False(installer.IsInstalled(packageB));
        Assert.False(Directory.Exists(Path.Combine(_root, "toolB")));
        Assert.False(Directory.Exists(Path.Combine(_root, "shared")));
    }

    [Fact]
    public void An_empty_package_is_neither_installed_nor_up_to_date()
    {
        var empty = new EnginePackage("empty", [], 0, []);
        var installer = Installer(new FakeServer([]));

        Assert.False(installer.IsInstalled(empty));
        Assert.False(installer.IsUpToDate(empty));
    }

    [Fact]
    public async Task CopyRuntime_copies_vcruntime_and_vcomp14_dlls_next_to_zip_component()
    {
        var vcConda = Conda(("Library/bin/vcruntime140.dll", "vc"));
        var vcComp = Conda(("Library/bin/vcomp140.dll", "vcomp"));
        // The installer checks for the program under its name on this OS.
        var program = OperatingSystem.IsWindows() ? "upscayl-bin.exe" : "upscayl-bin";
        var zip = Zip(($"tool/{program}", "bin"));

        var compVc = Component("vcruntime", "conda", vcConda);
        var compVcomp = Component("vcomp14", "conda", vcComp);
        var compTool = Component("upscayl-bin", "zip", zip);

        var package = new EnginePackage("upscayl-bin", ["upscayl-bin", "vcruntime", "vcomp14"], 1, ["upscaler"]);
        var server = new FakeServer(new()
        {
            [compVc.Url] = vcConda,
            [compVcomp.Url] = vcComp,
            [compTool.Url] = zip,
        });

        var installer = Installer(server, compVc, compVcomp, compTool);
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var toolDir = Path.Combine(_root, "upscayl-bin");
        Assert.True(File.Exists(Path.Combine(toolDir, program)));
        Assert.True(File.Exists(Path.Combine(toolDir, "vcruntime140.dll")));
        Assert.True(File.Exists(Path.Combine(toolDir, "vcomp140.dll")));
        Assert.Equal("vc", File.ReadAllText(Path.Combine(toolDir, "vcruntime140.dll")));
        Assert.Equal("vcomp", File.ReadAllText(Path.Combine(toolDir, "vcomp140.dll")));
    }

    // ───────────────────────── Without internet (#447) ─────────────────────────

    /// <summary>The downloads in a folder outside the engines folder, as on a USB stick or in Downloads.</summary>
    private string Files(params (EngineComponent Component, byte[] Bytes)[] files)
    {
        Directory.CreateDirectory(_files);
        foreach (var (component, bytes) in files)
            File.WriteAllBytes(Path.Combine(_files, EngineDownloads.FileName(component)), bytes);
        return _files;
    }

    [Fact]
    public async Task Files_brought_from_another_computer_are_checked_and_installed_without_the_network()
    {
        var archive = Zip(("tool-1.0/tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer([]);
        var installer = Installer(server, component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        // A file of another size in the folder is passed over.
        File.WriteAllText(Path.Combine(Files((component, archive)), "notes.txt"), "not an engine");
        var reports = new List<EngineInstallProgress>();

        await installer.InstallFromFilesAsync(package, [_files], new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
        Assert.True(installer.IsUpToDate(package));
        Assert.Empty(server.Requests);
        Assert.Contains(reports, r => r.Stage == EngineInstallStage.Copying);
        Assert.DoesNotContain(reports, r => r.Stage == EngineInstallStage.Downloading);
        // The user's file stays where it was; the copy that was unpacked is gone.
        Assert.Equal(archive, File.ReadAllBytes(Path.Combine(_files, EngineDownloads.FileName(component))));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Fact]
    public async Task A_file_of_the_right_size_under_another_name_is_found_too()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var installer = Installer(new FakeServer([]), component);
        Directory.CreateDirectory(_files);
        var renamed = Path.Combine(_files, "tool (1).zip");
        File.WriteAllBytes(renamed, archive);

        await installer.InstallFromFilesAsync(new EnginePackage("tool", ["tool"], 1, ["tool"]), [renamed], null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
    }

    [Fact]
    public async Task A_wrong_file_installs_nothing_of_the_package()
    {
        var tool = Zip(("tool.exe", "binary"));
        var expected = Zip(("other.exe", "binary"));
        var toolComponent = Component("tool", "zip", tool);
        var otherComponent = Component("other", "zip", expected);
        // The download's size with other bytes: a damaged copy or another build.
        var damaged = expected.ToArray();
        damaged[^10] ^= 0xFF;
        var installer = Installer(new FakeServer([]), toolComponent, otherComponent);
        var package = new EnginePackage("pair", ["tool", "other"], 1, []);

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() => installer.InstallFromFilesAsync(package,
            [Files((toolComponent, tool), (otherComponent, damaged))], null, TestContext.Current.CancellationToken));

        Assert.Equal("error.engine_file_mismatch", error.ErrorKey);
        Assert.Equal(EngineDownloads.FileName(otherComponent), error.Detail);
        // The first file was fine, but nothing is unpacked before every file has been checked.
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
        Assert.False(installer.IsInstalled(package));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Fact]
    public async Task Missing_files_are_named_and_nothing_is_installed()
    {
        var tool = Zip(("tool.exe", "binary"));
        var filter = Zip(("filter.xml", "<filter/>"));
        var toolComponent = Component("tool", "zip", tool);
        var filterComponent = Component("filter", "zip", filter);
        var installer = Installer(new FakeServer([]), toolComponent, filterComponent);
        var package = new EnginePackage("tool", ["tool", "filter"], 1, ["tool"]);

        var error = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            installer.InstallFromFilesAsync(package, [Files((toolComponent, tool))], null, TestContext.Current.CancellationToken));

        Assert.Equal("error.engine_files_missing", error.ErrorKey);
        Assert.Equal("filter.zip", error.Detail);
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
    }

    [Fact]
    public async Task Saved_files_install_on_another_computer_and_are_not_downloaded_twice()
    {
        var tool = Zip(("tool-1.0/tool.exe", "binary"));
        var filter = Zip(("filter.xml", "<filter/>"));
        var toolComponent = Component("tool", "zip", tool);
        var filterComponent = Component("filter", "zip", filter);
        var server = new FakeServer(new() { [toolComponent.Url] = tool, [filterComponent.Url] = filter });
        var installer = Installer(server, toolComponent, filterComponent);
        var package = new EnginePackage("tool", ["tool", "filter"], 1, ["tool"]);
        var folder = Path.Combine(_files, "Filee engines");
        var reports = new List<EngineInstallProgress>();

        var saved = await installer.SaveFilesAsync(package, folder, new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine(folder, "tool.zip"), Path.Combine(folder, "filter.zip")], saved);
        Assert.Equal(["filter.zip", "tool.zip"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal(1.0, reports.Max(r => r.Fraction), 3);
        Assert.False(installer.IsInstalled(package)); // saving installs nothing here
        Assert.Equal(2, server.Requests.Count);

        // Saving again keeps the files that match.
        await installer.SaveFilesAsync(package, folder, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Requests.Count);

        // The other computer: an engines folder of its own and no network.
        var offline = new EngineInstaller(new HttpClient(new FakeServer([])), Path.Combine(_files, "other-pc"),
            new[] { toolComponent, filterComponent }.ToDictionary(c => c.Id));
        await offline.InstallFromFilesAsync(package, [folder], null, TestContext.Current.CancellationToken);
        Assert.True(offline.IsUpToDate(package));
    }

    [Fact]
    public async Task A_download_that_doesnt_match_is_not_saved()
    {
        var tool = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", tool, sha256: new string('0', 64));
        var installer = Installer(new FakeServer(new() { [component.Url] = tool }), component);
        var folder = Path.Combine(_files, "out");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.SaveFilesAsync(new EnginePackage("tool", ["tool"], 1, ["tool"]), folder, null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Theory]
    [InlineData("https://example.test/jdk-21%2B9/OpenJDK21U-jre_21.0.13_9.zip", "OpenJDK21U-jre_21.0.13_9.zip")]
    [InlineData("https://example.test/a/b%2F..%2F..%2Fevil.zip", "evil.zip")]
    [InlineData("https://example.test/a/b%5C..%5Cevil.zip", "evil.zip")]
    [InlineData("https://example.test/", "tool-1.0")]
    public void The_saved_name_is_the_downloads_and_stays_in_the_folder(string url, string name)
    {
        Assert.Equal(name, EngineDownloads.FileName(new EngineComponent("tool", "1.0", url, "", 1, "zip")));
    }

    private sealed class SyncProgress(Action<EngineInstallProgress> report) : IProgress<EngineInstallProgress>
    {
        public void Report(EngineInstallProgress value) => report(value);
    }

    [Fact]
    public void ExifTools_Windows_build_is_renamed_so_it_doesnt_wait_for_a_key_and_its_folder_is_found()
    {
        var root = Directory.CreateTempSubdirectory("filee-exiftool-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "exiftool(-k).exe"), "");
            Directory.CreateDirectory(Path.Combine(root, "exiftool_files"));

            EngineInstaller.RenameExifTool("exiftool", root);

            Assert.True(File.Exists(Path.Combine(root, "exiftool.exe")));
            Assert.False(File.Exists(Path.Combine(root, "exiftool(-k).exe")));
            Assert.Equal(root, EngineEnvironment.FolderWithPrograms("exiftool", root, windows: true));
            // Another package's files are left as they are.
            File.WriteAllText(Path.Combine(root, "exiftool(-k).exe"), "");
            EngineInstaller.RenameExifTool("pandoc", root);
            Assert.True(File.Exists(Path.Combine(root, "exiftool(-k).exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExifTool_is_offered_on_every_platform_and_runs_the_Metadata_tools_converter()
    {
        var package = Assert.Single(EngineDownloads.Packages, p => p.Id == "exiftool");
        Assert.Equal(["exiftool"], package.ConverterIds);
        foreach (var runtime in new[] { "win-x64", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64" })
        {
            var component = EngineDownloads.ComponentsForRuntime(runtime)["exiftool"];
            Assert.Equal("13.60", component.Version);
            Assert.Equal(runtime == "win-x64" ? "zip" : "tar.gz", component.Kind);
        }
    }
}
