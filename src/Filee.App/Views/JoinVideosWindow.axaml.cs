using Avalonia.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class JoinVideosWindow : Window
{
    public JoinVideosWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is JoinVideosViewModel vm)
                vm.CloseRequested += Close;
        };
    }
}
