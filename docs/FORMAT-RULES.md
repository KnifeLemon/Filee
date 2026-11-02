# What Filee shows by default

Filee recognizes about 270 formats and ships many tools. These rules decide which of them a user sees by default,
so the everyday conversions stay easy to find as more arrive. They come from [#806](https://github.com/KnifeLemon/Filee/issues/806),
[#832](https://github.com/KnifeLemon/Filee/issues/832), [#844](https://github.com/KnifeLemon/Filee/issues/844) and
[discussion #862](https://github.com/KnifeLemon/Filee/discussions/862).

> **Status:** the fields and grades are in the code (`src/Filee.Core/Formats/FormatFields.cs`, tested by
> `FormatFieldsTests`). The field switches and the new default donuts are #806's work and are not on screen yet.
> Rules 3–6 describe how they behave once they are.

## Formats

### 1. Every format has fields and a grade

- **Fields** say who commonly makes the format in their normal work. A format can belong to several. By default it
  belongs to the field named after its category (`image`, `video`, `cad`, …); an `Overlaps` row replaces that, for
  example `dxf` → cad + vector, `srt` → subtitle + video + audio.
- **The grade** says how widely files in the format are made today: everyday, general, specialist or legacy. The
  criteria and tie-breaks are in [#844](https://github.com/KnifeLemon/Filee/issues/844). A grade hides nothing by
  itself; rule 5 uses it.
- `FormatFieldsTests` fails while a format has no grade, so no format comes in unclassified.

### 2. Which fields can be turned off

The 14 everyday fields can't be turned off: image, pdf, document, spreadsheet, presentation, hwp, text, video, audio,
ebook, archive, vector, subtitle and data.

These 12 can. Only **Developer** is on in a new install:

| Field | Id | On in a new install |
|---|---|---|
| CAD | `cad` | Off |
| 3D models | `model3d` | Off |
| Maps | `geo` | Off |
| Fonts | `font` | Off |
| Games | `game` | Off |
| Design & print | `design` | Off |
| Science & medical | `science` | Off |
| Developer | `developer` | **On** (TAR, GZ and YAML are common on Linux and macOS) |
| Technical writing & publishing | `writing` | Off |
| Video production & broadcast | `video-pro` | Off |
| Pro audio | `audio-pro` | Off |
| Old formats | `legacy` | Off |

### 3. What turning a field off hides

A format is hidden only when **every** field it belongs to can be turned off and is off. If any of its fields is on,
it shows.

A hidden format is left out of:

- the slots that convert into it, on any donut (`to-<format>`);
- the preset editor's target list and the other settings lists (toolbar editor palette, preset library).

Nothing else changes: Filee still recognizes the file, the presets themselves stay, and the CLI converts to every
format.

### 4. A dropped file's own fields stay visible on its donut

When a file from a turned-off field is dropped, its donut still opens, and the outputs in **that file's own fields**
are not hidden from the donut's slots. A font dropped on the font donut still offers font conversions, a map still
offers map formats. Without this, the default switches would leave the font donut with no conversion slot at all.

This exception is for the donut's slots only. The settings lists keep following the field switches.

### 5. Admitting new formats

| A new format that is | Comes in when |
|---|---|
| read only (input) | almost always: it adds recognition, not list entries or slots |
| an everyday or general output | as before |
| a specialist or legacy output | it belongs **only** to fields that can be turned off and are off by default; if it has no such field, the field is defined first |

This rule is not applied to formats Filee already had. Because Developer is on by default, a new output format that
only developers make can't come in for now; that is looked at again when such a request comes.

## Donuts

### 6. Front converts, back edits

- The front face holds format conversions.
- The back face holds tools that change the file itself: rotate, crop, compress and the like.
- A type with no editing tools keeps its less common conversions on the back.

### 7. New defaults reach new installs only

A changed default donut applies to new installs. Existing users' donuts are never changed by a migration: no slot is
moved, removed or added. A new tool reaches them through the toolbar editor's palette.

## Features

A feature (a built-in preset that isn't a plain `to-<format>` conversion: half size, rotate, merge images, extract all
frames, …) has a **named operation**. Some inputs get that operation; others only pass through as an ordinary
conversion to the feature's target format. For example, *Extract frames* saves every frame of a video, but on a still
image it is a plain PNG or JPG conversion.

### 8. Don't block inputs that pass as an ordinary conversion

- Such inputs keep working, so existing toolbars and presets don't stop working.
- Where the result differs a lot from what the name says, the user is told so, for example in the slot's tooltip:
  *Extract frames* (`extract-frames-png`, `extract-frames-jpg`) and *Extract Live Photo video* (`extract-motion`).
- An input is blocked only where the result would mislead. That is decided per feature, not by one rule for all.

### 9. When inputs grow, recheck the features

When a format or engine adds inputs, check which existing features now reach them and whether their default
placement still fits. Being able to run on a donut's files doesn't by itself put a feature on that donut.

A test is planned that runs the availability checks (`PresetAvailability`) over every default slot × the inputs of
its donut, so these differences show up in CI. An explicit "intended inputs" declaration is added only for the
features where that test keeps showing a misleading gap.
