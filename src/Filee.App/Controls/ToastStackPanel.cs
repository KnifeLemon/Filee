// Fits complete cards in arrival order; waiting cards are measured but neither drawn nor interactive.

using Avalonia;
using Avalonia.Controls;

namespace Filee.App.Controls;

/// <summary>A screen-sized notification stack, reserving room for its waiting-count footer only when needed.</summary>
public sealed class ToastStackPanel : Panel
{
    internal const double Gap = 8;
    internal const double FooterHeight = 36;
    private double _availableHeight = double.PositiveInfinity;

    /// <summary>Height for cards and the optional footer, excluding the ItemsControl margins.</summary>
    internal double AvailableHeight
    {
        get => _availableHeight;
        set { if (_availableHeight != value) { _availableHeight = value; InvalidateMeasure(); } }
    }

    /// <summary>Cards admitted by the last measure; the remainder wait in arrival order.</summary>
    internal int VisibleCount { get; private set; }

    /// <summary>Cards waiting for room.</summary>
    internal int PendingCount => Children.Count - VisibleCount;

    /// <summary>Clips waiting containers outside the admitted stack.</summary>
    public ToastStackPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var total = Children.Sum(c => c.DesiredSize.Height) + Math.Max(0, Children.Count - 1) * Gap;
        var budget = total <= AvailableHeight ? AvailableHeight : Math.Max(0, AvailableHeight - FooterHeight);
        var height = 0.0;
        VisibleCount = 0;
        foreach (var child in Children)
        {
            var next = height + (VisibleCount == 0 ? 0 : Gap) + child.DesiredSize.Height;
            // Always admit one card: an unusually tall card can use the window's scroll fallback.
            if (VisibleCount > 0 && next > budget) break;
            height = next;
            VisibleCount++;
        }
        return new Size(Children.Count == 0 ? 0 : Children.Max(c => c.DesiredSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var visible = i < VisibleCount;
            child.IsEnabled = visible;
            child.IsHitTestVisible = visible;
            if (visible)
            {
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height + Gap;
            }
            else child.Arrange(new Rect(0, finalSize.Height, finalSize.Width, 0));
        }
        return finalSize;
    }
}
