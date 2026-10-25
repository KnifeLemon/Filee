// DonutMenu: screen readers, the keyboard in the editor, a focus ring and high contrast.
//
// The ring is drawn by hand (no child controls), so a screen reader would find nothing in it: DonutMenuAutomationPeer
// presents the donut as a list and every slice as an item that can be selected (the keyboard's slice) and invoked
// (convert, open a group; in the editor: edit it, as ✎ does), with a disabled slice's reason as its help text.
// In the editor (Settings → Donut toolbar) the arrows move between slices, Enter / Space / F2 / the context-menu key
// edit one and Ctrl + arrow moves it; the ✎ button shows for the keyboard's slice as for the hovered one.
// With the system's high contrast on, the "high-contrast" class draws the ring in the theme's text colour on its
// background, with heavier outlines (Styles/Controls.axaml).

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    private DonutMenuAutomationPeer? _peer;
    private IPlatformSettings? _platformSettings;

    /// <summary>Ctrl + arrow in the editor: move the slice <c>From</c> to position <c>To</c>.</summary>
    public event EventHandler<(int From, int To)>? SegmentMoveRequested;

    /// <summary>The system asks for high contrast (Windows: Settings → Accessibility → Contrast themes).</summary>
    public bool IsHighContrast => Classes.Contains("high-contrast");

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new DonutMenuAutomationPeer(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _platformSettings = Application.Current?.PlatformSettings;
        if (_platformSettings is null)
            return;
        _platformSettings.ColorValuesChanged += OnColorValuesChanged;
        OnColorValuesChanged(this, _platformSettings.GetColorValues());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_platformSettings is not null)
            _platformSettings.ColorValuesChanged -= OnColorValuesChanged;
        _platformSettings = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues values)
    {
        Classes.Set("high-contrast", values.ContrastPreference == ColorContrastPreference.High);
        InvalidateVisual();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsEditMode && HandleEditKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// The editor's keyboard: arrows choose a slice, Enter / Space / F2 / the context-menu key edit it, Ctrl + arrow
    /// moves it. Returns false for keys left to the page (Tab moves the focus on).
    /// </summary>
    public bool HandleEditKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (!IsEditMode || Count == 0)
            return false;
        var current = _hoverIndex;
        var step = key switch
        {
            Key.Right or Key.Down => 1,
            Key.Left or Key.Up => -1,
            _ => 0,
        };
        if (step != 0 && current >= 0 && modifiers.HasFlag(KeyModifiers.Control))
        {
            var to = (current + step + Count) % Count;
            SegmentMoveRequested?.Invoke(this, (current, to));
            SetHover(to);
            return true;
        }
        if (step != 0)
        {
            SetHover(current < 0 ? (step > 0 ? 0 : Count - 1) : (current + step + Count) % Count);
            return true;
        }
        if (key is Key.Enter or Key.Space or Key.F2 or Key.Apps && current >= 0)
        {
            SegmentContextRequested?.Invoke(this, current);
            return true;
        }
        return false;
    }

    /// <summary>A screen reader chose a slice (selection pattern): it becomes the keyboard's slice.</summary>
    internal void SelectFromAutomation(int index)
    {
        if (index < 0 || index >= Count)
            return;
        SetHover(index);
        _keyboardHold = true;
        _keyAnchor = _lastPointer;
    }

    /// <summary>A screen reader invoked a slice: edit it in the editor, else what Enter does (convert or open a group).</summary>
    internal void InvokeFromAutomation(int index)
    {
        if (index < 0 || index >= Count)
            return;
        if (IsEditMode)
        {
            SegmentContextRequested?.Invoke(this, index);
            return;
        }
        SelectFromAutomation(index);
        HandleKey(Key.Enter);
    }

    /// <summary>The slice a screen reader can show: its box in this control's coordinates.</summary>
    internal Rect SliceBounds(int index) =>
        index < 0 || index >= Count ? default : CreateWedge(Center, InnerRadius, OuterRadius, index, Count).Bounds;

    internal DonutItem? ItemAt(int index) => Items is { } items && index >= 0 && index < items.Count ? items[index] : null;

    /// <summary>Tells a screen reader the keyboard's slice changed (called by SetHover).</summary>
    private void AnnounceHover(int previous, int current) => _peer?.SelectionChanged(previous, current);

    /// <summary>A ring around the donut while the keyboard has the focus on it.</summary>
    private void DrawFocusRing(DrawingContext context)
    {
        if (!IsFocused || !PseudoClasses.Contains(":focus-visible") || _progressMode)
            return;
        var pen = new Pen(AccentBrush ?? Foreground, IsHighContrast ? 3 : 2);
        var radius = OuterRadius + 4;
        context.DrawEllipse(null, pen, Center, radius, radius);
    }
}

