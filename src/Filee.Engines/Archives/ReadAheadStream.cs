// Runs a decoder on a thread of its own, a few blocks ahead of whoever reads it. A decoder that has to go through its
// data in order (one LZMA stream, a BCJ filter after it, the sub-streams of BCJ2) then works while the reader checks
// CRCs and writes files, or while the next stage of the chain works on what came before; the slowest stage sets the
// pace instead of the sum of them all.
//
// What the reader gets is exactly what the decoder gives, in the same order. The worker asks the decoder for
// PieceSize bytes at a time (what ArchiveFiles.CopyLimited asks for), so an error comes after the same bytes, give or
// take one piece: the decoder's exception itself is thrown to the reader once the bytes before it are read. Disposing
// stops the worker and waits for it, so the decoder (and the archive stream under it) is never used after that.

using System.Runtime.ExceptionServices;

namespace Filee.Engines.Archives;

/// <summary>A read-only stream that reads its inner stream ahead on a worker thread.</summary>
internal sealed class ReadAheadStream : Stream
{
    /// <summary>Streams shorter than this are read directly: a thread would cost more than it gives.</summary>
    public const long MinSize = 1 << 20;

    /// <summary>Bytes handed over at a time.</summary>
    private const int BlockSize = 1 << 20;

    /// <summary>Bytes the worker asks the decoder for at a time.</summary>
    private const int PieceSize = 1 << 16;

    /// <summary>Blocks read ahead at most (memory: this many blocks, plus the one being read).</summary>
    private const int Depth = 4;

    /// <summary>Tests turn read-ahead off (false) or on for any size (true).</summary>
    internal static readonly AsyncLocal<bool?> TestMode = new();

    /// <summary>Streams opened with a worker so far (for tests).</summary>
    internal static int Opened;

    private readonly Stream _inner;
    private readonly CancellationTokenSource _cancel;
    private readonly object _gate = new();
    private readonly Queue<Block> _filled = new();
    private readonly Stack<byte[]> _free = new();
    private readonly Thread _worker;
    private Block? _current;
    private int _at;
    private bool _stopping;
    private bool _workerDone;

    private sealed record Block(byte[] Data, int Count, bool End, ExceptionDispatchInfo? Error);

    private ReadAheadStream(Stream inner, CancellationToken cancellationToken)
    {
        _inner = inner;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        for (var i = 0; i < Depth + 1; i++)
            _free.Push(GC.AllocateUninitializedArray<byte>(BlockSize));
        Interlocked.Increment(ref Opened);
        _worker = new Thread(Work) { IsBackground = true, Name = "Filee read-ahead" };
        _worker.Start();
    }

    /// <summary>
    /// <paramref name="inner"/> read ahead on a worker thread when that pays off (more than one processor, at least
    /// <see cref="MinSize"/> bytes, or <paramref name="size"/> -1 for not known); else <paramref name="inner"/> itself.
    /// The result owns <paramref name="inner"/>.
    /// </summary>
    public static Stream Wrap(Stream inner, long size, CancellationToken cancellationToken)
    {
        var mode = TestMode.Value;
        if (mode == false || (mode is null && (Environment.ProcessorCount < 2 || (size >= 0 && size < MinSize))))
            return inner;
        return new ReadAheadStream(inner, cancellationToken);
    }

    private void Work()
    {
        var token = _cancel.Token;
        try
        {
            while (true)
            {
                byte[] data;
                lock (_gate)
                {
                    while (_free.Count == 0 && !_stopping)
                        Monitor.Wait(_gate);
                    if (_stopping)
                        return;
                    data = _free.Pop();
                }
                var count = 0;
                var end = false;
                ExceptionDispatchInfo? error = null;
                try
                {
                    while (count < data.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        var read = _inner.Read(data, count, Math.Min(PieceSize, data.Length - count));
                        if (read <= 0)
                        {
                            end = true;
                            break;
                        }
                        count += read;
                    }
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
                lock (_gate)
                {
                    _filled.Enqueue(new Block(data, count, end, error));
                    Monitor.PulseAll(_gate);
                }
                if (end || error is not null)
                    return;
            }
        }
        finally
        {
            lock (_gate)
            {
                _workerDone = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        while (true)
        {
            if (_current is { } current)
            {
                if (_at < current.Count)
                {
                    var count = Math.Min(buffer.Length, current.Count - _at);
                    current.Data.AsSpan(_at, count).CopyTo(buffer);
                    _at += count;
                    return count;
                }
                current.Error?.Throw();
                if (current.End)
                    return 0;
                lock (_gate)
                {
                    _free.Push(current.Data);
                    Monitor.PulseAll(_gate);
                }
                _current = null;
            }
            lock (_gate)
            {
                while (_filled.Count == 0)
                {
                    if (_workerDone)
                        throw new ObjectDisposedException(nameof(ReadAheadStream));
                    Monitor.Wait(_gate);
                }
                _current = _filled.Dequeue();
                _at = 0;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_stopping)
        {
            lock (_gate)
            {
                _stopping = true;
                Monitor.PulseAll(_gate);
            }
            _cancel.Cancel();
            // The decoder and the archive under it are left alone once this returns.
            _worker.Join();
            _cancel.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
