// SQL script → TXT. Pure .NET, no external engine.
// SSMS saves .sql files as UTF-16 (with BOM), UTF-8 (with or without BOM) or the Windows ANSI code page,
// so the text is decoded with the right encoding and always written back as UTF-8.

using System.Text;
using Filee.Core.Conversion;

namespace Filee.Engines.Text;

public sealed class SqlTextConverter : IConverter
{
    public string Id => "sqltext";
    public string DisplayName => "SQL script";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("sql", "txt"),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available();

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<string>>(async () =>
        {
            var output = step.Output.Allocate("txt");
            if (output is null)
                return [];

            var bytes = await File.ReadAllBytesAsync(step.InputPath, ct);
            ct.ThrowIfCancellationRequested();

            var text = Decode(bytes);
            await File.WriteAllTextAsync(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);

            progress?.Report(1);
            return [output];
        }, ct);

    internal static string Decode(byte[] bytes)
    {
        // 1. A byte order mark tells us exactly what the file is.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)       // UTF-16 LE (SSMS default "Unicode")
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)       // UTF-16 BE
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 2. No BOM: valid UTF-8 (this includes plain ASCII).
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 3. Not UTF-8: SSMS "ANSI" files use the Windows code page, Windows-1252 in most of the world.
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }
}