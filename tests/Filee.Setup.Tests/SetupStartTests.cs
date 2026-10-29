// What the program is when it starts: Setup, the uninstaller, or a Setup.exe whose download didn't finish (#599).

using System.Buffers.Binary;
using Filee.Setup.Payload;

namespace Filee.Setup.Tests;

public class SetupStartTests
{
    [Fact]
    public void A_complete_setup_installs()
    {
        using var bench = new SetupBench();
        var start = SetupStart.Of(bench.SetupExe, uninstall: false);
        using var payload = start.Payload;
        Assert.Equal(StartMode.Install, start.Mode);
        Assert.Equal("2.0.0", payload!.Manifest.Version);
    }

    [Theory]
    [InlineData("Filee-2.0.0-win-Setup.exe")]
    [InlineData("Filee-2.0.0-win-Setup (1).exe")]
    [InlineData("setup.exe")]
    public void A_setup_cut_off_in_its_payload_is_incomplete_not_the_uninstaller(string name)
    {
        using var bench = new SetupBench(app: (temp, _) => temp.Write(@"app\data\big.bin", new string('z', 200_000)));
        var bytes = File.ReadAllBytes(bench.SetupExe);
        var cut = bench.Temp.Sub("Downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(cut)!);
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);

        var start = SetupStart.Of(cut, uninstall: false);
        Assert.Equal(StartMode.Incomplete, start.Mode);
        Assert.Null(start.Payload);
        Assert.Contains("no payload", start.Problem);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(511)]
    [InlineData(512 + 10)] // into the trailer in front of it
    public void A_signed_setup_cut_off_in_its_signature_is_incomplete(int missing)
    {
        using var temp = new TempFolder();
        var signed = SignedSetup(temp);
        var bytes = File.ReadAllBytes(signed);
        var cut = temp.Sub("Filee-2.0.0-win-Setup.exe");
        File.WriteAllBytes(cut, bytes[..^missing]);

        var start = SetupStart.Of(cut, uninstall: false);
        Assert.Equal(StartMode.Incomplete, start.Mode);
        Assert.Contains("signature is cut off", start.Problem);
    }

    [Fact]
    public void A_signed_setup_without_only_its_signature_still_installs()
    {
        // Cut exactly where the signature starts: the payload is whole, its checksum says so when installing.
        using var temp = new TempFolder();
        var bytes = File.ReadAllBytes(SignedSetup(temp));
        var cut = temp.Sub("Filee-2.0.0-win-Setup.exe");
        File.WriteAllBytes(cut, bytes[..^512]);
        var start = SetupStart.Of(cut, uninstall: false);
        using var payload = start.Payload;
        Assert.Equal(StartMode.Install, start.Mode);
        payload!.Verify(cancellation: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("uninstall.exe")]
    [InlineData("Uninstall.EXE")]
    public void Uninstall_exe_without_payload_uninstalls(string name)
    {
        using var bench = new SetupBench();
        using (var payload = SetupPayload.Open(bench.SetupExe)!)
            payload.CopyProgramTo(bench.Temp.Sub(name));
        var start = SetupStart.Of(bench.Temp.Sub(name), uninstall: false);
        Assert.Equal(StartMode.Uninstall, start.Mode);
        Assert.Null(start.Payload);
    }

    [Fact]
    public void A_development_build_without_payload_still_runs_as_the_uninstaller()
    {
        using var temp = new TempFolder();
        var exe = temp.Write(SetupStart.DevelopmentExe, "MZ development build");
        Assert.Equal(StartMode.Uninstall, SetupStart.Of(exe, uninstall: false).Mode);
    }

    [Fact]
    public void The_uninstall_switch_uninstalls_under_any_name()
    {
        using var bench = new SetupBench();
        var plain = bench.Temp.Write("anything.exe", "MZ no payload");
        Assert.Equal(StartMode.Uninstall, SetupStart.Of(plain, uninstall: true).Mode);
        var start = SetupStart.Of(bench.SetupExe, uninstall: true);
        Assert.Equal(StartMode.Uninstall, start.Mode);
        Assert.Null(start.Payload);
    }

    [Fact]
    public void A_damaged_trailer_is_damaged()
    {
        using var bench = new SetupBench();
        var bytes = File.ReadAllBytes(bench.SetupExe);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - SetupPayload.TrailerLength + 16), 7);
        File.WriteAllBytes(bench.SetupExe, bytes);
        var start = SetupStart.Of(bench.SetupExe, uninstall: false);
        Assert.Equal(StartMode.Damaged, start.Mode);
        Assert.NotNull(start.Problem);
    }

    [Fact]
    public void Every_language_says_the_download_is_incomplete()
    {
        var english = new SetupTexts("en")["installer.error.incomplete"];
        Assert.Contains("filee.sh", english);
        Assert.All(SetupTexts.Languages, l =>
        {
            Assert.True(SetupTexts.Load(l.Code).TryGetValue("installer.error.incomplete", out var text), l.Code);
            Assert.Contains("filee.sh", text);
        });
    }

    /// <summary>A Setup.exe whose header points at a 512-byte certificate table at its end, as signtool leaves it.</summary>
    private static string SignedSetup(TempFolder temp)
    {
        temp.Write(@"app\Filee.exe", "Filee");
        var program = temp.Sub("program.exe");
        File.WriteAllBytes(program, PayloadTests.MinimalPe(0, 0));
        var unsigned = temp.Sub("unsigned.exe");
        SetupPayload.Write(program, temp.Sub("app"), unsigned, "2.0.0", [], "", fast: true);
        var body = File.ReadAllBytes(unsigned);
        var signed = new byte[body.Length + 512];
        body.CopyTo(signed, 0);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(signed.AsSpan(0x3C));
        var security = pe + 4 + 20 + 112 + 4 * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(security), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(security + 4), 512);
        var path = temp.Sub("signed.exe");
        File.WriteAllBytes(path, signed);
        using (var payload = SetupPayload.Open(path))
            Assert.NotNull(payload);
        return path;
    }
}
