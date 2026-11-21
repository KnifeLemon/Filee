// A Source engine model's physics (.phy, #1526): the convex pieces of its collision model and the settings studiomdl
// took from the QC, read to decompile them into a physics SMD and the QC's $collisionmodel or $collisionjoints.
// Layout (little endian), as in Valve's vcollide and IVP headers:
//
//   header           16 bytes: its size, id, the number of solids, the checksum of the .mdl
//   solid            its size, then "VPHY" with version, type, surface size, drag areas, axis map size (28 bytes) —
//                    older files start straight with the surface — and an IVP compact surface:
//   compact surface  48 bytes: mass centre, inertia, radius, size, the offset of its ledge tree, "IVPS" at 44
//   ledge tree       nodes of 28 bytes: offset of the right child (0 for a leaf), offset of the leaf's ledge, centre,
//                    radius, box; the left child follows the node
//   ledge            a convex piece, 16 bytes: offset of its points, the bone (+1) it belongs to, flags, triangles;
//                    then 16 bytes a triangle: index, three edges of (point, opposite) shorts
//   points           16 bytes: x, y, z in metres in IVP's axes (Source x = x, y = z, z = −y), and one unused float
//   key values       after the solids: text blocks such as solid { "name" "mass" … }, ragdollconstraint { … },
//                    editparams { "rootname" "totalmass" "concave" }
//
// The pieces of a solid named after a bone ($collisionjoints, a ragdoll) are in that bone's space; others are in the
// model's. Every count is capped and every offset checked against the file; the ledge tree is walked without
// recursion and stops at a node count, so a looping tree is a damaged file. Leaves may point at the same piece, so the
// triangles and points of all pieces together are capped as well.

using System.Numerics;
using Filee.Core.Conversion;

namespace Filee.Engines.Models3D;

/// <summary>A Source model's .phy: collision pieces and their settings.</summary>
internal sealed class SourcePhysics
{
    private const int MaxSolids = 1024;
    private const int MaxNodes = 65536;
    private const int MaxPoints = 65536;

    /// <summary>The most triangles, and points, of all the pieces of one .phy together.</summary>
    public const int MaxTotal = 1_000_000;

    /// <summary>The triangles and points read so far, over every solid and piece.</summary>
    private sealed class Budget(int max)
    {
        private long _triangles, _points;

        public void Take(long triangles, long points)
        {
            _triangles += triangles;
            _points += points;
            if (_triangles > max || _points > max)
                throw new ConversionRefusedException("error.document_too_large", $"a collision model of more than {max} triangles or points");
        }
    }

    /// <summary>Metres (IVP's unit) to inches (Source's).</summary>
    private const float InchesPerMetre = 1 / 0.0254f;

    /// <summary>A convex piece: its points (Source's axes, inches) and triangles (indices into them, counter-clockwise outward).</summary>
    internal sealed record Piece(List<Vector3> Points, List<int> Triangles);

    /// <summary>A solid: its pieces and the bone number the file stores with them (−1 for none).</summary>
    internal sealed record Solid(List<Piece> Pieces, int Bone);

    /// <summary>A key-value block of the text section, such as "solid" or "ragdollconstraint".</summary>
    internal sealed record Block(string Name, Dictionary<string, string> Values);

    public int Checksum { get; private init; }
    public List<Solid> Solids { get; } = [];
    public List<Block> Blocks { get; } = [];

    /// <summary>
    /// Reads the .phy at <paramref name="path"/>, refusing one whose pieces have more than <paramref name="maxTotal"/>
    /// triangles or points in all.
    /// </summary>
    public static SourcePhysics Read(string path, int maxTotal = MaxTotal)
    {
        var phy = SourceBytes.Read(path);
        var budget = new Budget(maxTotal);
        var headerSize = phy.I32(0);
        var solids = phy.Count(8, MaxSolids, "solids");
        var physics = new SourcePhysics { Checksum = phy.I32(12) };
        var at = headerSize;
        for (var s = 0; s < solids; s++)
        {
            var size = phy.I32(at);
            if (size <= 0 || !phy.Fits(at + 4, size, 1))
                throw phy.Damaged("a solid");
            physics.Solids.Add(ReadSolid(phy, at + 4, budget));
            at += 4 + size;
        }
        if (at < phy.Data.Length)
            physics.Blocks.AddRange(ReadBlocks(phy.String(at, phy.Data.Length - at)));
        return physics;
    }

    private static Solid ReadSolid(SourceBytes phy, int at, Budget budget)
    {
        var surface = phy.Data.AsSpan(at).StartsWith("VPHY"u8) ? at + 28 : at;
        if (!phy.Fits(surface, 48, 1) || !phy.Data.AsSpan(surface + 44, 4).SequenceEqual("IVPS"u8))
            throw new InvalidDataException($"{phy.File} has a solid Filee doesn't read (no IVP compact surface).");
        var root = surface + phy.I32(surface + 32);
        var pieces = new List<Piece>();
        var bone = -1;
        var stack = new Stack<int>();
        stack.Push(root);
        for (var visited = 0; stack.Count > 0; visited++)
        {
            if (visited > MaxNodes)
                throw new InvalidDataException($"{phy.File} is damaged (its collision tree doesn't end).");
            var node = stack.Pop();
            var right = phy.I32(node);
            if (right != 0)
            {
                stack.Push(node + right);
                stack.Push(node + 28);
                continue;
            }
            var ledge = node + phy.I32(node + 4);
            var piece = ReadPiece(phy, ledge);
            budget.Take(piece.Triangles.Count / 3, piece.Points.Count);
            pieces.Add(piece);
            bone = phy.I32(ledge + 4) - 1;
        }
        return new Solid(pieces, bone);
    }

    /// <summary>A convex piece (IVP compact ledge) at <paramref name="ledge"/>, in Source's axes and inches.</summary>
    private static Piece ReadPiece(SourceBytes phy, int ledge)
    {
        var pointTable = ledge + phy.I32(ledge);
        var triangles = phy.I16(ledge + 12);
        if (triangles < 0 || !phy.Fits(ledge + 16, triangles, 16))
            throw phy.Damaged("a collision piece");
        var points = new List<Vector3>();
        var local = new Dictionary<int, int>();
        var indices = new List<int>(triangles * 3);
        for (var t = 0; t < triangles; t++)
        {
            for (var e = 0; e < 3; e++)
            {
                var point = phy.U16(ledge + 16 + t * 16 + 4 + e * 4);
                if (!local.TryGetValue(point, out var index))
                {
                    if (points.Count >= MaxPoints)
                        throw new InvalidDataException($"{phy.File} is damaged (a collision piece claims too many points).");
                    var p = pointTable + point * 16;
                    var ivp = phy.Vector(p);
                    index = points.Count;
                    local[point] = index;
                    points.Add(new Vector3(ivp.X, ivp.Z, -ivp.Y) * InchesPerMetre);
                }
                indices.Add(index);
            }
        }
        return new Piece(points, indices);
    }

    /// <summary>The text section's blocks: a name, then "{", then quoted key and value pairs, then "}".</summary>
    internal static List<Block> ReadBlocks(string text)
    {
        var tokens = Vmt.Tokens(text);
        var blocks = new List<Block>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i + 1] != "{")
                continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var j = i + 2;
            for (; j + 1 < tokens.Count && tokens[j] != "}"; j += 2)
            {
                if (tokens[j + 1] is "{" or "}")
                    break;
                values[tokens[j]] = tokens[j + 1];
            }
            blocks.Add(new Block(tokens[i], values));
            i = j;
        }
        return blocks;
    }
}
