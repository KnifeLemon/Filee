---
name: filee
description: Convert files offline with the Filee command-line tool (images, documents, PDF, audio, video, ebooks). Use when the user asks to convert a file to another format, check whether Filee can convert a format, or read Filee's JSON result. Not for editing file contents.
---

# Filee

Filee converts files offline. The `filee` command shares formats, presets and engines with the Filee app.

The command is `filee` on every OS. If the shell can't find it, Filee may be installed but not on PATH. On Linux and macOS the installer links it as `$HOME/.local/bin/filee`, so that folder has to be on PATH. On Windows, run `cli\filee.cmd` (or `filee-cli.exe`) from the Filee folder by its full path.

## 1. Check that Filee is installed

```sh
filee version
```

It prints something like `filee 2.2.0` and exits with 0. If the command doesn't exist and you can't find the install, tell the user to download Filee from https://filee.sh and stop.

## 2. Check the conversion first

Ask Filee what a format can become before you convert:

```sh
filee formats md --json
```

Look at two lists in the result:

- `targets`: formats Filee can make right now.
- `withEngines`: formats Filee can make once the user gets an extra engine in the app.

If the target is in neither list, Filee can't do this conversion. Say so instead of trying.

## 3. Convert

Always pass `--json` and `-o <folder>`:

```sh
filee convert <files or folders...> --to <format> -o <folder> --json
```

Why `-o`: results land in one folder you chose instead of wherever the app's output settings put them (usually next to the originals), so you can find them.

Other useful flags: `--preset <preset>` instead of `--to` (see `filee presets --json`), `-r` for sub folders, `--skip` to leave existing outputs alone, `--overwrite` to replace them. By default Filee adds a number rather than overwriting.

## 4. Read the result

Exit code 0 doesn't mean every file was converted. It means no file failed or was cancelled. Always read the JSON on stdout.

| Exit code | Meaning | What to do |
|---|---|---|
| 0 | No file failed or was cancelled | Check `converted` and each file's `state`. A `skipped` file was not converted. |
| 1 | Some files failed or were cancelled | Read `error` (and `needsEngines`, if any) on each file whose `state` is `failed` or `cancelled`. |
| 2 | Wrong arguments or a missing input | Fix the command. No JSON; the reason is on stderr. |
| 3 | Nothing to convert | Check the input paths. No JSON; the reason is on stderr. |

In the JSON:

- `converted`, `skipped`, `failed`: counts for the whole run. `failed` also counts cancelled files.
- `files[].state`: `done`, `skipped`, `failed` or `cancelled` for each source.
- `files[].outputs`: the paths that were written. Use these exactly. Don't guess output names.

A file is converted only when its `state` is `done` and `outputs` isn't empty.

## 5. When a file fails

- If the failed file has `needsEngines`, Filee is missing an engine for this conversion. Tell the user which engine it names and ask them to open Filee, go to Settings → Engines, and get it. Then run the same command again.
- If the file's `state` is `cancelled`, the run was stopped before it finished. Tell the user it wasn't converted.
- Otherwise pass the file's `error` text on to the user. Don't retry blindly.

## Examples

### Convert one file

```sh
filee convert photo.png --to jpg -o converted --json
```

Output, trimmed to the fields that matter here:

```json
{ "converted": 1, "failed": 0,
  "files": [ { "source": ".../photo.png", "state": "done",
               "outputs": [ ".../converted/photo.jpg" ] } ] }
```

Exit code 0, `state` is `done`, and the new file is the path in `outputs`.

### A conversion that needs an engine

```sh
filee convert notes.md --to pptx -o converted --json
```

Output, trimmed:

```json
{ "converted": 0, "failed": 1,
  "files": [ { "state": "failed", "outputs": [],
               "errorKey": "error.no_route", "needsEngines": [ "pandoc" ] } ] }
```

Exit code 1. Tell the user something like: "Filee needs the pandoc engine for this. Open Filee, go to Settings → Engines, get it, then I'll try again."
