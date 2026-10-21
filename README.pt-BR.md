<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee: Arraste. Solte. Convertido." width="100%"></a>
</p>

<p align="center">
  <b>Um conversor de arquivos gratuito e de código aberto, usado arrastando.</b><br>
  Segure uma tecla (<kbd>Ctrl</kbd>, <kbd>Option</kbd> no macOS), arraste os arquivos e solte num formato do menu circular que abre no cursor.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Site</b></a> ·
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><b>Download</b></a> ·
  <a href="#instalação">Instalação</a> ·
  <a href="#linha-de-comando">Linha de comando</a> ·
  <a href="#desenvolvimento">Desenvolvimento</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/KnifeLemon/Filee?color=7F77DD&label=release" alt="Versão mais recente"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases"><img src="https://img.shields.io/github/downloads/KnifeLemon/Filee/total?color=1D9E75" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS prévia">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="Licença MIT"></a>
</p>

<p align="center">
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/weekly?language=C%23&v=2" alt="Filee: #1 C# repository of the week on Trendshift" width="250" height="55"></a>
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/daily?language=C%23&v=2" alt="Filee: #1 C# repository of the day on Trendshift" width="250" height="55"></a>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.es.md">Español</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.pl.md">Polski</a> · <a href="README.hi.md">हिन्दी</a> · <a href="README.ru.md">Русский</a> · <a href="README.ar.md">العربية</a> · <b>Português (Brasil)</b></p>

<table>
  <tr>
    <td width="33%" valign="top"><b>Bem no cursor</b><br>Não há janela para abrir. Os formatos aparecem onde você já está, só os que fazem sentido para os arquivos que você arrasta.</td>
    <td width="33%" valign="top"><b>255 formatos, sem outro programa</b><br>Imagens, PDF, Word, Excel, PowerPoint, HWP, e-books, arquivos compactados, fontes, CAD, modelos 3D e dados geoespaciais funcionam sem Office, Hancom Office ou LibreOffice.</td>
    <td width="33%" valign="top"><b>Fica no seu computador</b><br>Nada é enviado. O Filee funciona offline e salva o resultado ao lado do original.</td>
  </tr>
</table>

## Veja em ação

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="Arrastando viagem.jpg com Ctrl pressionado: um menu circular de formatos abre no cursor, o arquivo é solto em PNG, um anel de progresso se completa e viagem.png aparece" width="820"></a><br>
  <sub>Assista ao vídeo de 27 segundos e experimente a demonstração interativa em <a href="https://filee.sh">filee.sh</a></sub>
</p>

## Instalação

