// The frames of a Source engine animation (#1526), decoded from the .mdl (or the .ani file its animation blocks are in)
// into each bone's position and rotation relative to its parent, as an animation SMD stores them. Layout (studio.h):
//
//   mstudioanim_t    per animated bone: bone (byte), flags (byte), offset to the next bone's entry (short, 0 for the
//                    last), then its data: a rotation (Quaternion64 with flag 0x20, Quaternion48 with 0x02, or three
//                    run-length encoded tracks with 0x08), then a position (Vector48 with 0x01, or three tracks with 0x04)
//   tracks           three offsets (shorts, relative to the first; 0 for a track that stays 0) to runs of
//                    (valid, total) byte pairs, each followed by its valid values (shorts); frames after the valid ones
//                    repeat the last. A value times the bone's scale is added to its bind pose (not for delta animations)
//   Quaternion48     x and y in 16 bits, z in 15, the sign of w in 1; Quaternion64 x, y and z in 21 bits each
//   Vector48         three half-precision floats
//   sections         long animations are cut into sections of a number of frames, each with its own block and offset
//   movements        44 bytes each: end frame, motion flags, start and end speed, angle, direction, position reached;
//                    studiomdl took this motion out of the root bones, so it is added back
//
// Bones an animation does not touch keep their bind pose (zero for a delta animation).

using System.Numerics;

namespace Filee.Engines.Models3D;

/// <summary>Decodes the frames of a Source model's animations.</summary>
internal static class SourceAnimation
{
    /// <summary>The most frames Filee decodes from one animation.</summary>
    public const int MaxFrames = 100_000;

    /// <summary>The most bone entries one frame may chain (each bone at most once, in practice).</summary>
    private const int MaxEntries = SourceModel.MaxBones * 4;

    private const int MaxMovements = 4096;

    /// <summary>A bone's pose: position and rotation (radians, Source's Euler order) relative to its parent.</summary>
    internal readonly record struct Pose(Vector3 Position, Vector3 Rotation);

    // studio.h's mstudioanim_t flags and STUDIO_DELTA.
    private const int RawPosition = 0x01, RawRotation = 0x02, AnimatedPosition = 0x04, AnimatedRotation = 0x08, RawRotation64 = 0x20;
    public const int DeltaFlag = 0x04;

    /// <summary>The motion flags of a movement, in the order the QC writes them.</summary>
    public static readonly (int Flag, string Name)[] MotionFlags =
    [
        (0x0001, "X"), (0x0002, "Y"), (0x0004, "Z"), (0x0008, "XR"), (0x0010, "YR"), (0x0020, "ZR"),
        (0x0040, "LX"), (0x0080, "LY"), (0x0100, "LZ"), (0x0200, "LXR"), (0x0400, "LYR"), (0x0800, "LZR"),
    ];

    /// <summary>
    /// The poses of every bone in every frame of <paramref name="animation"/>, one frame at a time (so a long animation is
    /// written out as it is decoded, never held whole). <paramref name="ani"/> is the model's .ani file, needed when the
    /// animation lies in one of its blocks.
    /// </summary>
    public static IEnumerable<Pose[]> Decode(SourceModelDetails details, SourceModelDetails.Animation animation, SourceBytes? ani,
        CancellationToken cancellationToken)
    {
        var mdl = details.Model.Mdl;
        var bones = details.Model.Bones;
        var delta = (animation.Flags & DeltaFlag) != 0;
        var at = animation.Offset;
        var sectionFrames = mdl.I32(at + 84);
        if (sectionFrames < 0)
            throw new InvalidDataException($"{mdl.File} is damaged (an animation claims {sectionFrames} frames a section).");
        var movements = ReadMovements(mdl, at);
        for (var frame = 0; frame < animation.Frames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int block, index, local;
            if (sectionFrames > 0)
            {
                var section = frame / sectionFrames;
                local = frame - section * sectionFrames;
                var entry = at + mdl.I32(at + 80) + section * 8;
                (block, index) = (mdl.I32(entry), mdl.I32(entry + 4));
            }
            else
            {
                (block, index, local) = (mdl.I32(at + 52), mdl.I32(at + 56), frame);
            }

            var pose = new Pose[bones.Count];
            for (var b = 0; b < bones.Count; b++)
                pose[b] = delta ? default : new Pose(bones[b].Position, bones[b].Rotation);
            var (data, start) = Locate(details, ani, block, at, index);
            if (start >= 0)
                DecodeFrame(details, data, start, local, delta, pose);
            AddMovement(movements, frame, bones, pose);
            yield return pose;
        }
    }

