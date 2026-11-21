// Builds Source engine models (.mdl version 48 with its .vvd and .dx90.vtx) byte by byte for the tests: bones, textures
// with one texture folder, one body part with one model, and one mesh per material, each a triangle list in one strip
// group. Layouts as in Valve's studio.h and optimize.h (see Models3D/SourceModel.cs). For decompiling (#1526) a model
// can also get an attachment, a second LOD, one animation with its sequence and event, and a .phy with one convex piece.

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
    /// The extras a decompiled model is tested with: <paramref name="Animation"/> adds a three-frame animation "@wave"
    /// (bone 0 at rest with a Quaternion64 rotation and a Vector48 position of (1, 2, 3); bone 1's X rotation going up 1
    /// radian a frame from its bind pose, in a run-length encoded track) and a looping sequence "wave" with activity
    /// ACT_IDLE and event 5004 "test.sound" halfway; <paramref name="Attachment"/> adds "muzzle" on bone 1 at (1, 2, 3);
    /// <paramref name="Lod1"/> is a second LOD (switch point 50) with these triangles instead; <paramref name="Physics"/>
    /// writes a .phy with one convex piece, a tetrahedron with corners (0, 0, 0), (1, 0, 0), (0, 1, 0) and (0, 0, 1).
    /// </summary>
    public sealed record Extras(bool Animation = false, bool Attachment = false, Mesh[]? Lod1 = null, bool Physics = false);

    /// <summary>
    /// Writes "<paramref name="name"/>.mdl", ".vvd" and ".dx90.vtx" into <paramref name="folder"/> and returns the .mdl;
    /// <paramref name="vvdChecksum"/> sets a different checksum in the .vvd; <paramref name="fixups"/> stores LOD 0 as
    /// two runs of vertices.
    /// </summary>
    public static string Write(string folder, string name, string textureFolder, string[] textures, Mesh[] meshes,
        Bone[]? bones = null, int vvdChecksum = Checksum, bool fixups = false, Extras? extras = null)
    {
        bones ??= [new Bone("static_prop", -1, Vector3.Zero, Vector3.Zero)];
        extras ??= new Extras();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".mdl");
        File.WriteAllBytes(path, Mdl(name, textureFolder, textures, meshes, bones, extras));
        File.WriteAllBytes(Path.Combine(folder, name + ".vvd"), Vvd(meshes, vvdChecksum, fixups));
        File.WriteAllBytes(Path.Combine(folder, name + ".dx90.vtx"), Vtx(meshes, extras.Lod1));
        if (extras.Physics)
            File.WriteAllBytes(Path.Combine(folder, name + ".phy"), Phy(name));
        return path;
    }

    private static byte[] Mdl(string name, string textureFolder, string[] textures, Mesh[] meshes, Bone[] bones, Extras extras)
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
        I32(span, 332, 1); // CONTENTS_SOLID, as studiomdl writes by default
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
        if (extras.Animation && bones.Length > 1)
            Vec(span, boneAt + 216 + 84, new Vector3(0.01f, 0, 0)); // bone 1's rotation scale
        var result = data.Concat(strings.ToArray()).ToArray();
        if (extras.Animation || extras.Attachment)
            result = [.. result, .. MdlExtras(result, extras)];
        I32(result, 76, result.Length);
        return result;
    }

    /// <summary>The attachment, animation and sequence of <paramref name="extras"/>, appended to <paramref name="mdl"/> (whose header is set).</summary>
    private static byte[] MdlExtras(byte[] mdl, Extras extras)
    {
        const int attachment = 92, description = 100, sequence = 212, animationEvent = 80;
        var at = mdl.Length;
        var attachmentAt = at;
        var descriptionAt = attachmentAt + (extras.Attachment ? attachment : 0);
        var animationAt = descriptionAt + description;
        // Bone 0: Quaternion64 (identity) and Vector48 (1, 2, 3); bone 1: three track offsets, then the X track.
        var entry1 = animationAt + 4 + 8 + 6;
        var trackAt = entry1 + 4 + 6;
        var sequenceAt = trackAt + 2 + 3 * 2;
        var eventAt = sequenceAt + sequence;
        var indexAt = eventAt + animationEvent;
        var stringsAt = indexAt + 4;

        var strings = new MemoryStream();
        int Str(string text)
        {
            var position = stringsAt + (int)strings.Length;
            strings.Write(Encoding.ASCII.GetBytes(text + "\0"));
            return position;
        }
        var data = new byte[stringsAt - at];
        Span<byte> Local(int absolute) => data.AsSpan(absolute - at);

        if (extras.Attachment)
        {
            I32(mdl, 240, 1);
            I32(mdl, 244, attachmentAt);
            I32(Local(attachmentAt), 0, Str("muzzle") - attachmentAt);
            I32(Local(attachmentAt), 8, 1);
            // Identity rotation, position (1, 2, 3): rows of a 3×4 matrix.
            F32(Local(attachmentAt), 12, 1);
            F32(Local(attachmentAt), 24, 1);
            F32(Local(attachmentAt), 32, 1);
            F32(Local(attachmentAt), 40, 2);
            F32(Local(attachmentAt), 52, 1);
            F32(Local(attachmentAt), 56, 3);
        }
        if (extras.Animation)
        {
            I32(mdl, 180, 1);
            I32(mdl, 184, descriptionAt);
            var d = Local(descriptionAt);
            I32(d, 4, Str("@wave") - descriptionAt);
            F32(d, 8, 30);
            I32(d, 16, 3);
            I32(d, 56, animationAt - descriptionAt);

            var a = Local(animationAt);
            a[0] = 0;
            a[1] = 0x21; // Quaternion64 rotation and Vector48 position
            BinaryPrimitives.WriteInt16LittleEndian(a[2..], (short)(entry1 - animationAt));
            const ulong zero = 1048576;
            BinaryPrimitives.WriteUInt64LittleEndian(a[4..], zero | (zero << 21) | (zero << 42));
            BinaryPrimitives.WriteHalfLittleEndian(a[12..], (Half)1);
            BinaryPrimitives.WriteHalfLittleEndian(a[14..], (Half)2);
            BinaryPrimitives.WriteHalfLittleEndian(a[16..], (Half)3);
            var e1 = Local(entry1);
            e1[0] = 1;
            e1[1] = 0x08; // animated rotation
            BinaryPrimitives.WriteInt16LittleEndian(e1[4..], (short)(trackAt - (entry1 + 4)));
            var track = Local(trackAt);
            track[0] = 3; // valid
            track[1] = 3; // total
            BinaryPrimitives.WriteInt16LittleEndian(track[2..], 0);
            BinaryPrimitives.WriteInt16LittleEndian(track[4..], 100);
            BinaryPrimitives.WriteInt16LittleEndian(track[6..], 200);

            I32(mdl, 188, 1);
            I32(mdl, 192, sequenceAt);
            var q = Local(sequenceAt);
            I32(q, 4, Str("wave") - sequenceAt);
            I32(q, 8, Str("ACT_IDLE") - sequenceAt);
            I32(q, 12, 1); // looping
            I32(q, 20, 1);
            I32(q, 24, 1);
            I32(q, 28, eventAt - sequenceAt);
            I32(q, 56, 1);
            I32(q, 60, indexAt - sequenceAt);
            I32(q, 68, 1);
            I32(q, 72, 1);
            I32(q, 76, -1);
            I32(q, 80, -1);
            F32(q, 104, 0.2f);
            F32(q, 108, 0.2f);
            var e = Local(eventAt);
            F32(e, 0, 0.5f);
            I32(e, 4, 5004);
            Encoding.ASCII.GetBytes("test.sound").CopyTo(e[12..]);
        }
        return [.. data, .. strings.ToArray()];
    }

    /// <summary>A .phy with one solid of one convex piece (see <see cref="Extras"/>) and its key values.</summary>
    private static byte[] Phy(string name)
    {
        // Source (x, y, z) is IVP (x, −z, y) in metres.
        Vector3[] corners = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        int[][] faces = [[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]];
        const int header = 16, vphy = 28, surface = 48, ledge = 16, triangle = 16, point = 16, node = 28;
        var solidAt = header + 4;
        var surfaceAt = solidAt + vphy;
        var ledgeAt = surfaceAt + surface;
        var pointsAt = ledgeAt + ledge + faces.Length * triangle;
        var nodeAt = pointsAt + corners.Length * point;
        var end = nodeAt + node;
        var data = new byte[end];
        var span = data.AsSpan();
        I32(span, 0, header);
        I32(span, 8, 1);
        I32(span, 12, Checksum);
        I32(span, header, end - solidAt);
        "VPHY"u8.CopyTo(span[solidAt..]);
        I32(span, surfaceAt + 32, nodeAt - surfaceAt);
        "IVPS"u8.CopyTo(span[(surfaceAt + 44)..]);
        I32(span, ledgeAt, pointsAt - ledgeAt);
        BinaryPrimitives.WriteInt16LittleEndian(span[(ledgeAt + 12)..], (short)faces.Length);
        for (var f = 0; f < faces.Length; f++)
        {
            for (var e = 0; e < 3; e++)
                BinaryPrimitives.WriteUInt16LittleEndian(span[(ledgeAt + ledge + f * triangle + 4 + e * 4)..], (ushort)faces[f][e]);
        }
        for (var c = 0; c < corners.Length; c++)
            Vec(span, pointsAt + c * point, new Vector3(corners[c].X, -corners[c].Z, corners[c].Y) * 0.0254f);
        I32(span, nodeAt + 4, ledgeAt - nodeAt);
        var text = $"solid {{\n\"index\" \"0\"\n\"name\" \"{name}_phys\"\n\"mass\" \"5.000000\"\n\"inertia\" \"1.000000\"\n"
            + "\"damping\" \"0.000000\"\n\"rotdamping\" \"0.000000\"\n}\neditparams {\n\"rootname\" \"\"\n\"totalmass\" \"5.000000\"\n\"concave\" \"0\"\n}\n";
        return [.. data, .. Encoding.ASCII.GetBytes(text + "\0")];
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

    private static byte[] Vtx(Mesh[] meshes, Mesh[]? lod1)
    {
        const int header = 36, bodyPart = 8, model = 8, lod = 12, mesh = 9, group = 25, strip = 27;
        Mesh[][] lods = lod1 is null ? [meshes] : [meshes, lod1];
        var bodyPartAt = header;
        var modelAt = bodyPartAt + bodyPart;
        var lodAt = modelAt + model;
        var meshAt = lodAt + lods.Length * lod;
        var groupAt = meshAt + lods.Sum(l => l.Length) * mesh;
        var dataAt = groupAt + lods.Sum(l => l.Length) * group;

        var tail = new MemoryStream();
        var bytes = new byte[dataAt];
        var span = bytes.AsSpan();
        I32(span, 0, 7);
        I32(span, 4, 24);
        I32(span, 16, Checksum);
        I32(span, 20, lods.Length);
        I32(span, 28, 1);
        I32(span, 32, bodyPartAt);
        I32(span, bodyPartAt, 1);
        I32(span, bodyPartAt + 4, modelAt - bodyPartAt);
        I32(span, modelAt, lods.Length);
        I32(span, modelAt + 4, lodAt - modelAt);
        var meshIndex = 0;
        for (var l = 0; l < lods.Length; l++)
        {
            var lodPos = lodAt + l * lod;
            I32(span, lodPos, lods[l].Length);
            I32(span, lodPos + 4, meshAt + meshIndex * mesh - lodPos);
            F32(span, lodPos + 8, l * 50);
            meshIndex += lods[l].Length;
        }
        var all = lods.SelectMany(l => l).ToArray();
        for (var i = 0; i < all.Length; i++)
        {
            var m = all[i];
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
