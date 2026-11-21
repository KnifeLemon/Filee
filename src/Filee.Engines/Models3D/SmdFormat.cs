// Studiomdl data (.smd), the text format Valve's studiomdl compiles Source engine models from:
//
//   version 1
//   nodes                       one line per bone: index "name" parent (−1 for a root)
//   skeleton / time 0           one line per bone: index, position x y z, rotation x y z (radians), relative to its parent
//   triangles                   per triangle a material line, then three vertex lines:
//                               parent bone, position x y z, normal x y z, u v [, link count, (bone weight) per link]
//   end                         after each section
//
// Positions are in model space, Z up, in inches (Source's unit), triangles counter-clockwise, UVs from the bottom left.
// Reading takes the triangles of a reference SMD as a static model (an animation SMD has none). Writing from another
// model gives a static prop: one root bone, every vertex weighted to it. Writing from a Source model (MDL) keeps its
// bones, bind pose and weights, as a reference mesh studiomdl can compile again.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Models3D;

/// <summary>Reads and writes studiomdl data files.</summary>
internal static class SmdFormat
{
    /// <summary>The most triangles Filee reads from one file (about 1 GB of text).</summary>
    private const int MaxTriangles = 10_000_000;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The triangles of a reference SMD, one part per material.</summary>
    public static Model3D Read(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var model = new Model3D();
        var parts = new Dictionary<string, MeshPart>(StringComparer.Ordinal);
        var length = Math.Max(1, new FileInfo(path).Length);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        string? Next()
        {
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                var text = line.Trim();
                if (text.Length > 0 && !text.StartsWith("//", StringComparison.Ordinal))
                    return text;
            }
            return null;
        }

        var triangles = 0;
        var corners = new int[3];
        string? section = null;
        while (Next() is { } line)
        {
            if (section is null)
            {
                section = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
                if (section is "version")
                    section = null;
                continue;
            }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                section = null;
                continue;
            }
            if (section != "triangles")
                continue; // nodes, skeleton, vertex animation: not part of the static mesh

