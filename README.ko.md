<p align="center">
  <a href="https://filee.sh/ko/"><img src="docs/media/banner-ko.jpg" alt="Filee: 끌어서 놓으면, 변환 끝." width="100%"></a>
</p>

<p align="center">
  <b>드래그로 쓰는 무료 오픈소스 Windows 파일 변환기.</b><br>
  <kbd>Ctrl</kbd>을 누른 채 파일을 끌면 커서 위치에 도넛이 열립니다. 원하는 형식 위에 놓으면 끝.
</p>

<p align="center">
  <a href="https://filee.sh/ko/"><b>웹사이트</b></a> ·
  <a href="https://github.com/KnifeLemon/Filee/releases/latest/download/Filee-win-Setup.exe"><b>다운로드</b></a> ·
  <a href="#시작하기">시작하기</a> ·
  <a href="#문서">문서</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/KnifeLemon/Filee?color=7F77DD&label=release" alt="최신 릴리스"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases"><img src="https://img.shields.io/github/downloads/KnifeLemon/Filee/total?color=1D9E75" alt="다운로드 수"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT 라이선스"></a>
</p>

<p align="center"><a href="README.md">English</a> · <b>한국어</b> · <a href="README.zh-CN.md">简体中文</a></p>

<table>
  <tr>
    <td width="33%" valign="top"><b>커서 바로 옆에서</b><br>프로그램 창을 열 필요가 없어요. 끌고 있는 파일에 맞는 형식만 커서 위치에 나타납니다.</td>
    <td width="33%" valign="top"><b>한컴오피스 없이 HWP·HWPX</b><br>내장 HWPX 작성기가 워드 문서의 레이아웃을 유지하고, rhwp가 HWP ↔ HWPX, PDF, PNG를 변환합니다.</td>
    <td width="33%" valign="top"><b>내 PC 안에서만</b><br>아무것도 업로드하지 않아요. 인터넷 없이도 동작하고 결과는 원본 바로 옆에 저장됩니다.</td>
  </tr>
</table>

## 이렇게 동작해요

<p align="center">
  <a href="https://filee.sh/ko/"><img src="docs/media/demo-ko.webp" alt="Ctrl을 누른 채 여행사진.jpg를 끌면 커서 위치에 형식 도넛이 열리고, PNG에 놓으면 진행률 링이 차오른 뒤 여행사진.png가 생깁니다" width="820"></a><br>
  <sub>27초 영상과 직접 해보는 데모는 <a href="https://filee.sh/ko/">filee.sh</a>에서</sub>
</p>

## 기능

- **커서 위치에 뜨는 도넛 툴바.** 수정키 + 파일 드래그(기본 <kbd>Ctrl</kbd>). 수정키 조합, 마우스 버튼, 드래그 거리,
  길게 누르기, 선택한 파일용 키보드 단축키까지 모두 바꿀 수 있습니다.
- **파일 종류별 툴바.** 이미지, PDF, 문서, 스프레드시트, 프레젠테이션, HWP/HWPX, 텍스트, 혼합 파일용 프로필. 실제 도넛 위에서 드래그 앤 드롭으로
  프리셋을 넣고, 빼고, 순서를 바꿉니다. 우클릭이나 ✎로 프리셋을 편집합니다.
- **프리셋.** 품질, 크기 조정, DPI, 메타데이터, TIFF 압축, ICO 크기, PDF 합치기·나누기, 페이지 범위,
  저장 위치, 파일 이름 패턴, 이름 충돌 처리.
- **일괄 변환.** 1개든 100개든 동시에 변환하고, 하나가 실패해도 나머지는 계속됩니다.
- **경로 자동 계획.** 여러 단계 변환도 알아서 찾습니다(예: HWPX → PDF → PNG). 단계마다 가장 잘하는 엔진을 씁니다.
- **네 가지 사용 방법.** 드래그 동작, 탐색기 우클릭과 보내기, 탐색기에서 선택한 파일 + 단축키, 메인 창 드롭 영역.
- **말랑한 애니메이션 UI.** 라이트·다크·시스템 테마, 강조색, 모서리 둥글기, 저사양 PC를 위한 *애니메이션 줄이기*.
- **한국어, English, 简体中文.** 기본값은 Windows 언어를 따릅니다.

## 지원 형식

| 원본 | 변환 대상 |
|---|---|
| **이미지**: JPG, PNG, WEBP, TIFF, BMP, GIF, ICO, AVIF, HEIC(읽기) | 모든 이미지 형식, PDF |
| **PDF** | PNG, JPG, TIFF, 합치기, 나누기, 페이지 범위 |
| **문서**: DOCX, DOC, ODT, RTF | PDF, HWPX, 서로 변환, TXT, HTML |
| **스프레드시트**: XLSX, XLS, ODS, CSV | PDF, XLSX ↔ CSV, HWPX, HTML |
| **프레젠테이션**: PPTX, PPT, ODP | PDF, PNG, JPG, HWPX |
| **한글**: HWP, HWPX | PDF, HWPX ↔ HWP, DOCX, TXT, 마크다운, HTML |
| **텍스트**: TXT, 마크다운, HTML | DOCX, HWPX, PDF |

