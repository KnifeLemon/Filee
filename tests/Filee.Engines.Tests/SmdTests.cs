// Studiomdl data (.smd, #1526): an MDL becomes a reference SMD with its bones, bind pose and weights (as studiomdl
// reads it: counter-clockwise, UVs from the bottom left, inches, Z up); other models become a static prop with one root
// bone; an SMD's triangles become a model again.

using System.Globalization;
using System.Numerics;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Models3D;
using static Filee.Engines.Tests.SourceModelBuilder;

namespace Filee.Engines.Tests;

public class SmdTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> ConvertOkAsync(string source, string target)
    {
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    /// <summary>The lines of a section ("nodes", "skeleton", "triangles") up to its "end".</summary>
    private static List<string> Section(string[] lines, string name)
    {
        var start = Array.IndexOf(lines, name);
        Assert.True(start >= 0, $"no {name} section");
        return [.. lines.Skip(start + 1).TakeWhile(l => l != "end")];
    }

    private static float[] Numbers(string line) =>
        [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => float.Parse(f, CultureInfo.InvariantCulture))];

    [Fact]
    public void Smd_converts_to_and_from_the_3d_formats_in_one_step()
    {
        var planner = fx.Catalog.CreatePlanner(["model3d"]);
        foreach (var (from, to) in new[] { ("mdl", "smd"), ("obj", "smd"), ("glb", "smd"), ("fbx", "smd"), ("smd", "glb"), ("smd", "stl"), ("smd", "obj") })
            Assert.Single(planner.Plan(from, to)!.Steps);
    }

    [Fact]
    public async Task A_model_becomes_a_reference_smd_with_its_bones_and_weights()
    {
        Bone[] bones =
        [
            new("root", -1, Vector3.Zero, Vector3.Zero),
            new("lid", 0, new Vector3(0, 0, 5), new Vector3(0, 0, MathF.PI / 2)),
        ];
        var mdl = Write(fx.NewFolder(), "box", "", ["brick", "glass"], TileAndTriangle(weighted: true), bones);

        var smd = await ConvertOkAsync(mdl, "smd");

        var lines = File.ReadAllLines(smd);
        Assert.Equal("version 1", lines[0]);
        Assert.Equal(["0 \"root\" -1", "1 \"lid\" 0"], Section(lines, "nodes"));
        var skeleton = Section(lines, "skeleton");
        Assert.Equal("time 0", skeleton[0]);
        Assert.Equal([1, 0, 0, 5, 0, 0, MathF.PI / 2], Numbers(skeleton[2]), new FloatComparer());

        var triangles = Section(lines, "triangles");
        Assert.Equal(3 * 4, triangles.Count); // three triangles, each a material line and three vertices
        Assert.Equal(["brick", "brick", "glass"], new[] { triangles[0], triangles[4], triangles[8] });
        // The tile's first triangle (0, 1, 2) in Source's order comes reversed: (10, 10, 0), (0, 10, 0), (0, 0, 0).
        Assert.Equal([0, 10, 10, 0, 0, 0, 1, 1, 1, 1, 0, 1], Numbers(triangles[1]), new FloatComparer());
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 1], Numbers(triangles[3]), new FloatComparer());
        // Its corner at (10, 0, 0) is weighted 0.75 / 0.25 to both bones; UV (1, 1) from the top is (1, 0) from the bottom.
        Assert.Equal([0, 10, 0, 0, 0, 0, 1, 1, 0, 2, 0, 0.75f, 1, 0.25f], Numbers(triangles[5]), new FloatComparer());
        // The triangle above it hangs on the lid.
        Assert.Equal(1, Numbers(triangles[9])[0]);
        Assert.Equal([1, 1, 1], Numbers(triangles[9])[^3..], new FloatComparer());
    }

    [Fact]
    public async Task An_smd_becomes_a_model_facing_the_same_way_as_its_mdl()
    {
        var mdl = Write(fx.NewFolder(), "box", "", ["brick", "glass"], TileAndTriangle());
        var fromMdl = ModelConverter.Read(mdl, "mdl", null, Ct);

        var smd = await ConvertOkAsync(mdl, "smd");
        var fromSmd = ModelConverter.Read(smd, "smd", null, Ct);

        Assert.Equal(fromMdl.TriangleCount, fromSmd.TriangleCount);
        Assert.Equal(fromMdl.Bounds(), fromSmd.Bounds());
        Assert.Equal(["brick", "glass"], fromSmd.Materials.Select(m => m.Name));
        foreach (var part in fromSmd.Parts)
        {
            for (var t = 0; t < part.TriangleCount; t++)
                Assert.True(Vector3.Dot(part.FaceNormal(t), Vector3.UnitY) > 0.99f);
        }

        var glb = await ConvertOkAsync(smd, "glb");
        Assert.Equal(3, ModelConverter.Read(glb, "glb", null, Ct).TriangleCount);
    }

    [Fact]
    public void Another_model_becomes_a_static_prop_and_reads_back()
    {
        var model = new Model3D();
        model.Materials.Add(new Material3D { Name = "Painted Metal" });
        var part = new MeshPart { Name = "plate", Material = 0, TexCoords = [] };
        part.AddVertex(new Vector3(0, 0, 0), uv: new Vector2(0, 0));
        part.AddVertex(new Vector3(254, 0, 0), uv: new Vector2(1, 0));
        part.AddVertex(new Vector3(0, 0, -254), uv: new Vector2(0, 1));
        part.AddTriangle(0, 1, 2);
        model.Parts.Add(part);

        var path = Path.Combine(fx.NewFolder(), "plate.smd");
        using (var stream = File.Create(path))
            SmdFormat.Write(model, stream, Ct);

        var lines = File.ReadAllLines(path);
        Assert.Equal(["0 \"root\" -1"], Section(lines, "nodes"));
        var triangles = Section(lines, "triangles");
        Assert.Equal("Painted_Metal", triangles[0]);
        // 254 mm along X is 10 inches; −254 mm along Z (Y up) is +10 inches along Y (Z up).
        Assert.Equal([0, 10, 0, 0], Numbers(triangles[2])[..4], new FloatComparer());
        Assert.Equal([0, 0, 10, 0], Numbers(triangles[3])[..4], new FloatComparer());
        Assert.EndsWith(" 1 0 1.000000", triangles[1]);

        var back = SmdFormat.Read(path, null, Ct);
        back.Validate();
        Assert.Equal(1, back.TriangleCount);
        Assert.True(Vector3.Distance(model.Bounds().Max, back.Bounds().Max) < 0.01f);
        Assert.True(Vector3.Distance(model.Bounds().Min, back.Bounds().Min) < 0.01f);
        Assert.Equal(new Vector2(1, 0), back.Parts[0].TexCoord(1));
    }

    [Fact]
    public void A_broken_smd_is_refused_with_its_line()
    {
        var path = Path.Combine(fx.NewFolder(), "broken.smd");
        File.WriteAllText(path, "version 1\ntriangles\nmat\n0 1 2 3 0 0 1 0 0\n0 1 2 x 0 0 1 0 0\n");
        var error = Assert.Throws<InvalidDataException>(() => SmdFormat.Read(path, null, Ct));
        Assert.Contains("Line 5", error.Message);

        File.WriteAllText(path, "version 1\nnodes\n0 \"root\" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\n");
        var animation = Assert.Throws<InvalidDataException>(() => ModelConverter.Read(path, "smd", null, Ct));
        Assert.Contains("no triangles", animation.Message);
    }

    private sealed class FloatComparer : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => MathF.Abs(a - b) < 0.0005f;
        public int GetHashCode(float value) => 0;
    }
}
