// The clip window's editing, without the window: the stretch of the video between the start and end handles, cut into
// pieces with the blade. A piece can be removed (the rest closing up with the magnet on, a pause left with it off),
// put back, made louder or quieter, faster or slower; silent stretches can be taken out at once. Every change can be
// undone. ToEdit() gives what the conversion writes.

namespace Filee.Core.Presets;

/// <summary>What happens to a piece of the clip.</summary>
public enum ClipPieceState
{
    /// <summary>It plays.</summary>
    Kept,

    /// <summary>Cut out: what follows closes up (removed with the magnet on).</summary>
    Removed,

    /// <summary>Cut out, its time left as black and silence (removed with the magnet off).</summary>
    Blank,
}

/// <summary>A piece of the clip between two cuts, in seconds of the video.</summary>
public sealed record ClipPiece(double Start, double End, ClipPieceState State = ClipPieceState.Kept, double Volume = 1, double Speed = 1)
{
    public bool IsKept => State == ClipPieceState.Kept;

    /// <summary>How long it plays in the clip (nothing when removed).</summary>
    public double Length => State == ClipPieceState.Removed ? 0 : Math.Max(0, End - Start) / Speed;
}

/// <summary>Cuts a stretch of a video into pieces and changes them; see the file comment.</summary>
public sealed class ClipCutter
{
    /// <summary>Changes that can be undone.</summary>
    private const int UndoDepth = 100;

    /// <summary>A silent stretch shorter than this is a pause in speech, not a gap: it stays.</summary>
    public const double MinSilence = 0.4;

    /// <summary>Kept of a silence on either side next to sound, so words aren't clipped.</summary>
    public const double SilencePadding = 0.12;

    private List<ClipPiece> _pieces;
    private double _fadeIn, _fadeOut;
    private readonly Stack<(List<ClipPiece> Pieces, double FadeIn, double FadeOut)> _undo = new();
    private readonly Stack<(List<ClipPiece> Pieces, double FadeIn, double FadeOut)> _redo = new();

    /// <summary>What the last change did, when changes alike join into one undo step (a volume slider dragged).</summary>
    private string? _joining;

    /// <param name="duration">The video's length in seconds.</param>
    /// <param name="range">The clip to start with, kept inside the video.</param>
    public ClipCutter(double duration, ClipRange range)
    {
        Duration = Math.Max(ClipRange.MinLength, duration);
        var clip = ClipRange.Clamp(range.Start, range.End, Duration);
        _pieces = [new ClipPiece(clip.Start, clip.End)];
    }

    /// <summary>Raised after every change (also undo and redo).</summary>
    public event Action? Changed;

    public double Duration { get; }

    /// <summary>The pieces from the start to the end handle, one after the other without gaps.</summary>
    public IReadOnlyList<ClipPiece> Pieces => _pieces;

    public double Start => _pieces[0].Start;
    public double End => _pieces[^1].End;

    /// <summary>Seconds the clip fades in (0 for none).</summary>
    public double FadeIn
    {
        get => _fadeIn;
        set => Change(() => _fadeIn = ClipEdit.Fade(value), _fadeIn != ClipEdit.Fade(value));
    }

    /// <summary>Seconds the clip fades out (0 for none).</summary>
    public double FadeOut
    {
        get => _fadeOut;
        set => Change(() => _fadeOut = ClipEdit.Fade(value), _fadeOut != ClipEdit.Fade(value));
    }

    /// <summary>How long the clip plays, in seconds.</summary>
    public double Length => _pieces.Sum(p => p.Length);

    /// <summary>True when pieces were cut out leaving a pause.</summary>
    public bool HasGaps => _pieces.Any(p => p.State == ClipPieceState.Blank);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The piece at <paramref name="seconds"/> of the video, or -1 outside the clip.</summary>
    public int IndexAt(double seconds)
    {
        if (seconds < Start || seconds > End)
            return -1;
        for (var i = 0; i < _pieces.Count; i++)
        {
            if (seconds < _pieces[i].End || i == _pieces.Count - 1)
                return i;
        }
        return -1;
    }

