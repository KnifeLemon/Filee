// DonutMenu: pointer input. Hover, press, drag of a slice (editor) and release on a slice or the hub.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    // ───────────────────────── Input ─────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_progressMode)
        {
            OnProgressPointerMoved(p);
            return;
        }

        if (_press is { } press && IsEditMode && Distance(p, press.Start) > DragStartDistance && press.Index >= 0)
        {
            _press = null;
            SegmentDragStarted?.Invoke(this, (press.Index, e));
            return;
        }

        if (_keyboardHold)
        {
            if (_keyAnchor is not { } anchor)
                _keyAnchor = anchor = p;
            if (Distance(p, anchor) < KeyboardJitter)
                return;
            _keyboardHold = false;
        }
        _lastPointer = p;
        var hit = HitTest(p);
        if (IsFlipping)
            PointAtAngle(DonutGeometry.AngleOf(Center, p), hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment);
        TrackGroups(hit, p);
        SetHover(hit.Kind switch
        {
            DonutHitKind.Segment or DonutHitKind.EditButton or DonutHitKind.SubSegment => hit.Index,
            _ => -1,
        });
        // On a group's ring, why that slice (not the group) is disabled.
        var hovered = hit.Kind == DonutHitKind.SubSegment ? Items![hit.Index].Children![hit.Sub]
            : _hoverIndex >= 0 ? Items![_hoverIndex] : null;
        ToolTip.SetTip(this, hovered is { IsEnabled: false } ? hovered.DisabledReason : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_progressMode)
        {
            OnProgressPointerMoved(new Point(double.MaxValue / 4, 0));
            return;
        }
        if (_press is null && !LeftThroughSeam(e.GetPosition(this)))
        {
            CollapseGroup();
            SetHover(-1);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var hit = HitTest(point.Position);

        if (_progressMode)
        {
            if (point.Properties.IsLeftButtonPressed)
            {
                _press = (-1, point.Position, e);
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (hit.Kind is DonutHitKind.Segment or DonutHitKind.EditButton && (IsEditMode || AllowContextEdit))
            {
                SegmentContextRequested?.Invoke(this, hit.Index);
                e.Handled = true;
            }
            else if (hit.Kind == DonutHitKind.SubSegment && AllowContextEdit)
            {
                SubSegmentContextRequested?.Invoke(this, (hit.Index, hit.Sub));
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _press = (hit.Kind == DonutHitKind.Segment ? hit.Index : -1, point.Position, e);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is null)
            return;
        _press = null;

        if (_progressMode)
        {
            OnProgressPointerReleased(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        var hit = HitTest(e.GetPosition(this));
        switch (hit.Kind)
        {
            case DonutHitKind.EditButton:
                SegmentContextRequested?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Segment when !IsEditMode && Items![hit.Index] is { IsEnabled: true, IsGroup: false }:
                SegmentInvoked?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Segment when !IsEditMode && Items![hit.Index].Children is { Count: > 0 }:
                Expand(hit.Index); // no waiting when clicked; a tap has no hover to rest on at all
                break;
            case DonutHitKind.Outside when !IsEditMode && _expanded >= 0:
                CollapseGroup(); // clicked past the ring
                break;
            case DonutHitKind.SubSegment when !IsEditMode && Items![hit.Index].Children![hit.Sub].IsEnabled:
                SubSegmentInvoked?.Invoke(this, (hit.Index, hit.Sub));
                break;
            case DonutHitKind.Center when !IsEditMode:
                CenterInvoked?.Invoke(this, EventArgs.Empty);
                break;
        }
        e.Handled = true;
    }

    private void SetHover(int index)
    {
        if (_hoverIndex == index)
            return;
        var previous = _hoverIndex;
        _hoverIndex = index;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        AnnounceHover(previous, index);
        StartAnimation();
    }
}
