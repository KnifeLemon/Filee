# The logo and the icons

The logo is made by hand. Nothing generates it, so a change is made in these files and checked by eye.

| File | What it is | Used for |
|---|---|---|
| `src/Filee.App/Assets/Icons/filee.svg` | **The source artwork.** Start here. | Source for the other two files |
| `src/Filee.App/Assets/Icons/filee.png` | 512 px export of the SVG | About page, main window header, What's New header, dialogs, the Linux installer icon, and the macOS app icon (`build/build-portable.ps1` scales it into the `.icns`) |
| `src/Filee.App/Assets/Icons/filee.ico` | Several sizes in one file, see below | Window, taskbar and tray icons, and the `.exe` icons of Filee, Setup and the CLI |

The folder `src/Filee.App/Assets` is embedded into the app, so keep other files (notes, working files) out of it.

## The `.ico` frames

`filee.ico` must contain **256, 64, 48, 32, 24 and 16 px** (128 px is welcome too). Windows picks the frame that fits the
size it needs (16, 24, 32 and 48 px at 100 to 200 % display scaling) and shrinks a bigger one when a size is missing,
which blurs it. The 24 px frame is the one used at 150 % scaling (title bar, Explorer's details view, the Start menu
list). The 16, 24 and 32 px frames are drawn by hand, pixel by pixel, so the rounded corners stay crisp; they are not
downscaled from the big one.

Because nothing else keeps them, the hand-drawn frames live **inside the `.ico`**.

With [ImageMagick](https://imagemagick.org) (`magick`):

```powershell
magick filee.ico frame-%d.png                                  # take the frames out to edit them
magick 256.png 64.png 48.png 32.png 24.png 16.png filee.ico    # put the frames back, biggest first
magick identify filee.ico                                      # lists the frames: check all six sizes are there
```

## The logo drawn in code

The animated logo in the Setup window (Welcome, Progress and Uninstall screens) is not an image.
`src/Filee.Setup/Controls/DonutLogo.cs` draws it from the SVG's path data and colours, so it can assemble itself. When
`filee.svg` changes, update the paths and colours in `DonutLogo.cs` to match, or Setup shows the old logo.