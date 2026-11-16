// Source engine models (.mdl, versions 44 to 49: Garry's Mod, Counter-Strike: Source, Team Fortress 2, Half-Life 2), read
// by Filee with the two files studiomdl writes beside them: the vertices (.vvd) and the triangles (.dx90.vtx, else .vtx,
// .dx80.vtx or .sw.vtx). Layouts (little endian), as in Valve's studio.h / optimize.h:
//
//   .mdl  studiohdr_t (408 bytes): bones (216 bytes each: name, parent, position, rotation), textures (64 bytes each:
//         name), texture folders ($cdmaterials), the skin table (shorts: family × reference), body parts (16 bytes: name,
//         models) → models (148 bytes: name, meshes, first vertex) → meshes (116 bytes: material, vertex offset)
//   .vvd  "IDSV", version 4, the checksum of its .mdl, vertices per LOD, fixups (which runs of vertices LOD 0 uses);
//         48 bytes a vertex: three bone weights, three bones, bone count, position, normal, UV
//   .vtx  version 7: body parts → models → LODs → meshes (9 bytes) → strip groups (25 bytes; 33 in later branches)
//         → their vertices (9 bytes: the index of the mesh's vertex) and triangle lists (or strips) of u16
//
// The model is read in its bind pose with the default body groups (the first model of each body part), the first skin
// and LOD 0; the rest (every body group and LOD, animations, physics) is read for decompiling (SourceDecompiler).
// Source is Z up, one unit an inch, triangles clockwise, UVs from the top left: Filee turns that into its own
// conventions (Y up, millimetres, counter-clockwise, bottom left).
// The textures are found as Garry's Mod finds them: the model's material (a .vmt) in a "materials" folder above the
// model, its $basetexture (a .vtf) decoded by the VTF reader.

using System.Numerics;
using System.Runtime.ExceptionServices;
using Filee.Core.Conversion;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Models3D;

/// <summary>A Source engine model as read from its .mdl, .vvd and .vtx.</summary>
internal sealed partial class SourceModel
{
    /// <summary>One inch, Source's unit, in Filee's millimetres.</summary>
    public const float MillimetresPerUnit = 25.4f;

    internal const int MaxBones = 1024;
    internal const int MaxTextures = 4096;
    internal const int MaxBodyParts = 512;
    internal const int MaxMeshes = 16384;
    internal const int MaxVertices = 4_000_000;

    /// <summary>A bone in its bind pose, relative to its parent (Source's axes and units).</summary>
    internal readonly record struct Bone(string Name, int Parent, Vector3 Position, Vector3 Rotation);