            // A material line, then its triangle's three vertices.
            cancellationToken.ThrowIfCancellationRequested();
            if (++triangles > MaxTriangles)
                throw new InvalidDataException($"The SMD has more than {MaxTriangles:N0} triangles.");
            var material = line;
            if (!parts.TryGetValue(material, out var part))
            {
                model.Materials.Add(new Material3D { Name = Path.GetFileNameWithoutExtension(material) });
                part = new MeshPart { Name = Path.GetFileNameWithoutExtension(material), Material = model.Materials.Count - 1, Normals = [], TexCoords = [] };
                parts[material] = part;
                model.Parts.Add(part);
            }
            for (var k = 0; k < 3; k++)
            {
                var vertex = Next() ?? throw new InvalidDataException($"The SMD ends inside a triangle (line {lineNumber}).");
                var fields = vertex.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 9)
                    throw new InvalidDataException($"Line {lineNumber} of the SMD is not a triangle's vertex.");
                var position = new Vector3(Number(fields[1], lineNumber), Number(fields[2], lineNumber), Number(fields[3], lineNumber));
                var normal = new Vector3(Number(fields[4], lineNumber), Number(fields[5], lineNumber), Number(fields[6], lineNumber));
                var uv = new Vector2(Number(fields[7], lineNumber), Number(fields[8], lineNumber));
                corners[k] = part.AddVertex(MeshMath.ToYUp(position) * SourceModel.MillimetresPerUnit, MeshMath.ToYUp(normal), uv);
            }
            part.AddTriangle(corners[0], corners[1], corners[2]);
            progress?.Report(Math.Min(1, (double)stream.Position / length));
        }
        return model;
    }

    private static float Number(string text, int line) =>
        float.TryParse(text, NumberStyles.Float, Invariant, out var value) && float.IsFinite(value)
            ? value
            : throw new InvalidDataException($"Line {line} of the SMD has \"{text}\" where a number belongs.");

    /// <summary>A static prop: one root bone at the origin, every vertex weighted to it.</summary>
    public static void Write(Model3D model, Stream stream, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("version 1");
        writer.WriteLine("nodes");
        writer.WriteLine("0 \"root\" -1");
        writer.WriteLine("end");
        writer.WriteLine("skeleton");
        writer.WriteLine("time 0");
        writer.WriteLine("0 0.000000 0.000000 0.000000 0.000000 0.000000 0.000000");
        writer.WriteLine("end");
        writer.WriteLine("triangles");
        foreach (var part in model.Parts)
        {
            if (part.Normals is null)
                part.ComputeNormals();
            var material = MaterialName(model.MaterialOf(part), part.Name);
            var indices = part.Indices;
            for (var t = 0; t + 2 < indices.Count; t += 3)
            {
                if (t % 3000 == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                writer.WriteLine(material);
                for (var k = 0; k < 3; k++)
                {
                    var v = indices[t + k];
                    var position = MeshMath.ToZUp(part.Position(v)) / SourceModel.MillimetresPerUnit;
                    WriteVertex(writer, 0, position, MeshMath.ToZUp(part.Normal(v)), part.TexCoord(v));
                    writer.WriteLine(" 1 0 1.000000");
                }
            }
        }
        writer.WriteLine("end");
    }

    /// <summary>A Source model as a reference SMD: its bones in their bind pose, its triangles with their weights.</summary>
    public static void WriteSkeletal(SourceModel model, Stream stream, CancellationToken cancellationToken) =>
        WriteSkeletal(model, model.Meshes, stream, cancellationToken);

    /// <summary>A reference SMD of <paramref name="meshes"/> (some of <paramref name="model"/>'s) with its bones and weights.</summary>
    public static void WriteSkeletal(SourceModel model, IReadOnlyList<SourceModel.Mesh> meshes, Stream stream,
        CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { NewLine = "\n" };
        var bones = WriteBindPose(writer, model.Bones);
        writer.WriteLine("triangles");
        foreach (var mesh in meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var material = Clean(mesh.Material);
            // Source's triangles are clockwise; an SMD's counter-clockwise.
            for (var t = 0; t + 2 < mesh.Triangles.Count; t += 3)
            {
                writer.WriteLine(material);
                foreach (var index in new[] { mesh.Triangles[t + 2], mesh.Triangles[t + 1], mesh.Triangles[t] })
                {
                    var v = model.Vertices[index];
                    var bone = Math.Clamp(v.Bone0, 0, bones.Count - 1);
                    WriteVertex(writer, bone, v.Position, v.Normal, new Vector2(v.Uv.X, 1 - v.Uv.Y));
                    var links = new StringBuilder();
                    var count = 0;
                    foreach (var (b, w) in new[] { (v.Bone0, v.Weight0), (v.Bone1, v.Weight1), (v.Bone2, v.Weight2) }.Take(v.BoneCount))
                    {
                        if (b < 0 || b >= bones.Count)
                            continue;
                        links.Append(Invariant, $" {b} {w:F6}");
                        count++;
                    }
                    writer.WriteLine(count > 0 ? $" {count}{links}" : $" 1 {bone} 1.000000");
                }
            }
        }
        writer.WriteLine("end");
    }

    /// <summary>An animation SMD: the bones, then every frame's pose of every bone.</summary>
    public static void WriteAnimation(IReadOnlyList<SourceModel.Bone> bones, IEnumerable<SourceAnimation.Pose[]> frames, Stream stream,
        CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { NewLine = "\n" };
        WriteNodes(writer, bones);
        writer.WriteLine("skeleton");
        var f = 0;
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine(string.Create(Invariant, $"time {f++}"));
            for (var i = 0; i < bones.Count; i++)
                WriteBone(writer, i, frame[i].Position, frame[i].Rotation);
        }
        writer.WriteLine("end");
    }

    /// <summary>
    /// A physics SMD: the bones in their bind pose, then each convex piece's triangles (material "phy"), every vertex
    /// on its piece's bone, with the face's normal.
    /// </summary>
    public static void WritePhysics(IReadOnlyList<SourceModel.Bone> bones,
        IEnumerable<(int Bone, IReadOnlyList<Vector3> Points, IReadOnlyList<int> Triangles)> pieces, Stream stream,
        CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { NewLine = "\n" };
        var skeleton = WriteBindPose(writer, bones);
        writer.WriteLine("triangles");
        foreach (var (pieceBone, points, triangles) in pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bone = Math.Clamp(pieceBone, 0, skeleton.Count - 1);
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var (a, b, c) = (points[triangles[t]], points[triangles[t + 1]], points[triangles[t + 2]]);
                var normal = Vector3.Cross(b - a, c - a);
                normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitZ;
                writer.WriteLine("phy");
                foreach (var p in new[] { a, b, c })
                {
                    WriteVertex(writer, bone, p, normal, Vector2.Zero);
                    writer.WriteLine(string.Create(Invariant, $" 1 {bone} 1.000000"));
                }
            }
        }
        writer.WriteLine("end");
    }

    /// <summary>The nodes and the bind pose ("time 0"); a model without bones gets one root bone. Returns the bones written.</summary>
    private static IReadOnlyList<SourceModel.Bone> WriteBindPose(StreamWriter writer, IReadOnlyList<SourceModel.Bone> modelBones)
    {
        IReadOnlyList<SourceModel.Bone> bones = modelBones.Count > 0 ? modelBones : [new SourceModel.Bone("root", -1, Vector3.Zero, Vector3.Zero)];
        WriteNodes(writer, bones);
        writer.WriteLine("skeleton");
        writer.WriteLine("time 0");
        for (var i = 0; i < bones.Count; i++)
            WriteBone(writer, i, bones[i].Position, bones[i].Rotation);
        writer.WriteLine("end");
        return bones;
    }

    private static void WriteNodes(StreamWriter writer, IReadOnlyList<SourceModel.Bone> bones)
    {
        writer.WriteLine("version 1");
        writer.WriteLine("nodes");
        for (var i = 0; i < bones.Count; i++)
            writer.WriteLine($"{i} \"{bones[i].Name.Replace('"', '\'')}\" {bones[i].Parent}");
        writer.WriteLine("end");
    }

    private static void WriteBone(StreamWriter writer, int index, Vector3 p, Vector3 r) =>
        writer.WriteLine(string.Create(Invariant, $"{index} {p.X:F6} {p.Y:F6} {p.Z:F6} {r.X:F6} {r.Y:F6} {r.Z:F6}"));

    private static void WriteVertex(StreamWriter writer, int bone, Vector3 p, Vector3 n, Vector2 uv) =>
        writer.Write(string.Create(Invariant, $"{bone} {p.X:F6} {p.Y:F6} {p.Z:F6} {n.X:F6} {n.Y:F6} {n.Z:F6} {uv.X:F6} {uv.Y:F6}"));

    /// <summary>The material line: studiomdl looks the name up as a .vmt, so it is the material's or texture's name.</summary>
    private static string MaterialName(Material3D? material, string partName)
    {
        var name = material?.Name is { Length: > 0 } named ? named
            : material?.BaseColorTexture?.Name is { Length: > 0 } texture ? texture
            : partName;
        return Clean(name);
    }

    /// <summary>One word without spaces or quotes; "default" when nothing is left.</summary>
    private static string Clean(string name)
    {
        var clean = new string([.. name.Trim().Select(c => char.IsWhiteSpace(c) || c == '"' ? '_' : c)]);
        return clean.Length == 0 ? "default" : clean;
    }
}
