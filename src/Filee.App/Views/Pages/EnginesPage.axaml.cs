// Engines page: optional downloads (install, use your own copy, LibreOffice settings) and the priority order, changed
// by dragging a card or with its arrows.
//
// Dragging: the list captures the pointer (cards are re-created as they move), the card moves as soon as the pointer
// passes the middle of a neighbour, and the order is saved when the button is released. Near the top or bottom edge
// of the page the page scrolls by itself.
//
// Animation: the dragged card stays in the list as an empty outlined slot while a smaller copy of it (a picture taken
// when the drag starts) follows the pointer. The other cards slide to their new places, and on release the copy
// settles into the slot. It is all driven by one timer (like Controls/Spin.cs) and plain transforms, and only the
// transform changes: the layout positions (Bounds) are used to find the card under the pointer. With "Reduce
// animations" on, everything happens at once.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class EnginesPage : UserControl
{
    private const double DragThreshold = 5;
    private const double ScrollEdge = 48;
    private const double ScrollStep = 14;
    private const double GhostScale = 0.88;
    private const double SlideMs = 200;
    private const double SettleMs = 180;
    private const double PickupMs = 130;

    private sealed class EngineDrag(EngineItemViewModel item, Point start, Control card, Point grab)
    {
        public EngineItemViewModel Item { get; } = item;
        public Point Start { get; } = start;
        public Control Card { get; } = card;

        /// <summary>Where on the card the pointer took hold of it.</summary>
        public Point Grab { get; } = grab;
        public bool Active { get; set; }

        /// <summary>The pointer in the page's scroll viewer, for scrolling while it rests near an edge.</summary>
        public Point InScroller { get; set; }

        public Border? Ghost { get; set; }

        /// <summary>The pointer in the list, and where the copy was put for it.</summary>
        public Point Pointer { get; set; }
        public Point GhostAt { get; set; }
    }

    /// <summary>A card on its way to its place: it starts <see cref="From"/> pixels away and eases back to zero.</summary>
    private sealed class Slide(double from, long start)
    {
        public double From { get; } = from;
        public long Start { get; } = start;
    }

    /// <summary>The pointer over the list while a card is being dragged (the drag handle has the same one).</summary>
    private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);

    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _motionTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<Control, Slide> _slides = [];
    private EngineDrag? _drag;

    // The drag that has been released and is still settling: the copy flies to its slot.
    private EngineDrag? _settling;
    private Point _settleFrom;
    private Point _settleTo;
    private long _settleStart;
    private double _settleScale = GhostScale;

    // The copy shrinking when it is picked up.
    private long _pickupStart = -1;

    public EnginesPage()
    {
        InitializeComponent();
        EngineList.AddHandler(PointerMovedEvent, OnListPointerMoved, handledEventsToo: true);
        EngineList.AddHandler(PointerReleasedEvent, (_, e) => EndDrag(e.Pointer), handledEventsToo: true);
        EngineList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(null), handledEventsToo: true);
        _scrollTimer.Tick += (_, _) => AutoScroll();
        _motionTimer.Tick += (_, _) => Step();
        DetachedFromVisualTree += (_, _) =>
        {
            _motionTimer.Stop();
            _scrollTimer.Stop();
            FinishSettle(); // nothing half-done if the page goes away while the copy settles
        };
    }

    private EnginesPageViewModel? Vm => DataContext as EnginesPageViewModel;

    private ScrollViewer? Scroller => this.FindAncestorOfType<ScrollViewer>();

    /// <summary>Pressing a card (not its arrows, which handle the press) may start dragging it.</summary>
    private void OnEnginePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EngineItemViewModel item } card || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        // The copy is placed in the drag layer (always left to right), so where the pointer took hold of the card is taken there too:
        // in a right-to-left language the card's own x runs the other way. The cards fill the list, so the layer's x is the card's.
        var grab = new Point(e.GetPosition(DragLayer).X, e.GetPosition(card).Y);
        _drag = new EngineDrag(item, e.GetPosition(EngineList), card, grab);
        e.Pointer.Capture(EngineList);
        e.Handled = true;
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } drag)
            return;
        var p = e.GetPosition(EngineList);
        if (!drag.Active)
        {
            if (Math.Abs(p.Y - drag.Start.Y) < DragThreshold && Math.Abs(p.X - drag.Start.X) < DragThreshold)
                return;
            drag.Active = true;
            FinishSettle();
            drag.Ghost = CreateGhost(drag); // before the card turns into the empty slot
            drag.Item.IsDragging = true;
            EngineList.Cursor = DragCursor;
            _scrollTimer.Start();
        }
        if (Scroller is { } scroller)
            drag.InScroller = e.GetPosition(scroller);
        MoveGhost(drag, new Point(e.GetPosition(DragLayer).X, p.Y));
        Follow(p.Y);
    }

    /// <summary>A picture of the pressed card, with the accent outline, on the layer above the list. It starts full size and shrinks.</summary>
    private Border? CreateGhost(EngineDrag drag)
    {
        RenderTargetBitmap? bitmap = null;
        try
        {
            var size = drag.Card.Bounds.Size;
            var ghost = new Border
            {
                Width = size.Width,
                Height = size.Height,
                CornerRadius = (drag.Card as Border)?.CornerRadius ?? default,
                BorderThickness = new Thickness(1.5),
                BorderBrush = Resource("AccentBrush"),
                BoxShadow = this.TryFindResource("CardShadow", ActualThemeVariant, out var shadow) && shadow is BoxShadows shadows ? shadows : default,
                ClipToBounds = true,
                RenderTransformOrigin = RelativePoint.TopLeft,
                RenderTransform = new ScaleTransform(1, 1),
            };

            // At the screen's scale (125% displays, the app's zoom), or the picture is blurry.
            var scaling = (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) * UiZoom.Scale;
            bitmap = new RenderTargetBitmap(
                new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scaling)), Math.Max(1, (int)Math.Ceiling(size.Height * scaling))),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(drag.Card);
            ghost.Child = new Image { Source = bitmap, Stretch = Stretch.Fill };
            DragLayer.Children.Add(ghost);
            if (Motion.Enabled)
            {
                _pickupStart = _clock.ElapsedMilliseconds;
                _motionTimer.Start();
            }
            else
            {
                SetScale(ghost, GhostScale);
            }
            return ghost;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bitmap?.Dispose();
            return null; // no copy: the outlined slot alone still shows what is being moved
        }
    }

    private IBrush? Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private static void SetScale(Border ghost, double scale)
    {
        if (ghost.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
    }

    private static double Scale(Border ghost) => ghost.RenderTransform is ScaleTransform transform ? transform.ScaleX : 1;

    private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>Puts the copy where the pointer holds it: the pointer stays on the same spot of the (smaller) card.</summary>
    private static void MoveGhost(EngineDrag drag, Point inList)
    {
        if (drag.Ghost is not { } ghost)
            return;
        // The grab point shrinks with the card, so the pointer stays on the same spot of it.
        drag.Pointer = inList;
        var scale = Scale(ghost);
        drag.GhostAt = new Point(inList.X - drag.Grab.X * scale, inList.Y - drag.Grab.Y * scale);
        Canvas.SetLeft(ghost, drag.GhostAt.X);
        Canvas.SetTop(ghost, drag.GhostAt.Y);
    }

    /// <summary>One tick of every running animation: the pickup, the cards sliding, the copy settling.</summary>
    private void Step()
    {
        var now = _clock.ElapsedMilliseconds;
        var busy = false;

        if (_pickupStart >= 0)
        {
            var t = (now - _pickupStart) / PickupMs;
            var drag = _drag ?? _settling;
            if (drag?.Ghost is { } ghost && _settling is null)
            {
                SetScale(ghost, 1 + (GhostScale - 1) * Ease(t));
                if (_drag is { Active: true } active)
                    MoveGhost(active, active.Pointer);
            }
            if (t >= 1)
                _pickupStart = -1;
            else
                busy = true;
        }

        if (_slides.Count > 0)
        {
            foreach (var (card, slide) in _slides.ToList())
            {
                var t = (now - slide.Start) / SlideMs;
                if (t >= 1)
                {
                    card.RenderTransform = null;
                    _slides.Remove(card);
                }
                else
                {
                    card.RenderTransform = new TranslateTransform(0, slide.From * (1 - Ease(t)));
                    busy = true;
                }
            }
        }

        if (_settling is { } settling)
        {
            var t = (now - _settleStart) / SettleMs;
            if (settling.Ghost is { } ghost)
            {
                var e = Ease(t);
                SetScale(ghost, _settleScale + (1 - _settleScale) * e);
                Canvas.SetLeft(ghost, _settleFrom.X + (_settleTo.X - _settleFrom.X) * e);
                Canvas.SetTop(ghost, _settleFrom.Y + (_settleTo.Y - _settleFrom.Y) * e);
            }
            if (t >= 1)
                FinishSettle();
            else
                busy = true;
        }

        if (!busy)
            _motionTimer.Stop();
    }

    /// <summary>Where a card is (relative to the list) as it is laid out, without any slide in progress.</summary>
    private static double Top(Control card) => card.Bounds.Y;

    /// <summary>Where a card looks to be right now, a slide in progress included.</summary>
    private double Seen(Control card) => Top(card) + (_slides.TryGetValue(card, out var s) ? s.From * (1 - Ease((_clock.ElapsedMilliseconds - s.Start) / SlideMs)) : 0);

    /// <summary>Moves the dragged card to where <paramref name="y"/> (in the list) is: after every other card whose middle is above it.</summary>
    private void Follow(double y)
    {
        if (_drag is not { Active: true } drag || Vm is not { } vm)
            return;
        var index = 0;
        for (var i = 0; i < vm.Engines.Count; i++)
        {
            if (vm.Engines[i] == drag.Item || EngineList.ContainerFromIndex(i) is not { } card)
                continue;
            if (Top(card) + card.Bounds.Height / 2 < y)
                index++;
        }
        if (vm.Engines.IndexOf(drag.Item) == index)
            return;

        // Where every card looks to be now, so the ones that change place can slide from there.
        var before = new Dictionary<object, double>();
        for (var i = 0; i < vm.Engines.Count; i++)
        {
            if (EngineList.ContainerFromIndex(i) is { DataContext: { } data } card)
                before[data] = Seen(card);
        }

        vm.MoveTo(drag.Item, index);

        if (!Motion.Enabled)
            return;
        EngineList.UpdateLayout();
        var now = _clock.ElapsedMilliseconds;
        foreach (var old in _slides.Keys) // containers are re-created as they move: slides of old ones are over
            old.RenderTransform = null;
        _slides.Clear();
        for (var i = 0; i < vm.Engines.Count; i++)
        {
            if (EngineList.ContainerFromIndex(i) is not { DataContext: { } data } card || !before.TryGetValue(data, out var was))
                continue;
            var offset = was - Top(card);
            if (Math.Abs(offset) < 0.5)
                continue;
            _slides[card] = new Slide(offset, now);
            card.RenderTransform = new TranslateTransform(0, offset);
        }
        if (_slides.Count > 0)
            _motionTimer.Start();
    }

    private void AutoScroll()
    {
        if (_drag is not { Active: true } drag || Scroller is not { } scroller)
            return;
        var y = drag.InScroller.Y;
        var step = y < ScrollEdge ? -ScrollStep : y > scroller.Viewport.Height - ScrollEdge ? ScrollStep : 0;
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var offset = Math.Clamp(scroller.Offset.Y + step, 0, max);
        if (step == 0 || offset == scroller.Offset.Y)
            return;
        scroller.Offset = scroller.Offset.WithY(offset);
        if (scroller.TranslatePoint(drag.InScroller, EngineList) is { } inList)
        {
            MoveGhost(drag, new Point(drag.Pointer.X, inList.Y)); // x does not change while the page scrolls
            Follow(inList.Y);
        }
    }

    private void EndDrag(IPointer? pointer)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        _scrollTimer.Stop();
        pointer?.Capture(null);
        EngineList.Cursor = null;
        if (!drag.Active)
            return;
        Vm?.SavePriority();
        Settle(drag);
    }

    /// <summary>The copy flies into the empty slot and grows to full size, then the real card takes over.</summary>
    private void Settle(EngineDrag drag)
    {
        var index = Vm?.Engines.IndexOf(drag.Item) ?? -1;
        if (drag.Ghost is { } ghost && Motion.Enabled && index >= 0 && EngineList.ContainerFromIndex(index) is { } slot)
        {
            _pickupStart = -1;
            _settling = drag;
            _settleFrom = drag.GhostAt;
            _settleScale = Scale(ghost); // from the size it has now: a quick release during the pickup does not jump
            _settleTo = new Point(slot.Bounds.X, Top(slot));
            _settleStart = _clock.ElapsedMilliseconds;
            _motionTimer.Start();
            return;
        }
        _settling = drag;
        FinishSettle();
    }

    /// <summary>Ends a settle that is still running (its time is up, or a new drag starts): the card is a card again.</summary>
    private void FinishSettle()
    {
        if (_settling is not { } drag)
            return;
        _settling = null;
        drag.Item.IsDragging = false;
        if (drag.Ghost is { } ghost)
        {
            DragLayer.Children.Remove(ghost);
            if ((ghost.Child as Image)?.Source is IDisposable bitmap)
                bitmap.Dispose();
        }
    }

    /// <summary>The LibreOffice switches, in their own window.</summary>
    private async void OnPackageSettings(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
            return;

        if (DataContext is EnginesPageViewModel vm)
            await new LibreOfficeOptionsWindow { DataContext = vm }.ShowDialog(window);
    }

    private async void OnManageOllama(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
            return;
        var dlg = new OllamaModelManagerWindow();
        await dlg.ShowDialog(window);
        await Task.Run(Filee.Engines.Translation.TranslatorConverter.PerformCheck);
        var catalog = AppHost.Get<Filee.Core.Conversion.ConverterCatalog>();
        catalog.Refresh();
        _ = catalog.CheckInBackgroundAsync();
    }

    /// <summary>Picks the program of a copy the user already has (ffmpeg.exe, ebook-convert.exe, …) and uses it.</summary>
    private async void OnUseOwnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EnginePackageState package } || DataContext is not EnginesPageViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window window)
            return;
        var loc = AppHost.Get<ILocalizer>();
        var programs = Filee.Engines.Infrastructure.EngineEnvironment.OwnCopyPrograms[package.Package.Id];
        string? path;
        if (OperatingSystem.IsMacOS() && package.Package.Id is "libreoffice" or "calibre")
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = loc.Format("engines.use_own_app_folder", package.Name),
                AllowMultiple = false,
            });
            path = folders.FirstOrDefault()?.TryGetLocalPath();
        }
        else
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = loc.Format("engines.use_own_title", package.Name),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(programs[0]) { Patterns = [programs[0]] }, FilePickerFileTypes.All],
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }
        if (path is null)
            return;
        if (!vm.UseOwnCopy(package, path))
            await AppHost.Get<WindowService>().MessageAsync(window, loc.Format("engines.use_own_missing", string.Join(", ", programs)));
    }

    private void OnStopOwnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EnginePackageState package } && DataContext is EnginesPageViewModel vm)
            vm.StopUsingOwnCopy(package);
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EnginePackageState package } || DataContext is not EnginesPageViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window window)
            return;
        var loc = AppHost.Get<ILocalizer>();
        if (!await AppHost.Get<WindowService>().ConfirmAsync(window, loc.Format("engines.remove_confirm", package.Name), destructive: true))
            return;
        if (vm.Remove(package) is { } reason)
            await AppHost.Get<WindowService>().MessageAsync(window, loc[reason]);
    }
}
