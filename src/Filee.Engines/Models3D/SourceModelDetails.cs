// What a Source engine model (.mdl) holds besides its default mesh, read for decompiling it into a QC and its SMDs
// (#1526). Layouts (little endian) as in Valve's studio.h:
//
//   studiohdr_t      eye position 80, illumination position 92, hull 104/116, view box 128/140, flags 152,
//                    hitbox sets 172, animations 180, sequences 188, skin table 220, body parts 232, attachments 240,
//                    pose parameters 300, surface property 308, key values 312, mass 328, contents 332, include
//                    models 336, animation blocks 348, the directional light dot 376 (one byte)
//   mstudiobone_t    216 bytes; position scale 72 and rotation scale 84 turn compressed animation values into units
//   mstudiobodyparts_t 16 bytes → mstudiomodel_t 148 bytes (name, meshes, first vertex); the .vtx has a matching
//                    model → LODs (12 bytes: meshes, offset, switch point) → meshes
//   mstudioattachment_t 92 bytes: name, flags, bone, a 3×4 matrix relative to the bone
//   mstudiohitboxset_t 12 bytes → mstudiobbox_t 68 bytes: bone, group, min, max, name
//   mstudioanimdesc_t 100 bytes: name, fps, flags, frames, movements, animation block and offset, sections
//   mstudioseqdesc_t 212 bytes: name, activity, flags, weight, events (80 bytes each), blend grid of animations,
//                    blend parameters, fade times
//   mstudioposeparamdesc_t 20 bytes: name, flags, start, end, loop
//
// Every count is checked against a limit and every offset against the file before it is used; the triangles of all
// body groups and LODs, and the hitboxes, events and blended animations of the whole model, are capped in all, since
// many entries can point at the same data.

using System.Numerics;
using Filee.Core.Conversion;

namespace Filee.Engines.Models3D;

/// <summary>The parts of a Source model that a decompiled QC describes.</summary>
internal sealed class SourceModelDetails
{
    private const int MaxLods = 8;
    private const int MaxAttachments = 4096;
    private const int MaxHitboxes = 16384;
    private const int MaxAnimations = 16384;
    private const int MaxEvents = 4096;
    private const int MaxBlends = 4096;

    /// <summary>The most hitboxes, events and blended animations (each) of all sets and sequences together.</summary>
    public const int MaxTotalEntries = 262_144;

    /// <summary>Counts <paramref name="more"/> entries of <paramref name="what"/> against the model-wide cap.</summary>
    private static void Take(ref long used, long more, long max, string what)
    {
        used += more;
        if (used > max)
            throw new ConversionRefusedException("error.document_too_large", $"more than {max} {what}");
    }

    internal sealed record BodyModel(string Name, List<ModelLod> Lods);
    internal sealed record BodyPart(string Name, List<BodyModel> Models);
    /// <summary>One LOD of a body model: its switch point (−1 for the shadow LOD) and meshes (empty when removed).</summary>
    internal sealed record ModelLod(float SwitchPoint, List<SourceModel.Mesh> Meshes);
    /// <summary>An attachment: its bone, and position and rotation (radians, Source's Euler order) relative to it.</summary>
    internal sealed record Attachment(string Name, int Bone, Vector3 Position, Vector3 Rotation);
    internal sealed record Hitbox(int Bone, int Group, Vector3 Min, Vector3 Max);
    internal sealed record HitboxSet(string Name, List<Hitbox> Boxes);
    internal sealed record PoseParameter(string Name, int Flags, float Start, float End, float Loop);
    /// <summary>An animation's description; its frames are decoded by <see cref="SourceAnimation"/>.</summary>
    internal sealed record Animation(int Offset, string Name, float Fps, int Flags, int Frames, int SectionFrames);
    internal sealed record AnimationEvent(float Cycle, int Event, int Type, string Options, string Name);
    internal sealed record Sequence(string Name, string Activity, int ActivityWeight, int Flags, List<AnimationEvent> Events,
        int BlendWidth, List<int> Animations, int[] Parameters, float[] ParameterStart, float[] ParameterEnd, float FadeIn, float FadeOut);

