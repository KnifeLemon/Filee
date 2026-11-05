// "Run a program" on the app side: the confirmation step (run only after the command was approved on this
// computer), the editor's inline refusals, and the slices that watch folders and the palette show.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.Tests;

public class RunProgramTests
{
    private static Preset RunProgramPreset(string executable = "C:\\Tools\\convert.exe", string arguments = "{input} -o {output}") =>
        new()
        {
            Id = "test-run-program",
            Name = "Run a program",
            Kind = PresetKind.RunProgram,
            TargetFormat = "txt",
            Program = new ProgramOptions { Executable = executable, Arguments = arguments },
        };

    private static ConversionService Service(string dir) =>
        new(new JobQueue(new ConverterCatalog([])), new UserDataStore(dir), new KeyLocalizer(),
            new StubPlatform(), new WebDownloader());

    private static string TempDir()
    {
        var dir = Directory.CreateTempSubdirectory("filee-runprogram-").FullName;
        return dir;
    }

    // ───────────────────────── Confirmation ─────────────────────────

    [Fact]
    public void A_preset_runs_only_after_its_command_was_confirmed()
    {
        var dir = TempDir();
        try
        {
            var service = Service(dir);
            var preset = RunProgramPreset();

            Assert.False(service.ProgramConfirmed(preset));
            service.ConfirmProgram(preset);
            Assert.True(service.ProgramConfirmed(preset));

            // A changed command (or mode) is a different command: it asks again.
            preset.Program.Arguments = "{input} --fast -o {output}";
            Assert.False(service.ProgramConfirmed(preset));
            preset.Program.Arguments = "{input} -o {output}";
            preset.Program.AllFiles = true;
            Assert.False(service.ProgramConfirmed(preset));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_empty_program_is_never_confirmed()
    {
        var dir = TempDir();
        try
        {
            var service = Service(dir);
            var preset = RunProgramPreset(executable: "");
            Assert.False(service.ProgramConfirmed(preset));
            service.ConfirmProgram(preset); // must not throw
            Assert.False(service.ProgramConfirmed(preset));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_confirmation_text_cuts_a_long_file_list_to_five_paths_and_counts_the_rest()
    {
        var dir = TempDir();
        try
        {
            var service = Service(dir);
            var preset = RunProgramPreset(executable: "C:\\Tools\\conv.exe", arguments: "{inputs}");
            var files = Enumerable.Range(1, 7).Select(i => $"C:\\drop\\file {i}.jpg").ToList();

            var text = service.ProgramConfirmText(preset, files);

            Assert.Contains("file 1.jpg", text);
            Assert.Contains("file 5.jpg", text);
            Assert.DoesNotContain("file 6.jpg", text);
            Assert.DoesNotContain("file 7.jpg", text);
            Assert.Contains(new KeyLocalizer().Format("confirm.program_more", 2), text);

            // The common one- to three-file drop keeps every path in one readable command, without the cut line.
            var shortText = service.ProgramConfirmText(preset, files.Take(3).ToList());
            Assert.Contains("file 3.jpg", shortText);
            Assert.DoesNotContain(new KeyLocalizer().Format("confirm.program_more", 2), shortText);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ───────────────────────── The editor ─────────────────────────

    private static PresetEditorViewModel Editor(Preset preset) =>
        new(preset, AppHost.Get<ILocalizer>());

    [AvaloniaFact]
    public void The_editor_refuses_an_empty_program()
    {
        TestServices.EnsureInitialized("en");
        var editor = Editor(RunProgramPreset());
        editor.ProgramExecutable = "   ";

        Assert.False(editor.Apply());
        Assert.Equal(AppHost.Get<ILocalizer>()["presets.program_need_program"], editor.ProgramExecutableError);
    }

    [AvaloniaFact]
    public void The_editor_refuses_a_relative_program()
    {
        TestServices.EnsureInitialized("en");
        var editor = Editor(RunProgramPreset());
        editor.ProgramExecutable = "tools\\convert.exe";

        Assert.False(editor.Apply());
        Assert.Equal(AppHost.Get<ILocalizer>()["presets.program_need_absolute"], editor.ProgramExecutableError);
    }

    [AvaloniaFact]
    public void The_editor_refuses_inputs_glued_into_a_longer_argument()
    {
        TestServices.EnsureInitialized("en");
        var editor = Editor(RunProgramPreset());
        editor.ProgramArguments = "--files={inputs}";

        Assert.False(editor.Apply());
        Assert.Equal(AppHost.Get<ILocalizer>()["presets.program_inputs_alone"], editor.ProgramArgumentsError);

        // Standing alone it is fine again.
        editor.ProgramArguments = "--in {inputs}";
        Assert.Null(editor.ProgramArgumentsError);
    }

    [AvaloniaFact]
    public void The_editor_refuses_an_output_extension_with_other_characters()
    {
        TestServices.EnsureInitialized("en");
        var editor = Editor(RunProgramPreset());
        editor.ProgramOutputExt = "tx t";

        Assert.False(editor.Apply());
        Assert.Equal(AppHost.Get<ILocalizer>()["presets.program_bad_ext"], editor.ProgramOutputExtError);

        // Letters, digits and empty are fine.
        editor.ProgramOutputExt = "txt";
        Assert.Null(editor.ProgramOutputExtError);
        editor.ProgramOutputExt = "";
        Assert.Null(editor.ProgramOutputExtError);
    }

    [AvaloniaFact]
    public void The_accepted_formats_field_round_trips_into_the_preset()
    {
        TestServices.EnsureInitialized("en");
        var preset = RunProgramPreset();
        var editor = Editor(preset);
        editor.ProgramAccepts = " XLSX, docx, , Folder ";

        Assert.True(editor.Apply());

        Assert.Equal(["xlsx", "docx", "folder"], preset.Program.Accepts);
        Assert.False(preset.Program.AcceptsEverything);
        Assert.True(preset.Program.AcceptsFolders);

        // Editing the saved preset shows the list again, and emptying it takes every file.
        var reopened = Editor(preset);
        Assert.Equal("xlsx, docx, folder", reopened.ProgramAccepts);
        reopened.ProgramAccepts = "";
        Assert.True(reopened.Apply());
        Assert.True(preset.Program.AcceptsEverything);
    }

    [AvaloniaFact]
    public void A_valid_run_program_preset_applies()
    {
        TestServices.EnsureInitialized("en");
        var preset = RunProgramPreset();
        var editor = Editor(preset);

        Assert.True(editor.Apply());
        Assert.Equal("C:\\Tools\\convert.exe", preset.Program.Executable);
    }

    // ───────────────────────── Watch folders ─────────────────────────

    [AvaloniaFact]
    public void Watch_folders_do_not_offer_a_run_program_preset()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var preset = RunProgramPreset();
        store.Presets.Add(preset);
        try
        {
            var page = ActivatorUtilities.CreateInstance<WatchFoldersPageViewModel>(AppHost.Services);
            Assert.DoesNotContain(page.Presets, c => c.Value == preset.Id);
            // The page's own filter is the seam; FindConversion alone is not enough to keep it out.
            Assert.NotNull(store.FindConversion(preset.Id));
        }
        finally
        {
            store.Presets.Remove(preset);
        }
    }

    /// <summary>A platform stub: the confirmation tests never touch the clipboard or the file manager.</summary>
    private sealed class StubPlatform : IPlatformServices
    {
        public bool SupportsClipboard => true;
        public bool CopyToClipboard(IReadOnlyList<string> paths) => true;
        public ForegroundWindowInfo? GetForegroundWindow() => null;
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
            Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void OpenFile(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }
}