    // ───────────────────────── The ends ─────────────────────────

    /// <summary>
    /// Moves the start to <paramref name="seconds"/>. Past the end it takes the clip along, cuts and all, as far as the
    /// video goes.
    /// </summary>
    public void MoveStart(double seconds)
    {
        if (!double.IsFinite(seconds))
            return;
        var start = Math.Clamp(seconds, 0, Duration - ClipRange.MinLength);
        if (start == Start)
            return;
        Change(() =>
        {
            if (start > End - ClipRange.MinLength)
            {
                Shift(start - Start);
                Bound(Start, Math.Min(End, Duration));
            }
            else
                Bound(start, End);
        });
    }

    /// <summary>Moves the end to <paramref name="seconds"/>; before the start it takes the clip along.</summary>
    public void MoveEnd(double seconds)
    {
        if (!double.IsFinite(seconds))
            return;
        var end = Math.Clamp(seconds, ClipRange.MinLength, Duration);
        if (end == End)
            return;
        Change(() =>
        {
            if (end < Start + ClipRange.MinLength)
            {
                Shift(end - End);
                Bound(Math.Max(0, Start), End);
            }
            else
                Bound(Start, end);
        });
    }

    /// <summary>Moves the whole clip, cuts and all, to start at <paramref name="seconds"/> (inside the video).</summary>
    public void Move(double seconds)
    {
        if (!double.IsFinite(seconds))
            return;
        var start = Math.Clamp(seconds, 0, Math.Max(0, Duration - (End - Start)));
        if (start != Start)
            Change(() => Shift(start - Start));
    }

    /// <summary>
    /// The end <paramref name="seconds"/> after the start (of the video, cuts included); when the video ends sooner,
    /// its last <paramref name="seconds"/>.
    /// </summary>
    public void SetLength(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return;
        var length = Math.Clamp(seconds, ClipRange.MinLength, Duration);
        var start = Math.Min(Start, Duration - length);
        Change(() => Bound(start, start + length));
    }

    // ───────────────────────── Cuts ─────────────────────────

    /// <summary>
    /// The blade: cuts the piece at <paramref name="seconds"/> in two (both keep its volume and speed). False when it
    /// is outside the clip or too close to a cut.
    /// </summary>
    public bool Split(double seconds)
    {
        var index = IndexAt(seconds);
        if (index < 0)
            return false;
        var piece = _pieces[index];
        if (seconds - piece.Start < ClipRange.MinLength || piece.End - seconds < ClipRange.MinLength)
            return false;
        Change(() =>
        {
            _pieces[index] = piece with { End = seconds };
            _pieces.Insert(index + 1, piece with { Start = seconds });
        });
        return true;
    }

    /// <summary>
    /// Cuts piece <paramref name="index"/> out: with <paramref name="closeUp"/> (the magnet) what follows closes up,
    /// otherwise its time stays as a pause. False for the last piece that plays.
    /// </summary>
    public bool Remove(int index, bool closeUp)
    {
        if (index < 0 || index >= _pieces.Count)
            return false;
        var state = closeUp ? ClipPieceState.Removed : ClipPieceState.Blank;
        var piece = _pieces[index];
        if (piece.State == state || piece.IsKept && _pieces.Count(p => p.IsKept) == 1)
            return false;
        Change(() => _pieces[index] = piece with { State = state });
        return true;
    }

    /// <summary>Puts a piece that was cut out back.</summary>
    public bool Restore(int index)
    {
        if (index < 0 || index >= _pieces.Count || _pieces[index].IsKept)
            return false;
        Change(() => _pieces[index] = _pieces[index] with { State = ClipPieceState.Kept });
        return true;
    }

