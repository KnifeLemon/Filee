# Theme example sources and notices

The JSON files in this folder map these palettes to Filee's existing colour roles. They are optional
documentation examples. The light/dark mappings and foreground adjustments are Filee-specific adaptations.

| Example | Palette source (fixed revision) | License notice |
|---|---|---|
| [Catppuccin](catppuccin.json) | [catppuccin/palette `07d02aa1`](https://raw.githubusercontent.com/catppuccin/palette/07d02aa110ef9eb7e7427afca5c73ba9cf7f8ebd/palette.json) | [MIT](https://raw.githubusercontent.com/catppuccin/palette/07d02aa110ef9eb7e7427afca5c73ba9cf7f8ebd/LICENSE) |
| [Nord](nord.json) | [nordtheme/nord `1cef7160`](https://raw.githubusercontent.com/nordtheme/nord/1cef71605416a222e57225b544540ce0fcec18d4/src/nord.scss) | [MIT](https://raw.githubusercontent.com/nordtheme/nord/1cef71605416a222e57225b544540ce0fcec18d4/license) |
| [Dracula / Alucard Classic](dracula.json) | [dracula/draculatheme.com `88fbf0b3`](https://raw.githubusercontent.com/dracula/draculatheme.com/88fbf0b34e53d8f785546da3c2246b7edb73931e/content/spec.mdx) | [MIT](https://raw.githubusercontent.com/dracula/template/d36c038a7df2e0bd7567f539e7a5816df8145057/LICENSE) |
| [Classic Monokai (VS Code)](monokai.json) | [microsoft/vscode `a383e086`](https://raw.githubusercontent.com/microsoft/vscode/a383e08626828ceb9c32f45f4a2940070a2f27f1/extensions/theme-monokai/themes/monokai-color-theme.json) | [MIT](https://raw.githubusercontent.com/microsoft/vscode/a383e08626828ceb9c32f45f4a2940070a2f27f1/LICENSE.txt) |

Nord's surface roles follow its [colour guidelines](https://www.nordtheme.com/docs/colors-and-palettes/).
Dracula's values follow its specification; the MIT notice below is from its community-port template.
The Monokai example uses the classic palette from VS Code, without Monokai Pro assets.

## Foreground adjustments

The following secondary/status colours differ from the source palettes. Each is blended towards white
in the dark variant, or black in the light variant, in 1% steps until its contrast is at least 4.5:1
on all three example backgrounds (`page`, `surface`, `surfaceAlt`). Other colours are direct role mappings.
This covers these foreground/background pairs, not all controls, accent-derived colours or user edits.

| Example | Variant | Role | Source | Adapted |
|---|---|---|---|---|
| catppuccin | light | `danger` | `#D20F39` | `#C50E36` |
| catppuccin | light | `success` | `#40A02B` | `#2D701E` |
| catppuccin | light | `warning` | `#DF8E1D` | `#8A5812` |
| nord | light | `danger` | `#BF616A` | `#954C53` |
| nord | light | `success` | `#A3BE8C` | `#56654A` |
| nord | light | `warning` | `#EBCB8B` | `#6E5F41` |
| nord | dark | `danger` | `#BF616A` | `#DFB0B4` |
| nord | dark | `success` | `#A3BE8C` | `#ABC496` |
| dracula | light | `danger` | `#DE5735` | `#B2462A` |
| dracula | light | `success` | `#089108` | `#077A07` |
| dracula | light | `warning` | `#A39514` | `#746A0E` |
| dracula | dark | `textSecondary` | `#6272A4` | `#96A1C2` |
| dracula | dark | `danger` | `#DE5735` | `#E7866E` |
| dracula | dark | `success` | `#089108` | `#57B457` |
| dracula | dark | `warning` | `#A39514` | `#AFA333` |
| monokai | dark | `textSecondary` | `#90908A` | `#AEAEAA` |
| monokai | dark | `danger` | `#F92672` | `#FC8AB3` |

## License notices

### Catppuccin

```text
MIT License

Copyright (c) 2021 Catppuccin

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Nord

```text
MIT License (MIT)

Copyright (c) 2016-present Sven Greb <development@svengreb.de> (https://www.svengreb.de)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Dracula

```text
MIT License

Copyright (c) 2023 Dracula Theme

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### VS Code

```text
MIT License

Copyright (c) 2015 - present Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
