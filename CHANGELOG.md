# Changelog

What changed in each version of Filee, for the people using it. The release workflow puts the section of the
version it publishes at the top of the GitHub release notes, above the list of pull requests.

Add a `## <version>` section before tagging a release. Write what someone using Filee notices: what's new, where to
find it, what it does and doesn't do.

## 1.5.0

### FFmpeg, calibre and the others you already have are used by themselves

- Filee now finds FFmpeg, calibre, Pandoc, Ghostscript and LibreOffice that are already on your PC, without asking
  you to download them: on the PATH (Scoop's shims included) or where their installers put them (LibreOffice,
  calibre and Ghostscript under Program Files). Settings → Engines shows "Found on this PC" and the folder.
- If that copy is older than the version Filee is tested with, Settings → Engines recommends an update. It keeps
  working; most conversions don't need the newest version.
- Order: a copy you chose with **Use my copy…**, then Filee's own download (if you installed it), then one found on
  the PC. **Install** is still there if you'd rather use Filee's tested copy. **Check again** searches again after
  you install something.

### One multi-page TIFF

- New option for TIFF presets: **Put every file into one multi-page TIFF**. Every image, every PDF page and every
  page of a TIFF becomes a page of one TIFF, in order, named after the first file. One PDF is enough: a 10-page PDF
  becomes one 10-page TIFF.
- Pages use the preset's compression and are marked as pages of a multi-page document, so older viewers (fax
  software, Microsoft Office Document Imaging) show them page by page. Transparent areas get the preset's background.

### Arranging the donut

- On the Donut toolbar page, dragging a preset (or a slice) over the donut opens a gap where it will land, with a
  see-through slice showing its name. The other slices slide aside, and a moved slice leaves its old place.

## 1.4.0

### Updates install themselves

- In an installed copy, **Update** (bottom of the menu, tray menu, the update notice, Settings → General) now
  downloads the new installer inside Filee, checks it against the release's SHA-256 file and runs it without the setup
  wizard. Setup closes Filee, updates it, keeps your settings and engines and starts Filee again. Windows asks for
  administrator rights once.
- The sidebar, tray menu, Settings → General and the notice show the download progress.
- If the download or the installer fails (no connection, a file that doesn't match its checksum, an installer
  that won't start), Filee tells you, and after OK it opens filee.sh so you can download the new version there.
- The portable copy still opens the release page: download the new ZIP and unpack it over the old folder.
- Updating **to** 1.4.0 from an older version still goes the old way. From 1.4.0 on, it's one click.

### Default save location

- Settings → General → **Default save location**: next to the original (as before), in a subfolder such as
  `converted`, or in one fixed folder.
- Every preset follows it unless you give the preset its own location (Presets → Output → Save to).
- Built-in presets, and your presets that saved next to the original, follow the default from now on. Since the
  default starts as "next to the original", nothing changes until you pick another one.

### Keep the original file dates for every conversion

- New switch in Settings → General: converted files get the created and modified dates of the original, with every
  preset (photos, videos, PDFs, archive conversions).
- Not when several files become one (merged PDF, several files packed into one ZIP): that file has no single original.
- The per-preset option from 1.3.1 stays, for keeping dates with some presets only.
- The `filee` command follows the default save location and this switch too.

### Use the engines you already have

- Settings → Engines → **Use my copy…** next to FFmpeg, calibre, Pandoc, Ghostscript and LibreOffice. Pick the
  program (`ffmpeg.exe`, `ebook-convert.exe`, `pandoc.exe`, `gswin64c.exe` or `soffice.exe`) and Filee uses it
  instead of downloading its own. **Stop using it** goes back.
- FFmpeg needs `ffprobe.exe` in the same folder (most builds have both). Picking a build's main folder finds its
  `bin` folder too.
- The `filee` command uses the same copies.

## 1.3.1

### Keep the original file dates

- Presets → Output → **Keep the original file dates**: converted files get the created and modified dates of the
  original, so converted photos still sort by when they were taken. On the command line: `--keep-dates`.

### Conversions that were there but hard to find

- New **HWP** preset (saved through HWPX). It is on the PDF, documents, HWP, spreadsheet, presentation, text and
  e-book donuts, so Word, PDF, Excel, PowerPoint, Markdown and EPUB files can be saved as `.hwp` directly.
- On the donuts now: PDF → Markdown and EPUB, Word → PNG, HWP → JPG and EPUB. These worked before through another
  format, but weren't on any donut.
- Existing donuts get the new entries once, unless a donut is full or already has them.

### Texts

- Plainer wording in English, Korean and Chinese.
- Fixed: the Korean shortcut button said "recording", and removing a watch folder read like deleting it in Chinese.

## 1.3.0

### The `filee` command line

- Setup can add the `filee` command to PATH. `filee convert` converts files, wildcards (`*.heic`) and folders with
  `--to <format>` or one of your presets; `filee watch` converts files that land in a folder; `filee formats` and
  `filee presets` list what's available.
- `--json` prints the result for scripts. Exit codes: 0 all converted, 1 some failed, 2 wrong arguments or a missing
  file, 3 nothing to convert. Guide: https://filee.sh/cli/

### Watch folders

- A new **Watch folders** page: files that land in a folder you choose are converted while Filee runs, once
  downloads and copies are complete. Keep the originals, or move them to an `originals` folder so the folder works
  like an inbox.

### History

- Settings → General: turn off the list of recent conversions, or clear it.

### Fixes

- Ctrl+drag stopped working after the Photos app had been opened, until Filee was restarted. The donut now stays on
  top of other windows.
