// Settings → General → Network (#879): the proxy box saves an address or nothing, never what isn't one.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class ProxyPageTests
{
    [AvaloniaFact]
    public void The_proxy_box_saves_addresses_and_keeps_the_last_one_while_the_text_isnt_one()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.Proxy;
        using var page = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(),
            NullLogger<GeneralPageViewModel>.Instance);
        try
        {
            page.Proxy = " http://127.0.0.1:7890 ";
            Assert.False(page.ProxyInvalid);
            Assert.Equal("http://127.0.0.1:7890", store.Settings.Proxy);

            page.Proxy = "http://127.0.0.1:7890/files/setup.zip";
            Assert.True(page.ProxyInvalid);
            Assert.Equal("http://127.0.0.1:7890", store.Settings.Proxy);

            page.Proxy = "";
            Assert.False(page.ProxyInvalid);
            Assert.Null(store.Settings.Proxy);
        }
        finally
        {
            store.Settings.Proxy = before;
        }
    }
}
