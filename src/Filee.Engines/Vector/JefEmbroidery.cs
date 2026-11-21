// Janome JEF embroidery designs (issue #1716) as SVG, drawn like Tajima DST (DstEmbroidery). The layout follows
// pyembroidery's reader (MIT): a little-endian header whose first int32 is where the stitches start, the number of
// threads at offset 24 and, from offset 116, each thread's number in Janome's thread chart; then two signed bytes
// (dx, dy in 0.1 mm, Y up) per stitch, where 0x80 starts a command (0x01 next thread, 0x02 move, 0x10 end) followed
// by two more bytes. Thread 0 isn't a colour: a change to it is a stop for the same thread.

namespace Filee.Engines.Vector;

internal static class JefEmbroidery
{
    /// <summary>Where the thread numbers start; the stitches can't start before them.</summary>
    private const int ThreadTable = 116;

    /// <summary>More threads than a design has; a header claiming more is not a JEF header.</summary>
    private const int MaxThreads = 256;

    /// <summary>
    /// Janome's thread chart by number (1–78), as pyembroidery lists it (EmbThreadJef.py, MIT); number 0 is no colour.
    /// </summary>
    private static readonly string[] Chart =
    [
        "#000000", // 0 (no colour)
        "#000000", // 1 Black
        "#FFFFFF", // 2 White
        "#FFFF17", // 3 Yellow
        "#FF6600", // 4 Orange
        "#2F5933", // 5 Olive Green
        "#237336", // 6 Green
        "#65C2C8", // 7 Sky
        "#AB5A96", // 8 Purple
        "#F669A0", // 9 Pink
        "#FF0000", // 10 Red
        "#B1704E", // 11 Brown
        "#0B2F84", // 12 Blue
        "#E4C35D", // 13 Gold
        "#481A05", // 14 Dark Brown
        "#AC9CC7", // 15 Pale Violet
        "#FCF294", // 16 Pale Yellow
        "#F999B7", // 17 Pale Pink
        "#FAB381", // 18 Peach
        "#C9A480", // 19 Beige
        "#970533", // 20 Wine Red
        "#A0B8CC", // 21 Pale Sky
        "#7FC21C", // 22 Yellow Green
        "#E5E5E5", // 23 Silver Gray
        "#889B9B", // 24 Gray
        "#98D6BD", // 25 Pale Aqua
        "#B2E1E3", // 26 Baby Blue
        "#368BA0", // 27 Powder Blue
        "#4F83AB", // 28 Bright Blue
        "#386A91", // 29 Slate Blue
        "#071650", // 30 Navy Blue
        "#F999A2", // 31 Salmon Pink
        "#F9676B", // 32 Coral
        "#E3311F", // 33 Burnt Orange
        "#E2A188", // 34 Cinnamon
        "#B59474", // 35 Umber
        "#E4CF99", // 36 Blond
        "#FFCB00", // 37 Sunflower
        "#E1ADD4", // 38 Orchid Pink
        "#C3007E", // 39 Peony Purple
        "#80004B", // 40 Burgundy
        "#540571", // 41 Royal Purple
        "#B10525", // 42 Cardinal Red
        "#CAE0C0", // 43 Opal Green
        "#899856", // 44 Moss Green
        "#5C941A", // 45 Meadow Green
        "#003114", // 46 Dark Green
        "#5DAE94", // 47 Aquamarine
        "#4CBF8F", // 48 Emerald Green
        "#007772", // 49 Peacock Green
        "#595B61", // 50 Dark Gray
        "#FFFFF2", // 51 Ivory White
        "#B15818", // 52 Hazel
        "#CB8A07", // 53 Toast
        "#986C80", // 54 Salmon
        "#98692D", // 55 Cocoa Brown
        "#4D3419", // 56 Sienna
        "#4C330B", // 57 Sepia
        "#33200A", // 58 Dark Sepia
        "#523A97", // 59 Violet Blue
        "#0D217E", // 60 Blue Ink
        "#1E77AC", // 61 Sola Blue
        "#B2DD53", // 62 Green Dust
        "#F33689", // 63 Crimson
        "#DE649E", // 64 Floral Pink
        "#984161", // 65 Wine
        "#4C5612", // 66 Olive Drab
        "#4C881F", // 67 Meadow
        "#E4DE79", // 68 Mustard
        "#CB8A1A", // 69 Yellow Ocher
        "#CBA21C", // 70 Old Gold
        "#FF9805", // 71 Honey Dew
        "#FCB257", // 72 Tangerine
        "#FFE505", // 73 Canary Yellow
        "#F0331F", // 74 Vermilion
        "#1A842D", // 75 Bright Green
        "#386CAE", // 76 Ocean Blue
        "#E3C4B4", // 77 Beige Gray
        "#E3AC81", // 78 Bamboo
    ];

