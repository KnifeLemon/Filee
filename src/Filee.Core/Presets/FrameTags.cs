// What a marked video frame's copy says about itself (#1205): the video it came from, the frame's time and number, and
// the note typed with it, as one PNG text, so Cut a clip finds the copies of a video again (by it, never by file name)
// and the export writes the time and note under each picture. Times are written in the invariant culture, whatever the
// user's regional format: the video's length to the millisecond, the frame's time as the playhead had it.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>A marked frame's facts, as its copy carries them.</summary>
/// <param name="VideoName">The video's file name, without its folder.</param>
/// <param name="VideoSeconds">The video's length in seconds.</param>
/// <param name="VideoBytes">The video's size in bytes.</param>
/// <param name="FrameSeconds">
/// The frame's time from the video's start, as the playhead had it; shown, named and compared to the millisecond.
/// </param>
/// <param name="FrameNumber">
/// The frame's number, counted from 0 as FFmpeg counts; null when it couldn't be counted.
/// </param>
/// <param name="Note">The note typed with the frame; empty for none.</param>
public sealed record FrameFacts(string VideoName, double VideoSeconds, long VideoBytes, double FrameSeconds, long? FrameNumber, string Note);

/// <summary>The PNG text a marked frame's copy carries, and reading and writing it.</summary>
public static class FrameTags
{
    /// <summary>
    /// The PNG text key: one, holding the facts as JSON with every letter outside ASCII escaped, so the text is plain
    /// ASCII (Magick.NET reads back a long compressed iTXt as nothing, 19 Nov 2026), and a copy's text is replaced as
    /// a whole, never half kept from an earlier one.
    /// </summary>
    public const string Key = "Filee:Frame";

    /// <summary>The longest time a frame's text may hold, in seconds (a year): beyond is no video's.</summary>
    private const double MaxSeconds = 366 * 24 * 3600;

    /// <summary>
    /// The text <paramref name="facts"/> are written as, in the invariant culture: the video's length to the
    /// millisecond, and the frame's time as the playhead had it (written exactly), so going back to it shows that frame
    /// (a time rounded to the millisecond can be after it) and its millisecond, by which copies are told apart and
    /// named (<see cref="Milliseconds"/>), is the frame's own.
    /// </summary>
    public static string ToText(FrameFacts facts) =>
        JsonSerializer.Serialize(facts with
        {
            VideoSeconds = Milliseconds(facts.VideoSeconds) / 1000.0,
            FrameSeconds = double.IsFinite(facts.FrameSeconds) ? Math.Max(0, facts.FrameSeconds) : 0,
        }, FrameTagsJsonContext.Default.FrameFacts);

