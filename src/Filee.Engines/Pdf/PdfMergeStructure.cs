// Carries bookmarks, destinations, links and page-backed AcroForm fields when PDFsharp imports pages.
// The extra catalog graphs are untrusted: traversal is iterative, bounded and checked before linking.

using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Filee.Engines.Pdf;

internal sealed class PdfMergeStructure(PdfDocument output, PdfMergeStructure.Limits limits)
{
    internal sealed record Limits
    {
        public int OutlineItems { get; init; } = 100_000;
        public int OutlineDepth { get; init; } = 64;
        public int StringBytes { get; init; } = 4 * 1024 * 1024;
        public int NameTreeNodes { get; init; } = 100_000;
        public int NameTreeDepth { get; init; } = 64;
        public int NamedDestinations { get; init; } = 100_000;
        public int FieldDepth { get; init; } = 64;
        public int FieldItems { get; init; } = 100_000;
        public int ResourceObjects { get; init; } = 10_000;
        public int ResourceBytes { get; init; } = 4 * 1024 * 1024;
        public int ResourceItems { get; init; } = 1_000_000;
        public int AppearanceLength { get; init; } = 4096;
        public int AppearanceBytes { get; init; } = 1024 * 1024;
        public Action? BeforeCarry { get; init; }
        public PdfMergePaperFitter.Limits? Paper { get; init; }
    }

    private sealed class Outline(PdfDictionary dictionary, bool opened)
    {
        public PdfDictionary Dictionary { get; } = dictionary;
        public bool Opened { get; } = opened;
        public List<Outline> Children { get; } = [];
        public int VisibleChildren { get; set; }
    }

    private readonly List<Outline> _outlines = [];
    // Raw byte strings have exactly the same ordinal ordering as their bytes.
    private readonly SortedDictionary<string, (PdfItem Name, PdfArray Destination)> _destinations = new(StringComparer.Ordinal);
    private readonly PdfArray _fields = new(output);
    private readonly PdfDictionary _resources = new(output);
    private readonly HashSet<string> _fieldNames = new(StringComparer.Ordinal);
    private static readonly string[] ResourceKinds = ["/Font", "/ColorSpace", "/Pattern", "/ExtGState"];
    private int _filePosition;
    private int _stringBytes;
    private bool _needAppearances;
    private IReadOnlyDictionary<PdfObjectID, PdfMergePaperFitter.Transform>? _transforms;

    internal void Carry(PdfDocument source, IReadOnlyDictionary<PdfObjectID, PdfPage> pages, CancellationToken ct,
        string? sourceName = null, PdfPage? firstPage = null,
        IReadOnlyDictionary<PdfObjectID, PdfMergePaperFitter.Transform>? transforms = null)
    {
        _transforms = transforms;
        if (++_filePosition == 1)
            limits.BeforeCarry?.Invoke();
        ct.ThrowIfCancellationRequested();
        _stringBytes = 0;
        Outline? parent = null;
        if (sourceName is not null && firstPage is not null && limits.OutlineItems >= 1 && limits.OutlineDepth >= 1)
        {
            var title = new PdfString(SourceTitle(sourceName), PdfStringEncoding.Unicode);
            if (SpendStrings(ByteCount(title)))
            {
                var dictionary = new PdfDictionary(output);
                dictionary.Elements["/Title"] = title;
                var destination = new PdfArray(output);
                destination.Elements.Add(firstPage.Reference!);
                destination.Elements.Add(new PdfName("/Fit"));
                dictionary.Elements["/Dest"] = destination;
                parent = new Outline(dictionary, true);
                _outlines.Add(parent);
            }
        }
        _lookups.Clear();
        var catalog = source.Internals.Catalog;
        var names = ReadDestinations(catalog, pages, ct);
        if (sourceName is null)
            CarryOutlines(catalog, pages, names, _outlines, 1, 0, ct);
        else if (parent is not null)
            CarryOutlines(catalog, pages, names, parent.Children, 2, 1, ct);
        CarryLinks(source, pages, names, ct);
        CarryFields(catalog, ct);
    }

    private static string SourceTitle(string sourceName)
    {
        var truncated = sourceName.Length > 1024;
        var length = truncated ? 1023 : sourceName.Length;
        if (truncated && char.IsHighSurrogate(sourceName[length - 1]) && char.IsLowSurrogate(sourceName[length]))
            length--;
        var title = new StringBuilder(1024);
        for (var i = 0; i < length; i++)
        {
            var c = sourceName[i];
            if (char.IsHighSurrogate(c) && i + 1 < length && char.IsLowSurrogate(sourceName[i + 1]))
            {
                title.Append(c).Append(sourceName[++i]);
                continue;
            }
            title.Append(char.IsSurrogate(c) ? '\uFFFD'
                : char.GetUnicodeCategory(c) == UnicodeCategory.Control ||
                  c is '\u061C' or '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069'
                    ? ' ' : c);
        }
        if (truncated)
            title.Append('\u2026');
        return title.ToString();
    }

    private static PdfItem? Value(PdfItem? item) => item is PdfReference reference ? ValueObject(reference.Value) : ValueObject(item);

    private static PdfItem? ValueObject(PdfItem? item) => item switch
    {
        PdfStringObject text => new PdfString(text.Value, text.Encoding),
        PdfIntegerObject integer => new PdfInteger(integer.Value),
        PdfRealObject real => new PdfReal(real.Value),
        PdfNameObject name => new PdfName(name.Value),
        PdfBooleanObject boolean => new PdfBoolean(boolean.Value),
        PdfNullObject => PdfNull.Value,
        _ => item,
    };

    private static PdfDictionary? Dictionary(PdfItem? item) => Value(item) as PdfDictionary;
    private static PdfArray? Array(PdfItem? item) => Value(item) as PdfArray;
    private static string? Name(PdfItem? item) => (Value(item) as PdfName)?.Value;
    private static PdfString? Text(PdfItem? item) => Value(item) as PdfString;

    private static (PdfObjectID Id, PdfObject? Direct) Identity(PdfObject item) =>
        item.Reference is { } reference ? (reference.ObjectID, null) : (default, item);

