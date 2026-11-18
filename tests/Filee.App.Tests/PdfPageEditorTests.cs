using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class PdfPageEditorTests
{
    private static string MakePdf(string folder, string name, params (double Width, int Rotation)[] pages)
    {
        var path = Path.Combine(folder, name);
        using var document = new PdfDocument();
        for (var index = 0; index < pages.Length; index++)
        {
            var (width, rotation) = pages[index];
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(500);
            page.Rotate = rotation;
            using var graphics = XGraphics.FromPdfPage(page);
            var accent = new XSolidBrush(XColor.FromArgb(
                (byte)(70 + index * 35), (byte)(100 + index * 20), (byte)(180 - index * 25)));
            graphics.DrawRectangle(accent, 24, 24, width - 48, 88);
            graphics.DrawRectangle(accent, 24, 148, width * .62, 24);
            graphics.DrawRectangle(accent, 24, 192, width * .78, 16);
            graphics.DrawRectangle(accent, 24, 228, width * .48, 16);
        }
        document.Save(path);
        return path;
    }

    [AvaloniaFact]
    public async Task Editor_adds_reorders_rotates_deletes_and_saves_pages()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-").FullName;
        try
        {
            var first = MakePdf(folder, "source.pdf", (200, 0), (300, 0));
            var added = MakePdf(folder, "added.pdf", (400, 90));
            var store = new UserDataStore(Path.Combine(folder, "data"));
            store.Load();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-pages");
            preset.Output.Location = OutputLocation.CustomFolder;
            preset.Output.CustomFolder = folder;
            using var vm = new PdfPageEditorViewModel([first], preset, AppHost.Get<ILocalizer>(), store);

            Assert.Empty(vm.Pages); // The constructor does not parse the PDF on the UI thread.
            await vm.LoadThumbnailsAsync();
            await vm.AddFilesAsync([added]);
            vm.SelectedPage = vm.Pages[2];
            vm.MoveUpCommand.Execute(null);
            vm.MoveUpCommand.Execute(null);
            vm.RotateLeftCommand.Execute(null);
            vm.SelectedPage = vm.Pages[2];
            vm.DeleteCommand.Execute(null);

            Assert.Equal([400d, 200d], vm.Pages.Select(p => p.Path == added ? 400d : 200d));
            Assert.Equal("Rotated −90°", vm.Pages[0].RotationText.Replace('-', '−'));
            Assert.True(await vm.SaveAsync());

            var output = Path.Combine(folder, "source_edited.pdf");
            using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.Equal([400d, 200d], result.Pages.Cast<PdfPage>().Select(p => p.Width.Point));
            Assert.Equal([0, 0], result.Pages.Cast<PdfPage>().Select(p => p.Rotate));
            Assert.Contains(store.History, h => h.Outputs.Contains(output));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static PdfPageEditorViewModel Editor(string folder, string source, out UserDataStore store)
    {
        store = new UserDataStore(Path.Combine(folder, "data"));
        store.Load();
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-pages");
        preset.Output.Location = OutputLocation.CustomFolder;
        preset.Output.CustomFolder = folder;
        return new PdfPageEditorViewModel([source], preset, AppHost.Get<ILocalizer>(), store);
    }

    /// <summary>A page's width, which tells the test pages apart: each one has a width of its own.</summary>
    private static double Width(PdfPageItem page)
    {
        using var document = PdfReader.Open(page.Path, PdfDocumentOpenMode.Import);
        return document.Pages[page.PageIndex].Width.Point;
    }

    private static void SelectPages(PdfPageEditorViewModel vm, params int[] indexes)
    {
        vm.SelectedPages.Clear();
        foreach (var index in indexes)
            vm.SelectedPages.Add(vm.Pages[index]);
    }

    [AvaloniaFact]
    public async Task Several_selected_pages_move_rotate_delete_and_extract_together()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-multi-").FullName;
        try
        {
            var source = MakePdf(folder, "source.pdf", (210, 0), (220, 0), (230, 0), (240, 0), (250, 0));
            using var vm = Editor(folder, source, out _);
            await vm.LoadThumbnailsAsync();

            // Pages 2 and 4 (a gap between them) move down together; the block never splits.
            SelectPages(vm, 1, 3);
            Assert.Equal(vm.Pages[3], vm.SelectedPage); // The page picked last shows large.
            vm.MoveDownCommand.Execute(null);
            Assert.Equal([210d, 230d, 220d, 250d, 240d], vm.Pages.Select(Width));
            Assert.Equal([220d, 240d], vm.SelectionInOrder().Select(Width));
            // The last one is at the bottom now; the other still moves.
            Assert.True(vm.MoveDownCommand.CanExecute(null));
            vm.MoveDownCommand.Execute(null);
            Assert.Equal([210d, 230d, 250d, 220d, 240d], vm.Pages.Select(Width));
            Assert.False(vm.MoveDownCommand.CanExecute(null));
            vm.MoveUpCommand.Execute(null);
            Assert.Equal([210d, 230d, 220d, 240d, 250d], vm.Pages.Select(Width));

            vm.RotateRightCommand.Execute(null);
            Assert.Equal([0, 0, 90, 90, 0], vm.Pages.Select(page => page.Rotation));

            // Extracting gives one PDF of the selected pages, in page order.
            Assert.False(await vm.ExtractSelectedAsync());
            using (var extracted = PdfReader.Open(Path.Combine(folder, "source_extracted.pdf"), PdfDocumentOpenMode.Import))
                Assert.Equal(2, extracted.PageCount);

            vm.DeleteCommand.Execute(null);
            Assert.Equal([210d, 230d, 250d], vm.Pages.Select(Width));
            Assert.Equal([250d], vm.SelectionInOrder().Select(Width)); // The page that took their place.

            // Every page selected: deleting them all is not offered.
            SelectPages(vm, 0, 1, 2);
            Assert.False(vm.DeleteCommand.CanExecute(null));
            Assert.False(vm.MoveUpCommand.CanExecute(null));
            Assert.False(vm.MoveDownCommand.CanExecute(null));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Dragged_pages_land_before_the_drop_position()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-drag-").FullName;
        try
        {
            var source = MakePdf(folder, "source.pdf", (210, 0), (220, 0), (230, 0), (240, 0), (250, 0));
            using var vm = Editor(folder, source, out _);
            await vm.LoadThumbnailsAsync();

            // Pages 1 and 3 dropped between pages 4 and 5 (position 4 of the order before the drag).
            vm.MovePages([vm.Pages[0], vm.Pages[2]], 4);
            Assert.Equal([220d, 240d, 210d, 230d, 250d], vm.Pages.Select(Width));
            Assert.Equal([210d, 230d], vm.SelectionInOrder().Select(Width));

            // To the very end, and to the very start.
            vm.MovePages([vm.Pages[0]], vm.Pages.Count);
            Assert.Equal([240d, 210d, 230d, 250d, 220d], vm.Pages.Select(Width));
            vm.MovePages([vm.Pages[3], vm.Pages[4]], 0);
            Assert.Equal([250d, 220d, 240d, 210d, 230d], vm.Pages.Select(Width));

            // Dropped on their own place: nothing changes.
            vm.MovePages([vm.Pages[1], vm.Pages[2]], 2);
            Assert.Equal([250d, 220d, 240d, 210d, 230d], vm.Pages.Select(Width));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Pages_are_inserted_around_the_selection_and_replace_it()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-insert-").FullName;
        try
        {
            var source = MakePdf(folder, "source.pdf", (210, 0), (220, 0), (230, 0), (240, 0));
            var other = MakePdf(folder, "other.pdf", (410, 0), (420, 0));
            using var vm = Editor(folder, source, out _);
            await vm.LoadThumbnailsAsync();

            SelectPages(vm, 1, 2);
            await vm.InsertFilesAsync([other], after: true);
            Assert.Equal([210d, 220d, 230d, 410d, 420d, 240d], vm.Pages.Select(Width));
            Assert.Equal([410d, 420d], vm.SelectionInOrder().Select(Width)); // The new pages are selected.

            SelectPages(vm, 1);
            await vm.InsertFilesAsync([other], after: false);
            Assert.Equal([210d, 410d, 420d, 220d, 230d, 410d, 420d, 240d], vm.Pages.Select(Width));

            // Replace: the corrected pages stand where the old ones were, and the old ones are gone.
            SelectPages(vm, 4, 7);
            await vm.ReplaceSelectedAsync([other]);
            Assert.Equal([210d, 410d, 420d, 220d, 410d, 420d, 410d, 420d], vm.Pages.Select(Width));
            Assert.Equal([4, 5], vm.SelectionInOrder().Select(page => vm.Pages.IndexOf(page)));

            // A PDF that can't be opened replaces nothing.
            var broken = Path.Combine(folder, "broken.pdf");
            await File.WriteAllTextAsync(broken, "not a pdf", TestContext.Current.CancellationToken);
            SelectPages(vm, 0);
            await vm.ReplaceSelectedAsync([broken]);
            Assert.Equal(8, vm.Pages.Count);
            Assert.Equal(210d, Width(vm.Pages[0]));

            Assert.True(await vm.SaveAsync());
            using var result = PdfReader.Open(Path.Combine(folder, "source_edited.pdf"), PdfDocumentOpenMode.Import);
            Assert.Equal([210d, 410d, 420d, 220d, 410d, 420d, 410d, 420d], result.Pages.Cast<PdfPage>().Select(p => p.Width.Point));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Dropped_pdfs_are_inserted_at_the_drop_position_and_other_files_left_out()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-drop-").FullName;
        try
        {
            var source = MakePdf(folder, "source.pdf", (210, 0), (220, 0), (230, 0));
            var first = MakePdf(folder, "first.pdf", (410, 0), (420, 0));
            var second = MakePdf(folder, "second.PDF", (510, 0));
            var picture = Path.Combine(folder, "picture.png");
            await File.WriteAllBytesAsync(picture, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);
            using var vm = Editor(folder, source, out _);
            await vm.LoadThumbnailsAsync();

            // Two PDFs dropped between pages 1 and 2: each PDF's pages in order, the first PDF first, all selected.
            await vm.InsertFilesAtAsync([first, second], 1);
            Assert.Equal([210d, 410d, 420d, 510d, 220d, 230d], vm.Pages.Select(Width));
            Assert.Equal([410d, 420d, 510d], vm.SelectionInOrder().Select(Width));
            Assert.False(vm.HasStatus);

            // At the very start and the very end; a position past the end goes at the end.
            await vm.InsertFilesAtAsync([second], 0);
            await vm.InsertFilesAtAsync([second], 99);
            Assert.Equal([510d, 210d, 410d, 420d, 510d, 220d, 230d, 510d], vm.Pages.Select(Width));

            // A picture among them is left out, and the status line says so.
            await vm.InsertFilesAtAsync([picture, first], 2);
            Assert.Equal([510d, 210d, 410d, 420d, 410d, 420d, 510d, 220d, 230d, 510d], vm.Pages.Select(Width));
            Assert.Equal("Files that aren't PDFs were left out", vm.Status);

            // Nothing but other files: no pages, the usual "isn't supported" message.
            await vm.InsertFilesAtAsync([picture], 0);
            Assert.Equal(10, vm.Pages.Count);
            Assert.Equal("This file type isn't supported", vm.Status);

            // A .pdf that can't be opened adds nothing, as with Insert.
            var broken = Path.Combine(folder, "broken.pdf");
            await File.WriteAllTextAsync(broken, "not a pdf", TestContext.Current.CancellationToken);
            await vm.InsertFilesAtAsync([broken], 0);
            Assert.Equal(10, vm.Pages.Count);
            Assert.Equal(AppHost.Get<ILocalizer>()["error.conversion_failed"], vm.Status);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Pdfs_dragged_from_the_file_manager_show_the_drop_line_and_land_there()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-filedrop-").FullName;
        try
        {
            var source = MakePdf(folder, "source.pdf", (210, 0), (220, 0), (230, 0), (240, 0));
            var other = MakePdf(folder, "other.pdf", (410, 0), (420, 0));
            var picture = Path.Combine(folder, "picture.png");
            await File.WriteAllBytesAsync(picture, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);
            var vm = Editor(folder, source, out _);
            var window = new PdfPageEditorWindow { DataContext = vm };
            window.Show();
            await vm.LoadThumbnailsAsync();
            RenderTests.Pump();
            var list = window.PageOrder;

            // A point on a page in the strip, in the strip's coordinates; y is a share of the page's height.
            Point On(int index, double y)
            {
                var card = list.ContainerFromIndex(index)!;
                return card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height * y), list)!.Value;
            }
            static DataTransfer Files(params string[] paths)
            {
                var data = new DataTransfer();
                foreach (var path in paths)
                    data.Add(DataTransferItem.CreateFile(BrowserDropTests.VirtualFile.Create(Path.GetFileName(path), [], path).Item));
                return data;
            }
            DragDropEffects Raise(RoutedEvent<DragEventArgs> kind, DataTransfer data, Point at)
            {
                var args = new DragEventArgs(kind, data, list, at, KeyModifiers.None) { DragEffects = DragDropEffects.Copy | DragDropEffects.Move };
                list.RaiseEvent(args);
                Dispatcher.UIThread.RunJobs();
                return args.DragEffects;
            }

            // Only a picture: not taken, no line.
            using (var pictures = Files(picture))
            {
                Assert.Equal(DragDropEffects.None, Raise(DragDrop.DragEnterEvent, pictures, On(1, .3)));
                Assert.False(window.DropLine.IsVisible);
                Raise(DragDrop.DragLeaveEvent, pictures, new Point(-10, -10));
            }

            // A PDF and a picture over the bottom half of page 2: copied (never moved), the line under page 2.
            using var data = Files(other, picture);
            Assert.Equal(DragDropEffects.Copy, Raise(DragDrop.DragEnterEvent, data, On(1, .3)));
            Assert.Equal(DragDropEffects.Copy, Raise(DragDrop.DragOverEvent, data, On(1, .8)));
            Assert.True(window.DropLine.IsVisible);
            var lineCentre = Canvas.GetTop(window.DropLine) + window.DropLine.Height / 2;
            var below = list.ContainerFromIndex(1)!;
            Assert.Equal(below.TranslatePoint(new Point(0, below.Bounds.Height), list)!.Value.Y, lineCentre, 1);
            RenderTests.Pump();
            RenderTests.Save(window, "pdf-page-editor-file-drop.png");

            // Passing onto the next page inside the strip keeps the line; leaving the strip hides it.
            Raise(DragDrop.DragLeaveEvent, data, On(2, .2));
            Assert.True(window.DropLine.IsVisible);
            Raise(DragDrop.DragLeaveEvent, data, new Point(-10, -10));
            Assert.False(window.DropLine.IsVisible);

            Raise(DragDrop.DragEnterEvent, data, On(1, .8));
            Assert.Equal(DragDropEffects.Copy, Raise(DragDrop.DropEvent, data, On(1, .8)));
            for (var i = 0; i < 100 && (vm.Pages.Count < 6 || !vm.HasStatus); i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.DropLine.IsVisible);
            Assert.Equal([210d, 220d, 410d, 420d, 230d, 240d], vm.Pages.Select(Width));
            Assert.Equal([410d, 420d], vm.SelectionInOrder().Select(Width));
            Assert.Equal("Files that aren't PDFs were left out", vm.Status);
            window.Close();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Ctrl_click_selects_several_pages_and_a_drag_moves_them_all()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-mouse-").FullName;
        try
        {
            var source = MakePdf(folder, "source.pdf", (210, 0), (220, 0), (230, 0), (240, 0));
            var vm = Editor(folder, source, out _);
            var window = new PdfPageEditorWindow { DataContext = vm };
            window.Show();
            await vm.LoadThumbnailsAsync();
            RenderTests.Pump();

            // A point on a page in the strip, in window coordinates; y is a share of the page's height.
            Point On(int index, double y = .5)
            {
                var card = window.PageOrder.ContainerFromIndex(index)!;
                return card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height * y), window)!.Value;
            }
            void Click(int index, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.MouseDown(On(index), MouseButton.Left, modifiers);
                window.MouseUp(On(index), MouseButton.Left, modifiers);
                Dispatcher.UIThread.RunJobs();
            }

            Click(0);
            Click(2, RawInputModifiers.Control);
            Assert.Equal([210d, 230d], vm.SelectionInOrder().Select(Width));
            Assert.Equal(2, window.PageOrder.SelectedItems!.Count);
            Assert.True(vm.HasSelection);

            // Pressing the first selected page keeps both selected; dropped on the top half of page 4, they go before it.
            window.MouseDown(On(0), MouseButton.Left);
            for (var step = 1; step <= 8; step++)
            {
                var (from, to) = (On(0), On(3, .2));
                window.MouseMove(new Point(from.X, from.Y + (to.Y - from.Y) * step / 8));
            }
            Assert.True(window.DropLine.IsVisible);
            Assert.All(vm.SelectionInOrder(), page => Assert.True(page.IsMoving));
            window.MouseUp(On(3, .2), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            RenderTests.Pump();

            Assert.Equal([220d, 210d, 230d, 240d], vm.Pages.Select(Width));
            Assert.Equal([210d, 230d], vm.SelectionInOrder().Select(Width));
            Assert.False(window.DropLine.IsVisible);
            Assert.All(vm.Pages, page => Assert.False(page.IsMoving));
            Assert.Equal(2, window.PageOrder.SelectedItems!.Count);

            // A click without a drag on one of them leaves that page alone selected.
            Click(1);
            Assert.Equal([210d], vm.SelectionInOrder().Select(Width));
            Assert.Equal(vm.Pages[1], vm.SelectedPage);
            window.Close();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public async Task Editor_window_renders_previews_and_all_actions_without_clipping(string language)
    {
        TestServices.EnsureInitialized(language);
        var folder = Directory.CreateTempSubdirectory("filee-pdf-pages-ui-").FullName;
        try
        {
            var source = MakePdf(folder, "Annual report.pdf", (595, 0), (595, 0), (842, 0), (595, 0));
            var store = new UserDataStore(Path.Combine(folder, "data"));
            store.Load();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-pages");
            var vm = new PdfPageEditorViewModel([source], preset, AppHost.Get<ILocalizer>(), store)
            {
                SelectedPage = null,
            };
            var window = new PdfPageEditorWindow { DataContext = vm };
            window.Show();

            await vm.LoadThumbnailsAsync();
            vm.SelectedPage = vm.Pages[1];
            vm.RotateRightCommand.Execute(null);
            for (var i = 0; i < 100 && !vm.HasSelectedPreview; i++)
            {
                RenderTests.Pump();
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            RenderTests.Pump();
            RenderTests.Save(window, $"pdf-page-editor-{language}.png");

            Assert.True(window.PageOrder.Bounds.Width > 220);
            Assert.Equal(4, window.PageOrder.ItemsView.Count);
            Assert.All(vm.Pages, page => Assert.True(page.HasThumbnail));
            Assert.True(vm.HasSelectedPreview);
            window.Close();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
