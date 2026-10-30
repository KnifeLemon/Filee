<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee: Drag. Drop. Converted." width="100%"></a>
</p>

<p align="center">
  <b>Un convertitore di file gratuito e open source che si usa trascinando.</b><br>
  Tieni premuto un tasto (<kbd>Ctrl</kbd>, <kbd>Option</kbd> su macOS), trascina i file e rilasciali su un formato nel menu a donut che si apre sul cursore.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Sito web</b></a> ·
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><b>Download</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#installazione">Installazione</a> ·
  <a href="#riga-di-comando">Riga di comando</a> ·
  <a href="#sviluppo">Sviluppo</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/KnifeLemon/Filee?color=7F77DD&label=release" alt="Latest release"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases"><img src="https://img.shields.io/github/downloads/KnifeLemon/Filee/total?color=1D9E75" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS preview">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT license"></a>
</p>

<p align="center">
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/weekly?language=C%23&v=2" alt="Filee: #1 C# repository of the week on Trendshift" width="250" height="55"></a>
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/daily?language=C%23&v=2" alt="Filee: #1 C# repository of the day on Trendshift" width="250" height="55"></a>
  <a href="https://hellogithub.com/repository/KnifeLemon/Filee" target="_blank"><img src="https://abroad.hellogithub.com/v1/widgets/recommend.svg?rid=0847caedd5bd4077a70b90f83afdf83c&claim_uid=jAlIvVMhn0kcEsU" alt="Featured｜HelloGitHub" width="250" height="54" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.zh-CN.md">简体中文</a> ·
  <a href="README.zh-TW.md">繁體中文</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.pt-PT.md">Português (Portugal)</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.th.md">ไทย</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <b>Italiano</b>
</p>

<table>
  <tr>
    <td width="33%" valign="top">
      <b>Proprio dove sta il cursore</b><br>
      Nessuna finestra dell'app da aprire.
      I formati compaiono dove ti trovi già, solo quelli che hanno senso per i file che trascini.
    </td>
    <td width="33%" valign="top">
      <b>270 formati, nessun altro software</b><br>
      Immagini, PDF, Word, Excel, PowerPoint, HWP, e-book, archivi, font, CAD, modelli 3D e dati geospaziali funzionano senza Office, Hancom Office o LibreOffice.
    </td>
    <td width="33%" valign="top">
      <b>Resta sul tuo computer</b><br>
      Niente viene caricato online.
      Filee funziona offline e salva il risultato accanto all'originale.
    </td>
  </tr>
</table>

## Guardalo in azione

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="Dragging trip.jpg with Ctrl held: a donut of formats opens at the cursor, the file is dropped on PNG, a progress ring completes and trip.png appears" width="820"></a><br>
  <sub>Guarda il video di 27 secondi e prova la demo interattiva su <a href="https://filee.sh">filee.sh</a></sub>
</p>

## Installazione