    /// <summary>
    /// The facts <paramref name="text"/> holds; null unless it is a marked frame's: a video name, a length, size and
    /// time that are real numbers, not below 0, and a number not below 0 when there is one.
    /// </summary>
    public static FrameFacts? FromText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            return JsonSerializer.Deserialize(text, FrameTagsJsonContext.Default.FrameFacts) is { } facts
                   && !string.IsNullOrEmpty(facts.VideoName) && facts.VideoName.Length <= 1024 && facts.Note is { Length: <= 1000 }
                   && facts.VideoSeconds is >= 0 and <= MaxSeconds && facts.VideoBytes >= 0
                   && facts.FrameSeconds is >= 0 and <= MaxSeconds && facts.FrameNumber is null or >= 0
                ? facts
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The facts in the PNG at <paramref name="path"/>: its <see cref="Key"/> text (<c>tEXt</c>, <c>zTXt</c>, or
    /// <c>iTXt</c> plain or compressed) before the picture data, read without decoding the picture, so a folder of
    /// photos is looked through quickly. Null for a file that isn't a PNG, carries no marked frame's text, or can't be
    /// read.
    /// </summary>
    public static FrameFacts? ReadFile(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            using var reader = new BinaryReader(file);
            if (!reader.ReadBytes(8).AsSpan().SequenceEqual(PngSignature))
                return null;
            string? found = null;
            while (file.Position + 8 <= file.Length)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
                var type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (type is "IDAT" or "IEND" || length > int.MaxValue)
                    break;
                var text = type is "tEXt" or "zTXt" or "iTXt";
                if (text && length <= MaxTextChunk)
                {
                    if (TextOf(type, reader.ReadBytes((int)length)) is (Key, var value))
                        found = value;
                }
                else
                {
                    file.Seek(length, SeekOrigin.Current);
                }
                file.Seek(4, SeekOrigin.Current); // the CRC
            }
            return FromText(found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException
            or System.IO.InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A text chunk longer than this isn't a marked frame's (a note is at most 300 characters).</summary>
    private const int MaxTextChunk = 64 * 1024;

    /// <summary>A text chunk's keyword and text; null when it can't be read.</summary>
    private static (string Key, string Value)? TextOf(string type, byte[] data)
    {
        var end = Array.IndexOf(data, (byte)0);
        if (end <= 0)
            return null;
        var key = System.Text.Encoding.Latin1.GetString(data, 0, end);
        if (type == "tEXt")
            return (key, System.Text.Encoding.Latin1.GetString(data, end + 1, data.Length - end - 1));
        // zTXt: keyword, 0, method, the text compressed.
        if (key != Key)
            return null; // another program's text: not unpacked, so a damaged one can't hide ours
        if (type == "zTXt")
            return end + 2 > data.Length ? null : (key, System.Text.Encoding.Latin1.GetString(Unpack(data.AsSpan(end + 2))));
        // iTXt: keyword, 0, compressed?, method, language tag, 0, translated keyword, 0, text.
        if (end + 2 >= data.Length)
            return null;
        var compressed = data[end + 1] == 1;
        var language = Array.IndexOf(data, (byte)0, end + 3);
        var translated = language < 0 ? -1 : Array.IndexOf(data, (byte)0, language + 1);
        if (translated < 0)
            return null;
        var text = data.AsSpan(translated + 1);
        return (key, System.Text.Encoding.UTF8.GetString(compressed ? Unpack(text) : text));
    }

    /// <summary>A compressed text, unpacked; at most <see cref="MaxTextChunk"/> bytes of it.</summary>
    private static byte[] Unpack(ReadOnlySpan<byte> packed)
    {
        using var source = new MemoryStream(packed.ToArray());
        using var zlib = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
        var buffer = new byte[MaxTextChunk];
        var read = 0;
        while (read < buffer.Length && zlib.Read(buffer, read, buffer.Length - read) is > 0 and var n)
            read += n;
        return buffer[..read];
    }

    /// <summary>
    /// <paramref name="name"/> composed (NFC), a lone surrogate (which Windows allows in a name) as U+FFFD, as the
    /// frame's text writes it: a name compares the same before and after it is written. A name Unicode can't compose
    /// (one with U+FFFE, which NTFS and ext4 allow) is compared as it is.
    /// </summary>
    public static string Composed(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                text.Append(name[i]).Append(name[++i]);
            else
                text.Append(char.IsSurrogate(name[i]) ? '\uFFFD' : name[i]);
        }
        try
        {
            return text.ToString().Normalize();
        }
        catch (ArgumentException)
        {
            return text.ToString();
        }
    }

    /// <summary>
    /// <paramref name="facts"/> are of the video named <paramref name="videoName"/> of that length and size: a video of
    /// the same name elsewhere is a different one.
    /// </summary>
    public static bool SameVideo(FrameFacts facts, string videoName, double videoSeconds, long videoBytes) =>
        // In one Unicode form: a synced folder can hand a Mac's "é" (e and an accent) to a PC as one letter. Case still
        // counts: two names that differ only in case can be two files.
        string.Equals(Composed(facts.VideoName), Composed(videoName), StringComparison.Ordinal)
        && Math.Abs(facts.VideoSeconds - videoSeconds) < 0.001 && facts.VideoBytes == videoBytes;

    /// <summary>
    /// The frame's time and number as the window and the export show them: "2:04.700 · frame 3,741" (the number grouped
    /// in <paramref name="culture"/>; "frame" from <paramref name="frameWord"/>), or the time alone without one.
    /// </summary>
    /// <param name="frameWord">
    /// The word for a frame and its number, with <c>{0}</c> for the number ("frame {0}").
    /// </param>
    public static string Label(FrameFacts facts, string frameWord, IFormatProvider culture) =>
        Label(facts.FrameSeconds, facts.FrameNumber?.ToString("N0", culture), frameWord, culture);

    /// <summary>
    /// The label for a frame at <paramref name="seconds"/> with <paramref name="numberText"/> as its number ("3,741",
    /// or "…" while it is counted); the time alone with none.
    /// </summary>
    public static string Label(double seconds, string? numberText, string frameWord, IFormatProvider culture)
    {
        if (numberText is not { } grouped)
            return Time(seconds);
        string word;
        try
        {
            word = string.Format(culture, frameWord, grouped);
        }
        catch (FormatException)
        {
            word = grouped; // a translation with a broken placeholder: the number alone, rather than no export
        }
        return $"{Time(seconds)} · {word}";
    }

    /// <summary>
    /// A frame's time to the millisecond, as the clip window writes its times: <c>2:04.700</c>, <c>1:02:05.300</c>
    /// (hours only when there are any); a negative or unknown time is 0.
    /// </summary>
    public static string Time(double seconds)
    {
        var ms = Milliseconds(seconds);
        var (hours, minutes, secs, rest) = (ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000);
        var invariant = CultureInfo.InvariantCulture;
        return hours > 0
            ? string.Create(invariant, $"{hours}:{minutes:00}:{secs:00}.{rest:000}")
            : string.Create(invariant, $"{minutes}:{secs:00}.{rest:000}");
    }

    /// <summary>
    /// <paramref name="seconds"/> in whole milliseconds, as a copy keeps a time: the one rounding used wherever two
    /// frames' times are compared. A negative or unknown time is 0.
    /// </summary>
    public static long Milliseconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero) : 0;
}

/// <summary>
/// <see cref="FrameFacts"/> as compact JSON; System.Text.Json escapes every letter outside ASCII by default, which is
/// what keeps the text plain ASCII.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FrameFacts))]
internal sealed partial class FrameTagsJsonContext : JsonSerializerContext;
