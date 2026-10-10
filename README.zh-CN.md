<p align="center">
  <a href="https://filee.sh/zh-cn/"><img src="docs/media/banner-zh-CN.jpg" alt="Filee：拖一下，放一下，转好了。" width="100%"></a>
</p>

<p align="center">
  <b>用拖放操作的免费开源文件格式转换器。</b><br>
  按住一个键（<kbd>Ctrl</kbd>，macOS 为 <kbd>Option</kbd>）拖动文件，光标处会弹出格式圆环，放到想要的格式上即可。
</p>

<p align="center">
  <a href="https://filee.sh/zh-cn/"><b>官网</b></a> ·
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><b>下载</b></a> ·
  <a href="#安装">安装</a> ·
  <a href="#命令行">命令行</a> ·
  <a href="#文档">文档</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/KnifeLemon/Filee?color=7F77DD&label=release" alt="最新版本"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases"><img src="https://img.shields.io/github/downloads/KnifeLemon/Filee/total?color=1D9E75" alt="下载次数"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/macOS%20%7C%20Linux-preview-534AB7" alt="macOS | Linux 预览版">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT 许可证"></a>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.ko.md">한국어</a> · <b>简体中文</b></p>

<table>
  <tr>
    <td width="33%" valign="top"><b>就在光标旁</b><br>无需打开应用窗口。只显示适合当前拖动文件的格式，就在你所在的位置。</td>
    <td width="33%" valign="top"><b>180 种格式，无需其他软件</b><br>图片、PDF、Word、Excel、PowerPoint、HWP、电子书、压缩包、字体和 CAD，无需 Office、韩文办公软件或 LibreOffice。</td>
    <td width="33%" valign="top"><b>只在你的电脑上</b><br>不上传任何文件。Filee 离线工作，结果保存在原文件旁边。</td>
  </tr>
</table>

## 实际效果

<p align="center">
  <a href="https://filee.sh/zh-cn/"><img src="docs/media/demo-zh-CN.webp" alt="按住 Ctrl 拖动 旅行.jpg，光标处弹出格式圆环，放到 PNG 上，进度环走完后生成 旅行.png" width="820"></a><br>
  <sub>27 秒视频和可交互演示请访问 <a href="https://filee.sh/zh-cn/">filee.sh</a></sub>
</p>

## 安装

