// Builds Source engine models (.mdl version 48 with its .vvd and .dx90.vtx) byte by byte for the tests: bones, textures
// with one texture folder, one body part with one model, and one mesh per material, each a triangle list in one strip
// group. Layouts as in Valve's studio.h and optimize.h (see Models3D/SourceModel.cs).

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Tests;

internal static class SourceModelBuilder
{
    /// <summary>A vertex in Source's axes and units (inches, Z up), UV from the top left, weighted to up to two bones.</summary>
    public sealed record Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv, int Bone = 0, int Bone2 = -1, float Weight2 = 0);

    /// <summary>A mesh: its material (an index into the textures), vertices and triangles in Source's (clockwise) order.</summary>
    public sealed record Mesh(int Material, Vertex[] Vertices, int[] Triangles);

    /// <summary>A bone in its bind pose relative to its parent.</summary>
    public sealed record Bone(string Name, int Parent, Vector3 Position, Vector3 Rotation);

    public const int Checksum = 0x1EE7;

    /// <summary>
    /// Writes "<paramref name="name"/>.mdl", ".vvd" and ".dx90.vtx" into <paramref name="folder"/> and returns the .mdl;
    /// <paramref name="vvdChecksum"/> sets a different checksum in the .vvd; <paramref name="fixups"/> stores LOD 0 as
    /// two runs of vertices.
    /// </summary>
    public static string Write(string folder, string name, string textureFolder, string[] textures, Mesh[] meshes,
        Bone[]? bones = null, int vvdChecksum = Checksum, bool fixups = false)
    {
        bones ??= [new Bone("static_prop", -1, Vector3.Zero, Vector3.Zero)];
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".mdl");
        File.WriteAllBytes(path, Mdl(name, textureFolder, textures, meshes, bones));
        File.WriteAllBytes(Path.Combine(folder, name + ".vvd"), Vvd(meshes, vvdChecksum, fixups));
        File.WriteAllBytes(Path.Combine(folder, name + ".dx90.vtx"), Vtx(meshes));
        return path;
    }

    private static byte[] Mdl(string name, string textureFolder, string[] textures, Mesh[] meshes, Bone[] bones)
    {
        const int header = 408, bone = 216, texture = 64, bodyPart = 16, model = 148, mesh = 116;
        var boneAt = header;
        var textureAt = boneAt + bones.Length * bone;
        var folderTableAt = textureAt + textures.Length * texture;
        var skinAt = folderTableAt + 4;
        var bodyPartAt = skinAt + textures.Length * 2 + 2;
        var modelAt = bodyPartAt + bodyPart;
        var meshAt = modelAt + model;
        var stringsAt = meshAt + meshes.Length * mesh;

        var strings = new MemoryStream();
        int Str(string text)
        {
            var at = stringsAt + (int)strings.Length;
            strings.Write(Encoding.ASCII.GetBytes(text + "\0"));
            return at;
        }

        var data = new byte[stringsAt];
        var span = data.AsSpan();
        "IDST"u8.CopyTo(span);
        I32(span, 4, 48);
        I32(span, 8, Checksum);
        Encoding.ASCII.GetBytes($"test/{name}.mdl").CopyTo(span[12..]);
        I32(span, 156, bones.Length);
        I32(span, 160, boneAt);
        I32(span, 204, textures.Length);
        I32(span, 208, textureAt);
        I32(span, 212, 1);
        I32(span, 216, folderTableAt);
        I32(span, 220, textures.Length);
        I32(span, 224, 1);
        I32(span, 228, skinAt);
        I32(span, 232, 1);
        I32(span, 236, bodyPartAt);

        for (var i = 0; i < bones.Length; i++)
        {
            var at = boneAt + i * bone;
            I32(span, at, Str(bones[i].Name) - at);
            I32(span, at + 4, bones[i].Parent);
            Vec(span, at + 32, bones[i].Position);
            F32(span, at + 56, 1); // quaternion w
            Vec(span, at + 60, bones[i].Rotation);
        }
        for (var i = 0; i < textures.Length; i++)
        {
            var at = textureAt + i * texture;
            I32(span, at, Str(textures[i]) - at);
            BinaryPrimitives.WriteInt16LittleEndian(span[(skinAt + i * 2)..], (short)i);
        }
        I32(span, folderTableAt, Str(textureFolder));
        I32(span, bodyPartAt, Str("body") - bodyPartAt);
        I32(span, bodyPartAt + 4, 1);
        I32(span, bodyPartAt + 8, 1);
        I32(span, bodyPartAt + 12, modelAt - bodyPartAt);
        Encoding.ASCII.GetBytes(name).CopyTo(span[modelAt..]);
        I32(span, modelAt + 72, meshes.Length);
        I32(span, modelAt + 76, meshAt - modelAt);
        I32(span, modelAt + 80, meshes.Sum(m => m.Vertices.Length));
        I32(span, modelAt + 84, 0);
        var first = 0;
        for (var i = 0; i < meshes.Length; i++)
        {
            var at = meshAt + i * mesh;
            I32(span, at, meshes[i].Material);
            I32(span, at + 4, modelAt - at);
            I32(span, at + 8, meshes[i].Vertices.Length);
            I32(span, at + 12, first);
            first += meshes[i].Vertices.Length;
        }
        var result = data.Concat(strings.ToArray()).ToArray();
        I32(result, 76, result.Length);
        return result;
    }

    private static byte[] Vvd(Mesh[] meshes, int checksum, bool fixups)
    {
        var vertices = meshes.SelectMany(m => m.Vertices).ToArray();
        var fixupCount = fixups ? 2 : 0;
        var fixupAt = 64;
        var vertexAt = fixupAt + fixupCount * 12;
        var data = new byte[vertexAt + vertices.Length * 48];
        var span = data.AsSpan();
        "IDSV"u8.CopyTo(span);
        I32(span, 4, 4);
        I32(span, 8, checksum);
        I32(span, 12, 1);
        I32(span, 16, vertices.Length);
        I32(span, 48, fixupCount);
        I32(span, 52, fixupAt);
        I32(span, 56, vertexAt);
        I32(span, 60, data.Length);
        if (fixups)
        {
            var half = vertices.Length / 2;
            I32(span, fixupAt, 0);
            I32(span, fixupAt + 4, 0);
            I32(span, fixupAt + 8, half);
            I32(span, fixupAt + 12, 0);
            I32(span, fixupAt + 16, half);
            I32(span, fixupAt + 20, vertices.Length - half);
        }
        for (var i = 0; i < vertices.Length; i++)
        {
            var at = vertexAt + i * 48;
            var v = vertices[i];
            var two = v.Bone2 >= 0;
            F32(span, at, two ? 1 - v.Weight2 : 1);
            F32(span, at + 4, two ? v.Weight2 : 0);
            span[at + 12] = (byte)v.Bone;
            span[at + 13] = (byte)Math.Max(0, v.Bone2);
            span[at + 15] = (byte)(two ? 2 : 1);
            Vec(span, at + 16, v.Position);
            Vec(span, at + 28, v.Normal);
            F32(span, at + 40, v.Uv.X);
            F32(span, at + 44, v.Uv.Y);
        }
        return data;
    }

    private static byte[] Vtx(Mesh[] meshes)
    {
        const int header = 36, bodyPart = 8, model = 8, lod = 12, mesh = 9, group = 25, strip = 27;
        var bodyPartAt = header;
        var modelAt = bodyPartAt + bodyPart;
        var lodAt = modelAt + model;
        var meshAt = lodAt + lod;
        var groupAt = meshAt + meshes.Length * mesh;
        var dataAt = groupAt + meshes.Length * group;

        var tail = new MemoryStream();
        var bytes = new byte[dataAt];
        var span = bytes.AsSpan();
        I32(span, 0, 7);
        I32(span, 4, 24);
        I32(span, 16, Checksum);
        I32(span, 20, 1);
        I32(span, 28, 1);
        I32(span, 32, bodyPartAt);
        I32(span, bodyPartAt, 1);
        I32(span, bodyPartAt + 4, modelAt - bodyPartAt);
        I32(span, modelAt, 1);
        I32(span, modelAt + 4, lodAt - modelAt);
        I32(span, lodAt, meshes.Length);
        I32(span, lodAt + 4, meshAt - lodAt);
        for (var i = 0; i < meshes.Length; i++)
        {
            var m = meshes[i];
            var meshPos = meshAt + i * mesh;
            var groupPos = groupAt + i * group;
            I32(span, meshPos, 1);
            I32(span, meshPos + 4, groupPos - meshPos);

            var vertexPos = dataAt + (int)tail.Length;
            foreach (var index in Enumerable.Range(0, m.Vertices.Length))
            {
                var vertex = new byte[9];
                vertex[3] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(vertex.AsSpan(4), (ushort)index);
                tail.Write(vertex);
            }
            var indexPos = dataAt + (int)tail.Length;
            var u16 = new byte[2];
            foreach (var index in m.Triangles)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)index);
                tail.Write(u16);
            }
            var stripPos = dataAt + (int)tail.Length;
            var stripBytes = new byte[strip];
            I32(stripBytes, 0, m.Triangles.Length);
            I32(stripBytes, 8, m.Vertices.Length);
            BinaryPrimitives.WriteInt16LittleEndian(stripBytes.AsSpan(16), 1);
            stripBytes[18] = 0x01; // triangle list
            tail.Write(stripBytes);

            I32(span, groupPos, m.Vertices.Length);
            I32(span, groupPos + 4, vertexPos - groupPos);
            I32(span, groupPos + 8, m.Triangles.Length);
            I32(span, groupPos + 12, indexPos - groupPos);
            I32(span, groupPos + 16, 1);
            I32(span, groupPos + 20, stripPos - groupPos);
        }
        return [.. bytes, .. tail.ToArray()];
    }

    private static void I32(Span<byte> span, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(span[at..], value);

    private static void F32(Span<byte> span, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(span[at..], value);

    private static void Vec(Span<byte> span, int at, Vector3 v)
    {
        F32(span, at, v.X);
        F32(span, at + 4, v.Y);
        F32(span, at + 8, v.Z);
    }

    /// <summary>
    /// A 10 × 10 inch floor tile facing up (material 0, four vertices, two triangles) and a triangle 5 inches above it
    /// (material 1), both clockwise as Source stores them.
    /// </summary>
    public static Mesh[] TileAndTriangle(bool weighted = false) =>
    [
        new(0,
        [
            new(new(0, 0, 0), Vector3.UnitZ, new(0, 1)),
            new(new(0, 10, 0), Vector3.UnitZ, new(0, 0)),
            new(new(10, 10, 0), Vector3.UnitZ, new(1, 0)),
            new(new(10, 0, 0), Vector3.UnitZ, new(1, 1), weighted ? 0 : 0, weighted ? 1 : -1, weighted ? 0.25f : 0),
        ], [0, 1, 2, 0, 2, 3]),
        new(1,
        [
            new(new(0, 0, 5), Vector3.UnitZ, new(0, 0), weighted ? 1 : 0),
            new(new(0, 10, 5), Vector3.UnitZ, new(0, 1), weighted ? 1 : 0),
            new(new(10, 10, 5), Vector3.UnitZ, new(1, 1), weighted ? 1 : 0),
        ], [0, 1, 2]),
    ];
}
