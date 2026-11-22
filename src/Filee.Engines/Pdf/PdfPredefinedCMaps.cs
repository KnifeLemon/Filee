// The CIDs of a predefined CMap's codes (UniKS-UCS2-H, UniJIS-UTF16-V …), for PDF/A's widths of Unicode CMap fonts
// (PdfAFonts.EmbedUnicode): a CIDFont gives its widths (W, W2) by CID, and only the CMap says which CID a code is.
//
// The CMap files are Adobe's (BSD-3-Clause), which PdfPig carries as resources of its assembly to read text with
// them; Filee reads the same files from there rather than shipping a second copy. Only the CID mappings are read
// (begincidrange / begincidchar of two-byte codes, and the CMap a "usecmap" names first, as the -V CMaps use the -H
// ones); nothing of the file runs. A CMap PdfPig doesn't carry, or one that can't be read, is null and the caller falls
// back to what it knows without one.
//
// The resources are found by name: under PdfPig's prefix ("UglyToad.PdfPig.Resources.CMap.<name>"), else as any
// resource of its assembly whose name ends in ".CMap.<name>" or ".CMaps.<name>" (should a later PdfPig move them).
// Available lists what was found; a test checks the CMaps Filee needs are among them, so a PdfPig upgrade that drops
// or renames them fails there instead of quietly falling back.

using System.Collections.Concurrent;
using System.Text;

namespace Filee.Engines.Pdf;

internal static class PdfPredefinedCMaps
{
    /// <summary>Mappings one CMap may hold at most (the largest Unicode CMaps have a few thousand ranges).</summary>
    private const int MaxEntries = 100_000;

    /// <summary>Codes the ranges of one CMap may cover together at most (ranges may overlap).</summary>
    private const int MaxCodes = 1_000_000;

    /// <summary>CMaps one may use in turn ("usecmap") at most.</summary>
    private const int MaxDepth = 3;

    private const string Prefix = "UglyToad.PdfPig.Resources.CMap.";

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, int>?> Known = new(StringComparer.Ordinal);

    /// <summary>The CMap files PdfPig's assembly carries: name → resource name.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Resources = new(FindResources);

    /// <summary>Opens a CMap file by name instead of PdfPig's resources (tests: a missing or renamed resource).</summary>
    internal static readonly AsyncLocal<Func<string, Stream?>?> Open = new();

    /// <summary>The names of the CMap files Filee found among PdfPig's resources.</summary>
    internal static IReadOnlyCollection<string> Available => (IReadOnlyCollection<string>)Resources.Value.Keys;

    private static Dictionary<string, string> FindResources()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] names;
        try
        {
            names = typeof(UglyToad.PdfPig.PdfDocument).Assembly.GetManifestResourceNames();
        }
        catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException)
        {
            return found;
        }
        foreach (var resource in names.Order(StringComparer.Ordinal))
        {
            string? name = null;
            if (resource.StartsWith(Prefix, StringComparison.Ordinal))
                name = resource[Prefix.Length..];
            else if (resource.LastIndexOf(".CMap.", StringComparison.OrdinalIgnoreCase) is var at and >= 0)
                name = resource[(at + 6)..];
            else if (resource.LastIndexOf(".CMaps.", StringComparison.OrdinalIgnoreCase) is var at2 and >= 0)
                name = resource[(at2 + 7)..];
            // Names under PdfPig's own prefix win over look-alikes elsewhere.
            if (name is { Length: > 0 } && (!found.ContainsKey(name) || resource.StartsWith(Prefix, StringComparison.Ordinal)))
                found[name] = resource;
        }
        return found;
    }

    /// <summary>
    /// Two-byte code → CID of the predefined CMap <paramref name="name"/> (without the slash); null when it isn't one
    /// Filee can read.
    /// </summary>
    public static IReadOnlyDictionary<int, int>? Cids(string name) =>
        Open.Value is null ? Known.GetOrAdd(name.TrimStart('/'), static name => Read(name, 0)) : Read(name.TrimStart('/'), 0);

    private static Dictionary<int, int>? Read(string name, int depth)
    {
        if (depth > MaxDepth || name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            return null;
        byte[] data;
        try
        {
            using var stream = Open.Value is { } open ? open(name)
                : Resources.Value.TryGetValue(name, out var resource) ? typeof(UglyToad.PdfPig.PdfDocument).Assembly.GetManifestResourceStream(resource) : null;
            if (stream is null || (stream.CanSeek && stream.Length > 4 * 1024 * 1024))
                return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.Length > 4 * 1024 * 1024)
                return null;
            data = copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or BadImageFormatException)
        {
            return null;
        }
        return Parse(data, depth);
    }

    /// <summary>The CID mappings of a CMap file's text (the CMap it uses first, then its own over them).</summary>
    internal static Dictionary<int, int>? Parse(byte[] data, int depth = 0)
    {
        var cids = new Dictionary<int, int>();
        var codes = 0;
        var lexer = new PdfContentLexer(data);
        var operands = new List<object?>();
        while (lexer.Next() is { } token)
        {
            if (token is not PdfContentLexer.Operator op)
            {
                if (operands.Count > 3 * MaxEntries)
                    return null;
                operands.Add(token);
                continue;
            }
            switch (op.Name)
            {
                case "usecmap" when operands.Count > 0 && operands[^1] is PdfContentLexer.Name used:
                    if (Read(used.Value, depth + 1) is not { } inherited)
                        return null;
                    foreach (var (code, cid) in inherited)
                        cids[code] = cid;
                    break;
                case "endcidrange":
                    for (var i = 0; i + 2 < operands.Count; i += 3)
                    {
                        if (operands[i] is byte[] { Length: 2 } low && operands[i + 1] is byte[] { Length: 2 } high && operands[i + 2] is double first)
                        {
                            var (from, to) = (low[0] << 8 | low[1], high[0] << 8 | high[1]);
                            if ((codes += Math.Max(0, to - from + 1)) > MaxCodes)
                                return null;
                            for (var code = from; code <= to && cids.Count < MaxEntries; code++)
                                cids[code] = (int)first + code - from;
                        }
                    }
                    break;
                case "endcidchar":
                    for (var i = 0; i + 1 < operands.Count; i += 2)
                    {
                        if (operands[i] is byte[] { Length: 2 } code && operands[i + 1] is double cid && cids.Count < MaxEntries)
                            cids[code[0] << 8 | code[1]] = (int)cid;
                    }
                    break;
            }
            operands.Clear();
            if (cids.Count >= MaxEntries)
                return null;
        }
        return cids;
    }
}
