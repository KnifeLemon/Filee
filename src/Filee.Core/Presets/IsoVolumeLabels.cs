// Label limits shared by the preset editor, CLI and ISO writers; automatic names may be adapted, custom labels never are.

using System.Text;

namespace Filee.Core.Presets;

/// <summary>Volume labels that survive the selected writer's shortest descriptor field without truncation.</summary>
public static class IsoVolumeLabels
{
    /// <summary>The translated validation reason, or null for an automatic or valid custom label.</summary>
    public static string? ErrorKey(string? label, IsoFileSystem filesystem)
    {
        if (string.IsNullOrEmpty(label)) return null;
        if (label != label.Trim() || label.Any(char.IsControl)) return "error.iso_label";
        if (filesystem == IsoFileSystem.Joliet)
            return label.Length <= 16 && label.All(JolietCharacter) ? null : "error.iso_label";
        if (filesystem != IsoFileSystem.Udf201) return "error.iso_label";
        try
        {
            var bytes = label.Any(c => c > 255) ? new UnicodeEncoding(true, false, true).GetByteCount(label) : label.Length;
            return bytes <= 30 ? null : "error.iso_label";
        }
        catch (EncoderFallbackException) { return "error.iso_label"; }
    }

    /// <summary>Keeps representable source characters and truncates at the selected descriptor's limit.</summary>
    public static string FromName(string name, IsoFileSystem filesystem)
    {
        var label = new StringBuilder();
        if (filesystem == IsoFileSystem.Joliet)
        {
            foreach (var c in name)
                if (JolietCharacter(c)) label.Append(c);
            var text = label.ToString().Trim();
            var fittedJoliet = text[..Math.Min(16, text.Length)].TrimEnd();
            return fittedJoliet.Length == 0 ? "FILEE" : fittedJoliet;
        }
        foreach (var rune in name.EnumerateRunes())
            if (!Rune.IsControl(rune)) label.Append(rune.ToString());
        var unicode = label.ToString().Trim();
        var limit = unicode.Any(c => c > 255) ? 15 : 30;
        var length = Math.Min(limit, unicode.Length);
        if (length > 0 && char.IsHighSurrogate(unicode[length - 1])) length--;
        var fitted = unicode[..length].TrimEnd();
        return fitted.Length == 0 ? "FILEE" : fitted;
    }

    /// <summary>ISO 9660's primary descriptor needs its own ASCII-compatible representation of the Joliet label.</summary>
    public static string PrimaryLabel(string label) => new(label.ToUpperInvariant()
        .Select(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_').ToArray());

    private static bool JolietCharacter(char c) => !char.IsControl(c) && !char.IsSurrogate(c) && c is not ('*' or '/' or ':' or ';' or '?' or '\\');
}
