// The marks the "Annotate" window draws on a picture, without a window: the marks in the order drawn (the last on top),
// the one selected, and undo / redo of adding, moving, resizing, restyling, retyping and removing them. A drag is one
// undo step however far it goes.

using Filee.Core.Presets;

namespace Filee.App.Controls;

public sealed class AnnotationSet
{
    /// <summary>Most marks one picture takes.</summary>
    public const int MaxMarks = 500;

    /// <summary>Undo steps kept.</summary>
    private const int MaxUndo = 100;

    private readonly List<Annotation> _marks = [];
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private Snapshot? _gesture;
    private bool _gestureChanged;

    private sealed record Snapshot(Annotation[] Marks, int Selected);

    /// <summary>The marks, in the order drawn.</summary>
    public IReadOnlyList<Annotation> Marks => _marks;

    /// <summary>The selected mark, -1 for none.</summary>
    public int Selected { get; private set; } = -1;

    public Annotation? SelectedMark => Selected >= 0 ? _marks[Selected] : null;
    public int Count => _marks.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The next number a Number mark gets: one more than the highest on the picture.</summary>
    public int NextNumber => _marks.Where(m => m.Kind == AnnotationKind.Number).Select(m => m.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Something changed: a mark, the selection or what can be undone.</summary>
    public event Action? Changed;

    /// <summary>A drag starts: what it does until <see cref="End"/> is one undo step.</summary>
    public void Begin()
    {
        _gesture ??= Take();
        _gestureChanged = false;
    }

    /// <summary>The drag ends: one undo step when it changed anything.</summary>
    public void End()
    {
        if (_gesture is not { } before)
            return;
        _gesture = null;
        if (_gestureChanged)
            Push(before);
        Changed?.Invoke();
    }

    /// <summary>The drag is dropped: everything as it was before it (a click that drew nothing).</summary>
    public void Cancel()
    {
        if (_gesture is not { } before)
            return;
        _gesture = null;
        Restore(before);
        Changed?.Invoke();
    }

    /// <summary>Adds a mark and selects it; false when there are <see cref="MaxMarks"/> already.</summary>
    public bool Add(Annotation mark)
    {
        if (_marks.Count >= MaxMarks)
            return false;
        Record();
        _marks.Add(mark);
        Selected = _marks.Count - 1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Changes mark <paramref name="index"/> (a drag's step, or one undo step on its own).</summary>
    public void Change(int index, Action<Annotation> change)
    {
        if (index < 0 || index >= _marks.Count)
            return;
        Record();
        change(_marks[index]);
        Changed?.Invoke();
    }

    public void Select(int index)
    {
        index = index >= 0 && index < _marks.Count ? index : -1;
        if (index == Selected)
            return;
        Selected = index;
        Changed?.Invoke();
    }

    /// <summary>
    /// The next (or previous) mark in the order they were drawn, round and round: from none, the first (or the last).
    /// </summary>
    public void SelectNext(int step)
    {
        if (_marks.Count == 0)
            return;
        Select(Selected < 0
            ? (step >= 0 ? 0 : _marks.Count - 1)
            : ((Selected + step) % _marks.Count + _marks.Count) % _marks.Count);
    }

    /// <summary>
    /// Moves the selected mark by whole pixels of a <paramref name="width"/> × <paramref name="height"/> picture, as
    /// far as it stays on the picture (one undo step).
    /// </summary>
    public void Nudge(int dx, int dy, int width, int height)
    {
        if (SelectedMark is not { } mark || width <= 0 || height <= 0)
            return;
        var moveX = Math.Clamp((double)dx / width, -mark.Points.Min(p => p.X), 1 - mark.Points.Max(p => p.X));
        var moveY = Math.Clamp((double)dy / height, -mark.Points.Min(p => p.Y), 1 - mark.Points.Max(p => p.Y));
        if (moveX == 0 && moveY == 0)
            return;
        Change(Selected, moved =>
        {
            for (var i = 0; i < moved.Points.Count; i++)
                moved.Points[i] = new AnnotationPoint { X = moved.Points[i].X + moveX, Y = moved.Points[i].Y + moveY };
        });
    }

    /// <summary>Removes the selected mark; none is selected afterwards. False when none was.</summary>
    public bool RemoveSelected()
    {
        if (Selected < 0)
            return false;
        Record();
        _marks.RemoveAt(Selected);
        Selected = -1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Starts over with no marks and nothing to undo (a new picture).</summary>
    public void Reset()
    {
        _gesture = null;
        _marks.Clear();
        _undo.Clear();
        _redo.Clear();
        Selected = -1;
        Changed?.Invoke();
    }

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    private void Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0 || _gesture is not null)
            return;
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(Take());
        Restore(snapshot);
        Changed?.Invoke();
    }

    /// <summary>An undo step for a change made now, unless a drag records one for itself.</summary>
    private void Record()
    {
        if (_gesture is not null)
        {
            _gestureChanged = true;
            return;
        }
        Push(Take());
    }

    private void Push(Snapshot snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private Snapshot Take() => new([.. _marks.Select(m => m.Clone())], Selected);

    private void Restore(Snapshot snapshot)
    {
        _marks.Clear();
        _marks.AddRange(snapshot.Marks.Select(m => m.Clone()));
        Selected = Math.Min(snapshot.Selected, _marks.Count - 1);
    }
}
