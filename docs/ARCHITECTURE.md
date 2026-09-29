# Architecture

```
            ┌──────────────────────────── Filee.App (Avalonia) ─────────────────────────────┐
 global     │ TriggerService ──► GestureDetector ──► RadialController ──► RadialWindow/DonutMenu │
 mouse/keys │   (SharpHook)        (pure state         (drag / click       (transparent,          │
 ─────────► │                       machine)            mode)               topmost window)       │
            │                                              │ drop / click                         │
 Explorer   │ SingleInstance (pipe) ── --convert files ──►│                                      │
 context    │ Home drop zone ─────────────────────────────┘                                      │
 menu       │                                              ▼                                      │
            │                                    ConversionService ──► ToastWindow (progress)     │
            │ MainWindow (settings pages) ◄──► UserDataStore (JSON in %APPDATA%\Filee)            │
            └──────────────────────────────────────────────┬──────────────────────────────────────┘
                                                           ▼
            ┌──────────────────────────── Filee.Core ───────────────────────────────────────────┐
            │ JobQueue ──► RoutePlanner (Dijkstra over converter edges) ──► IConverter steps    │
            │ FormatRegistry · Preset · OutputPathResolver · ToolbarProfile · ProfileSelector   │
            └──────────────────────────────────────────────┬──────────────────────────────────────┘
                                                           ▼
            ┌──────────────────────────── Filee.Engines ────────────────────────────────────────┐
            │ Magick (images) · PdfSharp (image→PDF, merge, split) · Pdfium (PDF→image)          │
            │ LibreOffice (office, HWP via H2Orestart) · rhwp (HWP→PDF, HWP↔HWPX) · Unhwp       │
            │ HwpxWriter (DOCX / Pandoc AST → HWPX) · Pandoc · Word COM · EngineInstaller       │
            └────────────────────────────────────────────────────────────────────────────────────┘
```

## Flow of a conversion

1. **Trigger** – `TriggerService` receives global input from SharpHook and feeds `GestureDetector`.
   A drag gesture (modifier + drag past the threshold) opens the donut *before* files are known.
2. **Donut** – `RadialWindow` is transparent, topmost and not activated, so Explorer's drag continues.
   The first `DragEnter` delivers the file list; `RadialViewModel` picks a `ToolbarProfile` by extension
   (`ProfileSelector`) and asks `PresetAvailability` which presets can run (disabled slices get a reason).
3. **Drop** – the slice under the cursor is a preset. `ConversionService.Start` enqueues a `ConversionJob`.
   The drop is always reported as *Copy* so Explorer never deletes the source files.
4. **Plan** – for every file, `RoutePlanner` finds the cheapest chain of converter edges from the source format to
   the target (at most 3 hops). Engine priority (Settings → Engines) breaks ties.
5. **Run** – `JobQueue` runs steps with per-engine concurrency limits. Intermediate files live in a temp folder;
   final outputs are named by `OutputPathResolver` (pattern, folder, conflict policy; the source is never overwritten).
   "Merge into one PDF" converts each file to PDF and merges with PDFsharp.
6. **Report** – progress and results appear in the toast window and in Home → Recent conversions.

## Donut control

`Controls/DonutMenu.cs` draws the ring itself (no child controls) for speed. `DonutGeometry.cs` holds the pure math
(slice angles, hit testing) and is unit tested. The same control is the live toolbar and, with `IsEditMode`, the
editor on the *Donut toolbar* settings page (drag to reorder, ✎ / right-click to edit).
Animations are small springs advanced per frame; `Motion.Enabled == false` (*Reduce animations*) snaps them.

## Settings and data

`UserDataStore` owns `settings.json`, `presets.json`, `profiles.json` and `history.json`
(System.Text.Json source generation, atomic writes, broken files are kept as `*.broken`).
Bump `AppSettings.CurrentSchemaVersion` and add a step to `SettingsMigrations` when the format changes.

## Localization and theming

- `LocalizationService` loads `Assets/i18n/<lang>.json` into `Application.Resources`; XAML uses
  `{DynamicResource key}`, so switching language updates the UI live.
- `ThemeService` writes the accent colour, corner radii, fonts and Fluent's `SystemAccentColor*` into
  application resources. Light/dark surface colours are theme dictionaries in `Styles/Soft.axaml`.

## Platform code

Everything OS-specific goes through `IPlatformServices` (`Filee.Core/Platform`). Windows lives in
`Filee.Platform.Windows`; macOS will get its own project. Global input uses SharpHook on both.
