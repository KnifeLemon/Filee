// "filee presets check <folder>": checks one shared preset's folder for the preset gallery (#1530) with the rules the
// app installs by (SharedPresetFolderCheck), and decodes every picture with SkiaSharp, the library Filee draws and
// converts pictures with. The gallery's CI runs it on each submission and comments what it prints; with --json it
// gets the problems (code, file, path, message) and what the preset is, for meta.json. It reads no settings and
// writes nothing.

using System.Text.Json;
using Filee.Core.Gallery;
using SkiaSharp;

namespace Filee.Cli;

internal static class PresetsCheckCommand
{
    public static int Run(CliOptions options, TextWriter output, TextWriter errors)
    {
        var folder = options.Inputs[1];
        if (!Directory.Exists(folder))
        {
            errors.WriteLine($"filee: not a folder: {folder}");
            return Cli.UsageError;
        }
        var result = SharedPresetFolderCheck.Check(folder, new SkiaImageDecoder());
        if (options.Json)
            WriteJson(result, output);
        else if (!options.Quiet || !result.Ok)
            WriteText(result, output);
        return result.Ok ? Cli.Success : Cli.SomeFailed;
    }

    private static void WriteText(PresetCheckResult result, TextWriter output)
    {
        foreach (var error in result.Errors)
            output.WriteLine(error);
        if (!result.Ok)
        {
            output.WriteLine($"Found {result.Errors.Count} problem(s).");
            return;
        }
        var derived = result.Derived!;
        foreach (var warning in derived.Warnings)
            output.WriteLine($"warning ({warning.Severity}): {warning.Message} ({warning.Code}: {string.Join(", ", warning.Presets)})");
        output.WriteLine($"OK: {derived.Presets} preset(s), {derived.Profiles} donut(s). Kinds: {string.Join(", ", derived.Kinds)}. "
            + $"Formats: {(derived.From.Count == 0 ? "any" : string.Join(", ", derived.From))} → {(derived.To.Count == 0 ? "-" : string.Join(", ", derived.To))}. "
            + $"Needs Filee {derived.MinVersion} or later.");
    }

    private static void WriteJson(PresetCheckResult result, TextWriter output) => Cli.WriteJsonDocument(output, json =>
    {
        json.WriteStartObject();
        json.WriteBoolean("ok", result.Ok);
        json.WriteStartArray("errors");
        foreach (var error in result.Errors)
        {
            json.WriteStartObject();
            json.WriteString("code", error.Code);
            json.WriteString("file", error.File);
            json.WriteString("path", error.Path);
            json.WriteString("message", error.Message);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        if (result.Derived is { } derived)
        {
            json.WriteStartObject("derived");
            WriteList(json, "kinds", derived.Kinds);
            json.WriteStartObject("formats");
            WriteList(json, "from", derived.From);
            WriteList(json, "to", derived.To);
            json.WriteEndObject();
            json.WriteString("minVersion", derived.MinVersion);
            json.WriteNumber("presets", derived.Presets);
            json.WriteNumber("profiles", derived.Profiles);
            json.WriteString("sha256", derived.Sha256);
            WriteList(json, "languages", derived.Languages);
            json.WriteStartArray("warnings");
            foreach (var warning in derived.Warnings)
            {
                json.WriteStartObject();
                json.WriteString("code", warning.Code);
                json.WriteString("severity", warning.Severity);
                json.WriteString("message", warning.Message);
                WriteList(json, "presets", warning.Presets);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        else
            json.WriteNull("derived");
        json.WriteEndObject();
    });

    private static void WriteList(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
            json.WriteStringValue(value);
        json.WriteEndArray();
    }
}

/// <summary>
/// Decodes a picture with SkiaSharp, every pixel, so a file that is broken past its header fails. Only called for
/// pictures whose header said 2560 pixels or less on the long edge.
/// </summary>
internal sealed class SkiaImageDecoder : IGalleryImageDecoder
{
    public GalleryImageDecode Decode(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream, out var opened);
        if (codec is null)
            return new GalleryImageDecode(0, 0, 0, $"not a picture SkiaSharp reads ({opened})");
        var size = codec.Info;
        if (Math.Max(size.Width, size.Height) > SharedPresetFolderCheck.MaxLongEdge)
            return new GalleryImageDecode(size.Width, size.Height, 1, $"{size.Width} x {size.Height} is larger than its header said");
        var frames = Math.Max(1, codec.FrameCount);
        using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var decoded = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        return decoded == SKCodecResult.Success
            ? new GalleryImageDecode(size.Width, size.Height, frames, null)
            : new GalleryImageDecode(size.Width, size.Height, frames, decoded.ToString());
    }
}