    /// <summary>A vertex in Source's axes and units, UV from the top left, with up to three bone weights.</summary>
    internal readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv, int BoneCount,
        int Bone0, int Bone1, int Bone2, float Weight0, float Weight1, float Weight2);

    /// <summary>One mesh: its part name, material and triangles (indices into <see cref="Vertices"/>, Source's winding).</summary>
    internal sealed record Mesh(string Part, string Material, List<int> Triangles);

    public string Name { get; private init; } = "";
    public List<Bone> Bones { get; } = [];
    public List<Vertex> Vertices { get; } = [];
    public List<Mesh> Meshes { get; } = [];

    /// <summary>The folders the model's materials are looked up in ($cdmaterials, "/" separated, without the end slash).</summary>
    public List<string> MaterialFolders { get; } = [];

    /// <summary>The material of every skin reference in the first skin family (what a mesh's material number selects).</summary>
    public List<string> SkinMaterials { get; private set; } = [];

    /// <summary>The .mdl, its file name and folder; the .vtx and its layout, kept for decompiling.</summary>
    internal SourceBytes Mdl { get; private init; } = new([], "");
    internal SourceBytes Vtx { get; private set; } = new([], "");
    internal bool ExtendedVtx { get; private set; }
    internal string MdlPath { get; private init; } = "";

    /// <summary>The most triangles Filee reads from one model, over all its meshes (as many as an SMD may have).</summary>
    public const int MaxTriangles = 10_000_000;

    /// <summary>
    /// Reads the model at <paramref name="mdlPath"/> with its .vvd and .vtx, refusing one with more than
    /// <paramref name="maxTriangles"/> triangles.
    /// </summary>
    public static SourceModel Load(string mdlPath, CancellationToken cancellationToken, int maxTriangles = MaxTriangles)
    {
        var mdl = SourceBytes.Read(mdlPath);
        var basePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mdlPath))!, Path.GetFileNameWithoutExtension(mdlPath));
        var vvdPath = Companion(basePath, [".vvd"])
            ?? throw new FileNotFoundException($"{Path.GetFileName(mdlPath)} needs its vertex file {Path.GetFileName(basePath)}.vvd next to it.");
        var vtxPath = Companion(basePath, [".dx90.vtx", ".vtx", ".dx80.vtx", ".sw.vtx"])
            ?? throw new FileNotFoundException($"{Path.GetFileName(mdlPath)} needs its triangle file {Path.GetFileName(basePath)}.dx90.vtx next to it.");
        var vvd = SourceBytes.Read(vvdPath);
        var vtx = SourceBytes.Read(vtxPath);

        if (!mdl.Data.AsSpan().StartsWith("IDST"u8))
            throw new InvalidDataException($"{mdl.File} is not a Source engine model (IDST).");
        var version = mdl.I32(4);
        if (version is < 44 or > 49)
            throw new InvalidDataException($"{mdl.File} is a model of version {version}; Filee reads Source engine models of versions 44 to 49.");
        var checksum = mdl.I32(8);
        var model = new SourceModel { Name = mdl.String(12, 64), Mdl = mdl, MdlPath = Path.GetFullPath(mdlPath) };

        ReadBones(mdl, model);
        var textures = ReadTextures(mdl, model);
        model.SkinMaterials = textures;
        ReadVertices(vvd, checksum, model);
        cancellationToken.ThrowIfCancellationRequested();
        if (!vtx.Data.AsSpan().StartsWith((ReadOnlySpan<byte>)[7, 0, 0, 0]))
            throw new InvalidDataException($"{vtx.File} is not a triangle file of version 7.");
        if (vtx.I32(16) != checksum)
            throw new InvalidDataException($"{vtx.File} belongs to another build of {mdl.File}: its checksum differs.");
        // Strip groups and strips grew by 8 bytes in later branches (CS:GO); the layout whose indices all fit is used.
        model.Vtx = vtx;
        // A model too large in the first layout stays refused when the second one doesn't fit it at all.
        try
        {
            ReadMeshes(mdl, vtx, textures, model, extended: false, new TriangleBudget(maxTriangles), cancellationToken);
        }
        catch (Exception first) when (first is InvalidDataException or ConversionRefusedException)
        {
            model.Meshes.Clear();
            model.ExtendedVtx = true;
            try
            {
                ReadMeshes(mdl, vtx, textures, model, extended: true, new TriangleBudget(maxTriangles), cancellationToken);
            }
            catch (InvalidDataException) when (first is ConversionRefusedException)
            {
                ExceptionDispatchInfo.Throw(first);
            }
        }
        if (model.Meshes.Count == 0)
            throw new InvalidDataException($"{mdl.File} has no triangles in its default body groups.");
        return model;
    }

    /// <summary>The first of "<paramref name="basePath"/> + suffix" that exists, letter case aside.</summary>
    internal static string? Companion(string basePath, string[] suffixes)
    {
        var folder = Path.GetDirectoryName(basePath)!;
        var name = Path.GetFileName(basePath);
        var files = Directory.EnumerateFiles(folder).ToList();
        foreach (var suffix in suffixes)
        {
            var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name + suffix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return null;
    }

    private static void ReadBones(SourceBytes mdl, SourceModel model)
    {
        var count = mdl.Count(156, MaxBones, "bones");
        var start = mdl.I32(160);
        for (var i = 0; i < count; i++)
        {
            var at = start + i * 216;
            var parent = mdl.I32(at + 4);
            model.Bones.Add(new Bone(mdl.String(at + mdl.I32(at)), parent is >= -1 && parent < count ? parent : -1,
                mdl.Vector(at + 32), mdl.Vector(at + 60)));
        }
    }

    /// <summary>The texture name of every skin reference of the first skin family.</summary>
    private static List<string> ReadTextures(SourceBytes mdl, SourceModel model)
    {
        var count = mdl.Count(204, MaxTextures, "textures");
        var start = mdl.I32(208);
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var at = start + i * 64;
            names.Add(mdl.String(at + mdl.I32(at)).Replace('\\', '/'));
        }

        var folders = mdl.Count(212, 256, "texture folders");
        var folderTable = mdl.I32(216);
        for (var i = 0; i < folders; i++)
            model.MaterialFolders.Add(mdl.String(mdl.I32(folderTable + i * 4)).Replace('\\', '/').Trim('/'));

        // The skin table's first row maps each mesh's material reference to a texture.
        var references = mdl.Count(220, MaxTextures, "skin references");
        var families = mdl.Count(224, MaxTextures, "skins");
        var skinTable = mdl.I32(228);
        var skin = new List<string>(references);
        for (var i = 0; i < references; i++)
        {
            var texture = families > 0 ? mdl.I16(skinTable + i * 2) : i;
            skin.Add(texture >= 0 && texture < names.Count ? names[texture] : i < names.Count ? names[i] : "");
        }
        return skin.Count > 0 ? skin : names;
    }

    private static void ReadVertices(SourceBytes vvd, int checksum, SourceModel model)
    {
        if (!vvd.Data.AsSpan().StartsWith("IDSV"u8))
            throw new InvalidDataException($"{vvd.File} is not a Source engine vertex file (IDSV).");
        if (vvd.I32(8) != checksum)
            throw new InvalidDataException($"{vvd.File} belongs to another build of the model: its checksum differs.");
        var lod0 = vvd.Count(16, MaxVertices, "vertices");
        var fixups = vvd.Count(48, MaxVertices, "fixups");
        var fixupTable = vvd.I32(52);
        var vertexData = vvd.I32(56);
        // The tables are checked against the file before a single vertex is read or added.
        if (!vvd.Fits(vertexData, lod0, 48) || !vvd.Fits(fixupTable, fixups, 12))
            throw vvd.Damaged("the vertex table");

        void Add(int index)
        {
            var at = vertexData + index * 48;
            model.Vertices.Add(new Vertex(vvd.Vector(at + 16), vvd.Vector(at + 28), new Vector2(vvd.F32(at + 40), vvd.F32(at + 44)),
                Math.Clamp((int)vvd.Byte(at + 15), 1, 3), vvd.Byte(at + 12), vvd.Byte(at + 13), vvd.Byte(at + 14),
                vvd.F32(at), vvd.F32(at + 4), vvd.F32(at + 8)));
        }

        if (fixups == 0)
        {
            for (var i = 0; i < lod0; i++)
                Add(i);
            return;
        }
        // With fixups, LOD 0 is every run of vertices whose LOD is 0 or above, in order.
        for (var f = 0; f < fixups; f++)
        {
            var at = fixupTable + f * 12;
            var lod = vvd.I32(at);
            var source = vvd.I32(at + 4);
            var length = vvd.I32(at + 8);
            if (lod < 0)
                continue;
            if (source < 0 || length < 0 || model.Vertices.Count + length > MaxVertices || !vvd.Fits(vertexData, (long)source + length, 48))
                throw vvd.Damaged("a vertex fixup");
            for (var i = 0; i < length; i++)
                Add(source + i);
        }
    }

    /// <summary>
    /// The triangles a model may have in all. Strip groups and strips are reached through offsets, so many of them can
    /// point at the same table; every triangle is counted as it is added, wherever it comes from.
    /// </summary>
    internal sealed class TriangleBudget(int max)
    {
        private long _used;

        public void Take(long triangles)
        {
            _used += triangles;
            if (_used > max)
                throw new ConversionRefusedException("error.document_too_large", $"more than {max} triangles");
        }
    }

    private static void ReadMeshes(SourceBytes mdl, SourceBytes vtx, List<string> materials, SourceModel model, bool extended,
        TriangleBudget budget, CancellationToken cancellationToken)
    {
        var bodyParts = Math.Min(mdl.Count(232, MaxBodyParts, "body parts"), vtx.Count(28, MaxBodyParts, "body parts"));
        var mdlBodyParts = mdl.I32(236);
        var vtxBodyParts = vtx.I32(32);
        for (var b = 0; b < bodyParts; b++)
        {
            var mdlPart = mdlBodyParts + b * 16;
            var vtxPart = vtxBodyParts + b * 8;
            if (mdl.I32(mdlPart + 4) <= 0 || vtx.I32(vtxPart) <= 0)
                continue;
            // The default body group: the first model of the part, at LOD 0.
            var mdlModel = mdlPart + mdl.I32(mdlPart + 12);
            var vtxModel = vtxPart + vtx.I32(vtxPart + 4);
            if (vtx.I32(vtxModel) <= 0)
                continue;
            var lod = vtxModel + vtx.I32(vtxModel + 4);
            model.Meshes.AddRange(ReadLodMeshes(mdl, vtx, mdlModel, lod, materials, model.Vertices.Count, extended, budget, cancellationToken));
        }
    }

    /// <summary>
    /// The meshes of one model at one LOD: the model's meshes in the .mdl (at <paramref name="mdlModel"/>) with their
    /// triangles from the .vtx LOD at <paramref name="vtxLod"/>, as indices into the LOD 0 vertices (every LOD's vertex
    /// numbers count from the same first vertex of each mesh).
    /// </summary>
    internal static List<Mesh> ReadLodMeshes(SourceBytes mdl, SourceBytes vtx, int mdlModel, int vtxLod, IReadOnlyList<string> materials,
        int vertexCount, bool extended, TriangleBudget budget, CancellationToken cancellationToken)
    {
        var stripGroupSize = extended ? 33 : 25;
        var stripSize = extended ? 35 : 27;
        var modelName = mdl.String(mdlModel, 64);
        var firstVertex = mdl.I32(mdlModel + 84) / 48;
        var meshes = Math.Min(mdl.Count(mdlModel + 72, MaxMeshes, "meshes"), vtx.Count(vtxLod, MaxMeshes, "meshes"));
        var mdlMeshes = mdlModel + mdl.I32(mdlModel + 76);
        var vtxMeshes = vtxLod + vtx.I32(vtxLod + 4);
        var result = new List<Mesh>();

        for (var m = 0; m < meshes; m++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mdlMesh = mdlMeshes + m * 116;
            var material = mdl.I32(mdlMesh);
            var meshFirst = firstVertex + mdl.I32(mdlMesh + 12);
            var vtxMesh = vtxMeshes + m * 9;
            var groups = vtx.Count(vtxMesh, MaxMeshes, "strip groups");
            var groupTable = vtxMesh + vtx.I32(vtxMesh + 4);
            var triangles = new List<int>();
            for (var g = 0; g < groups; g++)
            {
                var group = groupTable + g * stripGroupSize;
                var groupVertices = vtx.Count(group, MaxVertices, "vertices");
                var vertexTable = group + vtx.I32(group + 4);
                var indexCount = vtx.Count(group + 8, MaxVertices * 3, "indices");
                var indexTable = group + vtx.I32(group + 12);
                var strips = vtx.Count(group + 16, MaxMeshes, "strips");
                var stripTable = group + vtx.I32(group + 20);

                int MeshVertex(int index)
                {
                    if ((uint)index >= (uint)groupVertices)
                        throw vtx.Damaged("a triangle");
                    var vertex = meshFirst + vtx.U16(vertexTable + index * 9 + 4);
                    if ((uint)vertex >= (uint)vertexCount)
                        throw vtx.Damaged("a triangle");
                    return vertex;
                }

                var anyStrip = false;
                for (var s = 0; s < strips; s++)
                {
                    var strip = stripTable + s * stripSize;
                    if ((vtx.Byte(strip + 18) & 0x02) == 0)
                        continue;
                    // A triangle strip: every next index makes a triangle with the two before it, alternating.
                    anyStrip = true;
                    var first = vtx.I32(strip + 4);
                    var count = vtx.I32(strip);
                    if (first < 0 || count < 0 || first + count > indexCount)
                        throw vtx.Damaged("a triangle strip");
                    for (var i = 2; i < count; i++)
                    {
                        var a = MeshVertex(vtx.U16(indexTable + (first + i - 2) * 2));
                        var c = MeshVertex(vtx.U16(indexTable + (first + i - 1) * 2));
                        var d = MeshVertex(vtx.U16(indexTable + (first + i) * 2));
                        if (a == c || c == d || a == d)
                            continue;
                        budget.Take(1);
                        if (i % 2 == 0)
                            triangles.AddRange([a, c, d]);
                        else
                            triangles.AddRange([c, a, d]);
                    }
                }
                if (anyStrip)
                    continue;
                if (indexCount % 3 != 0)
                    throw vtx.Damaged("a triangle list");
                budget.Take(indexCount / 3);
                for (var i = 0; i < indexCount; i++)
                    triangles.Add(MeshVertex(vtx.U16(indexTable + i * 2)));
            }
            if (triangles.Count == 0)
                continue;
            var name = material >= 0 && material < materials.Count ? materials[material] : $"material{material}";
            result.Add(new Mesh(modelName, name, triangles));
        }
        return result;
    }

    /// <summary>
    /// The model in Filee's conventions: one part per mesh, its textures looked up in the "materials" folder above the
    /// model (none when there is no such folder or the game's own textures are meant).
    /// </summary>
    public Model3D ToModel3D(string mdlPath, CancellationToken cancellationToken)
    {
        var result = new Model3D();
        var textures = new SourceMaterials(mdlPath, MaterialFolders);
        var materialIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in Meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!materialIndex.TryGetValue(mesh.Material, out var index))
            {
                index = result.Materials.Count;
                materialIndex[mesh.Material] = index;
                var (texture, doubleSided) = textures.Find(mesh.Material);
                result.Materials.Add(new Material3D
                {
                    Name = Path.GetFileName(mesh.Material),
                    BaseColorTexture = texture,
                    DoubleSided = doubleSided,
                });
            }

            var part = new MeshPart { Name = mesh.Part, Material = index, Normals = [], TexCoords = [] };
            var local = new Dictionary<int, int>();
            int Local(int vertex)
            {
                if (local.TryGetValue(vertex, out var at))
                    return at;
                var v = Vertices[vertex];
                at = part.AddVertex(MeshMath.ToYUp(v.Position) * MillimetresPerUnit, MeshMath.ToYUp(v.Normal), new Vector2(v.Uv.X, 1 - v.Uv.Y));
                local[vertex] = at;
                return at;
            }
            // Source's triangles are clockwise: reversed, they face the same way counter-clockwise.
            for (var t = 0; t + 2 < mesh.Triangles.Count; t += 3)
                part.AddTriangle(Local(mesh.Triangles[t + 2]), Local(mesh.Triangles[t + 1]), Local(mesh.Triangles[t]));
            result.Parts.Add(part);
        }
        return result;
    }
}