Baixe em [Releases](https://github.com/KnifeLemon/Filee/releases/latest).

- **Windows 10/11**: execute `Filee-<version>-win-Setup.exe`, ou descompacte o `win-Portable.zip` e execute `Filee.exe`.
  A instalação é o instalador do próprio Filee, nos idiomas do Filee, e não precisa de mais nada no PC. Ela pode adicionar o menu do
  Explorador, iniciar com o Windows, o comando `filee` e motores opcionais; instalações silenciosas usam as opções do Inno Setup
  (`/VERYSILENT /SUPPRESSMSGBOXES`).
- **macOS 13+** (prévia): descompacte o pacote `osx-arm64` ou `osx-x64` e execute `./install.sh`.
  A build ainda não é notarizada, então permita a primeira abertura em Ajustes do Sistema → Privacidade e Segurança → **Abrir Mesmo Assim**.
- **Linux x64/ARM64** (Ubuntu 22.04+): descompacte o pacote e execute `./install.sh` (sem root, precisa do Python 3).
  Gestos de arrastar precisam do X11. No Wayland, use a área de soltar e o menu do botão direito do gerenciador de arquivos.

O Filee mostra versões novas no menu e se atualiza sozinho, mantendo suas configurações e motores. Depois de uma atualização, alguns
slides mostram o que há de novo.

## Recursos

- **Um menu circular no cursor**: segure uma tecla, arraste os arquivos e solte num formato. Só aparecem formatos que servem para os arquivos.
- **Direto do navegador**: arraste uma imagem, um link de arquivo ou um download do Chrome, Edge, Firefox ou Safari do mesmo
  jeito. O Filee salva na pasta Downloads (ou guarda só o arquivo convertido) e converte. Links são baixados
  sem o login do navegador.
- **Um menu por tipo de arquivo**, organizado arrastando e soltando, com predefinições de qualidade, tamanho, DPI, páginas e compressão de PDF,
  vídeo e áudio, pasta de saída e nomes de arquivo.
- **Dois lados em cada menu**: pressione <kbd>Tab</kbd> ou gire a roda do mouse com ele aberto para virar e ver formatos e ferramentas menos comuns.
- **Recortar antes de converter**: a fatia Recortar abre as imagens uma a uma. Arraste a moldura, escolha uma proporção, e só essa parte é salva.
- **Vários arquivos de uma vez**, em paralelo. Caminhos com várias etapas (HWP → HWPX → DOCX) são encontrados automaticamente.
- **Pastas monitoradas** convertem cada arquivo que chega nelas.
- **Conversões recentes** no Início, cada uma com uma miniatura: abra o resultado, ou abra com outro aplicativo.
- **Outras entradas**: o menu do botão direito, um atalho de teclado, a área de soltar e o [comando `filee`](#linha-de-comando).
- Temas claro e escuro, com sua cor de destaque e a opacidade do menu circular, em English, Español, 日本語, 한국어, 简体中文, Polski, हिन्दी, Русский, العربية (da direita para a esquerda) e Português (Brasil).

## Capturas de tela

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Editor do menu circular, com perfis e predefinições"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Tela inicial com a área de soltar e as conversões recentes"></td>
  </tr>
  <tr>
    <td align="center"><sub>Organize o menu arrastando e soltando, um perfil por tipo de arquivo</sub></td>
    <td align="center"><sub>Início: área de soltar e conversões recentes</sub></td>
  </tr>
</table>

## Formatos

| De | Para |
|---|---|
| **Imagens**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …; leitura de GIMP XCF, RAW de câmera (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR, JPEG XR (Windows), Live Photo do iPhone (LIVP) | qualquer um destes (HEIC⁵), PDF, SVG (vetorizado), redimensionado, tons de cinza; Live/Motion Photos (iPhone, Android) → o vídeo delas (MP4, MOV⁵), e um vídeo → Live Photo (iPhone) ou Motion Photo (Android)⁵, experimental |
| **Vetor**: SVG, SVGZ, EMF, WMF, AI · EPS, PS¹ · CDR, VSD, CGM, ODG, FODG² | PDF (vetorial), PNG e outras imagens, SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, PPTX (uma imagem de cada página), SVG²; mesclar (numa ordem que você escolhe antes), dividir, intervalos de páginas, comprimir (PDFs menores) |
| **Documentos**: DOCX, DOCM, DOTX, EML, MSG do Outlook; leitura de OFD (faturas eletrônicas e documentos oficiais chineses) · DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML; MSG → EML · ODT, FODT, OTT² |
| **Planilhas**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV · OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML · XLT, OTS² |
| **Apresentações**: PPTX, PPTM, POTX, POTM, PPSX · PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX · OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Texto**: TXT, LOG, código de script (PY, PAC, BAT, COMMAND; leitura, nunca execução), Markdown, HTML; leitura de MHTML (páginas da web salvas) · reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | TXT em UTF-8, PDF, DOCX, HWPX, EPUB · entre si, slides PPTX³ |
| **E-books**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ · LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX · MOBI, AZW3⁴ |
| **Vídeo**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, Video CD (DAT), TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p, legendas (SRT, ASS, WebVTT) |
| **Áudio**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Legendas**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | entre si, em UTF-8 (arquivos coreanos em CP949 são lidos corretamente) |
| **Arquivos compactados**: ZIP, ANSYS Workbench (WBPZ), 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | extrair, ZIP, 7Z, TAR, TAR.GZ; quaisquer arquivos → um ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Fontes**: TTF, OTF, WOFF, WOFF2, EOT | entre si |
| **Dados**: JSON, JSON Lines, YAML, TOML, XML · vCard | entre si, CSV e XLSX (tabelas); vCard ↔ CSV |
| **Modelos 3D**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY; leitura de FBX | entre si, prévia em PNG |
| **Vetores geoespaciais**: Shapefile, GeoJSON, KML, KMZ, GPX · CSV com coordenadas | entre si e CSV / XLSX / JSON; dados projetados (UTM, Web Mercator, Lambert, Albers) saem como longitude / latitude em WGS 84 |

Formatos sem marca funcionam na hora, sem Office, Hancom Office ou LibreOffice.
Os marcados precisam de um motor opcional, baixado só quando necessário: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre,
⁵ FFmpeg ([detalhes](docs/ENGINES.md)).

## Linha de comando

O comando `filee` usa os mesmos motores e predefinições do app (a instalação ou o `install.sh` o adiciona).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Todos os comandos e opções: [filee.sh/cli](https://filee.sh/cli/) ou `filee help`.

## Desenvolvimento

Compile com o .NET 10 SDK no Windows, macOS ou Linux:

```bash
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # motores opcionais
dotnet run --project src/Filee.App
dotnet test
```

[Arquitetura](docs/ARCHITECTURE.md) · [Motores](docs/ENGINES.md) · [Adicionar um conversor](docs/ADDING-A-CONVERTER.md) ·
[Adicionar um idioma](docs/ADDING-A-LANGUAGE.md) · [Como contribuir](CONTRIBUTING.md)

Pull requests são bem-vindos. Se o Filee poupar alguns cliques, uma ⭐ ajuda outras pessoas a encontrá-lo.

## Política de assinatura de código

O Filee é desenvolvido e publicado por **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

As versões para Windows serão assinadas com um certificado de assinatura de código de código aberto da [Certum](https://www.certum.eu/), emitido
para Myeonggu Kim. O certificado está sendo emitido; até lá, as versões não são assinadas e o SmartScreen do Windows
pode avisar ao executar o instalador (“Mais informações” → “Executar mesmo assim”). Os pacotes de macOS são assinados ad hoc e não são notarizados.

Só o instalador e os arquivos gerados pelo fluxo de release das GitHub Actions deste repositório são assinados, e cada release
é aprovada manualmente. Programas de terceiros incluídos (rhwp, 7-Zip) mantêm os arquivos dos próprios editores.

### Política de privacidade

O Filee converte arquivos no seu computador e nunca os envia. Não coleta dados de uso e não envia telemetria.
Ele se conecta à internet só:

- para perguntar ao GitHub (`api.github.com`) se existe uma versão mais nova, pouco depois de iniciar e depois a cada algumas horas.
  Desligue isso em Configurações → Sobre (“Procurar atualizações automaticamente”).
- para baixar os motores de conversão que você escolher instalar, dos locais oficiais fixados em
  [`engines.json`](src/Filee.Engines/Infrastructure/engines.json).
- quando você clica num link, o que abre o navegador.

## Patrocínio

O Filee é gratuito e continua gratuito. Se quiser apoiá-lo, escolha o jeito que preferir
([Fairy](https://fairy.hada.io/@filee) aceita cartões coreanos e pagamento fácil).

**Seu nome ou logotipo aqui.** Apoiadores mensais aparecem aqui pelo nome, e patrocinadores têm o logotipo aqui e na
página Sobre do Filee. Quem apoia uma única vez também pode pedir para aparecer.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## Licença

MIT, © 2026 Myeonggu Kim (KnifeLemon) e colaboradores do Filee. Componentes de terceiros incluídos mantêm as próprias licenças, listadas em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#KnifeLemon/Filee&Date"><img src="https://api.star-history.com/svg?repos=KnifeLemon/Filee&type=Date" alt="Histórico de estrelas" width="600"></a>
</p>
