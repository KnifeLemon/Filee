// Biological sequence alignments in NEXUS (.nex/.nxs/.nexus), FASTA (.fasta/.fa/.fas/.fna/.faa) and PHYLIP
// (.phy/.phylip), issue #1798. Every format is read into an ordered list of (name, sequence) records with an optional
// datatype (DNA, RNA, protein or standard) and written from it. Output is UTF-8 without BOM and LF line endings.
// Files are never trusted: scanning is iterative, NEXUS comment nesting is bounded, and malformed inputs or unaligned
// sequences targeting PHYLIP/NEXUS are refused with ConversionRefusedException.

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;

namespace Filee.Engines.Data;

/// <summary>Biological alphabet carried by a <see cref="SequenceAlignment"/> when the source states one.</summary>
internal enum SequenceDataType
{
    Unknown,
    Dna,
    Rna,
    Protein,
    Standard,
}

/// <summary>One named sequence inside a <see cref="SequenceAlignment"/>.</summary>
internal sealed record SequenceRecord(string Name, string Sequence);

/// <summary>
/// In-memory representation and readers/writers for NEXUS, FASTA and PHYLIP sequence alignments.
/// </summary>
internal sealed class SequenceAlignment(IReadOnlyList<SequenceRecord> sequences, SequenceDataType dataType = SequenceDataType.Unknown)
{
    private const int MaxCommentDepth = 64;
    private const int FastaLineWidth = 60;
    private const int StrictPhylipNameWidth = 10;

    /// <summary>Sequences in their original order.</summary>
    public IReadOnlyList<SequenceRecord> Sequences { get; } = sequences;

    /// <summary>Declared sequence datatype (from NEXUS FORMAT), or <see cref="SequenceDataType.Unknown"/>.</summary>
    public SequenceDataType DataType { get; } = dataType;

    /// <summary>Reads an alignment from <paramref name="text"/> according to <paramref name="format"/> (<c>nex</c>, <c>fasta</c> or <c>phy</c>).</summary>
    public static SequenceAlignment Read(string text, string format, CancellationToken ct = default) => format switch
    {
        "nex" => ReadNexus(text, ct),
        "fasta" => ReadFasta(text, ct),
        "phy" => ReadPhylip(text, ct),
        _ => throw new NotSupportedException($"{format} is not a supported sequence alignment format."),
    };

    /// <summary>Serializes <paramref name="alignment"/> to <paramref name="format"/> (<c>nex</c>, <c>fasta</c> or <c>phy</c>).</summary>
    public static string Write(SequenceAlignment alignment, string format, CancellationToken ct = default) => format switch
    {
        "nex" => WriteNexus(alignment, ct),
        "fasta" => WriteFasta(alignment, ct),
        "phy" => WritePhylip(alignment, ct),
        _ => throw new NotSupportedException($"Sequence alignment → {format} is not supported."),
    };

    private static ConversionRefusedException Refuse(string reason) =>
        new("error.unsupported_source", reason);

    // ───────────────────────── NEXUS reader ─────────────────────────

    public static SequenceAlignment ReadNexus(string text, CancellationToken ct = default)
    {
        text = StripBom(text);
        var pos = 0;
        SkipWhitespaceOnly(text, ref pos);
        if (!MatchesKeywordAt(text, pos, "#NEXUS")
            || (pos + 6 < text.Length && !char.IsWhiteSpace(text[pos + 6]) && text[pos + 6] is not ('[' or ';')))
            throw Refuse("Not a NEXUS file: missing #NEXUS header.");
        pos += 6;

        SequenceAlignment? parsed = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadNexusToken(text, ref pos, ct, out var token))
                break;
            if (!token.IsQuoted && token.Value.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadNexusToken(text, ref pos, ct, out var blockName))
                    throw Refuse("Unexpected end of NEXUS file after BEGIN.");
                if (!TryReadNexusToken(text, ref pos, ct, out var semi) || semi.IsQuoted || semi.Value != ";")
                    throw Refuse($"Expected ';' after BEGIN {blockName.Value}.");

