// Mutation fuzzing input for the DOC reader (DocumentFuzzTests): a seed DOC is taken apart into its streams, whose
// bytes are changed (single bytes, interesting 16- and 32-bit values, cut short, chunks copied over), most often in
// the FIB and the table stream where the offsets and lengths are, and packed into a new compound file; now and then
// the bytes of the compound file itself are changed.

namespace Filee.Engines.Tests;

internal sealed class DocFuzzer
{
    private static readonly uint[] Interesting = [0, 1, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, 512, 1 << 20, 1 << 30];

    /// <summary>The FIB's length in the seeds (FibBase, FibRgW, FibRgLw and FibRgFcLcb97).</summary>
    private const int FibLength = 900;

    private readonly Random _random;
    private readonly List<(string Name, byte[] Data)> _streams = [];

    public DocFuzzer(byte[] seed, Random random)
    {
        _random = random;
        using var cfb = Filee.Engines.Documents.CompoundFile.Open(new MemoryStream(seed));
        foreach (var name in cfb.Streams)
            _streams.Add((name, cfb.Read(name)!));
    }

    /// <summary>A mutated copy of the seed.</summary>
    public byte[] Next()
    {
        var streams = _streams.Select(s => (s.Name, Data: (byte[])s.Data.Clone())).ToList();
        for (var rounds = 1 + _random.Next(4); rounds > 0; rounds--)
        {
            var pick = _random.Next(10);
            var name = pick < 4 ? "WordDocument" : pick < 9 ? "1Table" : "Data";
            var index = streams.FindIndex(s => s.Name == name);
            if (index < 0)
                index = _random.Next(streams.Count);
            streams[index] = (streams[index].Name, Mutate(streams[index].Data, streams[index].Name == "WordDocument"));
        }
        var file = HwpBuilder.Cfb(streams);
        if (_random.Next(10) == 0)
            file[512 + _random.Next(file.Length - 512)] ^= (byte)(1 + _random.Next(255));
        return file;
    }

    private byte[] Mutate(byte[] data, bool word)
    {
        if (data.Length < 8)
            return data;
        int Offset(int width) => word && _random.Next(2) == 0
            ? _random.Next(Math.Min(FibLength, data.Length) - width)
            : _random.Next(data.Length - width);
        switch (_random.Next(6))
        {
            case 0:
                data[Offset(1)] ^= (byte)(1 + _random.Next(255));
                return data;
            case 1:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(Offset(4) & ~3), Interesting[_random.Next(Interesting.Length)]);
                return data;
            case 2:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(Offset(2) & ~1), (ushort)Interesting[_random.Next(Interesting.Length)]);
                return data;
            case 3:
                return data[..Math.Min(data.Length, word ? FibLength + _random.Next(Math.Max(1, data.Length - FibLength)) : _random.Next(data.Length))];
            case 4:
                {
                    var length = 1 + _random.Next(Math.Min(64, data.Length / 2));
                    Array.Copy(data, _random.Next(data.Length - length), data, _random.Next(data.Length - length), length);
                    return data;
                }
            default:
                data[Offset(1)] = (byte)Interesting[_random.Next(Interesting.Length)];
                return data;
        }
    }
}