    /// <summary>The volume of piece <paramref name="index"/> (1 as recorded, 0 silent).</summary>
    public void SetVolume(int index, double volume)
    {
        if (index < 0 || index >= _pieces.Count || !double.IsFinite(volume))
            return;
        var value = Math.Clamp(Math.Round(volume, 2), 0, ClipPart.MaxVolume);
        if (_pieces[index].Volume != value)
            Change(() => _pieces[index] = _pieces[index] with { Volume = value }, join: $"volume {index}");
    }

    /// <summary>The speed of piece <paramref name="index"/> (1 as recorded).</summary>
    public void SetSpeed(int index, double speed)
    {
        if (index < 0 || index >= _pieces.Count || !double.IsFinite(speed) || speed <= 0)
            return;
        var value = Math.Clamp(speed, ClipPart.MinSpeed, ClipPart.MaxSpeed);
        if (_pieces[index].Speed != value)
            Change(() => _pieces[index] = _pieces[index] with { Speed = value });
    }

    /// <summary>Closes every pause left by pieces cut out with the magnet off. Returns how many.</summary>
    public int CloseGaps()
    {
        var gaps = _pieces.Count(p => p.State == ClipPieceState.Blank);
        if (gaps > 0)
            Change(() => _pieces = _pieces.Select(p => p.State == ClipPieceState.Blank ? p with { State = ClipPieceState.Removed } : p).ToList());
        return gaps;
    }

