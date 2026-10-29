// The parts of a picture the "Pixelate / blur" window hides, without a window: the frames (in parts of the picture),
// the one selected, and undo / redo of adding, moving, sizing and removing them. The canvas draws and drags them, the
// keys move and remove them, the window's view model turns them into the conversion.

using Avalonia;

namespace Filee.App.Controls;

public sealed class HideAreaSet
{
    /// <summary>Most parts one picture takes: more are a slip of the hand, not something to hide.</summary>
    public const int MaxAreas = 100;

    /// <summary>Undo steps kept.</summary>
    private const int MaxUndo = 100;

    private readonly List<Rect> _areas = [];
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];

    /// <summary>The state when a drag began (<see cref="Begin"/>): one undo step however far it went.</summary>
    private Snapshot? _gesture;

    private readonly record struct Snapshot(Rect[] Areas, int Selected);

    /// <summary>The frames, in parts of the picture (0 to 1), in the order they were drawn (the last on top).</summary>
    public IReadOnlyList<Rect> Areas => _areas;

    /// <summary>The selected frame, -1 for none.</summary>
    public int Selected { get; private set; } = -1;

    public Rect? SelectedArea => Selected >= 0 ? _areas[Selected] : null;
    public int Count => _areas.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Something changed: the frames, the selection or what can be undone.</summary>
    public event Action? Changed;

    /// <summary>A drag starts: what it does until <see cref="End"/> is undone in one step.</summary>
    public void Begin() => _gesture ??= Take();

    /// <summary>The drag ends: one undo step when it changed the frames.</summary>
    public void End()
    {
        if (_gesture is not { } before)
            return;
        _gesture = null;
        if (!before.Areas.SequenceEqual(_areas))
            Push(before);
        Changed?.Invoke();
    }

    /// <summary>Adds a frame and selects it; false when there are <see cref="MaxAreas"/> already.</summary>
    public bool Add(Rect area)
    {
        if (_areas.Count >= MaxAreas)
            return false;
        Record();
        _areas.Add(Fit(area));
        Selected = _areas.Count - 1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves or sizes frame <paramref name="index"/> (a drag; see <see cref="Begin"/>).</summary>
    public void Set(int index, Rect area)
    {
        if (index < 0 || index >= _areas.Count)
            return;
        var fitted = Fit(area);
        if (_areas[index] == fitted)
            return;
        Record();
        _areas[index] = fitted;
        Changed?.Invoke();
    }

    public void Select(int index)
    {
        index = index >= 0 && index < _areas.Count ? index : -1;
        if (index == Selected)
            return;
        Selected = index;
        Changed?.Invoke();
    }

    /// <summary>
    /// The next (or previous) frame in the order they were drawn, round and round: from none, the first (or the last).
    /// </summary>
    public void SelectNext(int step)
    {
        if (_areas.Count == 0)
            return;
        Select(Selected < 0
            ? (step >= 0 ? 0 : _areas.Count - 1)
            : ((Selected + step) % _areas.Count + _areas.Count) % _areas.Count);
    }

    /// <summary>Removes the selected frame; the one drawn before it is selected next. False when none is selected.</summary>
    public bool RemoveSelected()
    {
        if (Selected < 0)
            return false;
        Record();
        _areas.RemoveAt(Selected);
        Selected = _areas.Count == 0 ? -1 : Math.Max(0, Selected - 1);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every frame (undoable).</summary>
    public void Clear()
    {
        if (_areas.Count == 0)
            return;
        Record();
        _areas.Clear();
        Selected = -1;
        Changed?.Invoke();
    }

    /// <summary>Moves the selected frame by whole pixels of a <paramref name="width"/> × <paramref name="height"/> picture.</summary>
    public void Nudge(int dx, int dy, int width, int height)
    {
        if (Selected < 0)
            return;
        Set(Selected, CropMath.Nudge(_areas[Selected], dx, dy, width, height));
    }

    /// <summary>
    /// Starts over with <paramref name="areas"/> (a new picture: the last one's frames carry on), none selected and
    /// nothing to undo.
    /// </summary>
    public void Reset(IEnumerable<Rect> areas)
    {
        _gesture = null;
        _areas.Clear();
        _areas.AddRange(areas.Select(Fit).Take(MaxAreas));
        _undo.Clear();
        _redo.Clear();
        Selected = -1;
        Changed?.Invoke();
    }

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    /// <summary>
    /// The frame on top under <paramref name="point"/> (in parts of the picture), -1 for none; the selected one first, so
    /// a frame drawn over it later doesn't take it away while it is being worked on.
    /// </summary>
    public int HitTest(Point point)
    {
        if (Selected >= 0 && _areas[Selected].Contains(point))
            return Selected;
        for (var i = _areas.Count - 1; i >= 0; i--)
        {
            if (_areas[i].Contains(point))
                return i;
        }
        return -1;
    }

    private void Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0 || _gesture is not null)
            return;
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(Take());
        _areas.Clear();
        _areas.AddRange(snapshot.Areas);
        Selected = Math.Min(snapshot.Selected, _areas.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>An undo step for a change made now, unless a drag records one for itself.</summary>
    private void Record()
    {
        if (_gesture is null)
            Push(Take());
    }

    private void Push(Snapshot snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private Snapshot Take() => new([.. _areas], Selected);

    /// <summary>The frame inside the picture.</summary>
    private static Rect Fit(Rect area)
    {
        var left = Math.Clamp(Math.Min(area.Left, area.Right), 0, 1);
        var top = Math.Clamp(Math.Min(area.Top, area.Bottom), 0, 1);
        var right = Math.Clamp(Math.Max(area.Left, area.Right), left, 1);
        var bottom = Math.Clamp(Math.Max(area.Top, area.Bottom), top, 1);
        return new Rect(left, top, right - left, bottom - top);
    }
}
