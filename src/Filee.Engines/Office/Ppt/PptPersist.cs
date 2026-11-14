// The persist directory of a PowerPoint 97–2003 presentation ([MS-PPT] 2.1.2, 2.3.3–2.3.6): every save appends a
// UserEditAtom and a PersistDirectoryAtom to the PowerPoint Document stream; the Current User stream points to the last
// edit, each edit to the one before it. A persist directory maps persist ids (what the document refers to: slides,
// masters, notes, the document itself) to where their record starts in the stream; a later save's entries replace an
// earlier one's.
//
// The chain comes from the file: each edit's offset is visited once (a chain that loops ends there) and at most
// MaxEdits edits are followed. A directory entry lists as many offsets as fit in its record, and every offset is checked
// to hold a record inside the stream before it is used.

using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

internal sealed class PptPersist
{
    /// <summary>Saves followed back from the last one at most (PowerPoint merges them long before this).</summary>
    public const int MaxEdits = 65_536;

    private readonly byte[] _stream;
    private readonly Dictionary<uint, uint> _offsets = [];

    private PptPersist(byte[] stream) => _stream = stream;

    /// <summary>The persist id of the DocumentContainer, from the last edit.</summary>
    public uint DocumentRef { get; private set; }

    /// <summary>The last edit holds the persist id of an encryption session (CryptSession10Container).</summary>
    public bool Encrypted { get; private set; }

    /// <summary>
    /// Reads the edits from <paramref name="currentEdit"/> back to the first. Damage that leaves no document is invalid
    /// data.
    /// </summary>
    public static PptPersist Read(byte[] stream, uint currentEdit)
    {
        var persist = new PptPersist(stream);
        var visited = new HashSet<uint>();
        var directories = new List<OfficeArtRecord>();
        var offset = currentEdit;
        var first = true;
        while (visited.Add(offset) && visited.Count <= MaxEdits)
        {
            if (PptRecords.At(stream, offset) is not { Type: PptRecords.UserEditAtom, Length: >= 28 } edit)
            {
                if (first)
                    throw new InvalidDataException("The presentation is damaged: its last save (user edit) is not where the Current User stream says.");
                break;
            }
            if (first)
            {
                persist.DocumentRef = PptRecords.U32(stream, edit.Start + 16);
                persist.Encrypted = edit.Length >= 32 && PptRecords.U32(stream, edit.Start + 28) != 0;
                first = false;
            }
            if (PptRecords.At(stream, PptRecords.U32(stream, edit.Start + 12)) is { Type: PptRecords.PersistDirectoryAtom } directory)
                directories.Add(directory);
            var previous = PptRecords.U32(stream, edit.Start + 8);
            if (previous == 0)
                break;
            offset = previous;
        }

        // The oldest directory first, so later saves win.
        for (var i = directories.Count - 1; i >= 0; i--)
        {
            var directory = directories[i];
            var at = directory.Start;
            while (at + 4 <= directory.End)
            {
                var entry = PptRecords.U32(stream, at);
                var id = entry & 0xFFFFF;
                var count = (int)(entry >> 20);
                at += 4;
                for (var k = 0; k < count && at + 4 <= directory.End; k++, at += 4)
                    persist._offsets[id + (uint)k] = PptRecords.U32(stream, at);
            }
        }
        return persist;
    }

    /// <summary>The record persist id <paramref name="id"/> stands for, when it is listed and fits in the stream.</summary>
    public OfficeArtRecord? Record(uint id) =>
        _offsets.TryGetValue(id, out var offset) ? PptRecords.At(_stream, offset) : null;
}
