// The HWP and HWPX writers build the whole file in a temporary file next to the output, then move it into place, so a
// failed conversion never leaves half a document under the output's name. The temporary file has a name of its own
// for every run (not the output's name + ".tmp", which another file may already hold) and is created new.

namespace Filee.Engines.Hwp.Binary;

internal static class OutputFile
{
    /// <summary>Writes <paramref name="output"/> through a new temporary file beside it; the temporary file is removed on failure.</summary>
    public static void Write(string output, Action<Stream> write)
    {
        var full = Path.GetFullPath(output);
        var folder = Path.GetDirectoryName(full) ?? ".";
        var temp = Path.Combine(folder, $".{Path.GetFileName(full)}.filee-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                write(stream);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
