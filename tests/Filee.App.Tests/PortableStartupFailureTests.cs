// The window and stderr line of a portable copy whose data folder can't be written (#1590): drawn in the UI language's
// fonts, and the cause written where a packager or bug report sees it.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Filee.App.Services;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class PortableStartupFailureTests
{
    [AvaloniaTheory]
    [InlineData("zh-CN", FlowDirection.LeftToRight)]
    [InlineData("ar", FlowDirection.RightToLeft)]
    [InlineData("en", FlowDirection.LeftToRight)]
    public void The_window_uses_the_ui_fonts_and_direction_of_the_language(string language, FlowDirection direction)
    {
        var window = PortableStartupFailure.CreateWindow("Filee 无法写入便携数据文件夹", language);

        Assert.Equal(SoftPalette.FontFamilyFor(language), window.FontFamily);
        Assert.Equal(direction, window.FlowDirection);
        Assert.Contains(((StackPanel)window.Content!).Children.OfType<TextBlock>(), t => t.Text == "Filee 无法写入便携数据文件夹");
    }

    [Fact]
    public void Stderr_names_the_folder_the_reason_and_the_marker()
    {
        var error = new PortableStorageException("/opt/filee/data", new UnauthorizedAccessException("Access to the path '/opt/filee/data' is denied."));

        var line = PortableStartupFailure.Cause(error);

        Assert.Contains("/opt/filee/data", line);
        Assert.Contains("is denied", line);
        Assert.Contains(PortableMode.MarkerName, line);
    }
}
