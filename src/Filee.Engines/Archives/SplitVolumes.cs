// Split archives (.001, .7z.001, .zip.001, .tar.001): the volumes are plain pieces of one archive file, cut at fixed
// sizes. SplitVolumes reads them as one seekable stream without joining them on disk; the archive inside is told by
// its first bytes (7z, ZIP, RAR or TAR).

namespace Filee.Engines.Archives;

/// <summary>The volumes "name.001", "name.002" … read as one stream.</summary>
internal sealed class SplitVolumes : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;

    private SplitVolumes(FileStream[] parts)
    {
        _parts = parts;
        _starts = new long[parts.Length];
        long start = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            _starts[i] = start;
            start += parts[i].Length;
        }
        _length = start;
    }

    /// <summary>Opens the first volume <paramref name="firstPart"/> ("….001") and every one that follows it.</summary>
    public static SplitVolumes Open(string firstPart)
    {
        var extension = Path.GetExtension(firstPart);
        if (extension.Length < 2 || !extension[1..].All(char.IsAsciiDigit) || int.Parse(extension[1..], System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException($"{Path.GetFileName(firstPart)} is not the first volume of a split archive (….001).");
        var stem = firstPart[..^extension.Length];
        var width = extension.Length - 1;
        var parts = new List<FileStream>();
        try
        {
            for (var n = 1; n < 100_000; n++)
            {
                var path = stem + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');
                if (!File.Exists(path))
                    break;
                parts.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
            }
            return new SplitVolumes([.. parts]);
        }
        catch
        {
            foreach (var part in parts)
                part.Dispose();
            throw;
        }
    }

    /// <summary>Number of volumes found.</summary>
    public int Count => _parts.Length;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.Length == 0)
            return 0;
        var index = Array.BinarySearch(_starts, _position);
        if (index < 0)
            index = ~index - 1;
        while (index < _parts.Length - 1 && _position >= _starts[index] + _parts[index].Length)
            index++;
        var part = _parts[index];
        part.Position = _position - _starts[index];
        var read = part.Read(buffer[..(int)Math.Min(buffer.Length, part.Length - part.Position)]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var part in _parts)
                part.Dispose();
        base.Dispose(disposing);
    }
}