/// <summary>The donut for screen readers: a list of slices, the keyboard's slice selected.</summary>
internal sealed class DonutMenuAutomationPeer(DonutMenu owner) : ControlAutomationPeer(owner), ISelectionProvider
{
    private readonly List<DonutSliceAutomationPeer> _slices = [];

    public DonutMenu Menu { get; } = owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string? GetNameCore() =>
        base.GetNameCore() is { Length: > 0 } name ? name : Menu.CenterTitle;

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore()
    {
        var count = Menu.Items?.Count ?? 0;
        while (_slices.Count < count)
            _slices.Add(new DonutSliceAutomationPeer(this, _slices.Count));
        if (_slices.Count > count)
            _slices.RemoveRange(count, _slices.Count - count);
        return _slices;
    }

    bool ISelectionProvider.CanSelectMultiple => false;
    bool ISelectionProvider.IsSelectionRequired => false;

    IReadOnlyList<AutomationPeer> ISelectionProvider.GetSelection() =>
        Slice(Menu.ActiveIndex) is { } slice ? [slice] : [];

    /// <summary>Raises the selection change for the slices that lost and got it.</summary>
    internal void SelectionChanged(int previous, int current)
    {
        GetChildrenCore();
        Slice(previous)?.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, true, false);
        Slice(current)?.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, false, true);
        RaisePropertyChangedEvent(SelectionPatternIdentifiers.SelectionProperty, null, null);
    }

    private DonutSliceAutomationPeer? Slice(int index) => index >= 0 && index < _slices.Count ? _slices[index] : null;
}

/// <summary>One slice for screen readers: its label, why it's disabled, select and invoke.</summary>
internal sealed class DonutSliceAutomationPeer(DonutMenuAutomationPeer parent, int index)
    : AutomationPeer, IInvokeProvider, ISelectionItemProvider
{
    private DonutMenu Menu => parent.Menu;
    private DonutItem? Item => Menu.ItemAt(index);

    protected override void BringIntoViewCore()
    {
    }

    protected override string? GetAcceleratorKeyCore() => null;
    protected override string? GetAccessKeyCore() => null;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
    protected override string? GetAutomationIdCore() => Item?.Id;

    protected override Rect GetBoundingRectangleCore()
    {
        var box = Menu.SliceBounds(index);
        return TopLevel.GetTopLevel(Menu) is { } root && Menu.TransformToVisual(root) is { } transform
            ? box.TransformToAABB(transform)
            : box;
    }

    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];
    protected override string GetClassNameCore() => "DonutSlice";
    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override string? GetNameCore() => Item is { } item
        ? item.Caption is { Length: > 0 } caption && caption != item.Label ? $"{item.Label}, {caption}" : item.Label
        : null;

    protected override string? GetHelpTextCore() => Item is { IsEnabled: false } item ? item.DisabledReason : null;
    protected override AutomationPeer? GetParentCore() => parent;
    protected override bool HasKeyboardFocusCore() => Menu.IsFocused && Menu.ActiveIndex == index;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => Item?.IsEnabled ?? false;
    protected override bool IsKeyboardFocusableCore() => Menu.Focusable;

    protected override void SetFocusCore()
    {
        Menu.Focus(NavigationMethod.Tab);
        Menu.SelectFromAutomation(index);
    }

    protected override bool ShowContextMenuCore()
    {
        if (!Menu.IsEditMode)
            return false;
        Menu.InvokeFromAutomation(index);
        return true;
    }

    protected override bool TrySetParent(AutomationPeer? newParent) => ReferenceEquals(newParent, parent);

    void IInvokeProvider.Invoke() => Menu.InvokeFromAutomation(index);

    bool ISelectionItemProvider.IsSelected => Menu.ActiveIndex == index;
    ISelectionProvider? ISelectionItemProvider.SelectionContainer => parent;
    void ISelectionItemProvider.Select() => Menu.SelectFromAutomation(index);
    void ISelectionItemProvider.AddToSelection() => Menu.SelectFromAutomation(index);

    void ISelectionItemProvider.RemoveFromSelection()
    {
    }
}
