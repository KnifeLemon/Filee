// Files dragged out of a web browser, as Avalonia hands them to Filee: a Chrome picture (a Windows virtual file with
// its address), Firefox's temporary copy, a Linux link. They reach the donut and the drop zone as placeholders named
// like the files, and a drop on a slice saves and converts them into the folder Settings › General names.

using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Settings;
using Filee.Core.Web;

namespace Filee.App.Tests;

public class BrowserDropTests
{
    /// <summary>A 1×1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>
    /// What Avalonia gives for a Windows virtual file: a storage file with no local path (with <c>local</c>, a file on
    /// disk). Avalonia's storage interfaces can't be implemented by hand, so a proxy answers for one.
    /// </summary>
    public class VirtualFile : DispatchProxy
    {
        private string _name = "";
        private byte[] _contents = [];
        private string? _local;

        public bool Disposed { get; private set; }

        public IStorageFile Item { get; private set; } = null!;

        public static VirtualFile Create(string name, byte[] contents, string? local = null)
        {
            var item = DispatchProxy.Create<IStorageFile, VirtualFile>();
            var file = (VirtualFile)(object)item;
            (file._name, file._contents, file._local, file.Item) = (name, contents, local, item);
            return file;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Name" => _name,
            "get_Path" => _local is null ? new Uri($"virtual-file:///{Uri.EscapeDataString(_name)}") : new Uri(_local),
            "get_CanBookmark" => false,
            "OpenReadAsync" => Task.FromResult<Stream>(new MemoryStream(_contents, writable: false)),
            "GetBasicPropertiesAsync" => Task.FromResult(new StorageItemProperties((ulong)_contents.Length)),
            "Dispose" => Dispose(),
            _ => throw new NotSupportedException(method?.Name),
        };

        private object? Dispose()
        {
            Disposed = true;
            return null;
        }
    }