在 [Releases](https://github.com/KnifeLemon/Filee/releases/latest) 下载。

### Windows 10/11（64 位）

运行 `Filee-<版本>-win-Setup.exe`。安装程序会请求一次管理员权限，为所有用户安装。安装时可以选择资源管理器右键菜单、
登录时启动、`filee` 命令和可选引擎（视频/音频用的 FFmpeg 约 100 MB，少见格式用的 LibreOffice、calibre、Ghostscript、Pandoc）。

不想安装的话，把 `Filee-<版本>-win-Portable.zip` 解压后运行 `Filee.exe` 即可。

新版本发布后，菜单和托盘中会有提示。点击 **更新** 会下载安装程序，用发布页的 SHA-256 核对后安装，设置和引擎都会保留。
便携版则会打开发布页面。

### macOS 14 或更高版本（预览）

1. 下载 `Filee-<版本>-osx-arm64.tar.gz`（Apple 芯片）或 `Filee-<版本>-osx-x64.tar.gz`（Intel）。
2. 在解压后的文件夹中打开终端，运行 `./install.sh`。它会把 Filee.app 复制到 `~/Applications`，并把 `filee` 命令加入
   `~/.local/bin`。
3. 这个版本尚未经过公证（notarization），首次启动时 macOS 会阻止。请在 系统设置 → 隐私与安全性 中点 **仍要打开**。
4. Filee 会请求辅助功能权限，<kbd>Option</kbd>+拖动需要它。打开后立即生效。

在访达中右键点按文件会出现“用 Filee 转换”。FFmpeg 和 Ghostscript 用 Homebrew 安装（`brew install ffmpeg ghostscript`）后，
Filee 会自动找到。

### Linux x64 和 ARM64（预览）

1. 下载 `Filee-<版本>-linux-x64.tar.gz` 或 `Filee-<版本>-linux-arm64.tar.gz`。
2. 在解压后的文件夹中运行 `./install.sh`。它只为当前用户安装（无需 root），把 Filee 加入应用菜单，并把 `filee` 命令
   加入 `~/.local/bin`。需要 Python 3。

拖动手势需要 X11 会话（Ubuntu 在登录界面选择“Ubuntu on Xorg”）。Wayland 不允许应用监听全局输入，此时可以使用拖放区以及
Nautilus、Nemo、Thunar 或 Dolphin 的右键菜单。测试基准为 Ubuntu 24.04，所需系统库列在安装包中的 `INSTALL.txt` 里。

## 功能

- **光标处的格式圆环。** 按住修饰键（<kbd>Ctrl</kbd>，macOS 为 <kbd>Option</kbd>）拖动文件即可。修饰键、鼠标按键、拖动距离、
  长按手势和对选中文件使用的快捷键都可以自定义；不希望触发手势的应用可以从正在运行的应用中选择。
- **按文件类型的工具栏。** 图片、PDF、文档、表格、演示文稿、HWP、文本、电子书、视频、音频、矢量图、压缩包、CAD、字体，
  以及混合选择。直接在圆环上拖放排列预设，可以搜索配置和预设，以标签方式添加扩展名。
- **预设。** 质量、尺寸、DPI、元数据、PDF 合并/拆分/页码范围、视频画质与分辨率、音频码率/声道/WAV 格式、压缩级别、
  保存位置、文件名、重名处理和保留原文件日期。TIFF 预设还能把所有文件合并为一个多页 TIFF。
- **批量转换。** 1 个或 100 个文件并行转换，某个文件失败也不影响其余文件。"打包成一个 ZIP"可把任意文件压缩为一个压缩包，
  "合并 PDF"生成一个 PDF。
- **多步转换。** 自动找到需要多步的转换（HWP → HWPX → DOCX、EPUB → HWPX → PDF → PNG），每一步使用最擅长的引擎。
- **四种使用方式。** 拖动手势、文件管理器右键菜单（Windows 还有“发送到”）、对选中文件按快捷键、主窗口拖放区。
- **自动转换文件夹。** 放入指定文件夹的文件会在下载或复制完成后自动转换；原文件可以保留，也可以移走，让文件夹像收件箱一样使用。还可以只转换部分文件（以标签输入 `*.heic`、`scan_*` 或正则表达式），为转换后的文件设置单独的文件名规则，并用常用规则或自己写的正则表达式替换名称中的文字，实时预览结果。
- **引擎一目了然。** “设置 → 转换引擎”列出每个引擎的版本和可转换内容。电脑上已有的 FFmpeg、calibre、Pandoc、Ghostscript
  和 LibreOffice 会被自动找到并使用，不再另外下载。
- **浅色、深色或跟随系统**，自定义强调色，可开启“减少动画”。
- **简体中文、English、한국어**，默认跟随系统语言。

## 截图

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-zh-CN.png" alt="用配置文件和预设编辑圆环工具栏"></td>
    <td width="50%"><img src="docs/media/page-home-zh-CN.png" alt="带拖放区和最近转换记录的主页"></td>
  </tr>
  <tr>
    <td align="center"><sub>拖放排列圆环，每种文件类型一个配置</sub></td>
    <td align="center"><sub>主页：拖放区和最近转换记录</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/media/page-presets-zh-CN.png" alt="设置质量和输出选项的预设编辑器"></td>
    <td width="50%"><img src="docs/media/page-toolbar-dark.png" alt="深色主题下的圆环工具栏编辑器"></td>
  </tr>
  <tr>
    <td align="center"><sub>预设：质量、尺寸、PDF 页码、保存位置和文件名</sub></td>
    <td align="center"><sub>深色主题</sub></td>
  </tr>
</table>

## 支持格式

| 源格式 | 目标格式 |
|---|---|
| **图片**：JPG、PNG、WEBP、AVIF、TIFF、BMP、GIF、ICO、ICNS、JPEG XL、JPEG 2000、PSD/PSB、TGA、PPM；HEIC、GIMP XCF、相机 RAW（CR2、CR3、NEF、ARW、DNG 等）仅读取 | 上述格式互转、PDF、缩放、灰度 |
| **矢量图**：SVG、SVGZ、EMF、WMF、AI · EPS、PS¹ · CDR、VSD、CGM、ODG² | PDF（矢量）、PNG 等图片、SVG ↔ SVGZ、PDF → EPS/PS¹ |
| **PDF** | DOCX、HWPX、TXT、PNG、JPG、TIFF、CBZ；合并、拆分、页码范围 |
| **文档**：DOCX、DOCM、DOTX、EML · DOC、ODT、RTF、WPS、WPD、Pages 等² | PDF、DOCX、HWPX、EPUB、TXT、HTML |
| **表格**：XLSX、XLSM、XLS、ODS、CSV、TSV · Numbers、ET² | PDF、XLSX、ODS、CSV、TSV、HWPX、HTML |
| **演示文稿**：PPTX、PPTM、POTX、PPSX · PPT、ODP、Keynote² | PDF、PNG、JPG、DOCX、HWPX |
| **HWP**：HWP、HWPX | PDF、PNG、DOCX、HWPX ↔ HWP、TXT、Markdown、HTML |
| **文本**：TXT、Markdown、HTML · reStructuredText、LaTeX³ | PDF、DOCX、HWPX、EPUB |
| **电子书**：EPUB、MOBI、AZW3、AZW、AZW4、FB2、CBZ、CBR、CB7、HTMLZ、TXTZ · LIT、LRF、CHM、PDB 等⁴ | PDF、EPUB、DOCX、TXT、HTML、HWPX · MOBI、AZW3⁴ |
| **视频**⁵：MP4、MOV、MKV、WEBM、AVI、WMV、FLV、MPEG、TS、M2TS、3GP、OGV、VOB 等 | MP4、WEBM、MOV、MKV、AVI、GIF、MP3、M4A、720p |
| **音频**⁵：MP3、M4A、AAC、WAV、FLAC、OGG、OPUS、WMA、AIFF、AMR 等 | MP3、M4A、WAV、FLAC、OGG、OPUS、AAC |
| **压缩包**：ZIP、7Z、RAR、TAR、TAR.GZ、TAR.BZ2、TAR.XZ、GZ、ISO、CAB、DMG、ALZ、EGG 等 | 解压、ZIP、7Z、TAR、TAR.GZ；多个文件 → 一个 ZIP |
| **CAD**：DWG、DXF | PDF、SVG、PNG、DWG ↔ DXF |
| **字体**：TTF、OTF、WOFF、WOFF2、EOT | 互相转换 |

未标注的格式装好即用，无需 Microsoft Office、韩文办公软件或 LibreOffice；即使装了 Office 或韩文办公软件，Filee 也不会使用。
标注的格式需要可选引擎，只在电脑上没有时才下载：¹ Ghostscript、² LibreOffice、³ Pandoc、⁴ calibre、⁵ FFmpeg。
各引擎保留和不支持的内容见 [docs/ENGINES.md](docs/ENGINES.md)。

## 命令行

`filee` 命令无需打开应用即可转换文件、监视文件夹，与应用共用引擎和预设。Windows 在安装时勾选 **将 "filee" 命令添加到
PATH**；macOS 和 Linux 由 `install.sh` 添加。

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee convert D:\Scans --recursive --preset to-pdf --json
filee watch D:\Inbox --to pdf --move-originals
filee formats heic
filee presets
```

| 命令 | 作用 |
|---|---|
| `filee convert` | 用 `--to <格式>` 或 `--preset <预设>` 转换文件、通配符（`*.heic`）和文件夹 |
| `filee watch` | 转换放入文件夹的文件，直到按下 <kbd>Ctrl</kbd>+<kbd>C</kbd> |
| `filee formats` | 列出所有格式，或某个格式可以转换成什么 |
| `filee presets` | 列出应用中保存的预设 |

加上 `--json` 会输出便于脚本和其他程序读取的 JSON。退出码：`0` 全部转换，`1` 部分失败，`2` 参数错误或文件不存在，
`3` 没有可转换的文件。全部选项见[命令行指南](https://filee.sh/zh-cn/cli/)和 `filee help`。

## 文档

| 我想要… | 从这里开始 |
|---|---|
| 使用命令行 | [filee.sh/zh-cn/cli](https://filee.sh/zh-cn/cli/) |
| 了解应用的整体结构 | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| 了解各引擎负责哪些转换及其许可证 | [docs/ENGINES.md](docs/ENGINES.md) |
| 添加新的转换 | [docs/ADDING-A-CONVERTER.md](docs/ADDING-A-CONVERTER.md) |
| 把 Filee 翻译成我的语言 | [docs/ADDING-A-LANGUAGE.md](docs/ADDING-A-LANGUAGE.md) |
| 提交 Pull Request | [CONTRIBUTING.md](CONTRIBUTING.md) |

## 从源码构建

需要：.NET 10 SDK，以及 Windows 10/11、macOS 14 以上或 Linux。

```bash
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
pwsh build/fetch-fonts.ps1                       # 可选：Noto Sans 字体
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # 可选：开发用引擎（全部下载请省略 -Only，约 475 MB）
dotnet run --project src/Filee.App
dotnet test
```

设置保存在 `%APPDATA%\Filee`（Windows）、`~/Library/Application Support/Filee`（macOS）或 `~/.config/Filee`（Linux）。
从源码构建的版本不会修改右键菜单和开机自启动。`pwsh build/build-installer.ps1` 生成 Windows 安装程序；在 Mac 或 Linux 上运行
`pwsh build/build-portable.ps1` 生成该系统的安装包。详见 [CONTRIBUTING.md](CONTRIBUTING.md#linux-and-macos-development)。

| 项目 | 说明 |
|---|---|
| `src/Filee.Core` | 格式、预设、工具栏配置、设置、路径规划、任务队列。不依赖 UI 和系统 API。 |
| `src/Filee.Engines` | 每个转换引擎一个类（`IConverter`），包括 HWPX 写入器。 |
| `src/Filee.Platform.Windows` | 资源管理器集成、右键菜单、开机自启动。 |
| `src/Filee.Platform.MacOS`、`src/Filee.Platform.Linux` | macOS 和 Linux 的访达服务、文件管理器菜单、手势和开机自启动。 |
| `src/Filee.App` | Avalonia 界面：圆环工具栏、设置窗口、托盘、通知。 |
| `src/Filee.Cli` | `filee` 命令行（convert、watch、formats、presets），与应用共用引擎和预设。 |
| `tests/*` | xUnit v3 测试，包括无头 UI 渲染。 |

## 参与贡献

欢迎提交 Pull Request。适合入门的任务：[添加转换器](docs/ADDING-A-CONVERTER.md)和[添加语言](docs/ADDING-A-LANGUAGE.md)。
如果 Filee 帮你省了时间，点个 ⭐ 能让更多人发现它。

## 代码签名政策

Filee 的代码签名政策（Code signing policy）和隐私政策见[英文 README](README.md#code-signing-policy)。Filee 不会上传文件，
也不会发送任何使用数据。

## 许可证

MIT。随附的第三方组件遵循各自的许可证，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

<p align="center">
  <a href="https://star-history.com/#KnifeLemon/Filee&Date"><img src="https://api.star-history.com/svg?repos=KnifeLemon/Filee&type=Date" alt="Star history" width="600"></a>
</p>
