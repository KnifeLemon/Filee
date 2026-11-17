# Filee Yazi Plugin

Yazi plugin to convert selected or hovered files with [Filee](https://github.com/KnifeLemon/Filee), or open them directly in the interactive Filee Donut UI.

## Features

- Works on multi-file selections or the currently hovered file.
- Fast key menu (`ya.which`) for popular conversion targets:
  - **Images**: WebP, JPEG, PNG, AVIF, Remove Background
  - **Documents**: PDF, OCR to Markdown (`.md`), OCR to Text (`.txt`)
  - **Media**: MP3 (extract audio), MP4 (video compression)
  - **Archives**: ZIP, Extract
  - **Custom Format**: Prompt for any custom extension or preset supported by Filee
  - **GUI**: Open directly in the Filee Donut
- Non-blocking asynchronous conversion.
- Terminal notifications via Yazi's `ya.notify` and desktop notifications via `notify-send`.

## Installation

1. Copy or symlink this folder to your Yazi plugins directory:

   ```sh
   mkdir -p ~/.config/yazi/plugins
   ln -s "${XDG_DATA_HOME:-$HOME/.local/share}/filee/application/yazi/filee.yazi" ~/.config/yazi/plugins/filee.yazi
   ```

2. Add a keybinding in `~/.config/yazi/keymap.toml`:

   ```toml
   [[mgr.prepend_keymap]]
   on   = [ "c", "f" ]
   run  = "plugin filee"
   desc = "Convert with Filee"
   ```

## Requirements

- Yazi 25.5 or newer (for Yazi 0.3.x or older, use `[[manager.prepend_keymap]]` instead of `[[mgr.prepend_keymap]]`).
- `filee` CLI on `$PATH` (installed automatically to `~/.local/bin/filee` by `install.sh`).
- Optional: `notify-send` for desktop notifications.
