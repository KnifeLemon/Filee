// Decompiling Source engine models (#1526): an .mdl with its .vvd, .vtx and .phy becomes a folder with the QC and SMDs
// studiomdl builds it from: a reference SMD per body group model, an SMD for a LOD that differs, the physics SMD, and an
// animation SMD per animation, with the QC's body groups, LODs, materials, attachments, sequences, events and collision
// model. The test models are built by SourceModelBuilder; damaged animations and physics are refused, as are models
// over the model-wide budgets, and no file is named after a Windows device.

using System.Globalization;
using System.Numerics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Models3D;

namespace Filee.Engines.Tests;

public class SourceDecompileTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceModelBuilder.Bone[] TwoBones =
    [
        new("root", -1, Vector3.Zero, Vector3.Zero),
        new("arm", 0, new Vector3(0, 0, 5), new Vector3(0.5f, 0, 0)),
    ];

    private async Task<string> DecompileAsync(string mdl)
    {
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.DecompileModelId);
        var job = await fx.ConvertAsync([mdl], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var folder = Assert.Single(job.Outputs);
        Assert.True(Directory.Exists(folder));
        return folder;
    }

    /// <summary>The numbers of an SMD line, after its first <paramref name="skip"/> words.</summary>
    private static float[] Numbers(string line, int skip = 1) =>
        [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(skip).Select(n => float.Parse(n, CultureInfo.InvariantCulture))];

    [Fact]
    public void Mdl_decompiles_into_a_folder_in_one_step()
    {
        var planner = fx.Catalog.CreatePlanner(["model3d"]);
        Assert.Single(planner.Plan("mdl", FormatRegistry.Folder)!.Steps);
        Assert.Null(planner.Plan("glb", FormatRegistry.Folder));
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.DecompileModelId);
        Assert.Equal(FormatRegistry.Folder, preset.TargetFormat);
    }

    [Fact]
    public async Task A_model_becomes_a_qc_with_its_reference_lod_and_physics_smds()
    {
        var meshes = SourceModelBuilder.TileAndTriangle();
        // LOD 1 keeps only the floor tile's first triangle.
        SourceModelBuilder.Mesh[] lod1 = [meshes[0] with { Triangles = [0, 1, 2] }, meshes[1]];
        var mdl = SourceModelBuilder.Write(fx.NewFolder(), "tile", "models\\props\\", ["brick", "glass"], meshes,
            extras: new SourceModelBuilder.Extras(Lod1: lod1, Physics: true, Attachment: true), bones: TwoBones);

        var folder = await DecompileAsync(mdl);

        Assert.Equal(["tile.qc", "tile.smd", "tile_lod1.smd", "tile_physics.smd"],
            Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        var qc = File.ReadAllText(Path.Combine(folder, "tile.qc"));
        Assert.Contains("$modelname \"test/tile.mdl\"", qc);
        Assert.Contains("$bodygroup \"body\"\n{\n\tstudio \"tile.smd\"\n}", qc);
        Assert.Contains("$lod 50\n{\n\treplacemodel \"tile.smd\" \"tile_lod1.smd\"\n}", qc);
        Assert.Contains("$cdmaterials \"models\\props\\\"", qc);
        Assert.Contains("$attachment \"muzzle\" \"arm\" 1 2 3 rotate 0 0 0", qc);
        Assert.Contains("$collisionmodel \"tile_physics.smd\"\n{\n\t$mass 5\n\t$inertia 1\n\t$damping 0\n\t$rotdamping 0\n}", qc);
        // Written for every model: contents, both boxes and a $definebone per bone (pitch, yaw, roll in degrees).
        Assert.Contains("$contents \"solid\"\n", qc);
        Assert.Contains("$cbox 0 0 0 0 0 0\n", qc);
        Assert.Contains("$definebone \"root\" \"\" 0 0 0 0 0 0 0 0 0 0 0 0\n", qc);
        Assert.Contains("$definebone \"arm\" \"root\" 0 0 5 0 0 28.64", qc);

        // The reference SMD: both bones, then 3 triangles (2 + 1), counter-clockwise as SMDs are.
        var reference = File.ReadAllLines(Path.Combine(folder, "tile.smd"));
        Assert.Equal(["version 1", "nodes", "0 \"root\" -1", "1 \"arm\" 0", "end", "skeleton", "time 0"], reference[..7]);
        Assert.Equal(3, reference.Count(l => l is "brick" or "glass"));
        Assert.Equal(1, File.ReadAllLines(Path.Combine(folder, "tile_lod1.smd")).Count(l => l == "brick"));

        // The physics SMD: the tetrahedron's four faces, its corners in inches.
        var physics = File.ReadAllLines(Path.Combine(folder, "tile_physics.smd"));
        var start = Array.IndexOf(physics, "triangles");
        Assert.Equal(4, physics.Count(l => l == "phy"));
        var corners = physics.Skip(start + 1).Where(l => l is not ("phy" or "end"))
            .Select(l => Numbers(l)[..3]).Select(n => new Vector3(n[0], n[1], n[2])).Distinct().ToList();
        Assert.Equal(4, corners.Count);
        foreach (var corner in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.Contains(corners, c => Vector3.Distance(c, corner) < 1e-4f);
    }

    [Fact]
    public async Task Animations_become_animation_smds_and_sequences()
    {
        var mdl = SourceModelBuilder.Write(fx.NewFolder(), "waver", "models\\", ["brick", "glass"], SourceModelBuilder.TileAndTriangle(),
            extras: new SourceModelBuilder.Extras(Animation: true), bones: TwoBones);

        var folder = await DecompileAsync(mdl);

        var qc = File.ReadAllText(Path.Combine(folder, "waver.qc"));
        // The brace on the line, a backslash in the path, the fade times always.
        Assert.Contains("$sequence \"wave\" {\n\t\"waver_anims\\wave.smd\"\n\tactivity \"ACT_IDLE\" 1\n\tfadein 0.2\n\tfadeout 0.2\n"
            + "\tfps 30\n\tloop\n\t{ event 5004 1 \"test.sound\" }\n}", qc);
        var lines = File.ReadAllLines(Path.Combine(folder, "waver_anims", "wave.smd"));
        Assert.Equal(3, lines.Count(l => l.StartsWith("time ", StringComparison.Ordinal)));
        for (var frame = 0; frame < 3; frame++)
        {
            var at = Array.IndexOf(lines, $"time {frame}");
            // Bone 0: the raw rotation (none) and position (1, 2, 3).
            Assert.Equal(new float[] { 1, 2, 3, 0, 0, 0 }, Numbers(lines[at + 1]));
            // Bone 1: its bind position, and its X rotation 0.5 + one radian a frame (100 × 0.01).
            var arm = Numbers(lines[at + 2]);
            Assert.Equal(new float[] { 0, 0, 5 }, arm[..3]);
            Assert.Equal(0.5f + frame, arm[3], 4);
        }
    }

    [Fact]
    public async Task Illumination_and_eye_positions_are_turned_back_as_the_qc_wrote_them()
    {
        var folder = fx.NewFolder();
        var mdl = SourceModelBuilder.Write(folder, "lit", "models\\", ["brick", "glass"], SourceModelBuilder.TileAndTriangle());
        var bytes = File.ReadAllBytes(mdl);
        // studiomdl stores QC (x, y, z) as (−y, x, z): (2, −1, 3) stored was (−1, −2, 3) in the QC.
        foreach (var (at, value) in new[] { (80, 4f), (84, 5f), (88, 6f), (92, 2f), (96, -1f), (100, 3f) })
            BitConverter.GetBytes(value).CopyTo(bytes, at);
        File.WriteAllBytes(mdl, bytes);

        var qc = File.ReadAllText(Path.Combine(await DecompileAsync(mdl), "lit.qc"));

        Assert.Contains("$illumposition -1 -2 3\n", qc);
        Assert.Contains("$eyeposition 5 -4 6\n", qc);
    }

    [Fact]
    public void Euler_angles_survive_a_quaternion()
    {
        var random = new Random(1526);
        for (var i = 0; i < 1000; i++)
        {
            var angles = new Vector3(random.NextSingle() * 6 - 3, random.NextSingle() * 3 - 1.5f, random.NextSingle() * 6 - 3);
            var q = SourceAnimation.FromEuler(angles);
            var back = SourceAnimation.FromEuler(SourceAnimation.ToEuler(q));
            Assert.True(MathF.Abs(Quaternion.Dot(q, back)) > 0.99999f, $"{angles} came back as {SourceAnimation.ToEuler(q)}");
        }
        // Source's order: yaw (Z) applied last; a quarter turn about Z takes X to Y.
        var turn = SourceAnimation.FromEuler(new Vector3(0, 0, MathF.PI / 2));
        Assert.True(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, turn)) < 1e-5f);
    }

    [Fact]
    public void Animations_lods_physics_and_entries_are_counted_against_model_wide_budgets()
    {
        var folder = fx.NewFolder();
        var meshes = SourceModelBuilder.TileAndTriangle();
        SourceModelBuilder.Mesh[] lod1 = [meshes[0] with { Triangles = [0, 1, 2] }, meshes[1]];
        var mdl = SourceModelBuilder.Write(folder, "big", "models\\", ["brick", "glass"], meshes,
            extras: new SourceModelBuilder.Extras(Animation: true, Physics: true, Lod1: lod1), bones: TwoBones);
        void TooLarge(Action read) =>
            Assert.Equal("error.document_too_large", Assert.Throws<ConversionRefusedException>(read).ErrorKey);

        // The animation: 3 frames of 2 bones, refused before anything is written.
        var refused = Directory.CreateDirectory(Path.Combine(folder, "a")).FullName;
        TooLarge(() => SourceDecompiler.Decompile(mdl, refused, Ct, maxBoneFrames: 5));
        Assert.Empty(Directory.EnumerateFileSystemEntries(refused));
        SourceDecompiler.Decompile(mdl, Directory.CreateDirectory(Path.Combine(folder, "b")).FullName, Ct, maxBoneFrames: 6);

        // Every body group model at every LOD: 3 triangles at LOD 0 and 2 at LOD 1.
        var model = SourceModel.Load(mdl, Ct);
        TooLarge(() => SourceModelDetails.Read(model, Ct, maxTriangles: 4));
        Assert.Equal(2, SourceModelDetails.Read(model, Ct, maxTriangles: 5).BodyParts.Single().Models.Single().Lods.Count);

        // The one event of the one sequence.
        TooLarge(() => SourceModelDetails.Read(model, Ct, maxEntries: 0));

        // The collision tetrahedron: 4 triangles and 4 points.
        var phy = Path.ChangeExtension(mdl, ".phy");
        TooLarge(() => SourcePhysics.Read(phy, maxTotal: 3));
        Assert.Equal(4, SourcePhysics.Read(phy, maxTotal: 4).Solids.Single().Pieces.Single().Points.Count);
    }

    [Theory]
    [InlineData("con", "_con")]
    [InlineData("CON.phy", "_CON.phy")]
    [InlineData("nul", "_nul")]
    [InlineData("aux", "_aux")]
    [InlineData("prn", "_prn")]
    [InlineData("com1", "_com1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("com0", "com0")]
    [InlineData("console", "console")]
    [InlineData("a/b:c", "a_b_c")]
    public void File_names_never_name_a_windows_device(string name, string safe) =>
        Assert.Equal(safe, SourceDecompiler.SafeName(name));

    [Fact]
    public async Task A_model_with_a_damaged_animation_or_physics_is_refused()
    {
        var folder = fx.NewFolder();
        var mdl = SourceModelBuilder.Write(folder, "bad", "models\\", ["brick", "glass"], SourceModelBuilder.TileAndTriangle(),
            extras: new SourceModelBuilder.Extras(Animation: true, Physics: true), bones: TwoBones);
        var original = File.ReadAllBytes(mdl);

        // An animation claiming a million frames.
        var bytes = (byte[])original.Clone();
        var description = BitConverter.ToInt32(bytes, 184);
        BitConverter.GetBytes(1_000_000).CopyTo(bytes, description + 16);
        File.WriteAllBytes(mdl, bytes);
        Assert.Throws<InvalidDataException>(() => SourceDecompiler.Decompile(mdl, Directory.CreateDirectory(Path.Combine(folder, "a")).FullName, Ct));

        // A bone entry chaining back to itself.
        bytes = (byte[])original.Clone();
        var animation = description + BitConverter.ToInt32(bytes, description + 56);
        var second = animation + BitConverter.ToInt16(bytes, animation + 2);
        BitConverter.GetBytes((short)0).CopyTo(bytes, second + 2);
        BitConverter.GetBytes((short)(animation - second)).CopyTo(bytes, second + 2);
        File.WriteAllBytes(mdl, bytes);
        Assert.Throws<InvalidDataException>(() => SourceDecompiler.Decompile(mdl, Directory.CreateDirectory(Path.Combine(folder, "b")).FullName, Ct));

        // A collision tree whose node points at itself.
        File.WriteAllBytes(mdl, original);
        var phy = Path.ChangeExtension(mdl, ".phy");
        var physics = File.ReadAllBytes(phy);
        var surface = 16 + 4 + 28;
        var node = surface + BitConverter.ToInt32(physics, surface + 32);
        BitConverter.GetBytes(-28).CopyTo(physics, node);
        File.WriteAllBytes(phy, physics);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            SourceDecompiler.Decompile(mdl, Directory.CreateDirectory(Path.Combine(folder, "c")).FullName, Ct), Ct));
        Assert.Contains("damaged", error.Message);
    }
}