DOCX·XLSX·CSV·PPTX → PDF, 한글 파일, 마크다운은 설치하자마자 변환됩니다. MS 오피스·한컴오피스·LibreOffice가 없어도
되고, PC에 설치된 프로그램은 쓰지 않습니다. 오래된 형식(DOC·XLS·PPT·OpenDocument)과 DOCX·ODT로 저장하기는
LibreOffice, HTML과 마크다운 ↔ 워드는 Pandoc을 쓰는데, 둘 다 처음 실행할 때 받을지 물어보는 추가 엔진입니다.
HWPX를 어떻게 만들고 검증하는지는 [docs/ENGINES.md](docs/ENGINES.md#hwpx-writer)에 있습니다.

## 스크린샷

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-ko.png" alt="프로필과 프리셋으로 도넛 툴바를 편집하는 화면"></td>
    <td width="50%"><img src="docs/media/page-home-ko.png" alt="드롭 영역과 최근 변환 기록이 있는 홈 화면"></td>
  </tr>
  <tr>
    <td align="center"><sub>파일 종류마다 도넛을 드래그 앤 드롭으로 배치</sub></td>
    <td align="center"><sub>홈: 드롭 영역과 최근 변환 기록</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/media/preset-popup-ko.png" alt="품질과 저장 위치를 고르는 프리셋 편집 창" width="70%"></td>
    <td width="50%" align="center"><img src="docs/media/engine-setup-ko.png" alt="처음 실행할 때 추가 엔진을 고르는 창" width="80%"></td>
  </tr>
  <tr>
    <td align="center"><sub>프리셋 편집: 품질, 크기, 저장 위치, 파일 이름</sub></td>
    <td align="center"><sub>큰 엔진은 처음 실행할 때 골라서 설치</sub></td>
  </tr>
</table>

## 시작하기

**설치:** [`Filee-win-Setup.exe`](https://github.com/KnifeLemon/Filee/releases/latest/download/Filee-win-Setup.exe)를 받으세요
(Windows 10/11, 64비트). 다른 버전은 [Releases](https://github.com/KnifeLemon/Filee/releases)에 있습니다.

- **설치 파일은 가볍습니다.** 이미지, PDF, HWP/HWPX, 마크다운, DOCX·XLSX·PPTX → PDF, XLSX ↔ CSV는 설치하자마자 바로 변환됩니다.
- **큰 엔진은 골라서 설치합니다.** 처음 실행하면 LibreOffice(DOC·XLS·PPT 같은 오래된 오피스 형식, 약 420MB)와 Pandoc(HTML·마크다운 ↔ 워드, 약 42MB)을 받을지 묻습니다. 나중에 설정 → 변환 엔진에서 언제든 설치하거나 지울 수 있습니다.
- **업데이트는 알려 드려요.** 새 버전이 나오면 메뉴 하단, 알림, 트레이 메뉴에 표시되고 누르면 최신 릴리스 페이지가
  열립니다. 새 설치 파일을 실행하면 설정은 그대로 유지돼요.

macOS 지원은 계획 중입니다.

<details>
<summary><b>소스에서 빌드</b></summary>

필요: .NET 10 SDK, Windows 10/11.

```bash
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
pwsh build/fetch-fonts.ps1                       # 선택: Noto Sans 글꼴
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # 선택: 개발용 엔진 (전체는 -Only 생략, 약 475MB)
dotnet run --project src/Filee.App
dotnet test
```

설정은 `%APPDATA%\Filee`에 저장됩니다. 디버그 빌드는 탐색기 우클릭 메뉴나 자동 시작을 건드리지 않습니다.

| 프로젝트 | 설명 |
|---|---|
| `src/Filee.Core` | 형식, 프리셋, 툴바 프로필, 설정, 경로 계획, 작업 큐. UI·OS API 없음. |
| `src/Filee.Engines` | 변환 엔진마다 클래스 하나(`IConverter`). HWPX 작성기 포함. |
| `src/Filee.Platform.Windows` | 탐색기 연동, 우클릭 메뉴, 자동 시작. |
| `src/Filee.App` | Avalonia UI: 도넛 툴바, 설정 창, 트레이, 알림. |
| `tests/*` | xUnit v3 테스트(헤드리스 UI 렌더링 포함). |

</details>

## 문서

| 하고 싶은 일 | 여기서 시작 |
|---|---|
| 앱 구조 이해하기 | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| 어떤 엔진이 무엇을 변환하는지, 라이선스 | [docs/ENGINES.md](docs/ENGINES.md) |
| 새 변환 추가하기 | [docs/ADDING-A-CONVERTER.md](docs/ADDING-A-CONVERTER.md) |
| 새 언어로 번역하기 | [docs/ADDING-A-LANGUAGE.md](docs/ADDING-A-LANGUAGE.md) |
| PR 보내기 | [CONTRIBUTING.md](CONTRIBUTING.md) |

## 기여

PR 환영합니다. 시작하기 좋은 작업은 [변환기 추가](docs/ADDING-A-CONVERTER.md)와 [언어 추가](docs/ADDING-A-LANGUAGE.md)입니다.
Filee가 쓸만하다면 ⭐ 하나가 다른 사람들이 찾는 데 큰 도움이 됩니다.

## 라이선스

MIT. 포함된 서드파티 구성요소는 각자의 라이선스를 따릅니다: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#KnifeLemon/Filee&Date"><img src="https://api.star-history.com/svg?repos=KnifeLemon/Filee&type=Date" alt="Star history" width="600"></a>
</p>