/// <summary>
/// Finds a Source model's textures as Garry's Mod does: "materials/&lt;texture folder&gt;/&lt;texture&gt;.vmt" beside
/// the "models" folder the model is in, then that material's $basetexture as a .vtf. Only .vmt and .vtf files below that
/// "materials" folder are read, never through "..".
/// </summary>
internal sealed class SourceMaterials
{
    /// <summary>How far above the model its "models" folder is looked for (models/a/b/c/model.mdl is four).</summary>
    private const int MaxLevels = 10;

    private readonly string? _root;
    private readonly IReadOnlyList<string> _folders;
    private readonly Dictionary<string, Texture3D?> _textures = new(StringComparer.OrdinalIgnoreCase);

    public SourceMaterials(string mdlPath, IReadOnlyList<string> folders)
    {
        _folders = folders.Count > 0 ? folders : [""];
        // Only the game's or addon's own layout: a "models" folder above the model, with "materials" beside it.
        var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(mdlPath))!);
        for (var level = 0; level < MaxLevels && folder.Parent is { } parent; level++, folder = parent)
        {
            if (!folder.Name.Equals("models", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                _root = parent.EnumerateDirectories()
                    .FirstOrDefault(d => d.Name.Equals("materials", StringComparison.OrdinalIgnoreCase))?.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder Filee may not list: no textures
            }
            break;
        }
    }

    /// <summary>The texture of material <paramref name="name"/>, and whether the material is drawn from both sides.</summary>
    public (Texture3D? Texture, bool DoubleSided) Find(string name)
    {
        if (_root is null)
            return (null, false);
        foreach (var folder in _folders)
        {
            var relative = folder.Length > 0 ? folder + "/" + name : name;
            if (Resolve(relative + ".vmt") is { } vmt)
            {
                var material = Vmt.Read(vmt, path => Resolve(path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) ? path[10..] : path));
                var texture = material.BaseTexture is { } baseTexture ? Texture(StripExtension(baseTexture) + ".vtf") : null;
                return (texture, material.NoCull);
            }
            if (Resolve(relative + ".vtf") is not null)
                return (Texture(relative + ".vtf"), false);
        }
        return (null, false);
    }

    private static string StripExtension(string path) =>
        path.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;

    /// <summary>The decoded texture at <paramref name="relative"/> (below "materials"), as PNG, once per file.</summary>
    private Texture3D? Texture(string relative)
    {
        if (_textures.TryGetValue(relative, out var known))
            return known;
        Texture3D? texture = null;
        if (Resolve(relative) is { } path)
        {
            try
            {
                using var frames = Vtf.Read(path);
                if (frames.Count > 0)
                    texture = new Texture3D(frames[0].ToByteArray(MagickFormat.Png), "png", Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex) when (ex is InvalidDataException or MagickException or IOException)
            {
                // A texture Filee can't read leaves the material plain.
            }
        }
        _textures[relative] = texture;
        return texture;
    }

    /// <summary>
    /// The file at <paramref name="relative"/> below "materials" ("/" or "\" separated), letter case aside (Source's file
    /// system ignores it), or null.
    /// </summary>
    private string? Resolve(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
            return null;
        var current = _root!;
        for (var i = 0; i < parts.Length; i++)
        {
            var exact = Path.Combine(current, parts[i]);
            var last = i == parts.Length - 1;
            if (last ? File.Exists(exact) : Directory.Exists(exact))
            {
                current = exact;
                continue;
            }
            var entries = last ? Directory.EnumerateFiles(current) : Directory.EnumerateDirectories(current);
            var match = entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            current = match;
        }
        return current;
    }
}

