// "Export for the gallery" (#1530). Escape closes it.

using Avalonia.Controls;
using Avalonia.Input;
using Filee.App.Services;

namespace Filee.App.Views;

public partial class GalleryExportWindow : Window
{
    public GalleryExportWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
