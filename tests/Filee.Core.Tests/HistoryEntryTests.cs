// Recent conversions remember which files failed, why, and the preset they ran with, so they can be converted again.

using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class HistoryEntryTests
{
    [Fact]
    public async Task Failed_files_are_kept_with_their_reason_and_the_preset_as_it_ran()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var notes = dir.File("notes.xyz");
        var output = Path.Combine(dir.Path, "out");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var preset = new Preset { Id = "to-png", TargetFormat = "png", Output = { Location = OutputLocation.CustomFolder, CustomFolder = output } };
        var job = new ConversionJob([photo, notes], preset, "PNG");
        await queue.RunAsync(job);

        var store = new UserDataStore(dir.Path);
        store.Load();
        store.AddHistory(HistoryEntry.From(job));
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var entry = Assert.Single(reloaded.History);

        Assert.Equal(JobState.CompletedWithErrors, entry.State);
        var failure = Assert.Single(entry.Failures);
        Assert.Equal(notes, failure.Source);
        Assert.Equal("error.unsupported_source", failure.ErrorKey);
        Assert.Equal("png", entry.Preset!.TargetFormat);
        Assert.Equal(OutputLocation.CustomFolder, entry.Preset.Output.Location);
        Assert.Equal(output, entry.Preset.Output.CustomFolder);
    }

    [Fact]
    public async Task A_job_without_failures_keeps_no_preset()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var job = new ConversionJob([photo], new Preset { TargetFormat = "png" }, "PNG");
        await queue.RunAsync(job);

        var entry = HistoryEntry.From(job);

        Assert.Empty(entry.Failures);
        Assert.Null(entry.Preset);
    }

    [Fact]
    public async Task A_finished_job_remembers_its_results_as_they_were_for_undo()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var job = new ConversionJob([photo], new Preset { TargetFormat = "png" }, "PNG");
        await queue.RunAsync(job);

        var store = new UserDataStore(dir.Path);
        store.Load();
        store.AddHistory(HistoryEntry.From(job));
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var entry = Assert.Single(reloaded.History);

        var result = Assert.Single(entry.Undoable);
        Assert.Equal(Assert.Single(job.Outputs), result.Path);
        Assert.False(entry.OriginalsInTrash);
        Assert.False(entry.Undone);
        Assert.True(result.Unchanged());

        // Written again since (another conversion with "Overwrite", an edit): Undo must leave it alone.
        File.AppendAllText(result.Path, "edited");
        Assert.False(result.Unchanged());
        File.Delete(result.Path);
        Assert.False(result.Unchanged());
    }

    [Fact]
    public async Task A_document_whose_pictures_pandoc_put_beside_it_has_no_undo()
    {
        using var dir = new TempDir();
        var doc = dir.File("notes.docx");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("pandoc", new ConversionEdge("docx", "md")) { PictureFolder = true }]));
        var job = new ConversionJob([doc], new Preset { TargetFormat = "md" }, "MD");
        await queue.RunAsync(job);

        // Undo would move the .md and leave its "_files" folder of pictures behind.
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "notes_files")));
        Assert.Empty(HistoryEntry.From(job).Undoable);
    }

    [Fact]
    public async Task A_job_with_more_results_than_undo_takes_has_none()
    {
        using var dir = new TempDir();
        var pdf = dir.File("book.pdf");
        var converter = new FakeConverter("pdfium", new ConversionEdge("pdf", "png")) { OutputsPerInput = HistoryEntry.MaxUndoable + 1 };
        await using var queue = new JobQueue(new ConverterCatalog([converter]));
        var job = new ConversionJob([pdf], new Preset { TargetFormat = "png" }, "PNG");
        await queue.RunAsync(job);

        Assert.Equal(HistoryEntry.MaxUndoable + 1, job.Outputs.Count());
        Assert.Empty(HistoryEntry.From(job).Undoable);
    }

    [Fact]
    public void An_entry_from_before_undo_has_nothing_to_undo()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "history.json"), """[{"JobId":"a","PresetName":"PNG","Outputs":["photo.png"]}]""");
        var store = new UserDataStore(dir.Path);
        store.Load();

        var entry = Assert.Single(store.History);
        Assert.Empty(entry.Undoable);
        Assert.False(entry.Undone);
    }
}
