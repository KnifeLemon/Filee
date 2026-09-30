<p align="center">
  <a href="https://filee.sh/zh-cn/"><img src="docs/media/banner-zh-CN.jpg" alt="Filee：拖一下，放一下，转好了。" width="100%"></a>
</p>

<p align="center">
  <b>用拖放操作的免费开源 Windows 文件格式转换器。</b><br>
  按住 <kbd>Ctrl</kbd> 拖动文件，光标处会弹出格式圆环，放到想要的格式上即可。
</p>

<p align="center">
  <a href="https://filee.sh/zh-cn/"><b>官网</b></a> ·
  <a href="https://github.com/KnifeLemon/Filee/releases/latest/download/Filee-win-Setup.exe"><b>下载</b></a> ·
  <a href="#快速开始">快速开始</a> ·
  <a href="#文档">文档</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/KnifeLemon/Filee?color=7F77DD&label=release" alt="最新版本"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases"><img src="https://img.shields.io/github/downloads/KnifeLemon/Filee/total?color=1D9E75" alt="下载次数"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT 许可证"></a>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.ko.md">한국어</a> · <b>简体中文</b></p>

<table>
  <tr>
    <td width="33%" valign="top"><b>就在光标旁边</b><br>无需打开程序窗口。只显示适合当前拖动文件的格式，就在你的光标处。</td>
    <td width="33%" valign="top"><b>无需韩文办公软件处理 HWP/HWPX</b><br>内置 HWPX 写入器保留 Word 文档的版式；rhwp 负责 HWP ↔ HWPX、PDF 和 PNG。</td>
    <td width="33%" valign="top"><b>只在你的电脑上</b><br>不上传任何内容。离线也能使用，结果保存在原文件旁边。</td>
  </tr>
</table>

## 实际效果

<p align="center">
  <a href="https://filee.sh/zh-cn/"><img src="docs/media/demo-zh-CN.webp" alt="按住 Ctrl 拖动 旅行.jpg，光标处弹出格式圆环，放到 PNG 上，进度环走完后生成 旅行.png" width="820"></a><br>
  <sub>27 秒视频和可交互演示请访问 <a href="https://filee.sh/zh-cn/">filee.sh</a></sub>
</p>

## 功能

- **光标处的格式圆环。** 按住修饰键拖动文件（默认 <kbd>Ctrl</kbd>）。修饰键组合、鼠标按键、拖动距离、长按手势，
  以及对选中文件使用的快捷键都可以自定义。
- **按文件类型的工具栏。** 图片、PDF、文档、电子表格、演示文稿、HWP/HWPX、文本，以及用于混合文件的通用配置。
  直接在圆环上拖放来添加、移除和排序预设；右键或 ✎ 编辑预设。
- **预设。** 质量、缩放、DPI、元数据、TIFF 压缩、ICO 尺寸、PDF 合并与拆分、页码范围、
  保存位置、文件名模板和重名处理。
- **批量转换。** 1 个或 100 个文件并行转换，某个文件失败也不影响其余文件。
- **自动规划路径。** 自动找到多步转换路径（例如 HWPX → PDF → PNG），每一步使用最擅长的引擎。
- **四种使用方式。** 拖动手势、资源管理器右键和“发送到”、对资源管理器中选中的文件按快捷键、主窗口拖放区。
- **柔和的动画界面。** 浅色、深色或跟随系统，自定义强调色和圆角，旧电脑可开启“减少动画”。
- **简体中文、English、한국어**，默认跟随 Windows 语言。

## 支持格式

| 源格式 | 目标格式 |
|---|---|
| **图片**：JPG、PNG、WEBP、TIFF、BMP、GIF、ICO、AVIF、JPEG XL、JPEG 2000、PSD/PSB、TGA、PPM；HEIC、GIMP XCF、相机 RAW、EMF/WMF（读取） | 任意图片格式、ICNS、PDF |
| **矢量图**：SVG、SVGZ、AI、EPS、PS；ICNS | PDF（保持矢量）、PNG 等图片、SVG ↔ SVGZ、PDF → EPS/PS |
| **PDF** | PNG、JPG、TIFF、合并、拆分、页码范围 |
| **文档**：DOCX、DOC、ODT、RTF | PDF、HWPX、互相转换、TXT、HTML |
| **电子表格**：XLSX、XLS、ODS、CSV | PDF、XLSX ↔ CSV、HWPX、HTML |
| **演示文稿**：PPTX、PPT、ODP | PDF、PNG、JPG、HWPX |
| **HWP**：HWP、HWPX | PDF、HWPX ↔ HWP、DOCX、TXT、Markdown、HTML |
| **文本**：TXT、Markdown、HTML | DOCX、HWPX、PDF |