    private static DataTransferItem Platform(string format, string text, bool utf16 = true) =>
        DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat(format), (utf16 ? Encoding.Unicode : Encoding.UTF8).GetBytes(text + "\0"));

    /// <summary>Chrome or Edge on Windows dragging a picture: its bytes as a virtual file, plus where it came from.</summary>
    private static DataTransfer ChromePicture(VirtualFile file)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(file.Item));
        data.Add(Platform(BrowserDragFormats.UniformResourceLocatorW, "https://example.com/gallery/cat.png"));
        data.Add(Platform(BrowserDragFormats.MozUrl, "https://example.com/gallery/cat.png\nA cat"));
        data.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat(BrowserDragFormats.FileGroupDescriptorW),
            BrowserDragFormats.BuildFileGroupDescriptor([new FileDescriptor(file.Item.Name, Png.Length, false)])));
        return data;
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void Browser_drop_on_a_back_face_folder_slice_copies_the_original_bytes()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var destination = Directory.CreateTempSubdirectory("filee-copy-drop-").FullName;
        var preset = new Filee.Core.Presets.Preset
        {
            Name = "Folder",
            Kind = Filee.Core.Presets.PresetKind.CopyToFolder,
            TargetFormat = Filee.Core.Presets.Preset.CopyTarget,
            Output = new() { Location = Filee.Core.Presets.OutputLocation.CustomFolder, CustomFolder = destination }
        };
        var profile = store.Profiles.Single(p => p.Id == "images");
        var before = profile.BackPresetIds;
        var radial = AppHost.Get<RadialController>();
        var motion = Motion.Enabled;
        try
        {
            store.Presets.Add(preset); profile.BackPresetIds = [preset.Id];
            Motion.Set(false);
            var file = VirtualFile.Create("cat.png", Png);
            using var data = ChromePicture(file);
            radial.ShowForDrag(500, 500);
            var window = radial.Window!;
            var root = window.FindControl<Panel>("Root")!;
            var donut = window.DonutControl;
            root.RaiseEvent(new DragEventArgs(DragDrop.DragEnterEvent, data, root, new Point(5, 5), KeyModifiers.Control));
            Pump(); donut.Flip(); Pump();
            var model = AppHost.Get<RadialViewModel>();
            Assert.True(model.IsBackFace);
            var (at, id) = FirstSlice(donut, model);
            Assert.Equal(preset.Id, id);
            root.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, root, donut.TranslatePoint(at, root)!.Value, KeyModifiers.Control));
            var job = WaitFor(AppHost.Get<ConversionService>().Jobs[0]);
            Assert.Equal(JobState.Completed, job.State);
            var output = Assert.Single(job.Outputs);
            Assert.Equal(Path.Combine(destination, "cat.png"), output);
            Assert.Equal(Png, File.ReadAllBytes(output));
            Assert.True(file.Disposed);
        }
        finally
        {
            radial.Close(animate: false); Motion.Set(motion);
            profile.BackPresetIds = before; store.Presets.Remove(preset);
            Directory.Delete(destination, recursive: true);
        }
    }

    private static ConversionJob WaitFor(JobViewModel vm)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (vm.Job.State is JobState.Queued or JobState.Running && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Pump();
        return vm.Job;
    }

    private static string UseFolder(UserDataStore store, bool saveOnlyConverted)
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-app-tests", "downloads-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        store.Settings.BrowserDrops.Folder = folder;
        store.Settings.BrowserDrops.SaveOnlyConverted = saveOnlyConverted;
        return folder;
    }

    private static void Reset(UserDataStore store, string folder)
    {
        store.Settings.BrowserDrops.Folder = "";
        store.Settings.BrowserDrops.SaveOnlyConverted = false;
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public void A_picture_dragged_from_Chrome_is_offered_as_a_file_named_like_it()
    {
        TestServices.EnsureInitialized("en");
        var file = VirtualFile.Create("cat.png", Png);
        using var data = ChromePicture(file);

        Assert.True(DraggedFiles.Offered(data));
        var dragged = DraggedFiles.Read(data, forDrop: true);

        Assert.NotNull(dragged.Web);
        Assert.Equal("cat.png", Path.GetFileName(Assert.Single(dragged.Files)));
        Assert.False(File.Exists(dragged.Files[0]));
        dragged.Web.Dispose();
        Assert.True(file.Disposed); // the source app learns the transfer is over
    }

    [AvaloniaFact]
    public void Links_to_pages_and_text_selections_bring_nothing_but_links_to_files_do()
    {
        TestServices.EnsureInitialized("en");

        using var page = new DataTransfer();
        page.Add(Platform(BrowserDragFormats.UniformResourceLocatorW, "https://example.com/news/today"));
        page.Add(DataTransferItem.CreateText("https://example.com/news/today"));
        Assert.Same(DraggedItems.None, DraggedFiles.Read(page, forDrop: true));

        using var selection = new DataTransfer();
        selection.Add(DataTransferItem.CreateText("Read the report at https://example.com/report.pdf"));
        selection.Add(Platform(BrowserDragFormats.WindowsHtml, "<p>Read the <a href=\"https://example.com/report.pdf\">report</a></p>", utf16: false));
        Assert.Same(DraggedItems.None, DraggedFiles.Read(selection, forDrop: true));

        // Firefox or Chrome on Linux: the link's address and title, in UTF-8.
        using var linux = new DataTransfer();
        linux.Add(Platform(BrowserDragFormats.NetscapeUrl, "https://example.com/files/report.pdf\nQ3 report", utf16: false));
        var dragged = DraggedFiles.Read(linux, forDrop: true);
        Assert.Equal("report.pdf", Path.GetFileName(Assert.Single(dragged.Files)));
        dragged.Web!.Dispose();
    }

    [AvaloniaFact]
    public void Firefox_temporary_copies_count_as_browser_files_but_files_on_disk_stay_local()
    {
        TestServices.EnsureInitialized("en");
        var temp = Path.Combine(Path.GetTempPath(), "filee-app-tests", "moz-" + Guid.NewGuid().ToString("N")[..6], "dog.png");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllBytes(temp, Png);
        try
        {
            using var firefox = new DataTransfer();
            firefox.Add(DataTransferItem.CreateFile(StorageItem(temp)));
            firefox.Add(Platform(BrowserDragFormats.MozUrl, "https://example.com/dog.png\nDog"));
            var dragged = DraggedFiles.Read(firefox, forDrop: true);
            Assert.NotNull(dragged.Web);
            Assert.Equal("dog.png", Path.GetFileName(Assert.Single(dragged.Files)));
            Assert.NotEqual(temp, dragged.Files[0]);
            dragged.Web.Dispose();

            // The same file dragged from the file manager (no address): an ordinary local file.
            using var explorer = new DataTransfer();
            explorer.Add(DataTransferItem.CreateFile(StorageItem(temp)));
            var local = DraggedFiles.Read(explorer, forDrop: true);
            Assert.Null(local.Web);
            Assert.Equal([temp], local.Files);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static IStorageItem StorageItem(string path) => VirtualFile.Create(Path.GetFileName(path), Png, path).Item;

    [AvaloniaFact]
    public void A_picture_dropped_on_a_donut_slice_is_saved_converted_and_only_the_converted_file_kept()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var folder = UseFolder(store, saveOnlyConverted: true);
        var radial = AppHost.Get<RadialController>();
        var conversions = AppHost.Get<ConversionService>();
        try
        {
            radial.ShowForDrag(500, 500);
            var window = radial.Window!;
            var root = window.FindControl<Panel>("Root")!;
            var donut = window.DonutControl;
            var file = VirtualFile.Create("cat.png", Png);
            using var data = ChromePicture(file);

            root.RaiseEvent(new DragEventArgs(DragDrop.DragEnterEvent, data, root, new Point(5, 5), KeyModifiers.Control));
            Pump();
            var model = AppHost.Get<RadialViewModel>();
            Assert.Equal("cat.png", Path.GetFileName(Assert.Single(model.Files)));
            Assert.True(WebDrop.IsPlaceholder(model.Files[0]));
            Assert.False(file.Disposed); // the transfer isn't ended while the drag goes on

            var (at, preset) = FirstSlice(donut, model);
            var dropFile = VirtualFile.Create("cat.png", Png);
            using var dropData = ChromePicture(dropFile);
            var before = conversions.Jobs.Count;
            root.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, dropData, root, donut.TranslatePoint(at, root)!.Value, KeyModifiers.Control));
            Assert.True(conversions.Jobs.Count > before, $"no conversion started for {preset}");

            var job = WaitFor(conversions.Jobs[0]);

            Assert.Equal(JobState.Completed, job.State);
            var output = Assert.Single(job.Outputs);
            Assert.Equal(folder, Path.GetDirectoryName(output));
            Assert.Equal([output], Directory.GetFiles(folder)); // the original wasn't kept
            Assert.False(File.Exists(job.Files[0].SourcePath));
            Assert.True(dropFile.Disposed);
        }
        finally
        {
            radial.Close(animate: false);
            Reset(store, folder);
        }
    }

    [AvaloniaTheory]
    [InlineData("png", false)]
    [InlineData("pdf", false)]
    [InlineData("png", true)]
    [InlineData("pdf", true)]
    public void Standalone_svg_drag_reaches_png_and_pdf_or_reports_missing_resources(string target, bool external)
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var folder = UseFolder(store, saveOnlyConverted: true);
        var radial = AppHost.Get<RadialController>();
        var conversions = AppHost.Get<ConversionService>();
        try
        {
            using var data = new DataTransfer();
            var drawing = external ? "<image href='missing.png' width='20' height='20'/>"
                : "<rect width='20' height='20' fill='red'/>";
            data.Add(DataTransferItem.CreateText("<svg width='20' height='20'>" + drawing + "</svg>"));
            Assert.True(DraggedFiles.Offered(data));
            radial.ShowForDrag(500, 500);
            var window = radial.Window!;
            var root = window.FindControl<Panel>("Root")!;
            var donut = window.DonutControl;
            root.RaiseEvent(new DragEventArgs(DragDrop.DragEnterEvent, data, root, new Point(5, 5), KeyModifiers.Control));
            Pump();
            var model = AppHost.Get<RadialViewModel>();
            Assert.EndsWith(".svg", Assert.Single(model.Files));
            var (at, _) = FirstSlice(donut, model, target);
            var before = conversions.Jobs.Count;
            root.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, root, donut.TranslatePoint(at, root)!.Value, KeyModifiers.Control));
            Assert.Equal(before + 1, conversions.Jobs.Count);
            var job = WaitFor(conversions.Jobs[0]);
            if (external)
            {
                Assert.Equal(FileState.Failed, Assert.Single(job.Files).State);
                Assert.Contains("external", job.Files[0].ErrorDetail);
                Assert.Empty(job.Outputs);
                Assert.Empty(Directory.GetFiles(folder));
            }
            else
            {
                Assert.Equal(JobState.Completed, job.State);
                var output = Assert.Single(job.Outputs);
                Assert.Equal("." + target, Path.GetExtension(output));
                if (target == "png")
                {
                    using var bitmap = SkiaSharp.SKBitmap.Decode(output);
                    Assert.NotNull(bitmap);
                    Assert.Equal(SkiaSharp.SKColors.Red, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
                }
                else
                {
                    using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
                    Assert.Equal(1, pdf.NumberOfPages);
                    Assert.NotEmpty(pdf.GetPage(1).Paths);
                }
            }
        }
        finally
        {
            radial.Close(animate: false);
            Reset(store, folder);
        }
    }

    [AvaloniaTheory]
    [InlineData("ordinary text")]
    [InlineData("https://example.com/image.svg")]
    [InlineData("<svg><rect></svg>")]
    [InlineData("<div><svg></svg></div>")]
    [InlineData("<svg></svg><svg></svg>")]
    [InlineData("&lt;svg&gt;&lt;/svg&gt;")]
    public void Other_text_does_not_offer_files_to_the_donut(string text)
    {
        using var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        Assert.False(DraggedFiles.Offered(data));
        Assert.Empty(DraggedFiles.Read(data, forDrop: true).Files);
    }

    /// <summary>A point on a slice that converts these files, and its preset.</summary>
    private static (Point At, string Preset) FirstSlice(DonutMenu donut, RadialViewModel model, string? target = null)
    {
        var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
        var radius = model.OuterRadius * 0.75;
        for (var degrees = 0; degrees < 360; degrees += 5)
        {
            var angle = degrees * Math.PI / 180;
            var p = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
            var hit = donut.HitTest(p);
            if (hit.Kind == DonutHitKind.Segment && model.PresetAt(hit.Index, -1) is { } preset && donut.Converts(hit)
                && (target is null || preset.TargetFormat == target))
                return (p, preset.Id);
        }
        throw new InvalidOperationException("No slice converts a PNG.");
    }

    [AvaloniaFact]
    public void A_picture_dropped_on_the_drop_zone_opens_the_donut_for_it_and_closing_lets_it_go()
    {
        TestServices.EnsureInitialized("en");
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        vm.Navigate("home");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        var radial = AppHost.Get<RadialController>();
        try
        {
            var page = window.GetVisualDescendants().OfType<HomePage>().Single();
            var zone = page.FindControl<Border>("DropZone")!;
            var file = VirtualFile.Create("cat.png", Png);
            using var data = ChromePicture(file);

            zone.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, zone, new Point(20, 20), KeyModifiers.None));
            Pump();

            var model = AppHost.Get<RadialViewModel>();
            Assert.True(radial.IsVisible);
            Assert.True(model.IsClickMode);
            Assert.Equal("cat.png", Path.GetFileName(Assert.Single(model.Files)));
            Assert.NotEmpty(model.Presets);
            Assert.False(file.Disposed);

            radial.Close(animate: false);
            Assert.True(file.Disposed);
        }
        finally
        {
            radial.Close(animate: false);
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void The_settings_for_files_from_a_browser_render_and_save(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        vm.Navigate("general");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        try
        {
            var page = (Filee.App.ViewModels.Pages.GeneralPageViewModel)vm.CurrentPage;
            var title = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == loc["general.browser_drops"]);
            var card = title.FindAncestorOfType<Border>()!;
            var folderBox = card.GetVisualDescendants().OfType<TextBox>().Single();
            var check = card.GetVisualDescendants().OfType<CheckBox>().Single();

            // Nothing chosen: the Downloads folder shows as the placeholder.
            Assert.Equal("", folderBox.Text ?? "");
            Assert.Equal(AppHost.Get<Filee.Core.Platform.IPlatformServices>().DownloadsFolder, folderBox.PlaceholderText);
            Assert.Equal(page.DownloadsFolder, page.BrowserFolderInUse);

            card.BringIntoView();
            Pump();
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"page-general-browser-drops-{language}.png"));
                frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            var chosen = Path.Combine(Path.GetTempPath(), "Web downloads");
            page.BrowserFolder = "  " + chosen + " ";
            check.IsChecked = true;
            Pump();
            Assert.Equal(chosen, store.Settings.BrowserDrops.Folder);
            Assert.True(store.Settings.BrowserDrops.SaveOnlyConverted);
            Assert.Equal(new WebDropOptions(chosen, true), AppHost.Get<ConversionService>().BrowserDropOptions());
        }
        finally
        {
            store.Settings.BrowserDrops.Folder = "";
            store.Settings.BrowserDrops.SaveOnlyConverted = false;
            window.Close();
        }
    }
}
