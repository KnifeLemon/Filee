// Mutation fuzzing of the RTF reader: seeds written in code (a WordPad-style letter and a Word-style document with
// styles, lists, tables, notes, fields and a picture) are changed by TextFuzzer with RTF's own control words, and
// every input must be read or refused with InvalidDataException / ConversionRefusedException, in time, on a 1 MB
// stack. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one,
// FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class RtfFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 50;

    private static readonly string[] Tokens =
    [
        "{", "}", "\\par", "\\pard", "\\plain", "\\line", "\\tab", "\\page", "\\sect", "\\sectd", "\\~", "\\_", "\\-", "\\*", "\\\\",
        "\\'e4", "\\'b1\\'db", "\\'", "\\u-3913?", "\\u54620 ", "\\uc0", "\\uc2", "\\b", "\\i0", "\\fs-5", "\\fs99999", "\\f1", "\\f99",
        "\\cf2", "\\cf-1", "\\v", "\\s1", "\\s-1", "\\outlinelevel3", "\\ansicpg932", "\\ansicpg99999", "\\fcharset129", "\\cpg949",
        "\\trowd", "\\trleft-2147483648", "\\trrh-300", "\\trhdr", "\\cellx2000", "\\cellx2147483647", "\\cellx-5", "\\cell", "\\row",
        "\\intbl", "\\clmgf", "\\clmrg", "\\clvmgf", "\\clvmrg", "\\clcbpat2", "\\clbrdrt\\brdrs", "\\itap2", "\\nestcell", "\\nestrow",
        "{\\*\\listtable{\\list{\\listlevel\\levelnfc4{\\leveltext\\'02\\'00.;}}\\listid7}}", "{\\list", "\\listlevel", "\\listid7",
        "{\\*\\listoverridetable{\\listoverride\\listid7\\ls3}}", "\\ls3", "\\ls1", "\\ilvl8", "\\ilvl-1",
        "{\\field{\\*\\fldinst HYPERLINK \"https://example.invalid/\"}{\\fldrslt ", "{\\field{\\*\\fldinst PAGE}{\\fldrslt 1}}",
        "{\\fldrslt ", "{\\footnote ", "{\\footnote\\ftnalt ", "{\\header ", "{\\footerl ", "{\\*\\bkmkstart b}", "{\\info{\\title T}}",
        "{\\colortbl;\\red255\\green0\\blue0;}", "{\\stylesheet{\\s1 heading 1;}}", "{\\fonttbl{\\f1\\fcharset129 F;}}",
        "{\\pict\\pngblip\\picw1\\pich1\\picwgoal100\\pichgoal100 ", "{\\pict\\emfblip ", "{\\pict\\wmetafile8 ", "\\picscalex0",
        "\\bin4 ", "\\bin2147483647 ", "89504e470d0a1a0a", "0000000d49484452", "\\paperw0", "\\cols99", "\\pgnstarts-1",
    ];

    [Fact]
    public void RtfReader_reads_or_refuses_every_mutated_document() => Fuzz([Encoding.Latin1.GetBytes(Letter), Encoding.Latin1.GetBytes(Rich())]);

    private const string Letter =
        "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fnil\\fcharset0 Calibri;}{\\f1\\fnil\\fcharset129 \\'b8\\'bc\\'c0\\'ba;}}"
        + "{\\colortbl ;\\red255\\green0\\blue0;}\\viewkind4\\uc1 \\pard\\sa200\\sl276\\slmult1\\f0\\fs22 Dear \\b reader\\b0 ,\\par"
        + "\\qc\\f1 \\'c7\\'d1\\'b1\\'db\\par\\ql\\f0 caf\\u233?\\par}";

    /// <summary>A Word-style document with most of what the reader handles.</summary>
    private static string Rich()
    {
        // PNG bytes of a 1 × 1 picture (the reader copies them and looks only at the header).
        const string Png = "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000d4944415478da63f8cfc0f01f0005000201"
                           + "a0e1c2ea0000000049454e44ae426082";
        return "{\\rtf1\\ansi\\ansicpg1252\\uc1\\deff0{\\fonttbl{\\f0\\froman\\fcharset0 Times New Roman;}}"
               + "{\\colortbl;\\red0\\green0\\blue0;\\red255\\green255\\blue0;}"
               + "{\\stylesheet{\\ql \\fs24 Normal;}{\\s1\\ql\\keepn\\outlinelevel0 \\b\\fs32 heading 1;}}"
               + "{\\*\\listtable{\\list{\\listlevel\\levelnfc0\\levelstartat1{\\leveltext\\'02\\'00.;}}"
               + "{\\listlevel\\levelnfc4{\\leveltext\\'02\\'01);}}\\listid100}{\\list{\\listlevel\\levelnfc23{\\leveltext\\'01\\u-3913 ?;}}\\listid200}}"
               + "{\\*\\listoverridetable{\\listoverride\\listid100\\ls1}{\\listoverride\\listid200\\ls2}}"
               + "{\\info{\\title Report}}\\paperw11906\\paperh16838\\margl1134\\sectd\\headery709"
               + "{\\footer\\pard\\qc Page {\\field{\\*\\fldinst PAGE}{\\fldrslt 1}}\\par}"
               + "\\pard\\plain\\s1\\outlinelevel0\\b\\fs32 Introduction\\par"
               + "\\pard\\plain {\\listtext 1.\\tab}\\ls1\\ilvl0 First\\par\\ls1\\ilvl1 Inner\\par\\ls2 Dot\\par"
               + "\\pard See {\\field{\\*\\fldinst {HYPERLINK \"https://filee.sh\"}}{\\fldrslt {\\ul the site}}}"
               + "{\\*\\bkmkstart here}{\\*\\bkmkend here} and a note{\\super\\chftn}{\\footnote\\pard\\plain Note.\\par}.\\par"
               + "\\trowd\\trleft0\\clbrdrt\\brdrs\\clcbpat2\\clmgf\\cellx3000\\clmrg\\cellx6000\\clvmgf\\cellx9000"
               + "\\pard\\intbl Merged\\cell\\cell Tall\\cell\\row"
               + "\\trowd\\cellx3000\\cellx6000\\clvmrg\\cellx9000\\pard\\intbl A\\cell B\\cell\\cell\\row"
               + "\\pard\\intbl C\\cell D\\cell\\cell\\row"
               + "\\pard After.\\page {\\pict\\pngblip\\picw1\\pich1\\picwgoal1440\\pichgoal720 " + Png + "}\\par"
               + "\\sect\\sectd\\lndscpsxn Landscape\\par}";
    }

    private void Fuzz(List<byte[]> seeds) =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("rtf", "rtf", seeds, FuzzHarness.Text(Tokens), (input, media) => RtfReader.Read(input, media));
}
