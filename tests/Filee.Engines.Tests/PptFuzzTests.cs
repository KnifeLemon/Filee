// Mutation fuzzing of the PowerPoint 97–2003 reader (Office/Ppt): the seeds are the rich deck of PptReaderTests and a
// deck with a group, a picture and text runs, written in code. PptFuzzer changes their streams (the PowerPoint Document
// stream most often: records, lengths, persist directory and user edit), and every input must be translated and read
// by the slide renderer and the document reader, or refused with InvalidDataException / ConversionRefusedException, in
// time and on a 1 MB stack. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one,
// FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.Buffers.Binary;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Office.Ppt;
using Filee.Engines.Slides;
using static Filee.Engines.Tests.PptBuilder;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class PptFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 50;

    [Fact]
    public void PptReader_translates_or_refuses_every_mutated_presentation() =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("ppt", "ppt", [PptReaderTests.RichDeck().Build(), SmallDeck().Build()],
            (seed, random) => new PptFuzzer(seed, random).Next, (path, media) =>
            {
                PptxSlideReader.Read(path, cancellationToken: TestContext.Current.CancellationToken);
                PptReader.Read(path, media);
            });

    /// <summary>A slide with a group of a picture and a text box with runs, a line and an ellipse.</summary>
    private static PptBuilder SmallDeck()
    {
        var ppt = new PptBuilder();
        ppt.Pictures.Add(DocumentFuzzTests.TinyPng(3, 2));
        var slide = new Sheet();
        slide.Shapes.Add(Group(3000, (100, 100, 2000, 2000), (0, 0, 100, 100), false,
            Shape(3001, 75, (0, 0, 50, 50), [(0x0104, 1)], child: true),
            Shape(3002, 202, (50, 50, 100, 100), null, null,
                TextBox(4, "ab\rcd\vef", Styles([(3, 0, Paragraph(bullet: true, bulletChar: '*')), (6, 1, Paragraph(align: 1))],
                    [(2, Character(bold: true, size: 20, color: 0x123456)), (7, Character(color: 2))])), child: true)));
        slide.Shapes.Add(Shape(3003, 20, (0, 3000, 5000, 3000), [(0x01D0, 2), (0x01CE, 3)]));
        slide.Shapes.Add(Shape(3004, 3, (3000, 3000, 4000, 4000), [(0x0004, 90u << 16), (0x0181, 0x10000F0)]));
        ppt.Slides.Add(slide);
        return ppt;
    }

    /// <summary>
    /// Takes a seed apart into its streams and changes their bytes (single bytes, interesting 16- and 32-bit values at
    /// record headers and elsewhere, cut short, chunks copied over), most often in the PowerPoint Document stream, and
    /// packs a new compound file; now and then the compound file's own bytes change.
    /// </summary>
    private sealed class PptFuzzer
    {
        private static readonly uint[] Interesting = [0, 1, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, 0x0F, 8, 1 << 20];

        private readonly Random _random;
        private readonly List<(string Name, byte[] Data)> _streams = [];

        public PptFuzzer(byte[] seed, Random random)
        {
            _random = random;
            using var cfb = Filee.Engines.Documents.CompoundFile.Open(new MemoryStream(seed));
            foreach (var name in cfb.Streams)
                _streams.Add((name, cfb.Read(name)!));
        }

        public byte[] Next()
        {
            var streams = _streams.Select(s => (s.Name, Data: (byte[])s.Data.Clone())).ToList();
            for (var rounds = 1 + _random.Next(4); rounds > 0; rounds--)
            {
                var pick = _random.Next(10);
                var name = pick < 8 ? "PowerPoint Document" : pick < 9 ? "Pictures" : "Current User";
                var index = streams.FindIndex(s => s.Name == name);
                if (index < 0)
                    index = _random.Next(streams.Count);
                streams[index] = (streams[index].Name, Mutate(streams[index].Data));
            }
            var file = HwpBuilder.Cfb(streams);
            if (_random.Next(10) == 0)
                file[512 + _random.Next(file.Length - 512)] ^= (byte)(1 + _random.Next(255));
            return file;
        }

        private byte[] Mutate(byte[] data)
        {
            if (data.Length < 16)
                return data;
            // Record headers sit at 8-byte boundaries surprisingly often; aim there half of the time.
            int Offset(int width) => _random.Next(2) == 0 ? _random.Next((data.Length - width) / 8) * 8 + (width == 4 ? 4 : 0) : _random.Next(data.Length - width);
            switch (_random.Next(6))
            {
                case 0:
                    data[Offset(1)] ^= (byte)(1 + _random.Next(255));
                    return data;
                case 1:
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(Offset(4)), Interesting[_random.Next(Interesting.Length)]);
                    return data;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(Offset(2)), (ushort)Interesting[_random.Next(Interesting.Length)]);
                    return data;
                case 3:
                    return data[.._random.Next(data.Length)];
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
}
