// A document the Filee renderer converted without all of it: the toast's note under the card says what was left out or
// drawn another way, each kind once with its count, and how to get a faithful copy (LibreOffice) where one exists.

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class LeftOutToastTests
{
    private static LeftOutPart Part(string kind, int count = 0) => new($"notice.renderer_left_out.{kind}", count);

    /// <summary>A Word file with OLE drawings (as in #356) and an HWPX with a chart and vertical text, both converted.</summary>
    private static ConversionJob Job()
    {
        var conversion = new ConversionJob(["C:/drawings.docx", "C:/report.hwpx", "C:/plain.docx"], new Preset { TargetFormat = "pdf" }, "PDF");
        foreach (var file in conversion.Files)
            (file.State, file.Progress) = (FileState.Done, 1);
        conversion.Files[0].LeftOut = new LeftOutNotice("notice.renderer_left_out_libreoffice", [Part("objects_pictured", 3), Part("form_controls", 1)]);
        conversion.Files[1].LeftOut = new LeftOutNotice("notice.renderer_left_out", [Part("charts", 1), Part("vertical_text")]);
        return conversion;
    }

    [AvaloniaFact]
    public void The_note_names_what_was_left_out_of_each_document()
    {
        TestServices.EnsureInitialized("en");
        var job = new JobViewModel(Job(), AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });

        Assert.True(job.HasNotices);
        Assert.Equal(
            "drawings.docx — The Filee renderer couldn't draw all of this document: 3 embedded objects drawn from their saved pictures, " +
            "1 form control left out. Install LibreOffice in Settings → Engines for a faithful copy" + Environment.NewLine +
            "report.hwpx — The Filee renderer couldn't draw all of this document: 1 chart left out, vertical text set horizontally",
            job.Notices);
        Assert.False(job.HasErrors);
    }

    [AvaloniaFact]
    public void More_kinds_than_the_note_names_end_with_and_more()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var notice = new LeftOutNotice("notice.renderer_left_out",
            [Part("charts", 2), Part("smartart", 1), Part("objects", 1), Part("videos", 1), Part("vertical_text")]);

        Assert.Equal("The Filee renderer couldn't draw all of this document: 2 charts left out, 1 SmartArt graphic left out, " +
                     "1 embedded object left out, 1 video left out, and more", loc.LeftOut(notice));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void The_note_is_drawn_under_the_card(string language)
    {
        TestServices.EnsureInitialized(language);
        var service = AppHost.Get<ConversionService>();
        service.ToastJobs.Clear();
        var toast = new ToastWindow { DataContext = service };
        var conversion = Job();
        // Finished, as the queue leaves it (the state is the queue's to set).
        typeof(ConversionJob).GetProperty(nameof(ConversionJob.State))!.SetValue(conversion, JobState.Completed);
        var job = new JobViewModel(conversion, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });
        try
        {
            service.Jobs.Insert(0, job);
            service.ShowInToast(job);
            // Long enough for the card's fade-in to end.
            for (var i = 0; i < 120; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            Assert.True(toast.IsVisible);
            RenderTests.Save(toast, $"toast-left-out-{language}.png");
        }
        finally
        {
            service.ToastJobs.Clear();
            service.Jobs.Remove(job);
            toast.Close();
            TestServices.EnsureInitialized("en");
        }
    }
}
