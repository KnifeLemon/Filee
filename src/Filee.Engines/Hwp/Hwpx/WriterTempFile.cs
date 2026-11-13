// The ODT and PPTX writers build the whole package in a temporary file next to the output, then move it into place,
// so a failed conversion never leaves half a document under the output's name. The temporary file has a name of its
// own for every run (not the output's name + ".tmp", which another file may already hold), is created new, and is
// removed when writing fails.

namespace Filee.Engines.Hwp.Hwpx;

internal static class WriterTempFile
{
    /// <summary>A new, empty temporary file beside <paramref name="output"/>; <paramref name="path"/> is its name.</summary>
    public static FileStream Create(string output, out string path)
    {
        var full = Path.GetFullPath(output);
        var folder = Path.GetDirectoryName(full) ?? ".";
        path = Path.Combine(folder, $".{Path.GetFileName(full)}.filee-{Guid.NewGuid():N}.tmp");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
    }

    /// <summary>Moves a finished temporary file onto <paramref name="output"/>, replacing it.</summary>
    public static void Finish(string path, string output) => File.Move(path, Path.GetFullPath(output), overwrite: true);

    /// <summary>Removes a temporary file left by a failed write, if it is still there.</summary>
    public static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes <paramref name="output"/> through a new temporary file beside it; the temporary file is removed on failure.</summary>
    public static void Write(string output, Action<Stream> write)
    {
        var stream = Create(output, out var temp);
        try
        {
            using (stream)
                write(stream);
            Finish(temp, output);
        }
        finally
        {
            Discard(temp);
        }
    }
}
