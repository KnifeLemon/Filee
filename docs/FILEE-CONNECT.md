# Filee Connect

**Filee Connect** provides an instant, peer-to-peer Wi-Fi bridge between your PC and mobile devices (iPhone, iPad, Android). It enables seamless wireless note/text exchange and local file transfers without relying on external cloud servers, accounts, or internet connectivity.

---

## Architecture & Privacy

- **100% Local Network (LAN)**: Communication runs entirely over your local private Wi-Fi network using an embedded high-performance ASP.NET Core Kestrel HTTP/WebSocket server.
- **Strict Private Binding**: Kestrel binds strictly to the machine's private LAN IP (`options.Listen(lanIp, port)`), refusing to start if the network adapter does not expose a private IP (RFC 1918 / ULA / link-local).
- **Production Host Hardening**: Built with `WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" })` to ensure developer exception pages, `ASPNETCORE_*` overrides, and arbitrary directory configs are ignored.
- **Zero Cloud Footprint**: Data never leaves your local network. No external servers, logins, or tracking.
- **Origin & Host Verification**: Rejects cross-origin requests by enforcing strict Host and Origin matching on state-changing API endpoints and WebSockets (`/ws`), preventing malicious web pages on the same network or browser from querying the local server.
- **Ephemeral Storage**: Files are stored in the user data directory (`UserDataStore.Directory`, fully respecting Portable mode) under `ConnectShared`, and all stored files are completely wiped when the session ends.

---

## Pairing & Security Model

```
[Desktop: Start Session]
       │
       ├─── QR Pairing Secret (Random 16-byte hex) ───► Scanned by Mobile Camera
       │                                                         │
       │                                                         ▼
       │                                               GET /mobile/{secret}
       │                                            (Fixed-time comparison)
       │                                                         │
       └─── 6-Digit OTP PIN (30s auto-refresh)                   ▼
            (Immediate single-use consumption,       Hands phone its own device token
             5-attempt rate-limit lockout)           via secure session cookie
```

### 1. Dual-Factor Pairing
- **QR Code Pairing**: The QR code encodes a single-session random secret (`/mobile/{secret}`). Accessing this URL verifies the secret in constant time via `CryptographicOperations.FixedTimeEquals`, creates an authenticated session, issues a unique device token, and redirects to `/mobile`.
- **6-Digit PIN Pairing**: Users typing the address manually enter a 6-digit one-time PIN. The PIN auto-refreshes every 30 seconds. Upon successful pairing, the PIN is immediately consumed and refreshed. After 5 incorrect attempts, PIN pairing is paused for 2 minutes to prevent brute-force guessing over Wi-Fi.

### 2. Device Token Enforcement
- Every `/api/*`, `/preview/*`, `/download/*`, `/api/folder.zip`, and `/ws` request requires a valid device token (passed via `X-Device-Token` header or `filee_token` cookie).
- Requests lacking a valid token are rejected with `401 Unauthorized` or closed WebSocket connection.
- Desktop users can view connected devices and click **Disconnect** at any time to instantly revoke the device token and terminate its connection.

### 3. Received File Execution Safety
- Files received from mobile devices are never blindly opened with `UseShellExecute`.
- Safe file types (images, PDF, plain text, audio, and video) open directly in their respective default applications, determined strictly by a sanitized file extension on the PC side.
- Executables, scripts (`.exe`, `.bat`, `.cmd`, `.ps1`, `.sh`, `.vbs`, etc.), shortcuts, and unknown file types are revealed and selected in the system file manager (Windows Explorer, macOS Finder, or Linux file manager) rather than executed.
- Received files are tagged with security attributes:
  - **Windows**: Tagged with Mark of the Web (`Zone.Identifier: [ZoneTransfer] ZoneId=3` - Internet zone).
  - **macOS**: Tagged with `com.apple.quarantine`.

### 4. Direct Streaming & Quota Protection
- **Direct ZIP Streaming**: The `/api/folder.zip` endpoint streams compressed entries directly to the HTTP response stream (`ZipArchive(context.Response.Body, ...)`), avoiding buffering gigabytes in memory.
- **Concurrency & Quota Management**: Shared folder storage limit (2 GB) is reserved under lock before writing, preventing quota overflows from concurrent uploads. Individual uploads are capped at 500 MB per file.
- **WebSocket Throttling**: Message sizes are strictly capped at 64 KB, and mobile clients are restricted to valid message types.

---

## Workflow & User Experience

```
[Shortcut Trigger]
(Ctrl + Alt + Left Click)
          │
          ▼
    [Donut Menu]
 (Filee Connect Slice)
          │
          ▼
   [QR Code Modal] ───────────────► Mobile Camera Scan / PIN Entry
 (Wi-Fi Link & PIN)                        │
          │                                │ (Device Paired)
          ▼                                ▼
[Filee Connect Desktop Window] ◄────── [Mobile Web Client]
 • Quick Notes / Clipboard              • Clean Light Mode UI
 • Shared Folder Workspace              • Real-time WebSockets
 • Device Management                    • Safe File Downloads
```

### 1. Triggering Filee Connect
- **Global Shortcut**: Press <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Left Click</kbd> anywhere on your screen (inside any application window or desktop, except on files where file conversions take priority).
- **Customizable**: Go to **Settings → Shortcuts**. The **"Filee Connect Server Shortcut"** section allows configuring custom modifier keys, mouse buttons, and triggers.

### 2. QR Code Pairing Modal
- Choosing **Filee Connect** from the Donut toolbar launches the centered **QR Code Modal** (`FileeConnectQrWindow`).
- Displays the generated QR code (`QRCoder`), direct Wi-Fi URL, active 6-digit PIN with countdown, and network instructions:
  - *Please make sure both devices are on the same Wi-Fi network.*
  - *Scan with your default Camera or QR scanner app.*
  - *Trust the local HTTP connection.*

### 3. Automatic Transition
- As soon as your phone camera opens the link or the PIN is entered, the desktop automatically detects the connection.
- The QR Code Modal closes automatically, and the primary **Filee Connect Window** appears.
- Header displays **FileeConnect** on the left and **`{Device Name} Connected`** with an active green status dot and **Disconnect** button on the right.

---

## Features

### Quick Notes & Text Exchange
- **Desktop Composer**: Type notes or click **Paste Clipboard Text** to send text or URLs directly to your phone.
- **Mobile Composer**: Write messages on your phone or paste mobile clipboard text to send to your PC.
- **History & One-Click Copy**: Both PC and mobile retain message history with instant copy buttons and delivery timestamps.

### Shared Folder Workspace
- **Drag & Drop / Upload**: Upload individual files or entire folders from your PC or mobile device.
- **Instant Preview**: Directly view images, text files, and PDFs on mobile or desktop.
- **Folder ZIP Download**: Download entire shared directories as a single `.zip` archive streamed on-the-fly.
- **Live Sync**: Changes (uploads, deletions) sync in real-time across both screens via WebSockets.

### Clean Light-Mode Mobile Client
- The mobile web client (`mobile.html`) is designed with a persistent, clean **Light Mode** palette and features the official vector **Filee** logo.

### Seamless Disconnect & Reconnect
- When a device is connected, the host displays a **Disconnect** button which revokes the device's token and resets the phone back to the pairing PIN prompt.
- Once disconnected, the button seamlessly turns into **Connect Again**. Clicking it opens an in-window QR code & PIN overlay on top of the existing Filee window without clearing the session.
- Scanning the QR code or entering the PIN reconnects the phone with all previous messages and shared files preserved.