/// <summary>The few things Filee reads from a Valve material (.vmt, KeyValues text): $basetexture and $nocull.</summary>
internal static class Vmt
{
    internal sealed record Material(string? BaseTexture, bool NoCull);

    /// <summary>
    /// Reads <paramref name="path"/>; a "patch" material takes what it doesn't set from the material it includes
    /// (found through <paramref name="resolve"/>, one level).
    /// </summary>
    public static Material Read(string path, Func<string, string?> resolve)
    {
        var tokens = Tokens(Text(path));
        var material = Parse(tokens);
        if (tokens.Count > 0 && tokens[0].Equals("patch", StringComparison.OrdinalIgnoreCase)
            && Value(tokens, "include") is { } include && include.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase)
            && resolve(include.Replace('\\', '/')) is { } included && included != path)
        {
            var parent = Parse(Tokens(Text(included)));
            material = new Material(material.BaseTexture ?? parent.BaseTexture, material.NoCull || parent.NoCull);
        }
        return material;
    }

    /// <summary>The largest material file Filee reads; real ones are a few hundred bytes.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>The text of a material, or "" when it is larger than <see cref="MaxBytes"/> (the material stays plain).</summary>
    private static string Text(string path) => new FileInfo(path).Length > MaxBytes ? "" : File.ReadAllText(path);

    private static Material Parse(List<string> tokens) =>
        new(Value(tokens, "$basetexture"), Value(tokens, "$nocull") is "1");

    /// <summary>The value after the first key <paramref name="key"/>, at any depth.</summary>
    private static string? Value(List<string> tokens, string key)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Equals(key, StringComparison.OrdinalIgnoreCase) && tokens[i + 1] is not ("{" or "}"))
                return tokens[i + 1];
        }
        return null;
    }

    /// <summary>KeyValues tokens: quoted or bare words and braces; "//" comments are skipped.</summary>
    internal static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0)
                    end = text.Length;
                tokens.Add(text[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                    i++;
                tokens.Add(text[start..i]);
            }
        }
        return tokens;
    }
}
