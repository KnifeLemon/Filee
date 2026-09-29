// Main (settings) window: left navigation + current page.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.ViewModels;

/// <summary>Navigation entry. <see cref="Title"/> follows the UI language.</summary>
public sealed partial class NavItem(string key, string titleKey, string iconKey, Type pageType) : ObservableObject
{
    public string Key { get; } = key;
    public string TitleKey { get; } = titleKey;
    public Geometry? Icon { get; } = Application.Current?.FindResource(iconKey) as Geometry;
    public Type PageType { get; } = pageType;

    [ObservableProperty] private string _title = "";

    public void Refresh(ILocalizer loc) => Title = loc[TitleKey];
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _services;

    public MainWindowViewModel(IServiceProvider services, ILocalizer loc)
    {
        _services = services;
        Items =
        [
            new("home", "nav.home", "Icon.Home", typeof(HomePageViewModel)),
            new("general", "nav.general", "Icon.Settings", typeof(GeneralPageViewModel)),
            new("triggers", "nav.triggers", "Icon.Keyboard", typeof(TriggersPageViewModel)),
            new("toolbar", "nav.toolbar", "Icon.Donut", typeof(ToolbarPageViewModel)),
            new("presets", "nav.presets", "Icon.Tune", typeof(PresetsPageViewModel)),
            new("theme", "nav.theme", "Icon.Palette", typeof(ThemePageViewModel)),
            new("engines", "nav.engines", "Icon.Engine", typeof(EnginesPageViewModel)),
            new("about", "nav.about", "Icon.Info", typeof(AboutPageViewModel)),
        ];
        foreach (var item in Items)
            item.Refresh(loc);
        loc.LanguageChanged += (_, _) =>
        {
            foreach (var item in Items)
                item.Refresh(loc);
            CurrentPage = CreatePage(SelectedItem); // rebuild so code-generated labels update too
        };
        _selectedItem = Items[0];
        _currentPage = CreatePage(Items[0]);
    }

    public IReadOnlyList<NavItem> Items { get; }

    [ObservableProperty] private NavItem _selectedItem;
    [ObservableProperty] private ObservableObject _currentPage;

    public void Navigate(string key)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (item is not null)
            SelectedItem = item;
    }

    // Pages are recreated on every visit so they always reflect the latest settings.
    partial void OnSelectedItemChanged(NavItem value) => CurrentPage = CreatePage(value);

    // Old pages may hold event subscriptions on long-lived services.
    partial void OnCurrentPageChanged(ObservableObject? oldValue, ObservableObject newValue) => (oldValue as IDisposable)?.Dispose();

    private ObservableObject CreatePage(NavItem item) =>
        (ObservableObject)ActivatorUtilities.CreateInstance(_services, item.PageType);
}