    /// <summary>A thread's colour: its chart colour, or a colour of DST's palette for a number the chart doesn't have.</summary>
    internal static string ColorOf(int number, int thread) =>
        number is > 0 and < 79 ? Chart[number] : EmbroiderySvg.Fallback(thread);

    /// <summary>The stitched runs of each thread as (x, y) in 0.1 mm with Y up, and each thread's chart number.</summary>
    /// <exception cref="InvalidDataException">The file isn't a JEF embroidery design.</exception>
    public static (List<List<List<(int X, int Y)>>> Threads, List<int> Numbers) Read(byte[] data)
    {
        if (data.Length < ThreadTable)
            throw new InvalidDataException("The file isn't a Janome JEF embroidery design (it is shorter than its header).");
        var start = BitConverter.ToInt32(data, 0);
        var count = BitConverter.ToInt32(data, 24);
        if (count is < 0 or > MaxThreads || start < ThreadTable + count * 4 || start > data.Length)
            throw new InvalidDataException("The file isn't a Janome JEF embroidery design (its header doesn't add up).");
        var numbers = new List<int>(count);
        for (var i = 0; i < count; i++)
            numbers.Add(Math.Abs(BitConverter.ToInt32(data, ThreadTable + i * 4)));

        List<List<List<(int X, int Y)>>> threads = [[]];
        List<(int X, int Y)>? run = null;
        int x = 0, y = 0;
        var next = 1; // the entry of numbers the next colour change goes to
        for (var i = start; i + 2 <= data.Length; i += 2)
        {
            if (data[i] != 0x80)
            {
                int dx = (sbyte)data[i], dy = (sbyte)data[i + 1];
                if (run is null)
                {
                    // A stitch starts at the needle's last position.
                    run = [(x, y)];
                    threads[^1].Add(run);
                }
                x += dx;
                y += dy;
                run.Add((x, y));
                continue;
            }
            var command = data[i + 1];
            i += 2;
            if (command == 0x10 || i + 2 > data.Length)
                break;
            run = null;
            if (command == 0x02)
            {
                // A move without thread.
                x += (sbyte)data[i];
                y += (sbyte)data[i + 1];
            }
            else if (command == 0x01)
            {
                // A change to thread 0 stops the machine and goes on with the same thread (what pyembroidery does);
                // the move a colour change carries is ignored there too.
                if (next < numbers.Count && numbers[next] == 0)
                    numbers.RemoveAt(next);
                else
                {
                    next++;
                    threads.Add([]);
                }
            }
            else
            {
                break; // a command no reader knows
            }
        }
        // Empty threads go, with their numbers, so the colours stay with the threads that have stitches.
        for (var t = threads.Count - 1; t >= 0; t--)
        {
            if (threads[t].Count > 0)
                continue;
            threads.RemoveAt(t);
            if (t < numbers.Count)
                numbers.RemoveAt(t);
        }
        return (threads, numbers);
    }

    /// <summary>The design as SVG at its real size (millimetres), each thread in its Janome chart colour.</summary>
    public static string ToSvg(byte[] data)
    {
        var (threads, numbers) = Read(data);
        return EmbroiderySvg.Write(threads, t => ColorOf(t < numbers.Count ? numbers[t] : 0, t), "JEF");
    }
}