    /// <summary>
    /// The file and offset an animation's data starts at: the .mdl (block 0, relative to the description) or a block
    /// of the .ani; −1 when the animation has no data (every bone at its bind pose).
    /// </summary>
    private static (SourceBytes Data, int Start) Locate(SourceModelDetails details, SourceBytes? ani, int block, int description, int index)
    {
        var mdl = details.Model.Mdl;
        if (block == 0)
            return index == 0 ? (mdl, -1) : (mdl, description + index);
        if (block < 0 || block >= details.AnimationBlocks.Count)
            throw new InvalidDataException($"{mdl.File} is damaged (an animation is in block {block}).");
        if (ani is null)
            throw new FileNotFoundException($"{mdl.File} keeps animations in {Path.GetFileNameWithoutExtension(mdl.File)}.ani, which is not next to it.");
        var (blockStart, blockEnd) = details.AnimationBlocks[block];
        if (blockStart < 0 || blockEnd > ani.Data.Length || index < 0 || (long)blockStart + index >= blockEnd)
            throw ani.Damaged("an animation block");
        return (ani, blockStart + index);
    }

    /// <summary>Applies the animated bones' entries at <paramref name="start"/> for frame <paramref name="frame"/>.</summary>
    private static void DecodeFrame(SourceModelDetails details, SourceBytes data, int start, int frame, bool delta, Pose[] pose)
    {
        var bones = details.Model.Bones;
        var entry = start;
        for (var n = 0; n < MaxEntries; n++)
        {
            var bone = data.Byte(entry);
            var flags = data.Byte(entry + 1);
            var next = data.I16(entry + 2);
            // Bone 255 marks an animation with no animated bones (studiomdl writes it for a single still frame).
            if (bone == 255)
                return;
            if (bone >= bones.Count)
                throw data.Damaged("an animated bone");
            var (positionScale, rotationScale) = details.BoneScales[bone];
            var bind = bones[bone];
            var (position, rotation) = (pose[bone].Position, pose[bone].Rotation);
            var at = entry + 4;
            if ((flags & RawRotation64) != 0)
            {
                rotation = ToEuler(Quaternion64(data, at));
                at += 8;
            }
            if ((flags & RawRotation) != 0)
            {
                rotation = ToEuler(Quaternion48(data, at));
                at += 6;
            }
            if ((flags & AnimatedRotation) != 0)
            {
                rotation = (delta ? Vector3.Zero : bind.Rotation) + Tracks(data, at, frame) * rotationScale;
                at += 6;
            }
            if ((flags & RawPosition) != 0)
            {
                position = new Vector3(data.F16(at), data.F16(at + 2), data.F16(at + 4));
                at += 6;
            }
            if ((flags & AnimatedPosition) != 0)
                position = (delta ? Vector3.Zero : bind.Position) + Tracks(data, at, frame) * positionScale;
            pose[bone] = new Pose(position, rotation);
            if (next == 0)
                return;
            if (next < 0)
                throw data.Damaged("an animated bone");
            entry += next;
        }
        throw new InvalidDataException($"{data.File} is damaged (an animation frame never ends).");
    }

    /// <summary>The three run-length encoded tracks whose offsets are at <paramref name="at"/>, at <paramref name="frame"/>.</summary>
    private static Vector3 Tracks(SourceBytes data, int at, int frame)
    {
        float Track(int component)
        {
            var offset = data.I16(at + component * 2);
            return offset == 0 ? 0 : Value(data, at + offset, frame);
        }
        return new Vector3(Track(0), Track(1), Track(2));
    }

    /// <summary>One track's value at <paramref name="frame"/>: runs of (valid, total) and valid values, the last repeated.</summary>
    private static float Value(SourceBytes data, int at, int frame)
    {
        var k = frame;
        for (var runs = 0; runs <= MaxFrames; runs++)
        {
            var valid = data.Byte(at);
            var total = data.Byte(at + 1);
            if (total == 0)
                return 0;
            if (total > k)
                return data.I16(at + 2 * (valid > k ? k + 1 : valid));
            k -= total;
            at += (valid + 1) * 2;
        }
        throw new InvalidDataException($"{data.File} is damaged (an animation track never ends).");
    }

    private static Quaternion Quaternion48(SourceBytes data, int at)
    {
        var x = (data.U16(at) - 32768) / 32768f;
        var y = (data.U16(at + 2) - 32768) / 32768f;
        var zw = data.U16(at + 4);
        var z = ((zw & 0x7FFF) - 16384) / 16384f;
        return WithW(x, y, z, (zw & 0x8000) != 0);
    }

