// A mutation fuzzer for text formats (the markup readers: notebooks, outlines, wiki and lightweight markup): it takes a
// seed written in code and changes it a little at random — the format's own tokens put in, pieces repeated many times
// over (deep nesting, long runs of markers), lines cut, removed or swapped, bytes changed — and hands the result to the
// reader through DocumentFuzzTests.Fuzz.

using System.Text;

namespace Filee.Engines.Tests;

internal sealed class TextFuzzer(byte[] seed, Random random, IReadOnlyList<string> tokens)
{
    private static readonly string[] Common =
    [
        "\n", "\n\n", "\t", " ", "\0", "\uFEFF", "\u202E", "\uD800", "é", "한", "😀", "\\", "\"", "'", "<", ">", "&", "&amp;",
        "&#0;", "&#x110000;", "{", "}", "[", "]", "(", ")", "|", "*", "_", "-", "+", "=", "#", ":", ";", "!", "^", "~", "`",
        "/", "%", "0", "-1", "2147483648", "1e308", "NaN", "null", "true", new string('9', 400), new string('a', 5000),
        "http://example.invalid/a.png", "../../../../etc/passwd", "C:\\Windows\\win.ini", "file:///c:/x.png",
    ];

    private readonly string _text = Encoding.UTF8.GetString(seed);

    /// <summary>A mutated copy of the seed.</summary>
    public byte[] Next()
    {
        var text = new StringBuilder(_text);
        for (var rounds = 1 + random.Next(4); rounds > 0; rounds--)
            Mutate(text);
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        if (random.Next(10) == 0 && bytes.Length > 0)
            bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
        return bytes;
    }

    private string Token() => random.Next(3) == 0 ? Common[random.Next(Common.Length)] : tokens[random.Next(tokens.Count)];

    private void Mutate(StringBuilder text)
    {
        var at = text.Length == 0 ? 0 : random.Next(text.Length + 1);
        switch (random.Next(8))
        {
            case 0 or 1:
                text.Insert(at, Token());
                break;
            case 2:
                {
                    // One token repeated: nesting, long lines, runs of markers.
                    var token = Token();
                    var count = random.Next(4) == 0 ? 20_000 : 1 + random.Next(200);
                    text.Insert(at, string.Concat(Enumerable.Repeat(token, Math.Max(1, Math.Min(count, 2_000_000 / Math.Max(1, token.Length))))));
                    break;
                }
            case 3 when text.Length > 0:
                {
                    var start = random.Next(text.Length);
                    text.Remove(start, Math.Min(1 + random.Next(64), text.Length - start));
                    break;
                }
            case 4 when text.Length > 0:
                {
                    // A piece of the text repeated.
                    var start = random.Next(text.Length);
                    var length = Math.Min(1 + random.Next(200), text.Length - start);
                    var piece = text.ToString(start, length);
                    text.Insert(at, string.Concat(Enumerable.Repeat(piece, 1 + random.Next(50))));
                    break;
                }
            case 5:
                text.Length = at; // cut short
                break;
            case 6 when text.Length > 0:
                {
                    // Two lines swapped.
                    var lines = text.ToString().Split('\n');
                    if (lines.Length > 1)
                    {
                        var (a, b) = (random.Next(lines.Length), random.Next(lines.Length));
                        (lines[a], lines[b]) = (lines[b], lines[a]);
                        text.Clear().Append(string.Join('\n', lines));
                    }
                    break;
                }
            default:
                text.Insert(at, Token() + Token() + Token());
                break;
        }
    }
}

/// <summary>
/// A mutation fuzzer for JSON formats (notebooks): most changes edit the tree, so the file stays valid JSON and reaches
/// the reader's logic (values of the wrong type or extreme, members removed, array items repeated, a node nested in
/// itself); the rest go through <see cref="TextFuzzer"/>.
/// </summary>
internal sealed class JsonFuzzer(byte[] seed, Random random, IReadOnlyList<string> tokens)
{
    private readonly TextFuzzer _text = new(seed, random, tokens);
    private readonly string _json = Encoding.UTF8.GetString(seed);