                if (parsed is null
                    && !blockName.IsQuoted
                    && (blockName.Value.Equals("DATA", StringComparison.OrdinalIgnoreCase)
                        || blockName.Value.Equals("CHARACTERS", StringComparison.OrdinalIgnoreCase)))
                {
                    parsed = ReadNexusDataBlock(text, ref pos, ct);
                    if (parsed is not null)
                        return parsed;
                }
                else
                    SkipNexusBlock(text, ref pos, ct);
            }
        }

        throw Refuse("NEXUS file contains no DATA or CHARACTERS block with a MATRIX.");
    }

    private static SequenceAlignment? ReadNexusDataBlock(string text, ref int pos, CancellationToken ct)
    {
        int? ntax = null;
        int? nchar = null;
        var dataType = SequenceDataType.Unknown;
        char? matchChar = null;
        var interleave = false;
        SequenceAlignment? alignment = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadNexusToken(text, ref pos, ct, out var cmd))
                break;
            if (cmd.IsQuoted)
            {
                SkipNexusStatement(text, ref pos, ct);
                continue;
            }
            if (cmd.Value == ";")
                continue;
            if (cmd.Value.Equals("END", StringComparison.OrdinalIgnoreCase)
                || cmd.Value.Equals("ENDBLOCK", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadNexusToken(text, ref pos, ct, out var endSemi) && (endSemi.IsQuoted || endSemi.Value != ";"))
                    SkipNexusStatement(text, ref pos, ct);
                break;
            }
            if (cmd.Value.Equals("DIMENSIONS", StringComparison.OrdinalIgnoreCase))
            {
                ReadNexusDimensions(text, ref pos, ct, ref ntax, ref nchar);
                continue;
            }
            if (cmd.Value.Equals("FORMAT", StringComparison.OrdinalIgnoreCase))
            {
                ReadNexusFormat(text, ref pos, ct, ref dataType, ref matchChar, ref interleave);
                continue;
            }
            if (cmd.Value.Equals("MATRIX", StringComparison.OrdinalIgnoreCase))
            {
                if (alignment is null)
                    alignment = ReadNexusMatrix(text, ref pos, ntax, nchar, interleave, matchChar, dataType, ct);
                else
                    SkipNexusStatement(text, ref pos, ct);
                continue;
            }
            SkipNexusStatement(text, ref pos, ct);
        }

        return alignment;
    }

    private static void ReadNexusDimensions(string text, ref int pos, CancellationToken ct, ref int? ntax, ref int? nchar)
    {
        var tokens = ReadNexusStatementTokens(text, ref pos, ct);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsQuoted)
                continue;
            if (tokens[i].Value.Equals("NTAX", StringComparison.OrdinalIgnoreCase))
                ntax = ReadPositiveIntOption(tokens, ref i, "NTAX");
            else if (tokens[i].Value.Equals("NCHAR", StringComparison.OrdinalIgnoreCase))
                nchar = ReadPositiveIntOption(tokens, ref i, "NCHAR");
        }
    }

    private static int ReadPositiveIntOption(List<NexusToken> tokens, ref int index, string option)
    {
        if (index + 2 >= tokens.Count || tokens[index + 1].IsQuoted || tokens[index + 1].Value != "=")
            throw Refuse($"Malformed NEXUS {option} declaration.");
        var raw = tokens[index + 2].Value;
        index += 2;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw Refuse($"Invalid NEXUS {option} value '{raw}'.");
        return value;
    }

    private static void ReadNexusFormat(
        string text,
        ref int pos,
        CancellationToken ct,
        ref SequenceDataType dataType,
        ref char? matchChar,
        ref bool interleave)
    {
        var tokens = ReadNexusStatementTokens(text, ref pos, ct);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsQuoted)
                continue;
            var key = tokens[i].Value;
            if (key.Equals("DATATYPE", StringComparison.OrdinalIgnoreCase))
            {
                var value = ReadOptionValue(tokens, ref i, "DATATYPE");
                dataType = value.ToUpperInvariant() switch
                {
                    "DNA" or "NUCLEOTIDE" => SequenceDataType.Dna,
                    "RNA" => SequenceDataType.Rna,
                    "PROTEIN" => SequenceDataType.Protein,
                    "STANDARD" => SequenceDataType.Standard,
                    _ => SequenceDataType.Unknown,
                };
            }
            else if (key.Equals("MATCHCHAR", StringComparison.OrdinalIgnoreCase))
            {
                var value = ReadOptionValue(tokens, ref i, "MATCHCHAR");
                if (value.Length != 1)
                    throw Refuse($"Invalid NEXUS MATCHCHAR value '{value}'.");
                matchChar = value[0];
            }
            else if (key.Equals("MISSING", StringComparison.OrdinalIgnoreCase)
                || key.Equals("GAP", StringComparison.OrdinalIgnoreCase))
            {
                var value = ReadOptionValue(tokens, ref i, key);
                if (value.Length != 1)
                    throw Refuse($"Invalid NEXUS {key.ToUpperInvariant()} value '{value}'.");
            }
            else if (key.Equals("INTERLEAVE", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Count && !tokens[i + 1].IsQuoted && tokens[i + 1].Value == "=")
                {
                    var value = ReadOptionValue(tokens, ref i, "INTERLEAVE");
                    if (value.Equals("YES", StringComparison.OrdinalIgnoreCase))
                        interleave = true;
                    else if (value.Equals("NO", StringComparison.OrdinalIgnoreCase))
                        interleave = false;
                    else
                        throw Refuse($"Invalid NEXUS INTERLEAVE value '{value}'.");
                }
                else
                    interleave = true;
            }
            else if (key.Equals("NOINTERLEAVE", StringComparison.OrdinalIgnoreCase))
                interleave = false;
        }
    }

    private static string ReadOptionValue(List<NexusToken> tokens, ref int index, string option)
    {
        if (index + 2 >= tokens.Count || tokens[index + 1].IsQuoted || tokens[index + 1].Value != "=")
            throw Refuse($"Malformed NEXUS {option.ToUpperInvariant()} option.");
        index += 2;
        return tokens[index].Value;
    }

    private static SequenceAlignment ReadNexusMatrix(
        string text,
        ref int pos,
        int? ntax,
        int? nchar,
        bool interleave,
        char? matchChar,
        SequenceDataType dataType,
        CancellationToken ct)
    {
        var ordered = new List<(string Name, StringBuilder Seq)>();
        var byName = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);

        if (interleave || !nchar.HasValue)
            ReadNexusLineMatrix(text, ref pos, interleave, ordered, byName, ct);
        else
            ReadNexusSequentialMatrix(text, ref pos, nchar.Value, ordered, byName, ct);

        if (ordered.Count == 0 || ordered.All(entry => entry.Seq.Length == 0))
            throw Refuse("NEXUS MATRIX is empty.");
        if (ntax.HasValue && ordered.Count != ntax.Value)
            throw Refuse($"NEXUS NTAX mismatch: declared {ntax.Value}, found {ordered.Count}.");
        if (nchar.HasValue)
        {
            foreach (var (name, seq) in ordered)
                if (seq.Length != nchar.Value)
                    throw Refuse($"NEXUS NCHAR mismatch for '{name}': declared {nchar.Value}, found {seq.Length}.");
        }

        if (matchChar.HasValue)
        {
            var reference = ordered[0].Seq;
            var symbol = matchChar.Value;
            for (var i = 1; i < ordered.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var seq = ordered[i].Seq;
                for (var j = 0; j < seq.Length; j++)
                {
                    if (seq[j] == symbol)
                    {
                        if (j >= reference.Length)
                            throw Refuse($"NEXUS MATCHCHAR '{symbol}' in '{ordered[i].Name}' refers past the end of the first sequence.");
                        seq[j] = reference[j];
                    }
                }
            }
        }

        var records = ordered.Select(entry => new SequenceRecord(entry.Name, entry.Seq.ToString())).ToList();
        return new SequenceAlignment(records, dataType);
    }

    private static void ReadNexusLineMatrix(
        string text,
        ref int pos,
        bool interleave,
        List<(string Name, StringBuilder Seq)> ordered,
        Dictionary<string, StringBuilder> byName,
        CancellationToken ct)
    {
        var ended = false;
        while (!ended)
        {
            ct.ThrowIfCancellationRequested();
            SkipNexusTrivia(text, ref pos, ct);
            if (pos >= text.Length)
                throw Refuse("NEXUS MATRIX is missing its terminating ';'.");
            if (text[pos] == ';')
            {
                pos++;
                return;
            }

            var name = ReadNexusTaxonName(text, ref pos, ct);
            if (!byName.TryGetValue(name, out var seq))
            {
                seq = new StringBuilder();
                ordered.Add((name, seq));
                byName[name] = seq;
            }
            else if (!interleave)
                throw Refuse($"Duplicate taxon name '{name}' in non-interleaved NEXUS MATRIX.");

            while (pos < text.Length)
            {
                var c = text[pos];
                if (c is '\r' or '\n')
                    break;
                if (c == ';')
                {
                    pos++;
                    ended = true;
                    break;
                }
                if (c == '[')
                {
                    SkipNexusComment(text, ref pos, ct);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                    continue;
                }
                if (c is '{' or '(' or '}' or ')')
                    throw Refuse("NEXUS polymorphism or uncertainty notation ({...} / (...)) is not supported.");
                if (c == '\'')
                    throw Refuse("Unexpected quote inside NEXUS MATRIX sequence data.");
                seq.Append(c);
                pos++;
            }
        }
    }

    private static void ReadNexusSequentialMatrix(
        string text,
        ref int pos,
        int nchar,
        List<(string Name, StringBuilder Seq)> ordered,
        Dictionary<string, StringBuilder> byName,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SkipNexusTrivia(text, ref pos, ct);
            if (pos >= text.Length)
                throw Refuse("NEXUS MATRIX is missing its terminating ';'.");
            if (text[pos] == ';')
            {
                pos++;
                return;
            }

            var name = ReadNexusTaxonName(text, ref pos, ct);
            if (byName.ContainsKey(name))
                throw Refuse($"Duplicate taxon name '{name}' in sequential NEXUS MATRIX.");

            var seq = new StringBuilder(Math.Min(nchar, 1024));
            while (seq.Length < nchar)
            {
                ct.ThrowIfCancellationRequested();
                if (pos >= text.Length)
                    throw Refuse("NEXUS MATRIX is missing its terminating ';'.");
                var c = text[pos];
                if (c == ';')
                    throw Refuse($"NEXUS NCHAR mismatch for '{name}': declared {nchar}, found {seq.Length}.");
                if (c == '[')
                {
                    SkipNexusComment(text, ref pos, ct);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                    continue;
                }
                if (c is '{' or '(' or '}' or ')')
                    throw Refuse("NEXUS polymorphism or uncertainty notation ({...} / (...)) is not supported.");
                if (c == '\'')
                    throw Refuse("Unexpected quote inside NEXUS MATRIX sequence data.");
                seq.Append(c);
                pos++;
            }

            if (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('[' or ';'))
            {
                if (text[pos] is '{' or '(' or '}' or ')')
                    throw Refuse("NEXUS polymorphism or uncertainty notation ({...} / (...)) is not supported.");
                throw Refuse($"NEXUS sequence for '{name}' is longer than declared NCHAR={nchar}.");
            }

            ordered.Add((name, seq));
            byName[name] = seq;
        }
    }

    private static string ReadNexusTaxonName(string text, ref int pos, CancellationToken ct)
    {
        if (text[pos] == '\'')
        {
            var quoted = ReadNexusQuotedToken(text, ref pos, ct);
            if (quoted.Length == 0)
                throw Refuse("Empty quoted taxon name in NEXUS MATRIX.");
            return quoted;
        }
        if (text[pos] is '{' or '(' or '}' or ')')
            throw Refuse("NEXUS polymorphism or uncertainty notation ({...} / (...)) is not supported.");

        var start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('[' or ';' or '\'' or '{' or '(' or '}' or ')'))
            pos++;
        if (pos == start)
            throw Refuse("Malformed taxon name in NEXUS MATRIX.");
        // NEXUS specification: underscores in unquoted tokens stand for spaces.
        return text[start..pos].Replace('_', ' ');
    }

    private static void SkipNexusBlock(string text, ref int pos, CancellationToken ct)
    {
        var atStatementStart = true;
        while (TryReadNexusToken(text, ref pos, ct, out var token))
        {
            ct.ThrowIfCancellationRequested();
            if (!token.IsQuoted && token.Value == ";")
            {
                atStatementStart = true;
                continue;
            }
            if (atStatementStart
                && !token.IsQuoted
                && (token.Value.Equals("END", StringComparison.OrdinalIgnoreCase)
                    || token.Value.Equals("ENDBLOCK", StringComparison.OrdinalIgnoreCase)))
            {
                if (TryReadNexusToken(text, ref pos, ct, out var next) && (next.IsQuoted || next.Value != ";"))
                    SkipNexusStatement(text, ref pos, ct);
                return;
            }
            atStatementStart = false;
        }
    }

    private static void SkipNexusStatement(string text, ref int pos, CancellationToken ct)
    {
        while (TryReadNexusToken(text, ref pos, ct, out var token))
        {
            ct.ThrowIfCancellationRequested();
            if (!token.IsQuoted && token.Value == ";")
                return;
        }
    }

    private static List<NexusToken> ReadNexusStatementTokens(string text, ref int pos, CancellationToken ct)
    {
        var tokens = new List<NexusToken>();
        while (TryReadNexusToken(text, ref pos, ct, out var token))
        {
            ct.ThrowIfCancellationRequested();
            if (!token.IsQuoted && token.Value == ";")
                return tokens;
            tokens.Add(token);
        }
        throw Refuse("Unterminated NEXUS statement: missing ';'.");
    }

    private static bool TryReadNexusToken(string text, ref int pos, CancellationToken ct, out NexusToken token)
    {
        SkipNexusTrivia(text, ref pos, ct);
        if (pos >= text.Length)
        {
            token = default;
            return false;
        }

        var c = text[pos];
        if (c == '\'')
        {
            token = new NexusToken(ReadNexusQuotedToken(text, ref pos, ct), IsQuoted: true);
            return true;
        }
        if (c == '"')
        {
            token = new NexusToken(ReadNexusDoubleQuotedToken(text, ref pos, ct), IsQuoted: true);
            return true;
        }
        if (IsNexusPunctuation(c))
        {
            pos++;
            token = new NexusToken(c.ToString(), IsQuoted: false);
            return true;
        }

        var start = pos;
        while (pos < text.Length
            && !char.IsWhiteSpace(text[pos])
            && text[pos] is not ('[' or '\'' or '"')
            && !IsNexusPunctuation(text[pos]))
            pos++;
        token = new NexusToken(text[start..pos], IsQuoted: false);
        return true;
    }

    private static string ReadNexusQuotedToken(string text, ref int pos, CancellationToken ct)
    {
        pos++; // opening '
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var c = text[pos++];
            if (c == '\'')
            {
                if (pos < text.Length && text[pos] == '\'')
                {
                    sb.Append('\'');
                    pos++;
                    continue;
                }
                return sb.ToString();
            }
            sb.Append(c);
        }
        throw Refuse("Unterminated quoted token in NEXUS file.");
    }

    private static string ReadNexusDoubleQuotedToken(string text, ref int pos, CancellationToken ct)
    {
        pos++; // opening "
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var c = text[pos++];
            if (c == '"')
                return sb.ToString();
            sb.Append(c);
        }
        throw Refuse("Unterminated double-quoted string in NEXUS file.");
    }

    private static void SkipNexusTrivia(string text, ref int pos, CancellationToken ct)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos]))
            {
                pos++;
                continue;
            }
            if (text[pos] == '[')
            {
                SkipNexusComment(text, ref pos, ct);
                continue;
            }
            break;
        }
    }

    private static void SkipNexusComment(string text, ref int pos, CancellationToken ct)
    {
        pos++; // opening [
        var depth = 1;
        while (pos < text.Length && depth > 0)
        {
            ct.ThrowIfCancellationRequested();
            var c = text[pos++];
            if (c == '[')
            {
                depth++;
                if (depth > MaxCommentDepth)
                    throw Refuse($"NEXUS comments are nested deeper than {MaxCommentDepth} levels.");
            }
            else if (c == ']')
                depth--;
        }
        if (depth > 0)
            throw Refuse("Unterminated NEXUS comment.");
    }

    private static bool IsNexusPunctuation(char c) => c is ';' or '=' or '(' or ')' or '{' or '}' or ',';

    private static void SkipWhitespaceOnly(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            pos++;
    }

    private static bool MatchesKeywordAt(string text, int pos, string keyword) =>
        pos + keyword.Length <= text.Length
        && text.AsSpan(pos, keyword.Length).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase);

    private readonly record struct NexusToken(string Value, bool IsQuoted);

    // ───────────────────────── FASTA reader ─────────────────────────

    public static SequenceAlignment ReadFasta(string text, CancellationToken ct = default)
    {
        text = StripBom(text);
        var records = new List<SequenceRecord>();
        string? currentName = null;
        StringBuilder? currentSeq = null;

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            var trimmedStart = line.TrimStart();
            if (trimmedStart.Length == 0 || trimmedStart[0] == ';')
                continue;

            if (trimmedStart[0] == '>')
            {
                if (currentName is not null)
                    records.Add(new SequenceRecord(currentName, currentSeq!.ToString()));
                currentName = trimmedStart[1..].Trim();
                if (currentName.Length == 0)
                    throw Refuse("FASTA record has an empty '>' header.");
                currentSeq = new StringBuilder();
                continue;
            }

            if (currentName is null)
                throw Refuse("Not a FASTA file: sequence data appears before the first '>' header.");

            AppendNonWhitespace(currentSeq!, line);
        }

        if (currentName is not null)
            records.Add(new SequenceRecord(currentName, currentSeq!.ToString()));

        if (records.Count == 0)
            throw Refuse("Not a FASTA file: no '>' sequence records found.");

        return new SequenceAlignment(records);
    }

    // ───────────────────────── PHYLIP reader ─────────────────────────

    public static SequenceAlignment ReadPhylip(string text, CancellationToken ct = default)
    {
        text = StripBom(text);
        var lines = new List<string>();
        using (var reader = new StringReader(text))
        {
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
        }

        if (lines.Count < 2)
            throw Refuse("Not a PHYLIP file: missing header or sequence rows.");

        var headerParts = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (headerParts.Length != 2
            || !int.TryParse(headerParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ntax)
            || !int.TryParse(headerParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var nchar)
            || ntax <= 0
            || nchar <= 0)
            throw Refuse("Invalid PHYLIP header: expected '<ntax> <nchar>'.");

        var bodyLines = lines.Count - 1;
        if (bodyLines < ntax)
            throw Refuse($"PHYLIP taxon count mismatch: header declares {ntax}, found {bodyLines} rows.");

        // Try interleaved / single-line sequential first (relaxed, then strict 10-char), then multi-line sequential.
        if (TryReadPhylipInterleaved(lines, ntax, nchar, forceStrict: false, ct, out var records)
            || TryReadPhylipInterleaved(lines, ntax, nchar, forceStrict: true, ct, out records)
            || TryReadPhylipSequential(lines, ntax, nchar, forceStrict: false, ct, out records)
            || TryReadPhylipSequential(lines, ntax, nchar, forceStrict: true, ct, out records))
            return new SequenceAlignment(records!);

        throw Refuse($"PHYLIP alignment does not match declared dimensions ({ntax} taxa × {nchar} characters).");
    }

    private static bool TryReadPhylipInterleaved(
        List<string> lines,
        int ntax,
        int nchar,
        bool forceStrict,
        CancellationToken ct,
        out List<SequenceRecord>? result)
    {
        result = null;
        var bodyLines = lines.Count - 1;
        if (bodyLines % ntax != 0)
            return false;

        var names = new string[ntax];
        var seqs = new StringBuilder[ntax];
        for (var i = 0; i < ntax; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!TrySplitPhylipRow(lines[1 + i], forceStrict, out var name, out var chunk))
                return false;
            names[i] = name;
            var sb = new StringBuilder(Math.Min(nchar, 1024));
            AppendNonWhitespace(sb, chunk);
            seqs[i] = sb;
        }

        for (var lineIndex = 1 + ntax; lineIndex < lines.Count; lineIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var taxon = (lineIndex - 1) % ntax;
            AppendNonWhitespace(seqs[taxon], lines[lineIndex]);
        }

        for (var i = 0; i < ntax; i++)
            if (seqs[i].Length != nchar)
                return false;

        result = new List<SequenceRecord>(ntax);
        for (var i = 0; i < ntax; i++)
            result.Add(new SequenceRecord(names[i], seqs[i].ToString()));
        return true;
    }

    private static bool TryReadPhylipSequential(
        List<string> lines,
        int ntax,
        int nchar,
        bool forceStrict,
        CancellationToken ct,
        out List<SequenceRecord>? result)
    {
        result = null;
        var index = 1;
        var records = new List<SequenceRecord>(ntax);

        for (var i = 0; i < ntax; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (index >= lines.Count)
                return false;
            if (!TrySplitPhylipRow(lines[index++], forceStrict, out var name, out var chunk))
                return false;

            var sb = new StringBuilder(Math.Min(nchar, 1024));
            AppendNonWhitespace(sb, chunk);
            while (sb.Length < nchar && index < lines.Count)
            {
                ct.ThrowIfCancellationRequested();
                AppendNonWhitespace(sb, lines[index++]);
            }
            if (sb.Length != nchar)
                return false;
            records.Add(new SequenceRecord(name, sb.ToString()));
        }

        if (index != lines.Count)
            return false;

        result = records;
        return true;
    }

    private static bool TrySplitPhylipRow(string line, bool forceStrict, out string name, out ReadOnlySpan<char> sequence)
    {
        if (!forceStrict)
        {
            // Relaxed PHYLIP: leading whitespace skipped, then name, then whitespace, then sequence.
            var start = 0;
            while (start < line.Length && char.IsWhiteSpace(line[start]))
                start++;
            var sep = start;
            while (sep < line.Length && !char.IsWhiteSpace(line[sep]))
                sep++;
            if (sep > start && sep < line.Length)
            {
                var seqStart = sep;
                while (seqStart < line.Length && char.IsWhiteSpace(line[seqStart]))
                    seqStart++;
                if (seqStart < line.Length)
                {
                    name = line[start..sep];
                    sequence = line.AsSpan(seqStart);
                    return true;
                }
            }
        }

        // Strict PHYLIP: first 10 columns hold the name, sequence follows immediately (even without a space).
        if (line.Length <= StrictPhylipNameWidth)
        {
            name = "";
            sequence = default;
            return false;
        }
        name = line[..StrictPhylipNameWidth].Trim();
        sequence = line.AsSpan(StrictPhylipNameWidth);
        return name.Length > 0;
    }

    // ───────────────────────── Writers ─────────────────────────

    public static string WriteFasta(SequenceAlignment alignment, CancellationToken ct = default)
    {
        if (alignment.Sequences.Count == 0)
            throw Refuse("Cannot write FASTA: alignment contains no sequences.");

        var sb = new StringBuilder();
        foreach (var record in alignment.Sequences)
        {
            ct.ThrowIfCancellationRequested();
            var name = CollapseLineBreaks(record.Name);
            if (name.Length == 0)
                throw Refuse("Cannot write FASTA: a sequence has an empty name.");
            sb.Append('>').Append(name).Append('\n');
            var seq = record.Sequence;
            for (var offset = 0; offset < seq.Length; offset += FastaLineWidth)
            {
                var length = Math.Min(FastaLineWidth, seq.Length - offset);
                sb.Append(seq, offset, length).Append('\n');
            }
        }
        return sb.ToString();
    }

    public static string WritePhylip(SequenceAlignment alignment, CancellationToken ct = default)
    {
        var nchar = RequireAlignedLength(alignment, "PHYLIP");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append(alignment.Sequences.Count.ToString(CultureInfo.InvariantCulture))
          .Append(' ')
          .Append(nchar.ToString(CultureInfo.InvariantCulture))
          .Append('\n');

        foreach (var record in alignment.Sequences)
        {
            ct.ThrowIfCancellationRequested();
            var sanitized = SanitizePhylipName(record.Name);
            if (sanitized.Length == 0)
                throw Refuse("Cannot write PHYLIP: a sequence has an empty name.");
            if (!seen.Add(sanitized))
                throw Refuse($"Duplicate sequence name '{sanitized}' after sanitizing for PHYLIP.");
            sb.Append(sanitized).Append("  ").Append(record.Sequence).Append('\n');
        }
        return sb.ToString();
    }

    public static string WriteNexus(SequenceAlignment alignment, CancellationToken ct = default)
    {
        var nchar = RequireAlignedLength(alignment, "NEXUS");
        var dataType = alignment.DataType == SequenceDataType.Unknown
            ? GuessDataType(alignment.Sequences, ct)
            : alignment.DataType;
        var dataTypeKeyword = dataType switch
        {
            SequenceDataType.Dna => "DNA",
            SequenceDataType.Rna => "RNA",
            SequenceDataType.Protein => "PROTEIN",
            SequenceDataType.Standard => "STANDARD",
            _ => "DNA",
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.Append("#NEXUS\n")
          .Append("BEGIN DATA;\n")
          .Append("  DIMENSIONS NTAX=")
          .Append(alignment.Sequences.Count.ToString(CultureInfo.InvariantCulture))
          .Append(" NCHAR=")
          .Append(nchar.ToString(CultureInfo.InvariantCulture))
          .Append(";\n")
          .Append("  FORMAT DATATYPE=")
          .Append(dataTypeKeyword)
          .Append(" MISSING=? GAP=-;\n")
          .Append("  MATRIX\n");

        foreach (var record in alignment.Sequences)
        {
            ct.ThrowIfCancellationRequested();
            var name = CollapseLineBreaks(record.Name);
            if (name.Length == 0)
                throw Refuse("Cannot write NEXUS: a sequence has an empty name.");
            var formatted = FormatNexusName(name);
            var logicalKey = formatted.StartsWith('\'') ? name : name.Replace('_', ' ');
            if (!seen.Add(logicalKey))
                throw Refuse($"Duplicate sequence name '{name}' for NEXUS.");
            sb.Append("    ").Append(formatted).Append(' ').Append(record.Sequence).Append('\n');
        }

        sb.Append("  ;\nEND;\n");
        return sb.ToString();
    }

    private static int RequireAlignedLength(SequenceAlignment alignment, string formatName)
    {
        if (alignment.Sequences.Count == 0)
            throw Refuse($"Cannot write {formatName}: alignment contains no sequences.");
        var expected = alignment.Sequences[0].Sequence.Length;
        if (expected == 0)
            throw Refuse($"{formatName} requires non-empty sequences.");
        for (var i = 1; i < alignment.Sequences.Count; i++)
        {
            var actual = alignment.Sequences[i].Sequence.Length;
            if (actual != expected)
                throw Refuse($"{formatName} needs an alignment of equal-length sequences (found lengths {expected} and {actual}).");
        }
        return expected;
    }

    private static SequenceDataType GuessDataType(IReadOnlyList<SequenceRecord> sequences, CancellationToken ct)
    {
        var hasU = false;
        var hasT = false;
        foreach (var record in sequences)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var c in record.Sequence)
            {
                var upper = char.ToUpperInvariant(c);
                if (upper == 'U')
                    hasU = true;
                else if (upper == 'T')
                    hasT = true;
                else if (upper is not ('A' or 'C' or 'G' or 'N' or '-' or '?' or 'R' or 'Y' or 'K' or 'M' or 'S' or 'W' or 'B' or 'D' or 'H' or 'V'))
                    return SequenceDataType.Protein;
            }
        }
        return hasU && !hasT ? SequenceDataType.Rna : SequenceDataType.Dna;
    }

    private static string SanitizePhylipName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "";
        var sb = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
            sb.Append(char.IsWhiteSpace(c) ? '_' : c);
        return sb.ToString();
    }

    private static string FormatNexusName(string name)
    {
        var needsQuotes = false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                needsQuotes = true;
                break;
            }
        }
        if (!needsQuotes)
            return name;
        return "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string CollapseLineBreaks(string name) =>
        name.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void AppendNonWhitespace(StringBuilder target, ReadOnlySpan<char> source)
    {
        foreach (var c in source)
            if (!char.IsWhiteSpace(c))
                target.Append(c);
    }

    private static string StripBom(string text) => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
}