    private static Quaternion Quaternion64(SourceBytes data, int at)
    {
        var bits = data.U64(at);
        float Part(int shift) => ((long)((bits >> shift) & 0x1FFFFF) - 1048576) / 1048576.5f;
        return WithW(Part(0), Part(21), Part(42), (bits >> 63) != 0);
    }

    private static Quaternion WithW(float x, float y, float z, bool negative)
    {
        var w = MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y - z * z));
        return new Quaternion(x, y, z, negative ? -w : w);
    }

    /// <summary>
    /// A rotation as Source's Euler angles (radians): the rotation about X, then Y, then Z (z yaw, y pitch, x roll), as
    /// bones and SMDs store them.
    /// </summary>
    public static Vector3 ToEuler(Quaternion q)
    {
        var (x, y, z, w) = (q.X, q.Y, q.Z, q.W);
        var m00 = 1 - 2 * (y * y + z * z);
        var m10 = 2 * (x * y + w * z);
        var m20 = 2 * (x * z - w * y);
        var m21 = 2 * (y * z + w * x);
        var m22 = 1 - 2 * (x * x + y * y);
        var cosPitch = MathF.Sqrt(m00 * m00 + m10 * m10);
        if (cosPitch > 1e-6f)
            return new Vector3(MathF.Atan2(m21, m22), MathF.Atan2(-m20, cosPitch), MathF.Atan2(m10, m00));
        // Looking straight up or down: the roll takes all of the rotation about the vertical.
        var m11 = 1 - 2 * (x * x + z * z);
        var m12 = 2 * (y * z - w * x);
        return new Vector3(MathF.Atan2(-m12, m11), MathF.Atan2(-m20, cosPitch), 0);
    }

    /// <summary>Source's Euler angles (radians) as a quaternion, the inverse of <see cref="ToEuler"/>.</summary>
    public static Quaternion FromEuler(Vector3 angles)
    {
        var (sr, cr) = MathF.SinCos(angles.X / 2);
        var (sp, cp) = MathF.SinCos(angles.Y / 2);
        var (sy, cy) = MathF.SinCos(angles.Z / 2);
        return new Quaternion(sr * cp * cy - cr * sp * sy, cr * sp * cy + sr * cp * sy, cr * cp * sy - sr * sp * cy, cr * cp * cy + sr * sp * sy);
    }

    internal readonly record struct Movement(int EndFrame, int Flags, float StartSpeed, float EndSpeed, Vector3 Position);

    /// <summary>The movements (linear motion studiomdl took out of the root bones) of the animation at <paramref name="at"/>.</summary>
    public static List<Movement> ReadMovements(SourceBytes mdl, int at)
    {
        var count = mdl.Count(at + 20, MaxMovements, "movements");
        var start = at + mdl.I32(at + 24);
        var movements = new List<Movement>(count);
        for (var i = 0; i < count; i++)
        {
            var m = start + i * 44;
            movements.Add(new Movement(mdl.I32(m), mdl.I32(m + 4), mdl.F32(m + 8), mdl.F32(m + 12), mdl.Vector(m + 32)));
        }
        return movements;
    }

    /// <summary>
    /// Adds the motion reached by <paramref name="frame"/> to the root bones: the position the previous movements
    /// reached, plus the part of this movement's way covered so far (its speed changing evenly from start to end).
    /// </summary>
    private static void AddMovement(List<Movement> movements, int frame, IReadOnlyList<SourceModel.Bone> bones, Pose[] pose)
    {
        if (movements.Count == 0)
            return;
        var offset = Vector3.Zero;
        var previous = 0;
        foreach (var movement in movements)
        {
            if (movement.EndFrame >= frame)
            {
                var span = movement.EndFrame - previous;
                var t = span > 0 ? (frame - previous) / (float)span : 1;
                // Distance at time t with the speed going from start to end, as a share of the whole movement's.
                var whole = movement.StartSpeed + 0.5f * (movement.EndSpeed - movement.StartSpeed);
                var share = MathF.Abs(whole) > 1e-6f
                    ? (movement.StartSpeed * t + 0.5f * (movement.EndSpeed - movement.StartSpeed) * t * t) / whole
                    : t;
                offset += (movement.Position - offset) * share;
                break;
            }
            previous = movement.EndFrame;
            offset = movement.Position;
        }
        if (offset == Vector3.Zero)
            return;
        for (var b = 0; b < bones.Count; b++)
        {
            if (bones[b].Parent < 0)
                pose[b] = pose[b] with { Position = pose[b].Position + offset };
        }
    }
}