Scarica da [Releases](https://github.com/KnifeLemon/Filee/releases/latest).

<a href="https://apps.microsoft.com/detail/XPDCN05L89WS0C?referrer=appbadge&cid=github&mode=full"><img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Microsoft Store" width="200"></a>

- **Windows 10/11**: esegui `Filee-<versione>-win-Setup.exe`, oppure decomprimi `win-Portable.zip` ed esegui `Filee.exe`.
  - Setup è l'installer proprio di Filee, nelle lingue di Filee, e non richiede altro sul PC.
  - Può aggiungere la voce nel menu di Esplora risorse, l'avvio all'accesso, il comando `filee` ed engine opzionali.
  - Le installazioni silenziose accettano gli switch di Inno Setup (`/VERYSILENT /SUPPRESSMSGBOXES`).
- **macOS 13+** (anteprima): estrai il pacchetto `osx-arm64` o `osx-x64` ed esegui `./install.sh`.
  - Oppure apri il `.dmg` e trascina Filee in Applicazioni (il comando `filee` arriva con `install.sh`).
  - La build non è ancora notarizzata, quindi consenti il primo avvio in Impostazioni di Sistema → Privacy e sicurezza → **Apri comunque**.
- **Linux x64/ARM64** (Ubuntu 22.04+): estrai il pacchetto ed esegui `./install.sh` (senza root, serve Python 3).
  - I gesti di trascinamento richiedono X11.
    Su Wayland, usa la zona di rilascio e il menu contestuale del file manager.

Filee mostra le nuove versioni nel suo menu e si aggiorna da solo, mantenendo impostazioni ed engine.
Dopo un aggiornamento, alcune schermate mostrano le novità.

**Windows: Filee non si avvia (nessuna finestra, nessun errore)?**
Succede su Windows 10 non aggiornato da molto tempo, su un PC il cui processore supporta la protezione dello stack applicata via hardware (CET).
Windows attiva questa protezione ma manca una parte richiesta da .NET, quindi Filee e Setup si fermano subito.

- **Consigliato:** installa gli aggiornamenti Windows disponibili (l'ultimo aggiornamento cumulativo).
- **Se non è possibile aggiornare:** disattivare la protezione solo per Filee di solito funziona.
  Esegui questo in PowerShell come amministratore:

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

Fai lo stesso con il nome del file di Setup (es. `Filee-<versione>-win-Setup.exe`), oppure usa lo ZIP portabile, che richiede solo queste due righe.
Per annullare, rimuovi le voci in Sicurezza di Windows → Controllo app e browser → Impostazioni di protezione dagli exploit → Impostazioni programma.

## Funzionalità

- **Una donut al cursore**: tieni premuto un tasto, trascina i file e rilasciali su un formato.
  Compaiono solo i formati compatibili ai file.
- **Direttamente dal browser**: trascina un'immagine, un link a un file o un download fuori da Chrome, Edge, Firefox o Safari allo stesso modo.
  - Filee lo salva nella cartella Download (oppure mantiene solo il file convertito) e lo converte.
  - I link vengono scaricati senza l'accesso del tuo browser.
- **Una barra degli strumenti per ogni tipo di file**, organizzata trascinando, con preset per:
  - qualità, dimensione, DPI
  - pagine PDF e compressione
  - video e audio
  - cartella di destinazione e nomi dei file
- **Due facce per ogni donut**: premi <kbd>Tab</kbd> o gira la rotella del mouse mentre è aperta per passare ai formati e strumenti meno comuni.
- **Ritaglio prima della conversione**: la sezione Ritaglia apre le immagini una per volta.
  Trascina il riquadro, scegli un rapporto, e solo quella parte viene salvata.
- **Molti file insieme**, in parallelo.
  I percorsi con più passaggi (HWP → HWPX → DOCX) vengono trovati automaticamente.
- **Le cartelle monitorate** convertono ogni file che vi arriva.
- **Conversioni recenti** nella Home, ognuna con anteprima: apri il risultato, oppure aprilo con un'altra app.
- **Altri modi per avviarla**: il menu contestuale, una scorciatoia da tastiera, la zona di rilascio e il [comando `filee`](#riga-di-comando).
- Temi chiaro e scuro con colore d'accento e opacità della donut personalizzabili, in inglese, Español, 日本語, 한국어, 简体中文, 繁體中文, Polski, हिन्दी, Русский, العربية (da destra a sinistra), Português (Brasil), Français, नेपाली, ไทย, Türkçe, Português (Portugal), Deutsch, Italiano e Bahasa Indonesia.

## Screenshot

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Donut toolbar editor with profiles and presets"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Home screen with the drop zone and recent conversions"></td>
  </tr>
  <tr>
    <td align="center"><sub>Organizza la donut trascinando, un profilo per ogni tipo di file</sub></td>
    <td align="center"><sub>Home: zona di rilascio e conversioni recenti</sub></td>
  </tr>
</table>

## Formati

| Da | A |
|---|---|
| **Immagini**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …;<br>GIMP XCF, RAW fotocamera (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR, FITS, JPEG XR (Windows), lettura Live Photo iPhone (LIVP) | uno qualsiasi di questi (HEIC⁵), PDF, SVG (vettorializzato), ridimensionato, scala di grigi;<br>Live/Motion Photo (iPhone, Android) → il loro video (MP4, MOV⁵), e un video → Live Photo (iPhone) o Motion Photo (Android)⁵, sperimentale |
| **Vettoriali**: SVG, SVGZ, EMF, WMF, AI<br>EPS, PS¹<br>CDR, VSD, CGM, ODG, FODG² | PDF (vettoriale), PNG e altre immagini,<br>SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, PPTX (un'immagine per ogni pagina), SVG²;<br>unione (nell'ordine che scegli prima), divisione, intervalli di pagine, compressione (PDF più piccoli) |
| **Documenti**: DOCX, DOCM, DOTX, EML, Outlook MSG;<br>lettura OFD (fatture elettroniche cinesi, documenti ufficiali)<br>DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML;<br>MSG → EML<br>ODT, FODT, OTT² |
| **Fogli di calcolo**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV<br>OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML<br>XLT, OTS² |
| **Presentazioni**: PPTX, PPTM, POTX, POTM, PPSX<br>PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX<br>OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Testo**: TXT, LOG, codice sorgente di script (PY, PAC, BAT, COMMAND; solo lettura, mai eseguito), Markdown, HTML;<br>lettura MHTML (pagine web salvate)<br>reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | TXT UTF-8, PDF, DOCX, HWPX, EPUB<br>ciascuno negli altri, slide PPTX³ |
| **E-book**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ<br>LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX<br>MOBI, AZW3⁴ |
| **Video**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, Video CD (DAT), TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p,<br>sottotitoli (SRT, ASS, WebVTT) |
| **Audio**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Sottotitoli**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | ciascuno negli altri in UTF-8 (i file coreani in CP949 vengono letti correttamente) |
| **Archivi**: ZIP, ANSYS Workbench (WBPZ), 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | estrazione, ZIP, 7Z, TAR, TAR.GZ;<br>più file → un unico ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Font**: TTF, OTF, WOFF, WOFF2, EOT;<br>lettura FON (font bitmap di Windows) | ciascuno negli altri;<br>FON → un font per ogni dimensione |
| **Dati**: JSON, JSON Lines, YAML, TOML, XML<br>vCard, iCalendar | ciascuno negli altri, CSV e XLSX (tabelle);<br>vCard, iCalendar ↔ CSV |
| **Modelli 3D**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY;<br>lettura FBX | ciascuno negli altri, anteprima PNG |
| **Vettori geospaziali**: Shapefile, GeoJSON, KML, KMZ, GPX<br>CSV con coordinate; lettura Garmin FIT | ciascuno negli altri e CSV / XLSX / JSON;<br>i dati proiettati (UTM, Web Mercator, Lambert, Albers) escono come longitudine/latitudine WGS 84 |

I formati non contrassegnati funzionano subito, senza Office, Hancom Office o LibreOffice.
Quelli contrassegnati richiedono un engine opzionale, scaricato solo quando serve: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre, ⁵ FFmpeg ([dettagli](docs/ENGINES.md)).

## Riga di comando

Il comando `filee` usa gli stessi engine e preset dell'app (Setup o `install.sh` lo aggiunge).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Tutti i comandi e le opzioni: [filee.sh/cli](https://filee.sh/cli/) oppure `filee help`.

## Sviluppo

Compila con .NET 10 SDK su Windows, macOS o Linux:

```bash
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # engine opzionali
dotnet run --project src/Filee.App
dotnet test
```

[Architettura](docs/ARCHITECTURE.md) · [Engine](docs/ENGINES.md) · [Aggiungere un convertitore](docs/ADDING-A-CONVERTER.md) · [Aggiungere una lingua](docs/ADDING-A-LANGUAGE.md) · [Contribuire](CONTRIBUTING.md)

Le pull request sono benvenute.
Se Filee ti fa risparmiare qualche click, una ⭐ aiuta altre persone a trovarlo.

## Politica di firma del codice

Filee è sviluppato e pubblicato da **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

- Le release Windows sono firmate con un certificato open source di code signing [Certum](https://www.certum.eu/) rilasciato a Myeonggu Kim ("Open Source Developer MYEONGGU KIM").
  - Le release precedenti, fino alla 2.0.0, non sono firmate.
  - SmartScreen può comunque avvisare su una nuova release finché non è stata scaricata abbastanza volte ("Altre informazioni" → "Esegui comunque").
- I pacchetti macOS sono firmati ad hoc e non notarizzati.
- Solo i file compilati dal workflow di release di GitHub Actions di questo repository sono firmati, e ogni release è firmata e approvata manualmente.
  - Le librerie e i programmi firmati dai rispettivi autori (.NET, SkiaSharp, ImageMagick, il runtime C++ di Microsoft …) mantengono la propria firma; quelli inclusi che arrivano non firmati (rhwp, 7-Zip e la maggior parte delle librerie) vengono firmati con il certificato di Filee, come parte di Filee.

### Informativa sulla privacy

Filee converte i file sul tuo computer e non li carica mai online.
Non raccoglie dati di utilizzo e non invia telemetria.
Si connette a Internet solo:

- per chiedere a GitHub (`api.github.com`) se esiste una versione più recente, poco dopo l'avvio e poi ogni poche ore.
  Disattivabile in Impostazioni → Informazioni ("Controlla automaticamente gli aggiornamenti").
- per scaricare gli engine di conversione che scegli di installare, dalle posizioni ufficiali indicate in [`engines.json`](src/Filee.Engines/Infrastructure/engines.json), e le lingue per il riconoscimento del testo (OCR) usate da un preset, indicate in [`ocr-languages.json`](src/Filee.Engines/Infrastructure/ocr-languages.json).
- quando clicchi un link, che apre il tuo browser.

## Sostenitori

Filee è gratuito e resterà gratuito.
Se vuoi sostenerlo, scegli il modo che preferisci ([Fairy](https://fairy.hada.io/@filee) accetta carte coreane e easy-pay).

**Il tuo nome o logo qui.**
I sostenitori mensili sono elencati qui per nome, e gli sponsor ottengono il proprio logo qui e nella pagina Informazioni di Filee.
Anche chi fa una donazione una tantum può chiedere di essere elencato.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## Licenza

MIT, © 2026 Myeonggu Kim (KnifeLemon) e i contributori di Filee.
I componenti di terze parti inclusi mantengono le proprie licenze, elencate in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#KnifeLemon/Filee&Date"><img src="https://api.star-history.com/svg?repos=KnifeLemon/Filee&type=Date" alt="Star history" width="600"></a>
</p>
