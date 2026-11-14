// The prolog of Filee's PostScript files: FileeDict with a short procedure for each PDF operator the pages use
// (named like the operator, so a translated page reads like its content stream), text placement, pictures and
// patterns. Everything Filee defines lives in FileeDict, which an EPS's host never sees outside the EPS.

namespace Filee.Engines.Pdf.PostScript;

internal static class PsProlog
{
    /// <summary>The prolog, with LF line ends whatever the source file has.</summary>
    public static string Text { get; } = Source.ReplaceLineEndings("\n");

    private const string Source = """
        %%BeginResource: procset FileePdfToPs 1.0 0
        /FileeDict 64 dict def
        FileeDict begin
        /bd {bind def} bind def
        /q {gsave} bd /Q {grestore} bd /cm {concat} bd
        /w {setlinewidth} bd /J {setlinecap} bd /j {setlinejoin} bd /M {setmiterlimit} bd
        /d {setdash} bd /i {setflat} bd
        /m {moveto} bd /l {lineto} bd /c {curveto} bd
        /v {currentpoint 6 2 roll curveto} bd /y {2 copy curveto} bd /h {closepath} bd
        /re {4 2 roll moveto exch dup 0 rlineto exch 0 exch rlineto neg 0 rlineto closepath} bd
        /S {stroke} bd /F {fill} bd /F* {eofill} bd /N {newpath} bd /W {clip} bd /W* {eoclip} bd
        % text: [Trm] TS  font x (codes) [advances] X  ...  TE ; stroked: font x (code) XC ... TK
        /TS {gsave newpath /FileeTctm matrix currentmatrix def concat} bd
        /TE {grestore} bd
        /X {3 -1 roll 0 moveto 3 -1 roll setfont xshow} bd
        /XC {exch 0 moveto exch setfont false charpath} bd
        /TK {FileeTctm setmatrix stroke} bd
        % pictures read their data through FileeData, which is read to its end afterwards
        /FileeImage {image FileeData flushfile} bd
        /FileeImageMask {imagemask FileeData flushfile} bd
        % patterns are placed in the page's default space
        /FileePattern {gsave FileePageCTM setmatrix makepattern grestore setpattern} bd
        /FileePatternU {gsave FileePageCTM setmatrix makepattern grestore} bd
        end
        %%EndResource

        """;
}
