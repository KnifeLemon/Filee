# Changelog

What changed in each version of Filee, for the people using it. The release workflow puts the section of the
version it publishes under "What's Changed" in the GitHub release notes, above the list of pull requests.

Add a `## <version>` section before tagging a release. Write what someone using Filee notices: what's new, where to
find it, what it does and doesn't do.

## 1.8.0

### New

- **Failed files at a glance, and a retry.** When files of a conversion fail, Recent conversions on the Home page
  lists them with the reason (click "2 failed" to open the list; names and reasons can be selected and copied).
  **Retry** converts the files that failed again with the same settings and save location, even after the preset
  was changed or deleted. Files that are no longer there are left out. The progress card that pops up after a
  conversion has the same **Retry** button. Conversions from watch folders are listed and retried the same way;
  a retried file stays in the watched folder, also when the folder moves originals to "originals".
- **Watch folders: choose files and name them** (Settings → Watch folders → *More options*, all optional; empty
  fields change nothing):
  - *Files to convert*: patterns entered as tags. Typing `heic` adds `*.heic`, other text picks names that contain
    it, and `*` stands for any text, `?` for one character. Extensions are suggested while typing, and the card says
    how many files in the folder match. Other files in the folder are left alone.
  - *Names of converted files*: **Edit name rules** opens a window with the base name (typed, or built with buttons
    for the original name, date, time, number and preset name; empty keeps the preset's) and changes applied to it
    from top to bottom. Each change says what it does — replace text, remove text, add at the start or end, spaces
    to `_`, remove a copy number like "(2)", text in brackets, a leading number or symbols, all lowercase or
    uppercase — and asks only for the text it needs; a regular expression is there as the last, advanced kind.
    **Common replacements** on the left lists ready-made changes with an example of each; double-click one or press +
    to add it. The preview shows the name each file in the folder will get, and any name you type. Nothing changes
    until you save. The card shows the name in words ("[Original name]_scan") and the changes.
  - A **preview** on every watch folder shows a converted file as it will be saved (`IMG_0412.jpg →
    converted\Photo_0412.pdf`, using a file already in the folder when there is one), what happens when the name is
    taken and what happens to the original.

### Documentation

- docs/ENGINES.md said Filee never uses engines installed on the computer. It does, as the last choice after a copy
  you picked and Filee's own: that is how FFmpeg and Ghostscript are found on macOS. The order is now described.

## 1.7.2

### Fixes

- When the computer was busy (a screen recorder running, a slow graphics card), slices of the donut could stay lit
  after the pointer left them, pile up in the accent colour and fly apart as the donut closed. A late frame moved the
  highlight further than it should, and every following frame made it worse. The animations now stay steady however
  late the frames come. Turning on "Reduce animations" was the workaround until now.

## 1.7.1

### Fixes

- Moving the pointer from one slice of the donut to another no longer makes the slice it left flash in the accent
  colour as its highlight fades.
- After Filee sat unused for a long time, the donut could take seconds to appear or open without its animation. A
  frame the animation waited for could get lost; the donut now asks again and, if frames still don't come, shows
  itself fully open.
- With "Reduce animations" following Windows, a moment when Windows reported animations off (a remote session,
  waking from sleep) could switch them off in Filee until the setting was changed. Filee reads the Windows setting
  again whenever the donut or the main window opens.

## 1.7.0

### macOS and Linux (preview)

- Filee runs on macOS 14 or later (Apple silicon and Intel) and on Linux (x64 and ARM64). Download the package for
  your system from this release and run `install.sh` in it. These are previews: the macOS app isn't notarized yet,
  so macOS asks you to allow it once under System Settings → Privacy & Security.
- macOS: hold Option and drag files to open the donut. Filee asks for the Accessibility permission the gesture needs
  and starts it as soon as you switch Filee on. "Convert with Filee" is in Finder's right-click menu.
- Linux: the drag gesture works in an X11 session (on Ubuntu, "Ubuntu on Xorg" on the login screen). On Wayland, use
  the drop zone and the right-click action in Nautilus, Nemo, Thunar or Dolphin.
- FFmpeg and Ghostscript come from Homebrew on macOS and from your distribution's packages on Linux. Filee finds them
  by itself.

### New

- Search boxes above the profiles and the available presets (Donut toolbar) and above the preset list. A profile is
  also found by an extension it handles: type "png" to find the image donut.
- "Never trigger in these apps" (Shortcuts) holds the apps as tags. Type a name, with the running apps suggested, or
  pick one from the apps that have a window open.
- The update settings are on the About page now: the automatic check, Check now and the update button.
- The first-run engine choice starts with nothing ticked, so only what you pick is downloaded.

### Fixes

- On macOS and Linux, watch folders wait for a copy that pauses to finish instead of converting it half-written.

## 1.6.0

### New in the preset editor for audio

- Opus and the other lossy audio formats offer 32, 48 and 64 kbit/s as well, for speech and small files (#37).
- **Channels**: same as the source, mono or stereo, for every audio format except AMR (which is always mono).
- WAV gets a **sample rate** (8, 16, 22.05, 44.1 or 48 kHz, or the same as the source) and a **bit depth** (8, 16,
  24 or 32-bit, or 32-bit float). 16-bit stays the default.

### Fixes

- EPS and PS → PNG keep a transparent background where the drawing leaves the page empty, instead of white (#39).
  Ghostscript renders the PNG directly; JPG and other formats without transparency still get a white background.
- PDF → DOCX keeps rows laid out with tab stops, such as a numbered schedule with dates on the right ("1.  RFP
  Published   08/14/2026"): each row stays one paragraph with tabs at the PDF's positions, instead of the numbers,
  labels and dates coming out as separate paragraphs. Forms with a label and a value on each row come out the same
  way (#38).
- PDF → DOCX keeps the line breaks of address blocks and other short lines: "West Linn, Oregon 97068" no longer
  moves up onto the street line (#38). PDF → TXT has the tabs and line breaks too.

## 1.5.2

### Fixes

- Converting audio to Opus or Ogg Vorbis now keeps the album art. FFmpeg can't put a picture into those files, so
  Filee writes it the way they store pictures (a METADATA_BLOCK_PICTURE tag); title, artist and the other tags stay
  too. Matroska audio (MKA) gets the album art as a cover attachment.

## 1.5.1

### Fixes

- Sometimes, after Ctrl+drag opened the donut, the format under the dragged files didn't light up and dropping
  didn't pick it, until a while later. The donut's animation could wait forever for a frame that never came; it now
  starts again after a quarter of a second.
- Each drag over the donut writes one line to the log (`%APPDATA%\Filee\logs`), so if a donut ever ignores a drag
  again, the log shows whether the drag reached it.

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
