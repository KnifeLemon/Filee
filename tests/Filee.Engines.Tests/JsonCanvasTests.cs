// Obsidian's boards (.canvas, JSON Canvas 1.0) drawn as SVG, PDF and PNG, and written as Markdown. Every board is made
// here: the specification's own sample, a board with groups, colours, labelled lines, a picture and a note beside it,
// and damaged or hostile files. Files outside the canvas's folder (up, absolute) are never read: the 6×6 red picture
// in "private" must not reach any output.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Vector;
using Filee.Engines.Vector.Canvas;
using ImageMagick;

namespace Filee.Engines.Tests;

public class JsonCanvasTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>The sample board of jsoncanvas.org (MIT), with the files it shows next to it.</summary>
    private const string Sample = """
        {
        	"nodes":[
        		{"id":"754a8ef995f366bc","type":"group","x":-300,"y":-460,"width":610,"height":200,"label":"JSON Canvas"},
        		{"id":"8132d4d894c80022","type":"file","file":"readme.md","x":-280,"y":-200,"width":570,"height":560,"color":"6"},
        		{"id":"7efdbbe0c4742315","type":"file","file":"_site/logo.svg","x":-280,"y":-440,"width":217,"height":80},
        		{"id":"59e896bc8da20699","type":"text","text":"Learn more:\n\n- [Apps](/docs/apps.md)\n- [Spec](spec/1.0.md)\n- [Github](https://github.com/obsidianmd/jsoncanvas)","x":40,"y":-440,"width":250,"height":160},
        		{"id":"0ba565e7f30e0652","type":"file","file":"spec/1.0.md","x":360,"y":-400,"width":400,"height":400}
        	],
        	"edges":[
        		{"id":"6fa11ab87f90b8af","fromNode":"7efdbbe0c4742315","fromSide":"right","toNode":"59e896bc8da20699","toSide":"left"}
        	]
        }
        """;

    private sealed record Vault(string Folder, string Canvas, string Secret);

    /// <summary>A vault folder with a board, a picture and a note beside it, and a red picture in a sibling folder.</summary>
    private Vault MakeBoard(Action<JsonArray, JsonArray>? change = null)
    {
        var root = fx.NewFolder();
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "img"));
        Directory.CreateDirectory(Path.Combine(vault, "notes"));
        Directory.CreateDirectory(Path.Combine(root, "private"));
        File.WriteAllBytes(Path.Combine(vault, "img", "photo.png"), EbookBuilders.Png(64, 40, MagickColors.Teal));
        File.WriteAllText(Path.Combine(vault, "notes", "Meeting.md"), "---\ntags: [x]\n---\n# Kick-off\n\nNOTEBODY decided.\n");
        var secret = Path.Combine(root, "private", "secret.png");
        File.WriteAllBytes(secret, EbookBuilders.Png(6, 6, MagickColors.Red));

        var nodes = new JsonArray
        {
            Node("g1", "group", -40, -60, 800, 500, ("label", "Sprint 12"), ("color", "4")),
            Node("t1", "text", 0, 0, 300, 200, ("text", "# Goals\n\n**Ship** the *beta*, see [[Roadmap|the roadmap]].\n\n- [x] Done\n- [ ] Todo"), ("color", "1")),
            Node("f1", "file", 360, 0, 300, 180, ("file", "img/photo.png")),
            Node("f2", "file", 0, 250, 300, 180, ("file", "notes/Meeting.md"), ("color", "#3b82f6")),
            Node("l1", "link", 360, 250, 300, 120, ("url", "https://example.invalid/page")),
            Node("up", "file", 900, 0, 200, 100, ("file", "../private/secret.png")),
            Node("abs", "file", 900, 150, 200, 100, ("file", secret)),
            Node("k1", "text", 900, 300, 220, 120, ("text", "한국어 메모 日本語")),
        };
        var edges = new JsonArray
        {
            Edge("e1", "t1", "f1", ("fromSide", "right"), ("toSide", "left"), ("label", "then")),
            Edge("e2", "t1", "f2", ("fromEnd", "arrow"), ("color", "2")),
            Edge("e3", "f2", "gone"),
        };
        change?.Invoke(nodes, edges);
        var canvas = Path.Combine(vault, "plan.canvas");
        File.WriteAllText(canvas, new JsonObject { ["nodes"] = nodes, ["edges"] = edges }.ToJsonString());
        return new Vault(vault, canvas, secret);
    }

    private static JsonObject Node(string id, string type, int x, int y, int width, int height, params (string Key, string Value)[] more)
    {
        var node = new JsonObject { ["id"] = id, ["type"] = type, ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height };
        foreach (var (key, value) in more)
            node[key] = value;
        return node;
    }

    private static JsonObject Edge(string id, string from, string to, params (string Key, string Value)[] more)
    {
        var edge = new JsonObject { ["id"] = id, ["fromNode"] = from, ["toNode"] = to };
        foreach (var (key, value) in more)
            edge[key] = value;
        return edge;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private async Task<FileResult> FailAsync(string input, string target = "svg")
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.NotEqual(JobState.Completed, job.State);
        return job.Files.Single();
    }

    private string Write(string json, string name = "board.canvas")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllText(path, json);
        return path;
    }

    private static int Pictures(string svg) => svg.Split("data:image/").Length - 1;

    // ───────────────────────── Drawing ─────────────────────────

    [Fact]
    public async Task The_specification_sample_is_drawn_with_its_notes_and_logo()
    {
        var folder = fx.NewFolder();
        Directory.CreateDirectory(Path.Combine(folder, "_site"));
        Directory.CreateDirectory(Path.Combine(folder, "spec"));
        File.WriteAllText(Path.Combine(folder, "readme.md"), "# An open file format for infinite canvas data.\n\nREADMEBODY");
        File.WriteAllText(Path.Combine(folder, "spec", "1.0.md"), "# JSON Canvas Spec\n\nSPECBODY");
        File.WriteAllText(Path.Combine(folder, "_site", "logo.svg"),
            """<svg xmlns="http://www.w3.org/2000/svg" width="160" height="60"><rect width="160" height="60" fill="#8E1A63"/></svg>""");
        var canvas = Path.Combine(folder, "sample.canvas");
        File.WriteAllText(canvas, Sample);

        var svg = File.ReadAllText(await ConvertAsync(canvas, "svg"));
        Assert.Contains("READMEBODY", svg);
        Assert.Contains("SPECBODY", svg);
        Assert.Contains("Learn", svg);
        Assert.Contains("JSON Canvas", svg); // the group's label
        Assert.Equal(1, Pictures(svg)); // the logo, drawn and embedded
        using (var drawing = SvgRenderer.Load(await ConvertAsync(canvas, "svg")))
            Assert.True(drawing.Bounds.Width > 1060 && drawing.Bounds.Height > 820); // the board (1060 × 820) and a margin

        using var png = new MagickImage(await ConvertAsync(canvas, "png"));
        Assert.True(png.Width >= 1024);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(await ConvertAsync(canvas, "pdf")), 0, 4));
    }

    [Fact]
    public async Task Cards_groups_lines_and_colours_are_drawn()
    {
        var board = MakeBoard();
        var svg = File.ReadAllText(await ConvertAsync(board.Canvas, "svg"));
        Assert.Contains("Sprint 12", svg);
        Assert.Contains("Goals", svg);
        Assert.Contains("the roadmap", svg); // [[Roadmap|the roadmap]] shows its alias
        Assert.DoesNotContain("[[", svg);
        Assert.Contains("NOTEBODY", svg); // the note beside the canvas, without its properties
        Assert.DoesNotContain("tags:", svg);
        Assert.Contains("https://example.invalid/page", svg); // a link card shows its address, nothing is fetched
        Assert.Contains("then", svg); // a line's label
        Assert.Contains("#E93147", svg); // preset colour 1 (red)
        Assert.Contains("#3B82F6", svg); // a colour as #RRGGBB
        Assert.Contains("#EC7500", svg); // the line of colour 2
        Assert.Contains("한국어", svg);
        Assert.Equal(1, Pictures(svg)); // the picture beside the canvas only
    }

    [Fact]
    public async Task Pictures_outside_the_canvas_folder_are_never_read()
    {
        var board = MakeBoard();
        var svg = File.ReadAllText(await ConvertAsync(board.Canvas, "svg"));
        Assert.Equal(1, Pictures(svg));
        Assert.Contains("secret", svg); // shown by name, as a card whose file isn't there

        // The one picture is the teal one beside the canvas (the red one is 6 × 6), shrunk to at most what the card shows.
        var data = svg[(svg.IndexOf("base64,", StringComparison.Ordinal) + 7)..];
        data = data[..data.IndexOf('"')];
        using var picture = new MagickImage(Convert.FromBase64String(data));
        Assert.Equal((64u, 40u), (picture.Width, picture.Height));

        var markdown = await ConvertAsync(board.Canvas, "md");
        var copied = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(markdown)!, "plan_files"));
        using var only = new MagickImage(Assert.Single(copied));
        Assert.Equal(64u, only.Width);
    }

    [Theory]
    [InlineData("../private/secret.png")]
    [InlineData("..\\private\\secret.png")]
    [InlineData("img/../../private/secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\host\\share\\x.png")]
    [InlineData("//host/share/x.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://example.invalid/x.png")]
    [InlineData("~/x.png")]
    public void A_vault_path_leaving_the_folder_is_refused_by_its_text(string path)
    {
        var board = MakeBoard();
        Assert.Null(CanvasFiles.Locate(path, board.Folder));
    }

    [Fact]
    public void A_vault_path_from_above_the_canvas_folder_is_found_inside_it()
    {
        var board = MakeBoard();
        Assert.Equal(Path.Combine(board.Folder, "img", "photo.png"), CanvasFiles.Locate("img/photo.png", board.Folder));
        // The canvas in "Vault/Boards", the path from the vault's root: found below the canvas's folder only.
        Assert.Equal(Path.Combine(board.Folder, "img", "photo.png"), CanvasFiles.Locate("Boards/img/photo.png", board.Folder));
        Assert.Null(CanvasFiles.Locate("img/missing.png", board.Folder));
    }

    [Fact]
    public async Task A_line_to_a_missing_card_and_cards_of_unknown_types_are_left_out()
    {
        var board = MakeBoard((nodes, edges) =>
        {
            nodes.Add(Node("x1", "future-widget", 0, 600, 100, 100, ("text", "FUTURECARD")));
            edges.Add(Edge("e9", "t1", "x1", ("label", "TOFUTURE")));
        });
        var svg = File.ReadAllText(await ConvertAsync(board.Canvas, "svg"));
        Assert.DoesNotContain("FUTURECARD", svg);
        Assert.DoesNotContain("TOFUTURE", svg);
    }

    [Fact]
    public async Task Markup_in_card_text_cannot_break_the_drawing()
    {
        var board = MakeBoard((nodes, _) =>
            nodes.Add(Node("evil", "text", 0, 600, 300, 200, ("text", "</text><script>alert(1)</script> & \u0001 \"q\" <b>x</b>"))));
        var path = await ConvertAsync(board.Canvas, "svg");
        var svg = File.ReadAllText(path);
        Assert.DoesNotContain("<script", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0001", svg, StringComparison.Ordinal);
        using var drawing = SvgRenderer.Load(path); // well-formed
        await ConvertAsync(board.Canvas, "png");
    }

    [Fact]
    public async Task A_huge_text_card_is_clipped_to_its_card()
    {
        var text = string.Concat(Enumerable.Repeat("word ", 30_000));
        var path = Write(JsonSerializer.Serialize(new { nodes = new[] { new { id = "a", type = "text", x = 0, y = 0, width = 200, height = 100, text } } }));
        var svg = File.ReadAllText(await ConvertAsync(path, "svg"));
        Assert.True(svg.Length < 20_000, $"{svg.Length} characters"); // only the lines that fit are laid out
    }

    // ───────────────────────── Markdown ─────────────────────────

    [Fact]
    public async Task The_markdown_lists_groups_cards_and_connections()
    {
        var board = MakeBoard();
        var markdown = File.ReadAllText(await ConvertAsync(board.Canvas, "md"));
        Assert.StartsWith("# plan\n", markdown);
        Assert.Contains("## Sprint 12", markdown);
        Assert.Contains("### Goals", markdown); // the card's heading moved below the group's
        Assert.Contains("](plan_files/", markdown);
        Assert.Contains("[Meeting](notes/Meeting.md)", markdown);
        Assert.Contains("<https://example.invalid/page>", markdown);
        Assert.Contains("## Connections", markdown);
        Assert.Contains("- **Goals** → **photo**: then", markdown);
        Assert.Contains("- **Goals** ↔ **Meeting**", markdown);
        Assert.DoesNotContain("gone", markdown);
        Assert.True(markdown.IndexOf("Sprint 12", StringComparison.Ordinal) > markdown.IndexOf("[secret.png]", StringComparison.Ordinal),
            "cards outside groups come first");
    }

    // ───────────────────────── Refused and damaged files ─────────────────────────

    [Theory]
    [InlineData("""{"name":"some other app","version":3}""")]
    [InlineData("""{}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"nodes":"many"}""")]
    [InlineData("not json at all")]
    [InlineData("<canvas width=\"10\"></canvas>")]
    public async Task A_canvas_file_that_is_not_json_canvas_is_refused(string content)
    {
        var error = await FailAsync(Write(content));
        Assert.Equal("error.canvas_not_canvas", error.ErrorKey);
    }

    [Theory]
    [InlineData("""{"nodes":[],"edges":[]}""")]
    [InlineData("""{"edges":[]}""")]
    [InlineData("""{"nodes":[{"id":"a","type":"future","x":0,"y":0,"width":10,"height":10}]}""")]
    public async Task A_canvas_without_cards_is_refused(string content)
    {
        var error = await FailAsync(Write(content));
        Assert.Equal("error.canvas_empty", error.ErrorKey);
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"a","type":"text","x":0,"y":0,"width":10,"height":10,"text":"cut""")]
    [InlineData("""{"nodes":[{"id":"a","type":"text","x":0,"y":0,"width":10,"height":10}]}""")]
    [InlineData("""{"nodes":[{"id":"a","type":"text","x":"0","y":0,"width":10,"height":10,"text":""}]}""")]
    [InlineData("""{"nodes":[{"id":"a","type":"text","x":0,"y":0,"width":0,"height":10,"text":""}]}""")]
    [InlineData("""{"nodes":[{"type":"text","x":0,"y":0,"width":10,"height":10,"text":""}]}""")]
    [InlineData("""{"nodes":[{"id":"a","type":"text","x":0,"y":0,"width":10,"height":10,"text":""},{"id":"a","type":"text","x":0,"y":0,"width":10,"height":10,"text":""}]}""")]
    [InlineData("""{"nodes":[1]}""")]
    public void A_damaged_canvas_gives_a_clear_error(string content)
    {
        var error = Assert.Throws<InvalidDataException>(() => JsonCanvas.Parse(Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken));
        Assert.StartsWith("The canvas file is damaged", error.Message);
    }

    [Fact]
    public async Task A_damaged_canvas_fails_without_a_drawing()
    {
        var job = await fx.ConvertAsync([Write("""{"nodes":[{"id":"a","type":"text","x":0""")], new Preset { TargetFormat = "png" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public void Budgets_are_checked_before_anything_is_laid_out()
    {
        static ConversionRefusedException Refused(string json) =>
            Assert.Throws<ConversionRefusedException>(() => JsonCanvas.Parse(Encoding.UTF8.GetBytes(json), TestContext.Current.CancellationToken));

        var many = string.Join(',', Enumerable.Range(0, JsonCanvas.MaxNodes + 1)
            .Select(i => $$"""{"id":"n{{i}}","type":"text","x":0,"y":0,"width":10,"height":10,"text":""}"""));
        Assert.Equal("error.canvas_too_large", Refused($$"""{"nodes":[{{many}}]}""").ErrorKey);

        var lines = string.Join(',', Enumerable.Range(0, JsonCanvas.MaxEdges + 1).Select(i => $$"""{"id":"e{{i}}","fromNode":"a","toNode":"a"}"""));
        Assert.Equal("error.canvas_too_large",
            Refused($$"""{"nodes":[{"id":"a","type":"text","x":0,"y":0,"width":10,"height":10,"text":""}],"edges":[{{lines}}]}""").ErrorKey);

        Assert.Equal("error.canvas_too_large",
            Refused("""{"nodes":[{"id":"a","type":"text","x":1e300,"y":0,"width":10,"height":10,"text":""}]}""").ErrorKey);
        Assert.Equal("error.canvas_too_large",
            Refused("""{"nodes":[{"id":"a","type":"group","x":0,"y":0,"width":99999,"height":99999},{"id":"b","type":"group","x":900000,"y":0,"width":10,"height":10}]}""").ErrorKey);

        Assert.Equal("error.canvas_too_large", Assert.Throws<ConversionRefusedException>(() =>
            JsonCanvas.Parse(new byte[JsonCanvas.MaxFileBytes + 1], TestContext.Current.CancellationToken)).ErrorKey);
    }

    [Fact]
    public void Deeply_nested_json_is_refused_without_a_stack_overflow()
    {
        var deep = """{"nodes":[],"x":""" + new string('[', 10_000) + new string(']', 10_000) + "}";
        Assert.Throws<InvalidDataException>(() => JsonCanvas.Parse(Encoding.UTF8.GetBytes(deep), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Colours_are_read_as_presets_or_hex()
    {
        Assert.Equal(((byte)0xE9, (byte)0x31, (byte)0x47), CanvasColors.Parse("1"));
        Assert.Equal(((byte)0x78, (byte)0x52, (byte)0xEE), CanvasColors.Parse("6"));
        Assert.Equal(((byte)0x12, (byte)0x34, (byte)0x56), CanvasColors.Parse("#123456"));
        Assert.Equal(((byte)0xFF, (byte)0x00, (byte)0xAA), CanvasColors.Parse("#f0a"));
        Assert.Null(CanvasColors.Parse("7"));
        Assert.Null(CanvasColors.Parse("red"));
        Assert.Null(CanvasColors.Parse("#12345"));
        Assert.Null(CanvasColors.Parse(null));
    }

    // ───────────────────────── One card can't get around the budgets ─────────────────────────

    [Fact]
    public void A_long_vault_path_returns_quickly_without_walking_the_disk()
    {
        var board = MakeBoard();
        // "a/a/a/…/x.png" of a megabyte: without the cap this joins and stats an ever-longer string for every folder.
        var deep = string.Concat(Enumerable.Repeat("a/", 500_000)) + "x.png";
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(CanvasFiles.Locate(deep, board.Folder));
        Assert.True(started.ElapsedMilliseconds < 1_000, $"{started.ElapsedMilliseconds} ms");

        // A path at the limit is still looked for; one character past it is taken as not found.
        var withinLimit = string.Concat(Enumerable.Repeat("a/", (CanvasFiles.MaxPathLength - 10) / 2)) + "x.png";
        Assert.True(withinLimit.Length <= CanvasFiles.MaxPathLength);
        Assert.Null(CanvasFiles.Locate(new string('a', CanvasFiles.MaxPathLength + 1) + ".png", board.Folder));
    }

    [Fact]
    public void Only_the_first_few_folders_are_dropped_from_a_vault_path()
    {
        var board = MakeBoard();
        var prefix = string.Concat(Enumerable.Repeat("x/", CanvasFiles.MaxDroppedFolders + 1));
        // The real file is under more than MaxDroppedFolders leading folders: not reached, so the budget holds.
        Assert.Null(CanvasFiles.Locate(prefix + "img/photo.png", board.Folder));
        var near = string.Concat(Enumerable.Repeat("x/", CanvasFiles.MaxDroppedFolders));
        Assert.Equal(Path.Combine(board.Folder, "img", "photo.png"), CanvasFiles.Locate(near + "img/photo.png", board.Folder));
    }

    [Fact]
    public async Task A_link_card_with_a_huge_url_does_not_blow_the_budget()
    {
        var url = "https://example.invalid/" + new string('a', 5_000_000);
        var path = Write(JsonSerializer.Serialize(new
        {
            nodes = new[] { new { id = "a", type = "link", x = 0, y = 0, width = 300, height = 120, url } },
        }));
        var svg = File.ReadAllText(await ConvertAsync(path, "svg"));
        Assert.True(svg.Length < 200_000, $"{svg.Length} characters"); // the url is cut, and clipped to the card
    }

    /// <summary>A folder with one picture (<c>img/photo.png</c>) and a canvas that refers to it.</summary>
    private string MakePictureBoard(JsonArray nodes)
    {
        var folder = fx.NewFolder();
        Directory.CreateDirectory(Path.Combine(folder, "img"));
        File.WriteAllBytes(Path.Combine(folder, "img", "photo.png"), EbookBuilders.Png(64, 40, MagickColors.Teal));
        var canvas = Path.Combine(folder, "pic.canvas");
        File.WriteAllText(canvas, new JsonObject { ["nodes"] = nodes }.ToJsonString());
        return canvas;
    }

    [Fact]
    public async Task The_same_picture_shown_by_many_cards_of_one_size_is_embedded_once()
    {
        var nodes = new JsonArray();
        for (var i = 0; i < 20; i++)
            nodes.Add(Node($"p{i}", "file", 0, i * 160, 200, 140, ("file", "img/photo.png")));
        var svg = File.ReadAllText(await ConvertAsync(MakePictureBoard(nodes), "svg"));
        Assert.Equal(1, Pictures(svg)); // one <defs> copy, drawn by every card with <use>
        Assert.True(svg.Split("<use").Length - 1 >= 20);
    }

    [Fact]
    public async Task A_repeated_background_embeds_the_texture_once()
    {
        var nodes = new JsonArray
        {
            Node("bg", "group", 0, 0, 4000, 3000, ("label", "Textured"),
                ("background", "img/photo.png"), ("backgroundStyle", "repeat")),
        };
        var svg = File.ReadAllText(await ConvertAsync(MakePictureBoard(nodes), "svg"));
        Assert.Equal(1, Pictures(svg)); // one copy, not one per tile
        Assert.True(svg.Length < 2_000_000, $"{svg.Length} characters");
    }

    [Fact]
    public async Task A_markdown_file_link_with_a_heading_subpath_stays_a_link()
    {
        var path = Write(JsonSerializer.Serialize(new
        {
            nodes = new[] { new { id = "a", type = "file", x = 0, y = 0, width = 300, height = 120, file = "Notes/Plan.md", subpath = "#Next steps" } },
        }));
        var markdown = File.ReadAllText(await ConvertAsync(path, "md"));
        Assert.Contains("[Plan](Notes/Plan.md#Next%20steps)", markdown);
        Assert.DoesNotContain("#Next steps", markdown); // a space in the destination would break the link
    }

    [Fact]
    public async Task Deeply_nested_groups_in_the_markdown_writer_do_not_overflow_the_stack()
    {
        var nodes = new JsonArray();
        for (var i = 0; i < 5_000; i++)
        {
            var size = 20_000 - i;
            nodes.Add(Node($"g{i}", "group", i, i, size, size, ("label", $"G{i}")));
        }
        nodes.Add(Node("leaf", "text", 100, 100, 100, 100, ("text", "LEAFCARD")));
        var path = Write(new JsonObject { ["nodes"] = nodes }.ToJsonString());
        var markdown = File.ReadAllText(await ConvertAsync(path, "md"));
        Assert.Contains("LEAFCARD", markdown);
    }
}