    public byte[] Next()
    {
        if (random.Next(10) < 3)
            return _text.Next();
        var root = System.Text.Json.Nodes.JsonNode.Parse(_json)!;
        for (var rounds = 1 + random.Next(4); rounds > 0; rounds--)
        {
            var nodes = Nodes(root).ToList();
            var node = nodes[random.Next(nodes.Count)];
            if (node.Parent is not { } parent)
                continue;
            System.Text.Json.Nodes.JsonNode? replacement = random.Next(7) switch
            {
                0 => null,
                1 => System.Text.Json.Nodes.JsonValue.Create(tokens[random.Next(tokens.Count)]),
                2 => System.Text.Json.Nodes.JsonValue.Create(random.Next(3) switch { 0 => -1, 1 => int.MaxValue, _ => 0 }),
                3 => new System.Text.Json.Nodes.JsonArray(),
                4 => new System.Text.Json.Nodes.JsonObject(),
                5 => System.Text.Json.Nodes.JsonValue.Create(string.Concat(Enumerable.Repeat(tokens[random.Next(tokens.Count)], 1 + random.Next(5000)))),
                _ => node.DeepClone(),
            };
            if (parent is System.Text.Json.Nodes.JsonObject obj)
            {
                var name = obj.First(p => ReferenceEquals(p.Value, node)).Key;
                if (random.Next(8) == 0)
                    obj.Remove(name);
                else
                    obj[name] = replacement;
            }
            else if (parent is System.Text.Json.Nodes.JsonArray array)
            {
                var index = array.IndexOf(node);
                if (random.Next(3) == 0)
                    for (var copies = random.Next(50); copies > 0; copies--)
                        array.Insert(index, node.DeepClone());
                else
                    array[index] = replacement;
            }
        }
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> Nodes(System.Text.Json.Nodes.JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            System.Text.Json.Nodes.JsonObject obj => obj.Select(p => p.Value),
            System.Text.Json.Nodes.JsonArray array => array.AsEnumerable(),
            _ => Enumerable.Empty<System.Text.Json.Nodes.JsonNode?>(),
        };
        foreach (var child in children)
            if (child is not null)
                foreach (var nested in Nodes(child))
                    yield return nested;
    }
}

/// <summary>
/// A mutation fuzzer for XML formats that are one file (OPML, DocBook): most changes edit the tree, so the file stays
/// well-formed and reaches the reader's logic (attributes set to tokens, elements removed, repeated, renamed or moved
/// into one another); the rest go through <see cref="TextFuzzer"/>.
/// </summary>
internal sealed class XmlFuzzer(byte[] seed, Random random, IReadOnlyList<string> tokens)
{
    private readonly TextFuzzer _text = new(seed, random, tokens);
    private readonly string _xml = Encoding.UTF8.GetString(seed);

    public byte[] Next()
    {
        if (random.Next(10) < 3)
            return _text.Next();
        var document = System.Xml.Linq.XDocument.Parse(_xml, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        for (var rounds = 1 + random.Next(4); rounds > 0; rounds--)
        {
            var elements = document.Descendants().ToList();
            var element = elements[random.Next(elements.Count)];
            switch (random.Next(6))
            {
                case 0 when element.Attributes().Any():
                    {
                        var attributes = element.Attributes().ToList();
                        attributes[random.Next(attributes.Count)].Value = tokens[random.Next(tokens.Count)];
                        break;
                    }
                case 1 when element.Parent is not null:
                    element.Remove();
                    break;
                case 2 when element.Parent is not null:
                    for (var copies = random.Next(50); copies > 0; copies--)
                        element.AddAfterSelf(new System.Xml.Linq.XElement(element));
                    break;
                case 3:
                    {
                        // Moved into another element, or into a copy of itself.
                        var other = elements[random.Next(elements.Count)];
                        if (other != element && !other.Ancestors().Contains(element) && element.Parent is not null)
                        {
                            element.Remove();
                            other.Add(element);
                        }
                        else
                        {
                            element.Add(new System.Xml.Linq.XElement(element));
                        }
                        break;
                    }
                case 4:
                    element.Name = elements[random.Next(elements.Count)].Name;
                    break;
                default:
                    element.Add(new System.Xml.Linq.XText(tokens[random.Next(tokens.Count)]));
                    break;
            }
        }
        return Encoding.UTF8.GetBytes(document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
    }
}
