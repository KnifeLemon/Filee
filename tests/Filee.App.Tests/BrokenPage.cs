// A page whose view throws while it is built, for PageErrorTests. The namespaces mirror the app's, so ViewLocator's
// naming convention finds the view from the view model.

using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Filee.App.Tests.BrokenPages.ViewModels
{
    public sealed class BrokenPageViewModel : ObservableObject;
}

namespace Filee.App.Tests.BrokenPages.Views
{
    public sealed class BrokenPage : UserControl
    {
        // As a resource missing from a theme would throw from InitializeComponent.
        public BrokenPage() => throw new KeyNotFoundException("Static resource 'Missing' not found.");
    }
}