    public required SourceModel Model { get; init; }
    public int Version { get; private init; }
    public int Checksum { get; private init; }
    public int Flags { get; private init; }
    public Vector3 EyePosition { get; private init; }
    public Vector3 IlluminationPosition { get; private init; }
    public Vector3 HullMin { get; private init; }
    public Vector3 HullMax { get; private init; }
    public Vector3 ViewMin { get; private init; }
    public Vector3 ViewMax { get; private init; }
    public float Mass { get; private init; }
    public int Contents { get; private init; }
    public byte DirectionalLightDot { get; private init; }
    public string SurfaceProperty { get; private init; } = "";
    /// <summary>The model's key values ($keyvalues), as the text inside "mdlkeyvalue { … }".</summary>
    public string KeyValues { get; private init; } = "";

    /// <summary>Every texture name, and the texture number of each skin reference in each skin family.</summary>
    public List<string> Textures { get; } = [];
    public List<int[]> SkinFamilies { get; } = [];
    public List<string> MaterialFolders { get; } = [];
    public List<BodyPart> BodyParts { get; } = [];
    public List<Attachment> Attachments { get; } = [];
    public List<HitboxSet> HitboxSets { get; } = [];
    public List<PoseParameter> PoseParameters { get; } = [];
    public List<string> IncludeModels { get; } = [];
    public List<Animation> Animations { get; } = [];
    public List<Sequence> Sequences { get; } = [];
    /// <summary>The scale of each bone's compressed animated position and rotation values.</summary>
    public List<(Vector3 Position, Vector3 Rotation)> BoneScales { get; } = [];
    /// <summary>Where each animation block lies in the model's .ani file (block 0 is the .mdl itself).</summary>
    public List<(int Start, int End)> AnimationBlocks { get; } = [];

    /// <summary>Reads the details of <paramref name="model"/>, which was loaded from its .mdl, .vvd and .vtx.</summary>
    public static SourceModelDetails Read(SourceModel model, CancellationToken cancellationToken,
        int maxTriangles = SourceModel.MaxTriangles, int maxEntries = MaxTotalEntries)
    {
        var mdl = model.Mdl;
        var details = new SourceModelDetails
        {
            Model = model,
            Version = mdl.I32(4),
            Checksum = mdl.I32(8),
            EyePosition = mdl.Vector(80),
            IlluminationPosition = mdl.Vector(92),
            HullMin = mdl.Vector(104),
            HullMax = mdl.Vector(116),
            ViewMin = mdl.Vector(128),
            ViewMax = mdl.Vector(140),
            Flags = mdl.I32(152),
            SurfaceProperty = mdl.I32(308) == 0 ? "" : mdl.String(mdl.I32(308)),
            KeyValues = ReadKeyValues(mdl),
            Mass = mdl.F32(328),
            Contents = mdl.I32(332),
            DirectionalLightDot = mdl.Byte(376),
        };
        details.ReadBoneScales(mdl);
        details.ReadTextures(mdl);
        details.ReadBodyParts(new SourceModel.TriangleBudget(maxTriangles), cancellationToken);
        details.ReadAttachments(mdl);
        details.ReadHitboxes(mdl, maxEntries);
        details.ReadPoseParameters(mdl);
        details.ReadIncludeModels(mdl);
        details.ReadAnimations(mdl);
        details.ReadSequences(mdl, maxEntries, cancellationToken);
        return details;
    }

    /// <summary>The text inside "mdlkeyvalue { … }", the form $keyvalues takes in a QC.</summary>
    private static string ReadKeyValues(SourceBytes mdl)
    {
        var size = mdl.I32(316);
        if (size <= 0 || !mdl.Fits(mdl.I32(312), size, 1))
            return "";
        var text = mdl.String(mdl.I32(312), size);
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        return open >= 0 && close > open ? text[(open + 1)..close].Trim() : text.Trim();
    }

    private void ReadBoneScales(SourceBytes mdl)
    {
        var start = mdl.I32(160);
        for (var i = 0; i < Model.Bones.Count; i++)
        {
            var at = start + i * 216;
            BoneScales.Add((mdl.Vector(at + 72), mdl.Vector(at + 84)));
        }
    }

