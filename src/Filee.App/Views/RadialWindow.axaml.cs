// Thin view for the live donut: forwards drag & drop and pointer events to RadialController.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Filee.App.Controls;
using Filee.App.Services;

namespace Filee.App.Views;

public partial class RadialWindow : Window
{
    public RadialWindow()
    {
        InitializeComponent();
        Motion.Track(this);

        DragDrop.AddDragEnterHandler(Root, OnDragEnter);
        DragDrop.AddDragOverHandler(Root, OnDragOver);
        DragDrop.AddDragLeaveHandler(Root, OnDragLeave);
        DragDrop.AddDropHandler(Root, OnDrop);
    }

    public DonutMenu DonutControl => Donut;

    /// <summary>Files entered the window during a drag.</summary>
    public event Action<IReadOnlyList<string>>? FilesEntered;

    /// <summary>A drop landed on the donut: (hit, files).</summary>
    public event Action<DonutHit, IReadOnlyList<string>>? Dropped;

    public event Action? DragLeft;

    /// <summary>
    /// After a drag ends (drop or leave): how many drag-over events came in, where the last one was and how far that
    /// slice was drawn lit up (0 to 1).
    /// </summary>
    public event Action<int, DonutHit, double>? DragSummary;

    private int _dragOverEvents;
    private DonutHit _lastDragHit;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var files = LocalFiles(e);
        if (files.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        _dragOverEvents = 0;
        FilesEntered?.Invoke(files);
        OnDragOver(sender, e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hit = Donut.HitTest(e.GetPosition(Donut));
        _dragOverEvents++;
        _lastDragHit = hit;
        var files = e.DataTransfer.Contains(DataFormat.File);
        Donut.SetExternalHighlight(hit.Kind == DonutHitKind.Segment ? hit.Index : -1, hit.Kind == DonutHitKind.Center);

        // Always COPY. Reporting MOVE would make Explorer delete the source files after the drop.
        e.DragEffects = files && hit.Kind == DonutHitKind.Segment ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DragSummary?.Invoke(_dragOverEvents, _lastDragHit, Donut.LitAmount(_lastDragHit.Index));
        Donut.SetExternalHighlight(-1);
        DragLeft?.Invoke();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var hit = Donut.HitTest(e.GetPosition(Donut));
        DragSummary?.Invoke(_dragOverEvents, hit, Donut.LitAmount(hit.Index));
        Donut.SetExternalHighlight(-1);
        e.DragEffects = hit.Kind == DonutHitKind.Segment ? DragDropEffects.Copy : DragDropEffects.None;
        Dropped?.Invoke(hit, LocalFiles(e));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke();
            e.Handled = true;
        }
    }

    public event Action? EscapePressed;

    /// <summary>Local file paths in the drag data (folders and virtual items are ignored).</summary>
    private static IReadOnlyList<string> LocalFiles(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .Where(File.Exists)
            .ToList();
}
