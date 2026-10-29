# Custom themes

Filee's own colours can be replaced with a `theme.json` file in the folder that holds `settings.json`:

- Windows: `%APPDATA%\Filee\theme.json`
- Linux: `~/.config/Filee/theme.json`
- macOS: `~/Library/Application Support/Filee/theme.json`
- With `FILEE_DATA_DIR` set: `theme.json` in that folder

```jsonc
{
  "dark": {
    "page": "#101820",          // window background
    "surface": "#18222C",       // cards and donut slices
    "surfaceAlt": "#22303C",    // badges, small buttons, preview boxes
    "border": "#1CFFFFFF",      // card and control outlines
    "text": "#E8E4D8",
    "textSecondary": "#A8B0B8", // hints and captions
    "danger": "#F09595",
    "success": "#5DCAA5",
    "warning": "#EF9F27"
  },
  "light": {
    "page": "#F4F6F8"
  }
}
```

- `dark` and `light` are both optional, and so is every colour in them. Anything left out keeps Filee's colour.
- Colours are `#RRGGBB` or `#AARRGGBB`. Unknown names and other values are ignored and written to the log, and so is
  a file with no usable colour (for example `"Dark"` with a capital D) or one over 64 KB.
- The accent colour, corner radius and light/dark choice stay in **Settings > Theme**.
- Filee reads the file when it starts and whenever a setting is saved. Restart Filee, or change any setting, after
  editing it. Deleting the file brings the built-in colours back.

Keep `text` and `textSecondary` readable on `page`, `surface` and `surfaceAlt`: they are drawn on all three.

Keep `dark` colours dark and `light` colours light. In Filee's default Soft style, text boxes,
drop-downs, check boxes, menus and tooltips come from Avalonia's Fluent theme and keep its standard
light/dark look; they do not follow `theme.json`. Plain buttons use `surface` for their normal background
and `surfaceAlt` on hover. Under the Material You style,
text boxes, drop-downs and check boxes follow the dynamic tonal surface colors and corner radii
from `theme.json` and the accent colour. A white `page` in the `dark` variant would put white-on-white
controls on it.

## Ready-to-use examples

These examples adapt familiar palettes to Filee's surface, text and status colours. The accent colour stays the one
you chose in **Settings > Theme**, and the control-style limits above still apply.

| Example | Light | Dark |
|---|---|---|
| [Catppuccin](themes/catppuccin.json) | Latte | Mocha |
| [Nord](themes/nord.json) | Snow Storm-based colours | Polar Night-based colours |
| [Dracula](themes/dracula.json) | Alucard Classic | Dracula Classic |
| [Monokai](themes/monokai.json) | Filee's built-in light colours | Classic Monokai |

They are application-specific adaptations, not official ports. Some secondary text and status colours are adjusted
for readability on the example surfaces; see the [sources, adjustments and license notices](themes/NOTICE.md).
Accent-derived colours, focus and other control states still depend on your settings and style.

### Apply an example

1. Quit Filee, including its tray/menu-bar process.
2. Find your data folder using the paths at the top of this page. If `theme.json` already exists, save a copy under
   another name outside that folder before replacing it.
3. Open one of the JSON links above, choose **Raw**, and save the JSON as `theme.json` in your data folder. Make sure
   the file is named `theme.json`, rather than `theme.json.txt`.
4. Start Filee. Choose **Light**, **Dark** or **System** in **Settings > Theme** to use the corresponding variant.

Each example replaces the whole `theme.json` file. To keep parts of your own theme, merge the desired colour entries
into its existing `light` and `dark` objects instead. Monokai defines only `dark`: in Light mode, or when System
follows a light appearance, Filee uses its built-in light colours.

### Restore your previous colours

Quit Filee and move the example `theme.json` out of the data folder. To restore your previous custom theme, put your
backup back as `theme.json`; to use Filee's built-in colours, leave that file absent. Start Filee again. The accent
and your other appearance settings stay as chosen in **Settings > Theme**.
