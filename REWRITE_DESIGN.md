# Filee-Lite (Rust + Tauri v2) 개발 설계서

> **"Drag. Drop. Converted."**  
> 불필요한 복잡도를 걷어내고, 한글(HWP/HWPX)과 핵심 포맷에 집중한 초경량·초고속 크로스 플랫폼 파일 변환기

---

## 1. 프로젝트 비전 및 목표

### 1.1 배경 및 목적
- 기존 Filee의 핵심 사용자 경험(단축키 + 드래그 시 마우스 커서 위치에 나타나는 도넛 링 변환 인터랙션)을 유지합니다.
- CAD, 3D 모델, 특수 인쇄 포맷 등 과도하게 넓은 범위를 정리하고, **일상 업무에서 가장 많이 쓰이는 문서(한글 HWP/HWPX 포함), 이미지, 미디어**에 집중합니다.
- C#/.NET 10 기반의 런타임 상주 메모리(40~100MB+)와 배포 크기를 **Rust + Tauri v2** 기반으로 재작성하여 **상주 메모리 15MB 미만, 바이너리 15MB 안팎, 즉각적인 기동 속도**를 달성합니다.

### 1.2 핵심 설계 원칙 (Ponytail Philosophy)
1. **YAGNI (You Aren't Gonna Need It)**: CAD(DWG/DXF), 3D(GLTF), 복잡한 인앱 영상/이미지 편집 타임라인은 제외합니다.
2. **Pure Native 우선**: 한글 처리는 Rust 네이티브 크레이트인 `unhwp`를 최우선 활용하여 외장 CLI 종속성을 최소화합니다.
3. **0ms 반응성**: 도넛 창은 매번 새로 띄우지 않고, 사전에 띄워둔 투명 숨김 창(Pre-warmed Window)을 위치만 옮겨 표시합니다.

---

## 2. 지원 포맷 및 변환 범위 (Scope)

본 설계의 핵심은 **"한국 업무 환경의 4대 필수 문서(한글, 워드, 엑셀, 파워포인트) 완벽 지원"**입니다.

### 2.1 4대 필수 오피스 문서 및 지원 포맷

| 카테고리 | 입력 포맷 | 출력 포맷 | 변환 엔진 및 방식 |
| :--- | :--- | :--- | :--- |
| **한글 문서** | `hwp`, `hwpx` | `pdf`, `docx`, `md`, `txt`, `json`, `png` | • **순수 Rust**: `unhwp` (MD, TXT, JSON 추출)<br>• **렌더링**: `rhwp` (고품질 PDF/이미지 변환) |
| **워드** | `docx`, `doc` | `pdf`, `txt`, `md`, `html`, `png` | • **순수 Rust**: `docx-rs`, `pandoc`<br>• **시각 렌더링**: `libreoffice` (Headless) |
| **엑셀** | `xlsx`, `xls`, `csv` | `csv`, `json`, `md`, `xlsx`, `pdf` | • **순수 Rust**: `calamine` (고속 읽기), `rust_xlsxwriter` (쓰기)<br>• **인쇄 렌더링**: `libreoffice` (Headless) |
| **파워포인트** | `pptx`, `ppt` | `pdf`, `png` (슬라이드별 이미지), `txt` | • **순수 Rust**: `zip` + `quick-xml` (텍스트/슬라이드 추출)<br>• **슬라이드 렌더링**: `libreoffice` (Headless) |
| **이미지** | `png`, `jpg`, `jpeg`, `webp`, `gif`, `svg`, `ico`, `bmp` | `png`, `jpg`, `webp`, `ico`, `bmp` | • **순수 Rust**: `image`, `resvg` |
| **미디어** | `mp4`, `mp3`, `wav`, `webm`, `mkv`, `aac` | `mp3`, `wav`, `mp4`, `webm` | • `ffmpeg` (경량 바이너리 호출) |

---

## 3. 시스템 아키텍처

```
┌─────────────────────────────────────────────────────────────────┐
│                    Frontend (Tauri v2 WebView)                  │
│                                                                 │
│  - Svelte 5 / React + TailwindCSS (초경량 UI)                   │
│  - Transparent & Frameless Overlay Window                      │
│  - SVG 기반 동적 도넛 링 렌더러 (Slice 계산 & 드롭 영역 감지)   │
└────────────────────────────────▲────────────────────────────────┘
                                 │ Tauri IPC (Commands / Events)
┌────────────────────────────────▼────────────────────────────────┐
│                          Rust Backend                           │
│                                                                 │
│  ┌─────────────────────────┐      ┌──────────────────────────┐  │
│  │     Trigger Service     │      │      Route Planner       │  │
│  │  - rdev 전역 훅         │      │  - petgraph 유향 그래프  │  │
│  │  - Ctrl+Drag 감지       │      │  - Dijkstra 최적 경로    │  │
│  └─────────────────────────┘      └──────────────────────────┘  │
│                                                                 │
│  ┌─────────────────────────┐      ┌──────────────────────────┐  │
│  │      Job Queue          │      │     Conversion Core      │  │
│  │  - tokio 비동기 큐      │─────▶│  - unhwp (HWP/HWPX)      │  │
│  │  - mpsc 채널 진행률 통지│      │  - image (래스터 변환)   │  │
│  │  - 동시 실행 제한 (세마포어)    │  - pdfium / ffmpeg       │  │
│  └─────────────────────────┘      └──────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 4. 백엔드(Rust) 핵심 모듈 설계

### 4.1 Trigger Service (전역 입력 감지)
- **책임**: OS 백그라운드에서 마우스/키보드 입력을 감시하고, 드래그 제스처 발생 시 도넛 창을 활성화.
- **주요 라이브러리**: `rdev` 또는 OS별 네이티브 훅
- **동작 흐름**:
  1. `Ctrl` (macOS는 `Option`) 키 눌림 상태 추적.
  2. 키가 눌린 상태에서 마우스 좌클릭 후 `DragThreshold`(약 6px 이상) 이동 감지.
  3. 드래그 이벤트 발생 시 마우스 현재 좌표 `(x, y)` 획득.
  4. Tauri 윈도우 매니저에 좌표 전달 및 투명 창 표시 이벤트 발행.

### 4.2 Route Planner (변환 경로 탐색기)
- **책임**: 입력 포맷에서 대상 포맷까지 최적의 변환 경로(최대 3-hop) 탐색.
- **주요 라이브러리**: `petgraph`
- **변환 모델링**:
  - Node: 포맷 문자열 (`"hwp"`, `"md"`, `"pdf"`, `"png"`)
  - Edge: 변환 엔진 및 비용 (`Cost`)
  - *예시*: `HWP` $\xrightarrow{unhwp}$ `MD` $\xrightarrow{pandoc/pulldown}$ `PDF` $\xrightarrow{pdfium}$ `PNG`
  - 단일 변환기가 없어도 그래프 탐색으로 연계 변환 경로를 자동 생성.

### 4.3 4대 오피스 문서 변환 엔진 상세 설계

변환 작업은 크게 **① 데이터/텍스트 고속 추출(순수 Rust)**과 **② 시각 레이아웃 보존 렌더링(PDF/이미지)**의 2-트랙으로 동작합니다.

#### 1) 한글 엔진 (`hwp_engine`)
- **텍스트/마크다운 추출**: `unhwp` (순수 Rust 크레이트)를 통해 메모리 상에서 즉시 HWP/HWPX $\rightarrow$ Markdown/Text/JSON 변환.
- **문서 렌더링**: HWP 원본 모양을 그대로 살린 PDF 변환 시 `rhwp` 연동.

#### 2) 엑셀 엔진 (`excel_engine`)
- **XLSX / XLS / CSV 상호 변환**: 
  - 읽기: 순수 Rust 기반 `calamine` 크레이트 사용 (매우 빠르고 안정적, XLS/XLSX/XLSB/ODS 전 포맷 지원).
  - 쓰기: `rust_xlsxwriter` 크레이트를 통해 구조화된 데이터(CSV, JSON, Markdown 표)를 네이티브 XLSX 파일로 즉각 생성.
- **시트 인쇄/PDF 변환**: `libreoffice --headless` 백그라운드 호출.

#### 3) 워드 엔진 (`word_engine`)
- **DOCX 텍스트/구조 변환**: `docx-rs` 또는 `pandoc`을 통한 DOCX $\leftrightarrow$ Markdown / HTML / TXT 변환.
- **DOC(구형 바이너리) 및 완벽한 PDF 변환**: `libreoffice --headless`를 통한 무손실 PDF/인쇄본 렌더링.

#### 4) 파워포인트 엔진 (`ppt_engine`)
- **슬라이드별 텍스트/구조 추출**: PPTX는 OOXML ZIP 구조이므로 `zip` + `quick-xml`로 슬라이드 텍스트 추출.
- **슬라이드별 이미지(PNG) 및 PDF 변환**: `libreoffice --headless --convert-to pdf` 후 `pdfium`으로 각 슬라이드를 고화질 PNG로 일괄 추출.

### 4.4 Job Queue & 동시성 제어
- `tokio::sync::Semaphore`를 사용하여 CPU 코어 수에 맞게 동시 변환 작업 수 제한.
- 작업별 `mpsc` 채널을 통해 프론트엔드로 변환 진행률(`0% ~ 100%`) 및 완료/실패 이벤트를 스트리밍.

---

## 5. 프론트엔드(Tauri) UI/UX 설계

### 5.1 Pre-warmed Overlay Window
- 앱 실행 시 `donut` 윈도우를 미리 로드해 두고 `hide()` 처리:
  ```json
  {
    "title": "Donut",
    "width": 320,
    "height": 320,
    "resizable": false,
    "decorations": false,
    "transparent": true,
    "alwaysOnTop": true,
    "visible": false
  }
  ```
- 제스처 감지 시:
  ```rust
  // 위치를 커서 중심으로 즉시 이동 후 표시 (0ms 레이턴시)
  donut_window.set_position(PhysicalPosition::new(x - 160, y - 160))?;
  donut_window.show()?;
  ```

### 5.2 동적 도넛 링 (Donut Menu)
- **SVG Path 연산**: 드롭 가능한 타겟 포맷 수($N$)에 따라 원을 $N$등분하여 부채꼴 조각(Slice) 렌더링.
- **드롭 감지**: 파일이 특정 Slice 위로 드롭되면 해당 포맷 ID를 백엔드로 전달하고, 도넛 중앙에 진행 링(Progress Ring) 애니메이션 표시.

---

## 6. IPC 통신 프로토콜 명세

### Commands (Frontend $\rightarrow$ Backend)
- `get_available_formats(extensions: Vec<String>) -> Vec<FormatSlice>`: 드래그된 파일들의 교집합/유효 타겟 포맷 목록 조회
- `convert_files(request: ConvertRequest) -> Result<String, String>`: 변환 작업 등록 (Job ID 반환)
- `cancel_conversion(job_id: String) -> Result<(), String>`: 진행 중인 변환 취소

### Events (Backend $\rightarrow$ Frontend)
- `trigger:show { x: f64, y: f64, formats: Vec<String> }`: 도넛 창 표시 신호
- `trigger:hide`: 도넛 창 숨김 신호
- `conversion:progress { job_id: String, progress: f32 }`: 진행률 (0.0 ~ 1.0)
- `conversion:done { job_id: String, output_paths: Vec<String> }`: 완료 신호

---

## 7. 프로젝트 디렉터리 구조 제안

```
filee-lite/
├── Cargo.toml
├── src-tauri/
│   ├── Cargo.toml
│   ├── tauri.conf.json
│   ├── icons/
│   └── src/
│       ├── main.rs              # Tauri 엔트리포인트 및 윈도우 관리
│       ├── trigger.rs           # OS 전역 제스처 감지 (rdev)
│       ├── router/              # petgraph 기반 변환 라우팅
│       │   ├── mod.rs
│       │   └── planner.rs
│       ├── queue/               # tokio 기반 작업 큐
│       │   ├── mod.rs
│       │   └── job.rs
│       └── engines/             # 변환 엔진 레이어
│           ├── mod.rs
│           ├── hwp.rs           # unhwp & rhwp 연동 (HWP/HWPX)
│           ├── excel.rs         # calamine & rust_xlsxwriter 연동 (XLSX/XLS/CSV)
│           ├── word.rs          # docx-rs & pandoc 연동 (DOCX/DOC)
│           ├── ppt.rs           # pptx 파싱 및 슬라이드 이미지 추출 (PPTX/PPT)
│           ├── image.rs         # image/resvg 크레이트 연동
│           ├── pdf.rs           # pdfium 연동
│           └── media.rs         # ffmpeg 프로세스 래퍼
└── frontend/
    ├── package.json
    ├── svelte.config.js (또는 vite.config.ts)
    └── src/
        ├── App.svelte
        ├── lib/
        │   ├── DonutMenu.svelte # SVG 원형 도넛 메뉴
        │   ├── Slice.svelte     # 개별 포맷 조각
        │   └── Progress.svelte  # 도넛 중앙 진행률 애니메이션
        └── styles/
```

---

## 8. 단계별 개발 로드맵 (Milestones)

- **Phase 1: 4대 오피스 문서 및 이미지 엔진 구축 (CLI 프로토타입)**
  - `unhwp`: HWP/HWPX $\rightarrow$ Markdown / Text 변환 검증.
  - `calamine` + `rust_xlsxwriter`: XLS/XLSX $\leftrightarrow$ CSV/JSON/XLSX 변환 검증.
  - `docx-rs` / `pandoc`: DOCX $\leftrightarrow$ Markdown / PDF 변환 검증.
  - `image`: PNG $\leftrightarrow$ JPG $\leftrightarrow$ WEBP 상호 변환 검증.
- **Phase 2: Route Planner & 작업 큐 구현**
  - `petgraph` 기반 다단계 라우팅 로직 작성 (예: HWP $\rightarrow$ MD $\rightarrow$ PDF $\rightarrow$ PNG).
  - `tokio` 채널을 이용한 병렬 변환 큐 완성.
- **Phase 3: Tauri v2 투명 오버레이 윈도우 & UI**
  - 프론트엔드 SVG 도넛 링 렌더러 구현.
  - 마우스 드래그 앤 드롭 파일 수신 테스트.
- **Phase 4: 전역 제스처 감지 & 통합**
  - `Ctrl + 드래그` 시 마우스 커서 위치에 도넛 표시 연동.
  - 최종 릴리즈 바이너리 패키징 및 최적화 (Strip, LTO 적용).
