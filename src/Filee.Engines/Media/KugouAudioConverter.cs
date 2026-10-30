// KuGou Music encrypted audio (.kgm / .kgma / .vpr) → standard audio.
// Decrypts with the well-known KGM V2 XOR scheme (pure C#, no external deps),
// then hands the raw stream to FFmpeg for the requested target format.
// Algorithm constants match unlock-music / community KGM V2 implementations.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Media;

/// <summary>
/// Decrypts KuGou .kgm / .kgma / .vpr containers and converts the inner audio
/// (usually FLAC or MP3) to any of Filee's normal audio targets via FFmpeg.
/// </summary>
public sealed class KugouAudioConverter(ILogger? logger = null) : IConverter
{
    private static readonly string[] Sources = ["kgm", "kgma", "vpr"];

    // Common audio targets that MediaEncoding can write.
    private static readonly string[] Targets =
    [
        "mp3", "m4a", "flac", "wav", "ogg", "opus", "aac", "wma", "aiff", "mka"
    ];

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Id => "kugou";
    public string DisplayName => "KuGou Audio";
    public int MaxParallelism => 0; // CPU-bound decrypt; one per core is fine

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in Sources
           from target in Targets
           select new ConversionEdge(source, target, Cost: 12) // slightly higher: decrypt + possible re-encode
    ];

    public EngineStatus GetStatus()
    {
        // Always available: pure managed code. FFmpeg is only needed when the
        // target differs from the inner format; the UI already greys out media
        // targets when FFmpeg is missing.
        return EngineStatus.Available("KGM V2 (built-in)", EngineVersions.BuiltIn);
    }

    public Task<IReadOnlyList<string>> ConvertAsync(
        ConversionStep step,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isVpr = step.From is "vpr";
            var decryptedPath = Path.Combine(step.WorkDirectory, "kugou_decrypted.bin");

            // 1. Decrypt
            var detected = DecryptToFile(step.InputPath, decryptedPath, isVpr, progress, cancellationToken);
            progress?.Report(0.55);

            // 2. Decide output
            var output = step.Output.Allocate(step.To);
            if (output is null)
                return [];

            // If the user asked for the same format that was inside the container,
            // just rename/copy the decrypted stream (no quality loss, no FFmpeg).
            if (string.Equals(detected, step.To, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(decryptedPath, output, overwrite: true);
                progress?.Report(1);
                return [output];
            }

            // 3. Otherwise re-encode with FFmpeg (must be present for other audio targets)
            var ffmpegExe = LocateFfmpeg();
            if (ffmpegExe is null)
                throw new InvalidOperationException(
                    "FFmpeg is required to convert KuGou audio to a different format. " +
                    "Install it from Settings → Engines.");

            var args = BuildFfmpegArgs(decryptedPath, output, step.To);
            var result = ProcessRunner.RunAsync(
                ffmpegExe,
                args,
                timeout: TimeSpan.FromMinutes(30),
                cancellationToken: cancellationToken).GetAwaiter().GetResult();

            if (result.ExitCode != 0)
            {
                _logger.LogWarning("FFmpeg failed for {File}: {Err}", step.InputPath, result.StandardError);
                throw new InvalidOperationException(
                    $"FFmpeg could not convert the decrypted KuGou audio to {step.To}.");
            }

            progress?.Report(1);
            return [output];
        }, cancellationToken);

    private static string? LocateFfmpeg()
    {
        var dir = EngineEnvironment.FindBundled("ffmpeg");
        if (dir is null)
            return null;

        // Windows: ffmpeg.exe ; Linux/macOS: ffmpeg
        foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        // Some packages put it in a bin/ subfolder
        return Directory.EnumerateFiles(dir, "ffmpeg*", SearchOption.AllDirectories)
            .FirstOrDefault(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f);
                return n.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase);
            });
    }

    // ------------------------------------------------------------------
    // Decryption (KGM V2)
    // ------------------------------------------------------------------

    private static string DecryptToFile(
        string inputPath,
        string outputPath,
        bool isVpr,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var input = File.OpenRead(inputPath);
        if (input.Length < 0x3C)
            throw new InvalidDataException("File is too small to be a KuGou KGM/KGMA/VPR container.");

        Span<byte> header = stackalloc byte[0x3C];
        input.ReadExactly(header);

        // Magic check (optional but good)
        // KGM/KGMA: 7C D5 32 EB 86 02 7F 4B A8 AF A6 8E 0F FF 99 14
        // VPR:     05 28 BC 96 E9 E4 5A 43 91 AA BD D0 7A F5 36 31

        var headerLen = BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..]);
        if (headerLen < 0x3C || headerLen > input.Length)
            throw new InvalidDataException("Invalid KuGou header length.");

        // 17-byte file key
        var fileKey = new byte[17];
        header[0x1C..0x2C].CopyTo(fileKey);
        fileKey[16] = 0;

        input.Position = headerLen;

        using var output = File.Create(outputPath);
        var buffer = new byte[1 << 16];
        long offset = 0;
        long total = input.Length - headerLen;
        long done = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = input.Read(buffer);
            if (read == 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var enc = buffer[i];
                var med = (byte)(MaskV2((int)(offset + i)) ^ enc ^ fileKey[(offset + i) % 17]);
                med = (byte)(med ^ ((med & 0x0F) << 4));
                if (isVpr)
                    med ^= VprKey[(offset + i) % 17];
                buffer[i] = med;
            }

            output.Write(buffer, 0, read);
            offset += read;
            done += read;
            if (total > 0 && progress is not null)
                progress.Report(0.05 + 0.45 * done / total);
        }

        // Detect the real audio format from the first bytes of the decrypted stream
        output.Flush();
        return DetectAudioFormat(outputPath);
    }

    private static string DetectAudioFormat(string path)
    {
        Span<byte> magic = stackalloc byte[12];
        using (var fs = File.OpenRead(path))
        {
            var n = fs.Read(magic);
            if (n < 4)
                return "mp3"; // fallback
        }

        // FLAC
        if (magic[0] == (byte)'f' && magic[1] == (byte)'L' && magic[2] == (byte)'a' && magic[3] == (byte)'C')
            return "flac";
        // OGG
        if (magic[0] == (byte)'O' && magic[1] == (byte)'g' && magic[2] == (byte)'g' && magic[3] == (byte)'S')
            return "ogg";
        // WAV / RIFF
        if (magic[0] == (byte)'R' && magic[1] == (byte)'I' && magic[2] == (byte)'F' && magic[3] == (byte)'F')
            return "wav";
        // MP3 (ID3 or frame sync)
        if ((magic[0] == (byte)'I' && magic[1] == (byte)'D' && magic[2] == (byte)'3') ||
            (magic[0] == 0xFF && (magic[1] & 0xE0) == 0xE0))
            return "mp3";
        // M4A / AAC in MP4
        if (magic[4] == (byte)'f' && magic[5] == (byte)'t' && magic[6] == (byte)'y' && magic[7] == (byte)'p')
            return "m4a";

        return "mp3"; // safe default for KuGou
    }

    private static IReadOnlyList<string> BuildFfmpegArgs(string input, string output, string target)
    {
        // Minimal, reliable arguments. Quality is left to FFmpeg defaults /
        // the user's preset when the main FfmpegConverter is used; here we just
        // need a working conversion.
        return target switch
        {
            "mp3" => ["-y", "-i", input, "-c:a", "libmp3lame", "-q:a", "2", output],
            "flac" => ["-y", "-i", input, "-c:a", "flac", output],
            "wav" => ["-y", "-i", input, "-c:a", "pcm_s16le", output],
            "ogg" => ["-y", "-i", input, "-c:a", "libvorbis", "-q:a", "6", output],
            "opus" => ["-y", "-i", input, "-c:a", "libopus", "-b:a", "128k", output],
            "m4a" or "aac" => ["-y", "-i", input, "-c:a", "aac", "-b:a", "192k", output],
            "wma" => ["-y", "-i", input, "-c:a", "wmav2", output],
            "aiff" => ["-y", "-i", input, "-c:a", "pcm_s16be", output],
            "mka" => ["-y", "-i", input, "-c:a", "libopus", "-f", "matroska", output],
            _ => ["-y", "-i", input, "-c:a", "copy", output], // last resort
        };
    }

    // ------------------------------------------------------------------
    // Mask tables (KGM V2) – verified against public unlock-music / community code
    // ------------------------------------------------------------------

    private static byte MaskV1(int offset)
    {
        byte value = 0;
        while (offset >= 0x11)
        {
            value ^= Table1[offset % 272];
            offset >>= 4;
            value ^= Table2[offset % 272];
            offset >>= 4;
        }
        return value;
    }

    private static byte MaskV2(int offset) =>
        (byte)(TableV2[offset % 272] ^ MaskV1(offset >> 4));

    private static readonly byte[] Table1 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 33, 1, 97, 1, 33, 1, 225, 1, 33, 1, 97, 1, 33, 1,
        210, 35, 2, 2, 66, 66, 2, 2, 194, 194, 2, 2, 66, 66, 2, 2,
        211, 211, 2, 3, 99, 67, 99, 3, 227, 195, 227, 3, 99, 67, 99, 3,
        148, 180, 148, 101, 4, 4, 4, 4, 132, 132, 132, 132, 4, 4, 4, 4,
        149, 149, 149, 149, 4, 5, 37, 5, 229, 133, 165, 133, 229, 5, 37, 5,
        214, 182, 150, 182, 214, 39, 6, 6, 198, 198, 134, 134, 198, 198, 6, 6,
        215, 215, 151, 151, 215, 215, 6, 7, 231, 199, 231, 135, 231, 199, 231, 7,
        24, 56, 24, 120, 24, 56, 24, 233, 8, 8, 8, 8, 8, 8, 8, 8,
        25, 25, 25, 25, 25, 25, 25, 25, 8, 9, 41, 9, 105, 9, 41, 9,
        218, 58, 26, 58, 90, 58, 26, 58, 218, 43, 10, 10, 74, 74, 10, 10,
        219, 219, 27, 27, 91, 91, 27, 27, 219, 219, 10, 11, 107, 75, 107, 11,
        156, 188, 156, 124, 28, 60, 28, 124, 156, 188, 156, 109, 12, 12, 12, 12,
        157, 157, 157, 157, 29, 29, 29, 29, 157, 157, 157, 157, 12, 13, 45, 13,
        222, 190, 158, 190, 222, 62, 30, 62, 222, 190, 158, 190, 222, 47, 14, 14,
        223, 223, 159, 159, 223, 223, 31, 31, 223, 223, 159, 159, 223, 223, 14, 15,
        0, 32, 0, 96, 0, 32, 0, 224, 0, 32, 0, 96, 0, 32, 0, 241
    };

    private static readonly byte[] Table2 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 35, 1, 103, 1, 35, 1, 239, 1, 35, 1, 103, 1, 35, 1,
        223, 33, 2, 2, 70, 70, 2, 2, 206, 206, 2, 2, 70, 70, 2, 2,
        222, 222, 2, 3, 101, 71, 101, 3, 237, 207, 237, 3, 101, 71, 101, 3,
        157, 191, 157, 99, 4, 4, 4, 4, 140, 140, 140, 140, 4, 4, 4, 4,
        156, 156, 156, 156, 4, 5, 39, 5, 235, 141, 175, 141, 235, 5, 39, 5,
        219, 189, 159, 189, 219, 37, 6, 6, 202, 202, 142, 142, 202, 202, 6, 6,
        218, 218, 158, 158, 218, 218, 6, 7, 233, 203, 233, 143, 233, 203, 233, 7,
        25, 59, 25, 127, 25, 59, 25, 231, 8, 8, 8, 8, 8, 8, 8, 8,
        24, 24, 24, 24, 24, 24, 24, 24, 8, 9, 43, 9, 111, 9, 43, 9,
        215, 57, 27, 57, 95, 57, 27, 57, 215, 41, 10, 10, 78, 78, 10, 10,
        214, 214, 26, 26, 94, 94, 26, 26, 214, 214, 10, 11, 109, 79, 109, 11,
        149, 183, 149, 123, 29, 63, 29, 123, 149, 183, 149, 107, 12, 12, 12, 12,
        148, 148, 148, 148, 28, 28, 28, 28, 148, 148, 148, 148, 12, 13, 47, 13,
        211, 181, 151, 181, 211, 61, 31, 61, 211, 181, 151, 181, 211, 45, 14, 14,
        210, 210, 150, 150, 210, 210, 30, 30, 210, 210, 150, 150, 210, 210, 14, 15,
        0, 34, 0, 102, 0, 34, 0, 238, 0, 34, 0, 102, 0, 34, 0, 254
    };

    private static readonly byte[] TableV2 =
    {
        184, 213, 61, 178, 233, 175, 120, 140, 131, 51, 113, 81, 118, 160, 205, 55,
        47, 62, 53, 141, 169, 190, 152, 183, 231, 140, 34, 206, 90, 97, 223, 104,
        105, 137, 254, 165, 182, 222, 169, 119, 252, 200, 189, 189, 229, 109, 62, 90,
        54, 239, 105, 78, 190, 225, 233, 102, 28, 243, 217, 2, 182, 242, 18, 155,
        68, 208, 111, 185, 53, 137, 182, 70, 109, 115, 130, 6, 105, 193, 237, 215,
        133, 194, 48, 223, 162, 98, 190, 121, 45, 98, 98, 61, 13, 126, 190, 72,
        137, 35, 2, 160, 228, 213, 117, 81, 50, 2, 83, 253, 22, 58, 33, 59,
        22, 15, 195, 178, 187, 179, 226, 186, 58, 61, 19, 236, 246, 1, 69, 132,
        165, 112, 15, 147, 73, 12, 100, 205, 49, 213, 204, 76, 7, 1, 158, 0,
        26, 35, 144, 191, 136, 30, 59, 171, 166, 62, 196, 115, 71, 16, 126, 59,
        94, 188, 227, 0, 132, 255, 9, 212, 224, 137, 15, 91, 88, 112, 79, 251,
        101, 216, 92, 83, 27, 211, 200, 198, 191, 239, 152, 176, 80, 79, 15, 234,
        229, 131, 88, 140, 40, 44, 132, 103, 205, 208, 158, 71, 219, 39, 80, 202,
        244, 99, 99, 232, 151, 127, 27, 75, 12, 194, 193, 33, 76, 204, 88, 245,
        148, 82, 163, 243, 211, 224, 104, 244, 0, 35, 243, 94, 10, 123, 147, 221,
        171, 18, 178, 19, 232, 132, 215, 167, 159, 15, 50, 76, 85, 29, 4, 54,
        82, 220, 3, 243, 249, 78, 66, 233, 61, 97, 239, 124, 182, 179, 147, 80
    };

    private static readonly byte[] VprKey =
    {
        37, 223, 232, 166, 117, 30, 117, 14, 47, 128, 243, 45, 184, 182, 227, 17, 0
    };
}
