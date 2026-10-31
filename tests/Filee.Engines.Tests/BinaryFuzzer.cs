// A mutation fuzzer for binary containers (CHM, PDB, TCR, RB, SNB, LZX streams): each input is the seed with a few
// changes: bytes flipped, a 32-bit field set to a value at the edge of what readers compute with (both byte orders),
// a range cleared or repeated, or the file cut short.

namespace Filee.Engines.Tests;

internal sealed class BinaryFuzzer(byte[] seed, Random random)
{
    private static readonly uint[] Interesting = [0, 1, 2, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE];

    public byte[] Next()
    {
        var bytes = (byte[])seed.Clone();
        for (var changes = 1 + random.Next(3); changes > 0 && bytes.Length > 8; changes--)
        {
            switch (random.Next(6))
            {
                case 0:
                case 1:
                    for (var flips = 1 + random.Next(8); flips > 0; flips--)
                        bytes[Position(bytes)] ^= (byte)(1 << random.Next(8));
                    break;
                case 2:
                    {
                        var at = Math.Min(Position(bytes), bytes.Length - 4) & ~(random.Next(2) == 0 ? 3 : 0);
                        var value = Interesting[random.Next(Interesting.Length)];
                        var field = BitConverter.GetBytes(value);
                        if (random.Next(2) == 0)
                            Array.Reverse(field);
                        field.CopyTo(bytes, at);
                        break;
                    }
                case 3:
                    {
                        var at = Position(bytes);
                        Array.Clear(bytes, at, Math.Min(bytes.Length - at, 1 + random.Next(64)));
                        break;
                    }
                case 4:
                    {
                        var from = Position(bytes);
                        var to = Position(bytes);
                        var count = Math.Min(Math.Min(bytes.Length - from, bytes.Length - to), 1 + random.Next(256));
                        Array.Copy(bytes, from, bytes, to, count);
                        break;
                    }
                default:
                    bytes = bytes[..Math.Max(1, Position(bytes))];
                    break;
            }
        }
        return bytes;
    }

    /// <summary>A position, most often in the first kilobyte where the headers and tables are.</summary>
    private int Position(byte[] bytes) =>
        random.Next(3) == 0 ? random.Next(bytes.Length) : random.Next(Math.Min(bytes.Length, 1024));
}
