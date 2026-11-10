// A settings page that throws while it is built goes to the log with the page and the stack trace, and the window
// shows a short note in its place instead of keeping the previous page on screen (#1327).

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Filee.App.Tests;

public class PageErrorTests
{
    private sealed class ExceptionLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception), exception));
    }

    private static MainWindowViewModel Create(ExceptionLogger log, Func<Type, ObservableObject> createPage) =>
        new(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>(), log, createPage);

    private static ObservableObject Build(Type type) => (ObservableObject)ActivatorUtilities.CreateInstance(AppHost.Services, type);

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void A_page_that_throws_is_logged_and_shows_a_note_in_its_place(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var log = new ExceptionLogger();
        using var vm = Create(log, type => type == typeof(ThemePageViewModel)
            ? throw new InvalidOperationException("Sequence contains no matching element")
            : Build(type));
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        RenderTests.Pump();

        // Picked in the navigation, as the user does: the binding that writes the selection must not swallow it.
        var nav = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Classes.Contains("nav"));
        nav.SelectedItem = vm.Items.Single(i => i.Key == "theme");
        RenderTests.Pump();

        var error = Assert.IsType<ErrorPageViewModel>(vm.CurrentPage);
        Assert.Equal(loc["nav.theme"], error.Title);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("Could not open the theme page", line.Message);
        Assert.Contains("Sequence contains no matching element", line.Exception!.ToString());
        Assert.Contains(nameof(A_page_that_throws_is_logged_and_shows_a_note_in_its_place), line.Exception.StackTrace);

        var page = window.GetVisualDescendants().OfType<ErrorPage>().Single();
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == loc["nav.theme"] && t.IsEffectivelyVisible);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == loc["nav.page_error"] && t.IsEffectivelyVisible);
        RenderTests.Save(window, $"page-error-{language}.png");

        // The other pages still open.
        vm.Navigate("about");
        RenderTests.Pump();
        Assert.IsType<AboutPageViewModel>(vm.CurrentPage);
        Assert.Single(window.GetVisualDescendants().OfType<AboutPage>());
        window.Close();
    }

    [AvaloniaFact]
    public void A_view_that_throws_is_logged_and_replaced_by_the_note()
    {
        TestServices.EnsureInitialized("en");
        var log = new ExceptionLogger();

        var control = new ViewLocator { Log = log }.Build(new BrokenPages.ViewModels.BrokenPageViewModel());

        var page = Assert.IsType<ErrorPage>(control);
        Assert.False(Assert.IsType<ErrorPageViewModel>(page.DataContext).HasTitle);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("Could not build the BrokenPage view", line.Message);
        // The view's own exception, not the reflection wrapper around it.
        Assert.IsType<KeyNotFoundException>(line.Exception);
        Assert.Contains("Views.BrokenPage..ctor", line.Exception!.StackTrace);
    }

    [AvaloniaFact]
    public void A_view_that_throws_inside_the_window_leaves_the_note_on_screen()
    {
        TestServices.EnsureInitialized("en");
        using var vm = Create(new ExceptionLogger(), type => type == typeof(ThemePageViewModel)
            ? new BrokenPages.ViewModels.BrokenPageViewModel()
            : Build(type));
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        RenderTests.Pump();

        vm.Navigate("theme");
        RenderTests.Pump();

        Assert.IsType<BrokenPages.ViewModels.BrokenPageViewModel>(vm.CurrentPage);
        Assert.Single(window.GetVisualDescendants().OfType<ErrorPage>());
        Assert.Empty(window.GetVisualDescendants().OfType<HomePage>());
        window.Close();
    }
}
