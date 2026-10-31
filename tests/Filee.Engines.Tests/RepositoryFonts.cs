// The Noto fonts build/fetch-fonts.ps1 puts into src/Filee.App/Assets/Fonts as a font collection of their own, so layout
// tests (ParagraphLayoutTests, PageComposerTests) get the same fonts on every computer. CI fetches them; tests skip
// without them.

using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

/// <summary>The Noto fonts of src/Filee.App/Assets/Fonts as the only fonts there are.</summary>
internal sealed class RepositoryFonts : IFontCollection
{
    public const string Latin = "Noto Sans";
    public const string Korean = "Noto Sans KR";
    public const string Arabic = "Noto Sans Arabic";
    public const string Thai = "Noto Sans Thai";
    public const string Devanagari = "Noto Sans Devanagari";

    private readonly Dictionary<string, List<FontFace>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PageFont> _fonts = new(StringComparer.OrdinalIgnoreCase);

    public static RepositoryFonts Load()
    {
        var fonts = new RepositoryFonts();
        var folder = Folder();
        if (folder is null)
            return fonts;
        (string File, string Family, int Weight)[] files =
        [
            ("NotoSans-Regular.ttf", Latin, 400), ("NotoSans-Bold.ttf", Latin, 700),
            ("NotoSansKR-Regular.otf", Korean, 400), ("NotoSansKR-Bold.otf", Korean, 700),
            ("NotoSansArabic-Regular.ttf", Arabic, 400), ("NotoSansThai-Regular.ttf", Thai, 400),
            ("NotoSansDevanagari-Regular.ttf", Devanagari, 400),
        ];
        foreach (var (file, family, weight) in files)
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path))
                continue;
            var font = PageFont.FromFile(path);
            var typeface = font.Typeface;
            if (weight == 400)
                fonts._fonts[family] = font;
            if (!fonts._families.TryGetValue(family, out var faces))
                fonts._families[family] = faces = [];
            faces.Add(new FontFace(family, weight, false, c => typeface.ContainsGlyph(c), () => font));
        }
        return fonts;
    }

    public bool Has(string family) => _fonts.ContainsKey(family);

    public PageFont Font(string family) => _fonts[family];

    public IReadOnlyList<FontFace> Family(string name) => _families.GetValueOrDefault(name) ?? [];

    public FontFace? ForCharacter(int codePoint, FontStyle style, string? language) =>
        _families.Values.SelectMany(f => f).FirstOrDefault(f => f.Weight == 400 && f.Has(codePoint));

    private static string? Folder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                return Path.Combine(dir.FullName, "src", "Filee.App", "Assets", "Fonts");
        }
        return null;
    }
}