    /// <summary>
    /// Takes the silent stretches of the video out of the clip (<paramref name="silences"/>, from FFmpeg's
    /// silencedetect): each one at least <see cref="MinSilence"/> long, less <see cref="SilencePadding"/> next to sound,
    /// is cut out and the rest closes up. Something always stays. Returns how many stretches were taken out; one undo
    /// puts them all back.
    /// </summary>
    public int RemoveSilences(IEnumerable<ClipRange> silences)
    {
        var stretches = new List<(double Start, double End)>();
        foreach (var silence in silences.OrderBy(s => s.Start))
        {
            var start = silence.Start <= Start + 1e-6 ? Start : silence.Start + SilencePadding;
            var end = silence.End >= End - 1e-6 ? End : silence.End - SilencePadding;
            start = Math.Max(start, Start);
            end = Math.Min(end, End);
            if (end - start >= Math.Max(MinSilence - 2 * SilencePadding, ClipRange.MinLength))
                stretches.Add((start, end));
        }
        if (stretches.Count == 0)
            return 0;

        var before = Snapshot();
        var pieces = _pieces;
        var removed = 0;
        foreach (var (start, end) in stretches)
        {
            pieces = Cut(pieces, start);
            pieces = Cut(pieces, end);
            var next = pieces.Select(p => p.IsKept && p.Start >= start - 1e-6 && p.End <= end + 1e-6
                ? p with { State = ClipPieceState.Removed } : p).ToList();
            if (next.Any(p => p.IsKept))
            {
                removed++;
                pieces = next;
            }
        }
        if (removed == 0)
            return 0;
        Remember(before);
        _joining = null;
        _pieces = pieces;
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Takes back the last change.</summary>
    public bool Undo() => Step(_undo, _redo);

    /// <summary>Does the last undone change again.</summary>
    public bool Redo() => Step(_redo, _undo);

    /// <summary>What the conversion writes: the pieces that play (neighbours alike joined), the pauses and the fades.</summary>
    /// <exception cref="InvalidOperationException">Every piece is cut out.</exception>
    public ClipEdit ToEdit()
    {
        var edit = new ClipEdit { FadeIn = FadeIn, FadeOut = FadeOut };
        foreach (var piece in _pieces.Where(p => p.State != ClipPieceState.Removed))
        {
            var blank = piece.State == ClipPieceState.Blank;
            if (edit.Parts.Count > 0 && edit.Parts[^1] is var last && !blank && !last.Blank
                && Math.Abs(last.End - piece.Start) < 1e-9 && last.Volume == piece.Volume && last.Speed == piece.Speed)
            {
                last.End = piece.End;
                continue;
            }
            edit.Parts.Add(new ClipPart { Start = piece.Start, End = piece.End, Blank = blank, Volume = piece.Volume, Speed = piece.Speed });
        }
        if (!edit.Parts.Any(p => !p.Blank))
            throw new InvalidOperationException("Every piece of the clip is cut out.");
        return edit;
    }

    // ───────────────────────── Inside ─────────────────────────

    private (List<ClipPiece>, double, double) Snapshot() => ([.. _pieces], _fadeIn, _fadeOut);

    private void Remember((List<ClipPiece>, double, double) state)
    {
        _undo.Push(state);
        _redo.Clear();
        if (_undo.Count > UndoDepth)
        {
            var kept = _undo.Take(UndoDepth).Reverse().ToList();
            _undo.Clear();
            foreach (var item in kept)
                _undo.Push(item);
        }
    }

    /// <param name="join">
    /// Changes in a row with the same value are one undo step: dragging a volume slider is undone at once.
    /// </param>
    private void Change(Action change, bool changes = true, string? join = null)
    {
        if (!changes)
            return;
        var before = Snapshot();
        change();
        if (join is null || join != _joining || _undo.Count == 0)
            Remember(before);
        else
            _redo.Clear();
        _joining = join;
        Changed?.Invoke();
    }

    private bool Step(Stack<(List<ClipPiece>, double, double)> from, Stack<(List<ClipPiece>, double, double)> to)
    {
        if (from.Count == 0)
            return false;
        to.Push(Snapshot());
        (_pieces, _fadeIn, _fadeOut) = from.Pop();
        _joining = null;
        Changed?.Invoke();
        return true;
    }

    private void Shift(double delta) =>
        _pieces = _pieces.Select(p => p with { Start = p.Start + delta, End = p.End + delta }).ToList();

    /// <summary>
    /// The pieces between <paramref name="start"/> and <paramref name="end"/>: those outside dropped, the first and last
    /// trimmed or stretched to the ends, slivers shorter than <see cref="ClipRange.MinLength"/> joined to their
    /// neighbour. Something always plays.
    /// </summary>
    private void Bound(double start, double end)
    {
        var inside = _pieces
            .Select(p => p with { Start = Math.Max(p.Start, start), End = Math.Min(p.End, end) })
            .Where(p => p.End - p.Start > 1e-9)
            .ToList();
        if (inside.Count == 0)
            inside = [_pieces[start >= End ? ^1 : 0] with { Start = start, End = end }];
        inside[0] = inside[0] with { Start = start };
        inside[^1] = inside[^1] with { End = end };
        // Slivers left at the ends join the piece next to them.
        while (inside.Count > 1 && inside[0].End - inside[0].Start < ClipRange.MinLength)
        {
            inside.RemoveAt(0);
            inside[0] = inside[0] with { Start = start };
        }
        while (inside.Count > 1 && inside[^1].End - inside[^1].Start < ClipRange.MinLength)
        {
            inside.RemoveAt(inside.Count - 1);
            inside[^1] = inside[^1] with { End = end };
        }
        if (!inside.Any(p => p.IsKept))
            inside[0] = inside[0] with { State = ClipPieceState.Kept };
        _pieces = inside;
    }

    /// <summary><paramref name="pieces"/> cut at <paramref name="seconds"/>, unless it is (nearly) a cut already.</summary>
    private static List<ClipPiece> Cut(List<ClipPiece> pieces, double seconds)
    {
        var index = pieces.FindIndex(p => seconds > p.Start + 1e-6 && seconds < p.End - 1e-6);
        if (index < 0)
            return pieces;
        var piece = pieces[index];
        var copy = new List<ClipPiece>(pieces);
        copy[index] = piece with { End = seconds };
        copy.Insert(index + 1, piece with { Start = seconds });
        return copy;
    }
}
