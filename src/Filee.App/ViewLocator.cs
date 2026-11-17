// Resolves a view for a view model by naming convention:
//   Filee.App.ViewModels.Pages.HomePageViewModel  →  Filee.App.Views.Pages.HomePage

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Microsoft.Extensions.Logging;

namespace Filee.App;

public sealed class ViewLocator : IDataTemplate
{
    /// <summary>Where a view that throws while it is built is reported; Filee's log unless a test sets one.</summary>
    internal ILogger? Log { get; init; }

    public Control? Build(object? data)
    {
        if (data is null)
            return null;
        var name = data.GetType().FullName!
            .Replace(".ViewModels.", ".Views.", StringComparison.Ordinal)
            .Replace("ViewModel", "", StringComparison.Ordinal);
        var type = data.GetType().Assembly.GetType(name);
        if (type is null)
            return new TextBlock { Text = "View not found: " + name };
        try
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            // Thrown from inside the content binding, this would be swallowed and leave the previous page on screen
            // with nothing in the log (#1327).
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            (Log ?? Program.Log).LogError(cause, "Could not build the {View} view", type.Name);
            return new ErrorPage { DataContext = new ErrorPageViewModel(null) };
        }
    }

    public bool Match(object? data) => data is ObservableObject && data.GetType().Name.EndsWith("PageViewModel", StringComparison.Ordinal);
}
