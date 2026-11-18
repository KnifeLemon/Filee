// The preset gallery's window (#1530). Escape closes it; the search box has the keys when it opens, Tab goes on to the
// filters and the list.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Views;

public partial class PresetGalleryWindow : Window
{
    public PresetGalleryWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
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