    private void ReadTextures(SourceBytes mdl)
    {
        var count = mdl.Count(204, SourceModel.MaxTextures, "textures");
        var start = mdl.I32(208);
        for (var i = 0; i < count; i++)
            Textures.Add(mdl.String(start + i * 64 + mdl.I32(start + i * 64)).Replace('\\', '/'));
        var folders = mdl.Count(212, 256, "texture folders");
        var folderTable = mdl.I32(216);
        for (var i = 0; i < folders; i++)
            MaterialFolders.Add(mdl.String(mdl.I32(folderTable + i * 4)));

        var references = mdl.Count(220, SourceModel.MaxTextures, "skin references");
        var families = mdl.Count(224, SourceModel.MaxTextures, "skins");
        var table = mdl.I32(228);
        if (!mdl.Fits(table, (long)references * families, 2))
            throw mdl.Damaged("the skin table");
        for (var f = 0; f < families; f++)
        {
            var row = new int[references];
            for (var r = 0; r < references; r++)
                row[r] = mdl.I16(table + (f * references + r) * 2);
            SkinFamilies.Add(row);
        }
    }

    /// <summary>Every model of every body part, at every LOD the .vtx has, their triangles counted against one budget.</summary>
    private void ReadBodyParts(SourceModel.TriangleBudget budget, CancellationToken cancellationToken)
    {
        var (mdl, vtx) = (Model.Mdl, Model.Vtx);
        var parts = Math.Min(mdl.Count(232, SourceModel.MaxBodyParts, "body parts"), vtx.Count(28, SourceModel.MaxBodyParts, "body parts"));
        var mdlParts = mdl.I32(236);
        var vtxParts = vtx.I32(32);
        for (var b = 0; b < parts; b++)
        {
            var mdlPart = mdlParts + b * 16;
            var vtxPart = vtxParts + b * 8;
            var part = new BodyPart(mdl.RelativeString(mdlPart, mdlPart), []);
            var models = Math.Min(mdl.Count(mdlPart + 4, SourceModel.MaxBodyParts, "body group models"),
                vtx.Count(vtxPart, SourceModel.MaxBodyParts, "body group models"));
            for (var m = 0; m < models; m++)
            {
                var mdlModel = mdlPart + mdl.I32(mdlPart + 12) + m * 148;
                var vtxModel = vtxPart + vtx.I32(vtxPart + 4) + m * 8;
                var model = new BodyModel(mdl.String(mdlModel, 64), []);
                var lods = vtx.Count(vtxModel, MaxLods, "LODs");
                for (var l = 0; l < lods; l++)
                {
                    var lod = vtxModel + vtx.I32(vtxModel + 4) + l * 12;
                    model.Lods.Add(new ModelLod(vtx.F32(lod + 8), SourceModel.ReadLodMeshes(mdl, vtx, mdlModel, lod,
                        Model.SkinMaterials, Model.Vertices.Count, Model.ExtendedVtx, budget, cancellationToken)));
                }
                part.Models.Add(model);
            }
            BodyParts.Add(part);
        }
    }

    private void ReadAttachments(SourceBytes mdl)
    {
        var count = mdl.Count(240, MaxAttachments, "attachments");
        var start = mdl.I32(244);
        for (var i = 0; i < count; i++)
        {
            var at = start + i * 92;
            // A 3×4 matrix, row by row: the rotation's rows, each with the position's component last.
            var rows = new float[12];
            for (var k = 0; k < 12; k++)
                rows[k] = mdl.F32(at + 12 + k * 4);
            var rotation = new Matrix4x4(rows[0], rows[4], rows[8], 0, rows[1], rows[5], rows[9], 0, rows[2], rows[6], rows[10], 0, 0, 0, 0, 1);
            Attachments.Add(new Attachment(mdl.RelativeString(at, at), mdl.I32(at + 8), new Vector3(rows[3], rows[7], rows[11]),
                SourceAnimation.ToEuler(Quaternion.CreateFromRotationMatrix(rotation))));
        }
    }

    private void ReadHitboxes(SourceBytes mdl, int maxEntries)
    {
        long total = 0;
        var sets = mdl.Count(172, MaxAttachments, "hitbox sets");
        var start = mdl.I32(176);
        for (var s = 0; s < sets; s++)
        {
            var at = start + s * 12;
            var set = new HitboxSet(mdl.RelativeString(at, at), []);
            var boxes = mdl.Count(at + 4, MaxHitboxes, "hitboxes");
            Take(ref total, boxes, maxEntries, "hitboxes");
            for (var h = 0; h < boxes; h++)
            {
                var box = at + mdl.I32(at + 8) + h * 68;
                set.Boxes.Add(new Hitbox(mdl.I32(box), mdl.I32(box + 4), mdl.Vector(box + 8), mdl.Vector(box + 20)));
            }
            HitboxSets.Add(set);
        }
    }