    private PdfObject? Imported(PdfObject external)
    {
        if (external.Reference is null)
            return null;
        try
        {
            return output.Internals.MapExternalObject(external);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private bool SpendStrings(int count)
    {
        if (count > limits.StringBytes - _stringBytes)
            return false;
        _stringBytes += count;
        return true;
    }

    private static string Bytes(PdfString text)
    {
        if (text.Encoding != PdfStringEncoding.Unicode && !text.Value.Any(c => c > 255))
            return text.Value;
        return "\xfe\xff" + Encoding.Latin1.GetString(Encoding.BigEndianUnicode.GetBytes(text.Value));
    }

    // One indirect string read at many places yields the same string instance, so a long one is measured
    // and looked up once. Short strings are cheap and may be shared instances such as string.Empty.
    private const int CacheLength = 256;
    private const int SharedNameBytes = 32;
    private readonly Dictionary<string, int> _byteCounts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (PdfItem Name, PdfArray Destination)?> _lookups = new(ReferenceEqualityComparer.Instance);
    // The next suffix to try for a name already taken, so later sources do not walk the taken ones again.
    private readonly Dictionary<string, int> _destinationSuffixes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, string Name), int> _resourceSuffixes = new();
    private readonly Dictionary<PdfDictionary, PdfDictionary?> _actions = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _links = new(ReferenceEqualityComparer.Instance);

    private int ByteCount(PdfString text)
    {
        if (text.Value.Length < CacheLength)
            return Count(text);
        if (!_byteCounts.TryGetValue(text.Value, out var count))
        {
            count = Count(text);
            _byteCounts.Add(text.Value, count);
        }
        return count;
    }

    private static int Count(PdfString text) =>
        text.Encoding == PdfStringEncoding.Unicode || text.Value.Any(c => c > 255)
            ? 2 + Encoding.BigEndianUnicode.GetByteCount(text.Value)
            : text.Value.Length;

    private static string DecodeName(string name)
    {
        var decoded = new StringBuilder(name.Length);
        for (var i = name.StartsWith('/') ? 1 : 0; i < name.Length; i++)
        {
            if (name[i] == '#' && i + 2 < name.Length &&
                byte.TryParse(name.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                decoded.Append((char)b);
                i += 2;
            }
            else
                decoded.Append(name[i]);
        }
        return decoded.ToString();
    }

    private static PdfString Raw(string bytes) => new(bytes, PdfStringEncoding.RawEncoding);

    private PdfArray? MapDestination(PdfItem? item, IReadOnlyDictionary<PdfObjectID, PdfPage> pages)
    {
        var source = Array(item);
        if (source is null || source.Elements.Count < 2 || source.Elements[0] is not PdfReference reference ||
            !pages.TryGetValue(reference.ObjectID, out var page))
            return null;
        var kind = Name(source.Elements[1]);
        var length = kind switch
        {
            "/XYZ" => 5,
            "/Fit" or "/FitB" => 2,
            "/FitH" or "/FitBH" or "/FitV" or "/FitBV" => 3,
            "/FitR" => 6,
            _ => -1,
        };
        if (length < 0 || source.Elements.Count > length ||
            (kind == "/FitR" && source.Elements.Count != length))
            return null;
        var values = new List<PdfItem>(length) { page.Reference!, new PdfName(kind!) };
        for (var i = 2; i < length; i++)
        {
            var value = i < source.Elements.Count ? Value(source.Elements[i]) : PdfNull.Value;
            if (value is not (PdfInteger or PdfReal) && !(value is PdfNull && kind != "/FitR"))
                return null;
            values.Add(value switch
            {
                PdfInteger integer => new PdfInteger(integer.Value),
                PdfReal real => new PdfReal(real.Value),
                _ => PdfNull.Value,
            });
        }
        var result = new PdfArray(output);
        foreach (var value in values)
            result.Elements.Add(value);
        if (_transforms is not null && _transforms.TryGetValue(reference.ObjectID, out var transform) &&
            !PdfMergePaperFitter.Destination(result, transform))
            return null;
        return result;
    }

    private Dictionary<(bool Legacy, string Bytes), string> ReadDestinations(PdfDictionary catalog,
        IReadOnlyDictionary<PdfObjectID, PdfPage> pages, CancellationToken ct)
    {
        var names = new Dictionary<(bool, string), string>();
        var count = 0;
        // Returns false when the destination is not usable; the key is only built for a usable one.
        bool Register(bool legacy, Func<string> keyOf, PdfItem? item)
        {
            if (count >= limits.NamedDestinations)
                return false;
            if (Dictionary(item) is { } dictionary)
                item = dictionary.Elements["/D"];
            if (MapDestination(item, pages) is not { } destination)
                return false;
            var key = keyOf();
            if (names.ContainsKey((legacy, key)))
                return true;
            var renamed = key;
            var suffix = _destinationSuffixes.GetValueOrDefault(key, 2);
            while (_destinations.ContainsKey(renamed))
                renamed = key + "-" + (suffix++).ToString(CultureInfo.InvariantCulture);
            if (!SpendStrings(renamed.Length))
                return true;
            // Direct short names have bounded size; long names must not be written once per link.
            PdfItem name = Raw(renamed);
            if (renamed.Length >= SharedNameBytes)
            {
                var shared = new PdfStringObject(output, renamed) { Encoding = PdfStringEncoding.RawEncoding };
                output.Internals.AddObject(shared);
                name = shared.Reference!;
            }
            names.Add((legacy, key), renamed);
            _destinations.Add(renamed, (name, destination));
            if (!ReferenceEquals(renamed, key))
                _destinationSuffixes[key] = suffix;
            count++;
            return true;
        }

        if (Dictionary(catalog.Elements["/Names"]) is { } catalogNames &&
            Dictionary(catalogNames.Elements["/Dests"]) is { } tree)
        {
            var stack = new Stack<(PdfDictionary Node, int Depth)>();
            var visited = new HashSet<(PdfObjectID Id, PdfObject? Direct)>();
            // Nodes may share one /Names or /Kids array; reading it again would multiply the work. A /Kids
            // array is read again only from a shallower depth, where children cut by the depth limit may fit.
            var expandedNames = new HashSet<(PdfObjectID Id, PdfObject? Direct)>();
            var expandedKids = new Dictionary<(PdfObjectID Id, PdfObject? Direct), int>();
            var seenKeys = new HashSet<string>(ReferenceEqualityComparer.Instance);
            stack.Push((tree, 1));
            while (stack.TryPop(out var entry))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Depth > limits.NameTreeDepth || visited.Count >= limits.NameTreeNodes ||
                    !visited.Add(Identity(entry.Node)))
                    continue;
                if (Array(entry.Node.Elements["/Names"]) is { } pairs && expandedNames.Add(Identity(pairs)))
                    for (var i = 0; i + 1 < pairs.Elements.Count; i += 2)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Keys are unique in a name tree; a long key repeated by reference would be converted
                        // and hashed again for every pair.
                        if (Text(pairs.Elements[i]) is { } key && ByteCount(key) <= limits.StringBytes - _stringBytes &&
                            (key.Value.Length < CacheLength || !seenKeys.Contains(key.Value)) &&
                            Register(false, () => Bytes(key), pairs.Elements[i + 1]) && key.Value.Length >= CacheLength)
                            seenKeys.Add(key.Value);
                    }
                if (Array(entry.Node.Elements["/Kids"]) is { } kids &&
                    (!expandedKids.TryGetValue(Identity(kids), out var depth) || entry.Depth < depth))
                {
                    expandedKids[Identity(kids)] = entry.Depth;
                    for (var i = kids.Elements.Count - 1; i >= 0; i--)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Dictionary(kids.Elements[i]) is { } child && !visited.Contains(Identity(child)))
                            stack.Push((child, entry.Depth + 1));
                    }
                }
            }
        }
        if (Dictionary(catalog.Elements["/Dests"]) is { } old)
            foreach (var key in old.Elements.Keys)
            {
                ct.ThrowIfCancellationRequested();
                // The reader has already decoded PDF name escapes in dictionary keys and PdfName.Value.
                if (key.Length - 1 <= limits.StringBytes - _stringBytes)
                    Register(true, () => key[1..], old.Elements[key]);
            }
        return names;
    }

    private PdfItem? Destination(PdfItem? item, IReadOnlyDictionary<PdfObjectID, PdfPage> pages,
        Dictionary<(bool Legacy, string Bytes), string> names, bool link)
    {
        item = Value(item);
        if (item is PdfArray)
            return MapDestination(item, pages);
        if (item is PdfString textKey && ByteCount(textKey) > limits.StringBytes)
            return null;
        if (item is PdfName nameKey && nameKey.Value.Length - 1 > limits.StringBytes)
            return null;
        // A PdfName and a PdfString never share one string instance, so one cache serves both kinds.
        var value = item switch
        {
            PdfName name => name.Value,
            PdfString text => text.Value,
            _ => null,
        };
        if (value is null)
            return null;
        if (value.Length < CacheLength || !_lookups.TryGetValue(value, out var resolved))
        {
            var key = item is PdfString text ? (false, Bytes(text)) : (true, value[1..]);
            resolved = names.TryGetValue(key, out var found) ? _destinations[found] : null;
            if (value.Length >= CacheLength)
                _lookups.Add(value, resolved);
        }
        if (resolved is not { } hit)
            return null;
        return link ? hit.Name : hit.Destination;
    }

    private void CarryOutlines(PdfDictionary catalog, IReadOnlyDictionary<PdfObjectID, PdfPage> pages,
        Dictionary<(bool Legacy, string Bytes), string> names, List<Outline> into, int depth, int count, CancellationToken ct)
    {
        if (Dictionary(catalog.Elements["/Outlines"]) is not { } root)
            return;
        var stack = new Stack<(PdfDictionary Source, List<Outline> Into, int Depth)>();
        if (Dictionary(root.Elements["/First"]) is { } first)
            stack.Push((first, into, depth));
        var visited = new HashSet<(PdfObjectID Id, PdfObject? Direct)>();
        while (stack.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (count >= limits.OutlineItems)
                break;
            if (!visited.Add(Identity(entry.Source)))
                continue;
            if (Dictionary(entry.Source.Elements["/Next"]) is { } next)
                stack.Push((next, entry.Into, entry.Depth));
            if (entry.Depth > limits.OutlineDepth)
                continue;
            var title = Text(entry.Source.Elements["/Title"]);
            if (title is null || !SpendStrings(ByteCount(title)))
                continue;
            var dictionary = new PdfDictionary(output);
            dictionary.Elements["/Title"] = new PdfString(title.Value, title.Encoding);
            var destination = Destination(entry.Source.Elements["/Dest"], pages, names, false);
            if (destination is null && Dictionary(entry.Source.Elements["/A"]) is { } action && Name(action.Elements["/S"]) == "/GoTo")
                destination = Destination(action.Elements["/D"], pages, names, false);
            if (destination is not null)
                dictionary.Elements["/Dest"] = destination;
            if (Array(entry.Source.Elements["/C"]) is { Elements.Count: 3 } color &&
                color.Elements.All(item => Value(item) is PdfInteger or PdfReal))
            {
                var copy = new PdfArray(output);
                foreach (var item in color.Elements)
                    copy.Elements.Add(Value(item)!);
                dictionary.Elements["/C"] = copy;
            }
            if (Value(entry.Source.Elements["/F"]) is PdfInteger style)
                dictionary.Elements["/F"] = new PdfInteger(style.Value);
            var outline = new Outline(dictionary, Value(entry.Source.Elements["/Count"]) is PdfInteger { Value: > 0 });
            entry.Into.Add(outline);
            count++;
            if (Dictionary(entry.Source.Elements["/First"]) is { } child)
                stack.Push((child, outline.Children, entry.Depth + 1));
        }
    }

    private void CarryLinks(PdfDocument source, IReadOnlyDictionary<PdfObjectID, PdfPage> pages,
        Dictionary<(bool Legacy, string Bytes), string> names, CancellationToken ct)
    {
        foreach (var page in source.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (Array(page.Elements["/Annots"]) is not { } annotations ||
                Array(pages[page.Reference!.ObjectID].Elements["/Annots"]) is not { } imported)
                continue;
            for (var i = 0; i < annotations.Elements.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (Dictionary(annotations.Elements[i]) is not { } annotation || Name(annotation.Elements["/Subtype"]) != "/Link")
                    continue;
                var mapped = Imported(annotation) as PdfDictionary;
                mapped ??= i < imported.Elements.Count ? Dictionary(imported.Elements[i]) : null;
                // An /Annots array may list one annotation many times; rewriting it again would copy its action again.
                if (mapped is null || !_links.Add(mapped))
                    continue;
                if (annotation.Elements.ContainsKey("/Dest"))
                {
                    var destination = Destination(annotation.Elements["/Dest"], pages, names, true);
                    if (destination is not null)
                        mapped.Elements["/Dest"] = destination;
                    else
                        mapped.Elements.Remove("/Dest");
                }
                if (Dictionary(annotation.Elements["/A"]) is { } action && Name(action.Elements["/S"]) == "/GoTo")
                {
                    // Links sharing one action get one rewritten action; copying it per link would write a large
                    // direct /Next graph once per link.
                    var shared = Dictionary(mapped.Elements["/A"]);
                    if (shared is not null && _actions.TryGetValue(shared, out var done))
                    {
                        if (done is null)
                            mapped.Elements.Remove("/A");
                        else
                            mapped.Elements["/A"] = done.Reference;
                        continue;
                    }
                    var destination = Destination(action.Elements["/D"], pages, names, true);
                    if (destination is null)
                    {
                        mapped.Elements.Remove("/A");
                        if (shared is not null)
                            _actions.Add(shared, null);
                    }
                    else
                    {
                        // A fresh dictionary, as the imported action may be shared; its other entries such as
                        // /Next are already output objects and stay.
                        var copy = new PdfDictionary(output);
                        if (shared is not null)
                            foreach (var entry in shared.Elements)
                                copy.Elements[entry.Key] = entry.Value;
                        copy.Elements["/S"] = new PdfName("/GoTo");
                        copy.Elements["/D"] = destination;
                        output.Internals.AddObject(copy);
                        mapped.Elements["/A"] = copy.Reference;
                        if (shared is not null)
                            _actions.Add(shared, copy);
                    }
                }
            }
        }
    }

    private void CarryFields(PdfDictionary catalog, CancellationToken ct)
    {
        if (Dictionary(catalog.Elements["/AcroForm"]) is not { } form)
            return;
        _needAppearances |= Value(form.Elements["/NeedAppearances"]) is PdfBoolean { Value: true };
        if (Array(form.Elements["/Fields"]) is not { } fields)
            return;
        var visited = new HashSet<(PdfObjectID Id, PdfObject? Direct)>();
        var expanded = new HashSet<(PdfObjectID Id, PdfObject? Direct)>();
        var prepared = new List<(PdfDictionary Root, PdfDictionary Imported, List<PdfDictionary> Nodes)>();
        // Names a field's own /DR uses (LibreOffice writes one) are kept free when form resources are renamed.
        var localNames = new HashSet<(string Kind, string Name)>();
        var collected = new HashSet<(string Kind, (PdfObjectID Id, PdfObject? Direct) Dictionary)>();
        var fieldItems = 0;
        foreach (var item in fields.Elements)
        {
            ct.ThrowIfCancellationRequested();
            if (item is not PdfReference || Dictionary(item) is not { } root)
                continue;
            var stack = new Stack<(PdfDictionary Field, PdfDictionary? Parent, int Depth, string? Type)>();
            var nodes = new List<PdfDictionary>();
            stack.Push((root, null, 1, null));
            var valid = true;
            var signature = false;
            while (stack.TryPop(out var entry))
            {
                ct.ThrowIfCancellationRequested();
                if (++fieldItems > limits.FieldItems || entry.Depth > limits.FieldDepth || !visited.Add(Identity(entry.Field)) ||
                    (entry.Parent is null ? Value(entry.Field.Elements["/Parent"]) is not (null or PdfNull) :
                        !ReferenceEquals(Dictionary(entry.Field.Elements["/Parent"]), entry.Parent)) ||
                    (entry.Field.Elements.ContainsKey("/T") && Text(entry.Field.Elements["/T"]) is null))
                {
                    valid = false;
                    break;
                }
                var type = Name(entry.Field.Elements["/FT"]) ?? entry.Type;
                signature |= type == "/Sig";
                nodes.Add(entry.Field);
                if (entry.Field.Elements.ContainsKey("/Kids"))
                {
                    // A /Kids array shared by two fields gives its children two parents.
                    if (Array(entry.Field.Elements["/Kids"]) is not { } kids || !expanded.Add(Identity(kids)))
                    {
                        valid = false;
                        break;
                    }
                    for (var i = kids.Elements.Count - 1; i >= 0; i--)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (kids.Elements[i] is not PdfReference || Dictionary(kids.Elements[i]) is not { } child ||
                            visited.Contains(Identity(child)))
                        {
                            valid = false;
                            break;
                        }
                        stack.Push((child, entry.Field, entry.Depth + 1, type));
                    }
                    if (!valid)
                        break;
                }
            }
            if (!valid || signature || Imported(root) is not PdfDictionary imported)
                continue;
            foreach (var node in nodes)
                if (Dictionary(node.Elements["/DR"]) is { } local)
                    foreach (var kind in ResourceKinds)
                        if (Dictionary(local.Elements[kind]) is { } own && collected.Add((kind, Identity(own))))
                            foreach (var key in own.Elements.Keys)
                            {
                                ct.ThrowIfCancellationRequested();
                                localNames.Add((kind, key));
                            }
            prepared.Add((root, imported, nodes));
        }

        var resourceNames = CarryResources(Dictionary(form.Elements["/DR"]), localNames, ct);
        var cache = new Dictionary<(string Value, PdfStringEncoding Encoding), PdfString>();
        var candidates = new List<(string? Name, PdfDictionary Field)>();
        var appearanceBytes = 0;
        var aliased = new HashSet<(string Kind, (PdfObjectID Id, PdfObject? Direct) Dictionary)>();
        foreach (var (root, imported, nodes) in prepared)
        {
            ct.ThrowIfCancellationRequested();
            var writes = new List<(PdfDictionary Field, PdfString Appearance)>();
            var bytes = 0;
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                var appearance = Text(node.Elements["/DA"]);
                if (ReferenceEquals(node, root) && !node.Elements.ContainsKey("/DA"))
                    appearance = Text(form.Elements["/DA"]);
                if (appearance is null || ByteCount(appearance) > limits.AppearanceLength ||
                    Imported(node) is not PdfDictionary mapped)
                    continue;
                var key = (appearance.Value, appearance.Encoding);
                if (!cache.TryGetValue(key, out var rewritten))
                {
                    rewritten = RewriteAppearance(appearance, resourceNames, ct);
                    cache.Add(key, rewritten);
                }
                bytes += ByteCount(rewritten);
                if (bytes > limits.AppearanceBytes - appearanceBytes)
                    break;
                writes.Add((mapped, rewritten));
            }
            if (bytes > limits.AppearanceBytes - appearanceBytes)
                continue;
            appearanceBytes += bytes;
            foreach (var (field, appearance) in writes)
            {
                ct.ThrowIfCancellationRequested();
                field.Elements["/DA"] = appearance;
            }
            // Some writers (LibreOffice) also put a /DR on the field itself, and viewers such as poppler look the
            // /DA names up there first; give the renamed names the same resources in that dictionary too. A kid
            // that inherits its /DA needs them as much as a field with its own.
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                if (Imported(node) is not PdfDictionary field ||
                    Dictionary(field.Elements["/DR"]) is not { } local)
                    continue;
                foreach (var kind in ResourceKinds)
                {
                    if (Dictionary(local.Elements[kind]) is not { } own || !aliased.Add((kind, Identity(own))))
                        continue;
                    foreach (var name in own.Elements.Keys.ToArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (resourceNames.TryGetValue((kind, name), out var renamed) && renamed != name &&
                            !own.Elements.ContainsKey(renamed))
                            own.Elements[renamed] = own.Elements[name];
                    }
                }
            }
            if (!root.Elements.ContainsKey("/Q") && Value(form.Elements["/Q"]) is PdfInteger alignment)
                imported.Elements["/Q"] = new PdfInteger(alignment.Value);
            candidates.Add((Text(root.Elements["/T"])?.Value, imported));
        }

        // Names this source adds; names of earlier sources stay in _fieldNames and are not copied.
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (name is not null && !_fieldNames.Contains(name))
                reserved.Add(name);
        }
        PdfDictionary? prefix = null;
        PdfArray? prefixedKids = null;
        foreach (var (name, field) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (field.Reference is null)
                output.Internals.AddObject(field);
            if (name is not null && _fieldNames.Contains(name))
            {
                if (prefix is null)
                {
                    var basis = _filePosition.ToString(CultureInfo.InvariantCulture);
                    var prefixName = basis;
                    for (var suffix = 2; reserved.Contains(prefixName) || _fieldNames.Contains(prefixName); suffix++)
                        prefixName = basis + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                    prefix = new PdfDictionary(output);
                    prefixedKids = new PdfArray(output);
                    prefix.Elements["/T"] = new PdfString(prefixName);
                    prefix.Elements["/Kids"] = prefixedKids;
                    output.Internals.AddObject(prefix);
                    _fields.Elements.Add(prefix.Reference!);
                    reserved.Add(prefixName);
                }
                field.Elements["/Parent"] = prefix.Reference;
                prefixedKids!.Elements.Add(field.Reference!);
            }
            else
                _fields.Elements.Add(field.Reference!);
        }
        _fieldNames.UnionWith(reserved);
    }

    private Dictionary<(string Kind, string Name), string> CarryResources(PdfDictionary? source,
        HashSet<(string Kind, string Name)> localNames, CancellationToken ct)
    {
        // Also after a resource limit stops the copy: the resources already carried keep their defaults.
        var names = CopyResources(source, localNames, ct);
        KeepDefaultSpaces(names);
        return names;
    }

    private Dictionary<(string Kind, string Name), string> CopyResources(PdfDictionary? source,
        HashSet<(string Kind, string Name)> localNames, CancellationToken ct)
    {
        var names = new Dictionary<(string, string), string>();
        if (source is null)
            return names;
        var copied = new Dictionary<PdfObject, PdfObject>(ReferenceEqualityComparer.Instance);
        var objectCount = 0;
        var scalarBytes = 0;
        // Entries read while checking resources, kept or left out, so a wide shared array is not read again per resource.
        var work = 0;
        foreach (var kind in ResourceKinds)
        {
            ct.ThrowIfCancellationRequested();
            if (Dictionary(source.Elements[kind]) is not { } resources)
                continue;
            var target = Dictionary(_resources.Elements[kind]) ?? new PdfDictionary(output);
            var reserved = new HashSet<string>(resources.Elements.Keys, StringComparer.Ordinal);
            foreach (var key in resources.Elements.Keys)
            {
                ct.ThrowIfCancellationRequested();
                var value = Value(resources.Elements[key]);
                if (kind == "/ColorSpace" ? value is not (PdfName or PdfArray) : value is not PdfDictionary)
                    continue;
                // Preflight the whole resource before allocating or linking any output objects.
                var pending = new Stack<PdfItem>();
                var graph = new List<PdfObject>();
                var visited = new HashSet<(PdfObjectID Id, PdfObject? Direct)>();
                // Strings and names are copied as direct values at every use, so their bytes are budgeted
                // per use; a resource that leads to a page would copy the page graph outside the page tree.
                var bytes = 0;
                var page = false;
                pending.Push(value);
                while (pending.TryPop(out var item))
                {
                    ct.ThrowIfCancellationRequested();
                    item = Value(item)!;
                    bytes += item switch
                    {
                        PdfString text => ByteCount(text),
                        PdfName name => name.Value.Length,
                        _ => 0,
                    };
                    if (bytes > limits.ResourceBytes - scalarBytes)
                        return names;
                    if (item is not (PdfDictionary or PdfArray) || item is not PdfObject obj ||
                        copied.ContainsKey(obj) || !visited.Add(Identity(obj)))
                        continue;
                    if (obj is PdfDictionary { } typed && Name(typed.Elements["/Type"]) is "/Page" or "/Pages" or "/Catalog")
                    {
                        page = true;
                        break;
                    }
                    graph.Add(obj);
                    if (graph.Count > limits.ResourceObjects - objectCount)
                        return names;
                    work += obj is PdfDictionary entries ? entries.Elements.Count : ((PdfArray)obj).Elements.Count;
                    if (work > limits.ResourceItems)
                        return names;
                    if (obj is PdfDictionary dictionary)
                        foreach (var entry in dictionary.Elements)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (dictionary.Stream is null || entry.Key != "/Length")
                                pending.Push(entry.Value!);
                        }
                    else if (obj is PdfArray array)
                        foreach (var entry in array.Elements)
                        {
                            ct.ThrowIfCancellationRequested();
                            pending.Push(entry);
                        }
                }
                if (page)
                {
                    // Count the work of a resource that is left out too, so a shared graph is not walked again
                    // for every resource that leads into it.
                    objectCount += graph.Count;
                    scalarBytes += bytes;
                    continue;
                }
                foreach (var obj in graph)
                {
                    ct.ThrowIfCancellationRequested();
                    PdfObject clone = obj is PdfDictionary ? new PdfDictionary(output) : new PdfArray(output);
                    if (obj.Reference is not null)
                        output.Internals.AddObject(clone);
                    copied.Add(obj, clone);
                }
                PdfItem Copy(PdfItem item)
                {
                    var direct = Value(item) ?? PdfNull.Value;
                    if (direct is PdfObject obj && copied.TryGetValue(obj, out var clone))
                        return item is PdfReference ? clone.Reference! : clone;
                    return direct;
                }
                foreach (var obj in graph)
                {
                    ct.ThrowIfCancellationRequested();
                    if (obj is PdfDictionary dictionary)
                    {
                        var clone = (PdfDictionary)copied[obj];
                        foreach (var entry in dictionary.Elements)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (dictionary.Stream is null || entry.Key != "/Length")
                                clone.Elements[entry.Key] = Copy(entry.Value!);
                        }
                        if (dictionary.Stream is not null)
                            clone.CreateStream((byte[])dictionary.Stream.Value.Clone());
                    }
                    else if (obj is PdfArray array)
                    {
                        var clone = (PdfArray)copied[obj];
                        foreach (var entry in array.Elements)
                        {
                            ct.ThrowIfCancellationRequested();
                            clone.Elements.Add(Copy(entry));
                        }
                    }
                }
                objectCount += graph.Count;
                scalarBytes += bytes;
                var renamed = key;
                // A default colour space silently replaces a device space for every field of the merged form, so it
                // always gets a plain name, and this source's /DA selects it explicitly (RewriteAppearance).
                if (target.Elements.ContainsKey(renamed) || (kind == "/ColorSpace" && DefaultSpaces.ContainsValue(key)))
                {
                    // Only suffixes already in the merged dictionary stay taken for later sources; names this
                    // source reserves are free again for the next one.
                    var suffix = _resourceSuffixes.GetValueOrDefault((kind, key), 2);
                    while (target.Elements.ContainsKey(Suffixed(key, suffix)))
                        suffix++;
                    _resourceSuffixes[(kind, key)] = suffix;
                    do
                        renamed = Suffixed(key, suffix++);
                    while (reserved.Contains(renamed) || localNames.Contains((kind, renamed)) || target.Elements.ContainsKey(renamed));
                }
                target.Elements[renamed] = Copy(resources.Elements[key]!);
                _resources.Elements[kind] = target;
                names.Add((kind, key), renamed);
            }
        }
        return names;
    }

    // A moved default space must still replace its device space wherever this source's colour space resources
    // name that device space: a plain alias such as /CS1 /DeviceRGB, or the base or alternate of an
    // /Indexed, /Pattern, /Separation or /DeviceN array, also as the /ColorSpace of a shading pattern's shading,
    // which has no resources of its own. Tiling patterns resolve defaults in their own /Resources.
    private void KeepDefaultSpaces(Dictionary<(string Kind, string Name), string> names)
    {
        if (Dictionary(_resources.Elements["/ColorSpace"]) is not { } spaces)
            return;
        var defaults = new Dictionary<string, PdfItem>(StringComparer.Ordinal);
        foreach (var (device, space) in DefaultSpaces)
            if (names.TryGetValue(("/ColorSpace", space), out var moved) && spaces.Elements[moved] is { } value)
            {
                // Shared by reference, so a large default array is written once however many aliases use it.
                if (value is PdfObject { Reference: null } direct)
                {
                    output.Internals.AddObject(direct);
                    spaces.Elements[moved] = direct.Reference;
                }
                defaults[device] = spaces.Elements[moved]!;
            }
        if (defaults.Count == 0)
            return;
        // Carried objects may also be used by a tiling pattern's own resources, where a device space follows that
        // pattern's defaults, so a default is applied to a copy made once per object, never to the object itself.
        var kept = new Dictionary<PdfObject, PdfItem?>(ReferenceEqualityComparer.Instance);
        PdfItem Indirect(PdfObject copy)
        {
            output.Internals.AddObject(copy);
            return copy.Reference!;
        }
        // A plain device name, or an array whose base or alternate slot leads to one. ISO 32000 allows at most
        // /Pattern, then /Indexed, then /Separation or /DeviceN above the device space, so three arrays are followed.
        PdfItem? Kept(PdfItem? item, int depth = 0)
        {
            var value = Value(item);
            if (value is PdfName device)
                return defaults.TryGetValue(device.Value, out var alias) ? alias : null;
            if (depth >= 3 || value is not PdfArray array)
                return null;
            if (kept.TryGetValue(array, out var done))
                return done;
            kept[array] = null;
            var slot = Name(array.Elements.Count > 0 ? array.Elements[0] : null) switch
            {
                "/Indexed" or "/Pattern" => 1,
                "/Separation" or "/DeviceN" => 2,
                _ => -1,
            };
            if (slot < 0 || slot >= array.Elements.Count || Kept(array.Elements[slot], depth + 1) is not { } inner)
                return null;
            var copy = new PdfArray(output);
            foreach (var entry in array.Elements)
                copy.Elements.Add(entry);
            copy.Elements[slot] = inner;
            return kept[array] = Indirect(copy);
        }
        // One memo per replaced key: a dictionary used both as a pattern and as a shading needs both copies.
        var keptDictionaries = new Dictionary<string, Dictionary<PdfObject, PdfItem?>>(StringComparer.Ordinal);
        PdfItem? KeptDictionary(PdfDictionary dictionary, string key, Func<PdfItem?, PdfItem?> keep)
        {
            if (!keptDictionaries.TryGetValue(key, out var memo))
                keptDictionaries[key] = memo = new Dictionary<PdfObject, PdfItem?>(ReferenceEqualityComparer.Instance);
            if (memo.TryGetValue(dictionary, out var done))
                return done;
            memo[dictionary] = null;
            if (keep(dictionary.Elements[key]) is not { } replacement)
                return null;
            var copy = new PdfDictionary(output);
            foreach (var entry in dictionary.Elements)
                if (dictionary.Stream is null || entry.Key != "/Length")
                    copy.Elements[entry.Key] = entry.Value;
            // Mesh shadings (types 4 to 7) keep their data in the stream.
            if (dictionary.Stream is not null)
                copy.CreateStream((byte[])dictionary.Stream.Value.Clone());
            copy.Elements[key] = replacement;
            return memo[dictionary] = Indirect(copy);
        }
        var patterns = Dictionary(_resources.Elements["/Pattern"]);
        foreach (var ((kind, name), renamed) in names)
        {
            if (kind == "/ColorSpace" && !DefaultSpaces.ContainsValue(name) && Kept(spaces.Elements[renamed]) is { } alias)
                spaces.Elements[renamed] = alias;
            else if (kind == "/Pattern" && Dictionary(patterns?.Elements[renamed]) is { } pattern &&
                Value(pattern.Elements["/PatternType"]) is PdfInteger { Value: 2 } &&
                KeptDictionary(pattern, "/Shading", shading =>
                    Dictionary(shading) is { } dictionary ? KeptDictionary(dictionary, "/ColorSpace", space => Kept(space)) : null) is { } copy)
                patterns!.Elements[renamed] = copy;
        }
    }

    private static readonly Dictionary<string, string> DefaultSpaces = new(StringComparer.Ordinal)
    {
        ["/DeviceGray"] = "/DefaultGray",
        ["/DeviceRGB"] = "/DefaultRGB",
        ["/DeviceCMYK"] = "/DefaultCMYK",
    };

    // Device colour operators and their colour space, operand count and general replacement operator.
    private static readonly Dictionary<string, (string Space, int Operands, string Operator)> DeviceColours = new(StringComparer.Ordinal)
    {
        ["g"] = ("/DeviceGray", 1, "sc"),
        ["G"] = ("/DeviceGray", 1, "SC"),
        ["rg"] = ("/DeviceRGB", 3, "sc"),
        ["RG"] = ("/DeviceRGB", 3, "SC"),
        ["k"] = ("/DeviceCMYK", 4, "sc"),
        ["K"] = ("/DeviceCMYK", 4, "SC"),
    };

    private static string Encoded(string name)
    {
        var result = new StringBuilder(name.Length + 1).Append('/');
        foreach (var c in name.AsSpan(1))
            if (c < 33 || c > 126 || Delimiter(c) || c == '#')
                result.Append('#').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                result.Append(c);
        return result.ToString();
    }

    private static string Suffixed(string key, int suffix) => key + "_" + suffix.ToString(CultureInfo.InvariantCulture);

    private readonly record struct Token(int Start, int Length, string? Name, string? Word);

    private static bool White(char c) => c is '\0' or '\t' or '\n' or '\f' or '\r' or ' ';
    private static bool Delimiter(char c) => White(c) || c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    internal static PdfString ScaleAppearance(PdfString source, double scale) =>
        RewriteAppearance(source, new(), CancellationToken.None, scale);

    private static PdfString RewriteAppearance(PdfString source, Dictionary<(string Kind, string Name), string> names,
        CancellationToken ct, double? scale = null)
    {
        var text = source.Value;
        var operands = new List<Token>();
        var changes = new List<(int Start, int Length, string Text)>();
        var compositeDepth = 0;
        string? Default(string device) =>
            names.TryGetValue(("/ColorSpace", DefaultSpaces[device]), out var renamed) ? renamed : null;
        for (var i = 0; i < text.Length;)
        {
            ct.ThrowIfCancellationRequested();
            if (White(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '%')
            {
                while (i < text.Length && text[i] is not ('\r' or '\n'))
                    i++;
                continue;
            }
            var start = i;
            string? name = null;
            string? word = null;
            if (text[i] == '/')
            {
                while (++i < text.Length && !Delimiter(text[i])) { }
                name = "/" + DecodeName(text[start..i]);
            }
            else if (text[i] == '(')
            {
                var depth = 1;
                for (i++; i < text.Length && depth > 0; i++)
                {
                    if (text[i] == '\\')
                        i++;
                    else if (text[i] == '(')
                        depth++;
                    else if (text[i] == ')')
                        depth--;
                }
                if (depth != 0)
                    return new PdfString(source.Value, source.Encoding);
            }
            else if (text[i] == '<' && (i + 1 >= text.Length || text[i + 1] != '<'))
            {
                for (i++; i < text.Length && text[i] != '>'; i++)
                    if (!White(text[i]) && !Uri.IsHexDigit(text[i]))
                        return new PdfString(source.Value, source.Encoding);
                if (i == text.Length)
                    return new PdfString(source.Value, source.Encoding);
                i++;
            }
            else if (Delimiter(text[i]))
            {
                // Composite operands are legal, but are not resource-name operands.
                if (scale is not null)
                {
                    if (text[i] is '[' or '{' || text[i] == '<' && i + 1 < text.Length && text[i + 1] == '<')
                    {
                        compositeDepth++;
                        if (text[i] == '<')
                            i++;
                    }
                    else if (text[i] is ']' or '}' || text[i] == '>' && i + 1 < text.Length && text[i + 1] == '>')
                    {
                        compositeDepth--;
                        if (text[i] == '>')
                            i++;
                    }
                }
                i++;
            }
            else
            {
                while (i < text.Length && !Delimiter(text[i]))
                    i++;
                var value = text[start..i];
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                    value is not ("true" or "false" or "null"))
                    word = value;
            }
            var token = new Token(start, i - start, name, word);
            if (scale is not null && compositeDepth != 0)
            {
                operands.Clear();
                continue;
            }
            if (word is null)
            {
                operands.Add(token);
                continue;
            }
            var kind = word switch
            {
                "Tf" => "/Font",
                "cs" or "CS" => "/ColorSpace",
                "scn" or "SCN" => "/Pattern",
                "gs" => "/ExtGState",
                _ => null,
            };
            var index = operands.Count - (word == "Tf" ? 2 : 1);
            // Device colour spaces and /Pattern are built-in names for cs and CS, not resource names; a device space
            // this source had a default for selects that default by its new name.
            if (kind == "/ColorSpace" && index >= 0 && operands[index].Name is "/DeviceGray" or "/DeviceRGB" or "/DeviceCMYK" or "/Pattern")
            {
                if (operands[index].Name is { } device && DefaultSpaces.ContainsKey(device) && Default(device) is { } space)
                    changes.Add((operands[index].Start, operands[index].Length, Encoded(space)));
                kind = null;
            }
            if (kind is not null && index >= 0 && operands[index] is { Name: { } resource } operand &&
                names.TryGetValue((kind, resource), out var renamed) && renamed != resource)
                changes.Add((operand.Start, operand.Length, Encoded(renamed)));
            if (word == "Tf" && scale is { } factor && index >= 0 && operands[index].Name is not null &&
                double.TryParse(text.AsSpan(operands[index + 1].Start, operands[index + 1].Length), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var size) && double.IsFinite(size) && size >= 0)
            {
                var changed = size * factor;
                if (!double.IsFinite(changed) || size > 0 && changed <= 0)
                    throw new InvalidDataException("Invalid scaled font size.");
                changes.Add((operands[index + 1].Start, operands[index + 1].Length, PdfMergePaperFitter.NumberText(changed)));
            }
            if (DeviceColours.TryGetValue(word, out var colour) && operands.Count == colour.Operands &&
                Default(colour.Space) is { } replacement)
            {
                changes.Add((operands[0].Start, 0, Encoded(replacement) + (colour.Operator == "sc" ? " cs " : " CS ")));
                changes.Add((token.Start, token.Length, colour.Operator));
            }
            operands.Clear();
        }
        if (changes.Count == 0)
            return new PdfString(source.Value, source.Encoding);
        var result = new StringBuilder(text.Length);
        var position = 0;
        foreach (var (start, length, replacement) in changes)
        {
            ct.ThrowIfCancellationRequested();
            result.Append(text, position, start - position);
            result.Append(replacement);
            position = start + length;
        }
        result.Append(text, position, text.Length - position);
        return new PdfString(result.ToString(), source.Encoding);
    }

    internal void Write(CancellationToken ct)
    {
        var catalog = output.Internals.Catalog;
        if (_destinations.Count > 0)
        {
            var pairs = new PdfArray(output);
            foreach (var value in _destinations.Values)
            {
                ct.ThrowIfCancellationRequested();
                pairs.Elements.Add(value.Name);
                pairs.Elements.Add(value.Destination);
            }
            var tree = new PdfDictionary(output);
            tree.Elements["/Names"] = pairs;
            var names = new PdfDictionary(output);
            names.Elements["/Dests"] = tree;
            catalog.Elements["/Names"] = names;
        }
        if (_outlines.Count > 0)
        {
            var root = new PdfDictionary(output);
            root.Elements["/Type"] = new PdfName("/Outlines");
            output.Internals.AddObject(root);
            var stack = new Stack<(PdfDictionary Parent, List<Outline> Children, bool Finish)>();
            stack.Push((root, _outlines, false));
            while (stack.TryPop(out var entry))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Finish)
                {
                    var visible = 0;
                    foreach (var child in entry.Children)
                    {
                        ct.ThrowIfCancellationRequested();
                        child.VisibleChildren = child.Children.Sum(c => 1 + (c.Opened ? c.VisibleChildren : 0));
                        if (child.Children.Count > 0)
                            child.Dictionary.Elements["/Count"] = new PdfInteger(child.Opened ? child.VisibleChildren : -child.VisibleChildren);
                        visible += 1 + (child.Opened ? child.VisibleChildren : 0);
                    }
                    if (ReferenceEquals(entry.Parent, root))
                        root.Elements["/Count"] = new PdfInteger(visible);
                    continue;
                }
                stack.Push((entry.Parent, entry.Children, true));
                for (var i = 0; i < entry.Children.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var child = entry.Children[i];
                    output.Internals.AddObject(child.Dictionary);
                }
                for (var i = 0; i < entry.Children.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var child = entry.Children[i];
                    child.Dictionary.Elements["/Parent"] = entry.Parent.Reference;
                    if (i > 0)
                        child.Dictionary.Elements["/Prev"] = entry.Children[i - 1].Dictionary.Reference;
                    if (i + 1 < entry.Children.Count)
                        child.Dictionary.Elements["/Next"] = entry.Children[i + 1].Dictionary.Reference;
                    if (child.Children.Count > 0)
                        stack.Push((child.Dictionary, child.Children, false));
                }
                entry.Parent.Elements["/First"] = entry.Children[0].Dictionary.Reference;
                entry.Parent.Elements["/Last"] = entry.Children[^1].Dictionary.Reference;
            }
            catalog.Elements["/Outlines"] = root.Reference;
        }
        if (_fields.Elements.Count > 0)
        {
            var form = new PdfDictionary(output);
            form.Elements["/Fields"] = _fields;
            if (_resources.Elements.Count > 0)
                form.Elements["/DR"] = _resources;
            if (_needAppearances)
                form.Elements["/NeedAppearances"] = new PdfBoolean(true);
            output.Internals.AddObject(form);
            catalog.Elements["/AcroForm"] = form.Reference;
        }
    }
}