DOCX、XLSX、CSV、PPTX → PDF，所有 HWP 转换和 Markdown 装好即用：无需 Microsoft Office、韩文办公软件或 LibreOffice，
Filee 也从不调用电脑上已安装的程序。较旧的格式（DOC、XLS、PPT、OpenDocument）以及保存为 DOCX / ODT 使用 LibreOffice，
HTML 与 Markdown ↔ Word 使用 Pandoc，EPS / PostScript 使用 Ghostscript，这些可选引擎会在首次启动时询问是否下载。
HWPX 的写入与验证方式见 [docs/ENGINES.md](docs/ENGINES.md#hwpx-writer)。

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

## 快速开始

**安装：** 下载 [`Filee-win-Setup.exe`](https://github.com/KnifeLemon/Filee/releases/latest/download/Filee-win-Setup.exe)
（Windows 10/11，64 位），或在 [Releases](https://github.com/KnifeLemon/Filee/releases) 页面选择其他版本。

- **安装包很小。** 图片、PDF、HWP/HWPX、Markdown、DOCX / XLSX / PPTX → PDF 以及 XLSX ↔ CSV 装好即用。
- **大型引擎按需下载。** 首次启动时会询问是否下载 LibreOffice（DOC、XLS、PPT 等旧版办公格式，约 420 MB）和 Pandoc（HTML 与 Markdown ↔ Word，约 42 MB）；之后可随时在“设置 → 转换引擎”中安装或删除。
- **有更新会提醒你。** 新版本发布后，Filee 会在菜单底部、通知和托盘菜单中提示，点击即可打开最新发布页面下载。
  运行新的安装程序即可更新，设置会保留。

macOS 版本正在计划中。

<details>
<summary><b>从源码构建</b></summary>

需要：.NET 10 SDK、Windows 10/11。

```bash
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
pwsh build/fetch-fonts.ps1                       # 可选：Noto Sans 字体
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # 可选：开发用引擎（全部下载请省略 -Only，约 475 MB）
dotnet run --project src/Filee.App
dotnet test
```

设置保存在 `%APPDATA%\Filee`。调试版本不会修改资源管理器右键菜单和开机自启动。

| 项目 | 说明 |
|---|---|
| `src/Filee.Core` | 格式、预设、工具栏配置、设置、路径规划、任务队列。不依赖 UI 和系统 API。 |
| `src/Filee.Engines` | 每个转换引擎一个类（`IConverter`），包括 HWPX 写入器。 |
| `src/Filee.Platform.Windows` | 资源管理器集成、右键菜单、开机自启动。 |
| `src/Filee.App` | Avalonia 界面：圆环工具栏、设置窗口、托盘、通知。 |
| `tests/*` | xUnit v3 测试，包括无头 UI 渲染。 |

</details>

## 文档

| 我想要… | 从这里开始 |
|---|---|
| 了解应用的整体结构 | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| 了解各引擎负责哪些转换及其许可证 | [docs/ENGINES.md](docs/ENGINES.md) |
| 添加新的转换 | [docs/ADDING-A-CONVERTER.md](docs/ADDING-A-CONVERTER.md) |
| 把 Filee 翻译成我的语言 | [docs/ADDING-A-LANGUAGE.md](docs/ADDING-A-LANGUAGE.md) |
| 提交 Pull Request | [CONTRIBUTING.md](CONTRIBUTING.md) |

## 参与贡献

欢迎提交 Pull Request。适合入门的任务：[添加转换器](docs/ADDING-A-CONVERTER.md)和[添加语言](docs/ADDING-A-LANGUAGE.md)。
如果 Filee 帮你省了时间，点个 ⭐ 能让更多人发现它。

## 许可证

MIT。随附的第三方组件遵循各自的许可证，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

<p align="center">
  <a href="https://star-history.com/#KnifeLemon/Filee&Date"><img src="https://api.star-history.com/svg?repos=KnifeLemon/Filee&type=Date" alt="Star history" width="600"></a>
</p>