    private void ReadPoseParameters(SourceBytes mdl)
    {
        var count = mdl.Count(300, MaxAttachments, "pose parameters");
        var start = mdl.I32(304);
        for (var i = 0; i < count; i++)
        {
            var at = start + i * 20;
            PoseParameters.Add(new PoseParameter(mdl.RelativeString(at, at), mdl.I32(at + 4), mdl.F32(at + 8), mdl.F32(at + 12), mdl.F32(at + 16)));
        }
    }

    private void ReadIncludeModels(SourceBytes mdl)
    {
        var count = mdl.Count(336, MaxAttachments, "include models");
        var start = mdl.I32(340);
        for (var i = 0; i < count; i++)
            IncludeModels.Add(mdl.RelativeString(start + i * 8, start + i * 8 + 4));
        var blocks = mdl.Count(352, MaxAnimations, "animation blocks");
        var table = mdl.I32(356);
        for (var i = 0; i < blocks; i++)
            AnimationBlocks.Add((mdl.I32(table + i * 8), mdl.I32(table + i * 8 + 4)));
    }

    private void ReadAnimations(SourceBytes mdl)
    {
        var count = mdl.Count(180, MaxAnimations, "animations");
        var start = mdl.I32(184);
        for (var i = 0; i < count; i++)
        {
            var at = start + i * 100;
            var frames = mdl.I32(at + 16);
            if (frames is < 0 or > SourceAnimation.MaxFrames)
                throw new InvalidDataException($"{mdl.File} is damaged (an animation claims {frames} frames).");
            Animations.Add(new Animation(at, mdl.RelativeString(at, at + 4), mdl.F32(at + 8), mdl.I32(at + 12), frames, mdl.I32(at + 84)));
        }
    }

    private void ReadSequences(SourceBytes mdl, int maxEntries, CancellationToken cancellationToken)
    {
        long totalEvents = 0, totalBlends = 0;
        var count = mdl.Count(188, MaxAnimations, "sequences");
        var start = mdl.I32(192);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = start + i * 212;
            var events = new List<AnimationEvent>();
            var eventCount = mdl.Count(at + 24, MaxEvents, "animation events");
            Take(ref totalEvents, eventCount, maxEntries, "animation events");
            for (var e = 0; e < eventCount; e++)
            {
                var ev = at + mdl.I32(at + 28) + e * 80;
                events.Add(new AnimationEvent(mdl.F32(ev), mdl.I32(ev + 4), mdl.I32(ev + 8), mdl.String(ev + 12, 64),
                    mdl.RelativeString(ev, ev + 76)));
            }
            var width = mdl.I32(at + 68);
            var height = mdl.I32(at + 72);
            if (width is < 1 or > MaxBlends || height is < 1 or > MaxBlends || (long)width * height > MaxBlends)
                throw new InvalidDataException($"{mdl.File} is damaged (a sequence claims {width} × {height} blended animations).");
            Take(ref totalBlends, width * height, maxEntries, "blended animations");
            var table = at + mdl.I32(at + 60);
            var animations = new List<int>(width * height);
            for (var k = 0; k < width * height; k++)
            {
                var index = mdl.I16(table + k * 2);
                if (index < 0 || index >= Animations.Count)
                    throw new InvalidDataException($"{mdl.File} is damaged (a sequence uses animation {index}).");
                animations.Add(index);
            }
            Sequences.Add(new Sequence(mdl.RelativeString(at, at + 4), mdl.RelativeString(at, at + 8), mdl.I32(at + 20),
                mdl.I32(at + 12), events, width, animations, [mdl.I32(at + 76), mdl.I32(at + 80)],
                [mdl.F32(at + 84), mdl.F32(at + 88)], [mdl.F32(at + 92), mdl.F32(at + 96)], mdl.F32(at + 104), mdl.F32(at + 108)));
        }
    }
}
