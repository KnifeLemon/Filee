// Unpacks the usual ZIP (stored and Deflate entries, nearly every ZIP there is) with .NET's own reader, whose
// native zlib inflates several times faster than SharpCompress's managed one. An archive with any other method
// (LZMA, BZip2, PPMd, Deflate64 … from 7-Zip or WinZip) is left to SharpCompress, before anything is written. Names
// without the UTF-8 flag are read as UTF-8 when valid, else with the system's OEM code page, as in LibraryArchives.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>ZIP through System.IO.Compression.</summary>
internal static class ZipReader
{
    /// <summary>
    /// Unpacks the ZIP in <paramref name="stream"/>; false (nothing written) when an entry needs SharpCompress or
    /// .NET cannot read the archive at all.
    /// </summary>
    public static bool TryExtract(Stream stream, string name, string folder, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try
        {
            stream.Position = 0;
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, NameEncoding.Instance);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        using (zip)
        {
            List<ZipArchiveEntry> entries;
            try
            {
                entries = [.. zip.Entries];
                if (entries.Any(e => e.IsEncrypted))
                    throw ArchiveSafety.Encrypted(name);
                // Opening an entry reads its local header and tells an unsupported method before any file is written.
                foreach (var entry in entries.Where(e => !IsFolder(e) && e.Length > 0))
                    entry.Open().Dispose();
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException && !ArchiveSafety.IsRefusal(ex))
            {
                return false;
            }

            ArchiveSafety.CheckListing([.. entries.Select(e => new ArchiveItem(e.FullName, IsFolder(e), IsFolder(e) ? 0 : e.Length, IsLink: IsLink(e)))],
                name, stream.Length, folder);
            var writer = new EntryWriter(folder, new UnpackBudget(name, stream.Length, folder), cancellationToken);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modified = entry.LastWriteTime.Year > 1980 ? entry.LastWriteTime.LocalDateTime : (DateTime?)null;
                if (IsFolder(entry))
                    writer.AddFolder(entry.FullName, modified);
                else if (!IsLink(entry))
                    using (var data = entry.Open())
                        writer.AddFile(entry.FullName, data, entry.Length, entry.Length > 0 ? entry.Crc32 : null, modified);
            }
            writer.Finish();
            return true;
        }
    }

    private static bool IsFolder(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>A Unix symbolic link (mode in the upper 16 bits) or a Windows reparse point.</summary>
    private static bool IsLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0;

    /// <summary>UTF-8 when the bytes are valid UTF-8, else the OEM code page (see <see cref="LibraryArchives.DecodeName"/>).</summary>
    private sealed class NameEncoding : Encoding
    {
        public static readonly NameEncoding Instance = new();

        public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => GetString(bytes, index, count).Length;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            var text = GetString(bytes, byteIndex, byteCount);
            text.CopyTo(0, chars, charIndex, text.Length);
            return text.Length;
        }

        public override string GetString(byte[] bytes, int index, int count) => LibraryArchives.DecodeName(bytes.AsSpan(index, count));
        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
