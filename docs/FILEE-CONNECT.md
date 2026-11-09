# Filee Connect

**Filee Connect** provides an instant, peer-to-peer Wi-Fi bridge between your PC and mobile devices (iPhone, iPad, Android). It enables seamless wireless note/text exchange and local file transfers without relying on external cloud servers, accounts, or internet connectivity.

---

## Architecture & Privacy

- **100% Local Network (LAN)**: Communication runs entirely over your local private Wi-Fi network using an embedded high-performance ASP.NET Core Kestrel HTTP/WebSocket server.
- **Strict Private Binding**: Kestrel binds strictly to the machine's private LAN IP (`options.Listen(lanIp, port)`), refusing to start if the network adapter does not expose a private IP (RFC 1918 / ULA / link-local).
- **Production Host Hardening**: Built with `WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" })` to ensure developer exception pages, `ASPNETCORE_*` overrides, and arbitrary directory configs are ignored.
- **Zero Cloud Footprint**: Data never leaves your local network. No external servers, logins, or tracking.
- **Origin & Host Verification (DNS Rebinding Protection)**: Defends against DNS rebinding attacks by strictly validating that the `Host` header equals the exact private `{lanIp}:{port}` bound by the server. On cross-origin or state-changing requests, the `Origin` header must match this exact LAN authority.
- **Ephemeral & Neutral Storage**: Files are stored in the user data directory (`UserDataStore.Directory`, fully respecting Portable mode) under `ConnectShared`. Files outside the safe allowlist are saved with neutral names (`{id}.bin`) on disk to prevent Windows Explorer from evaluating malicious extensions (`.scf`, `.url`, etc.) and leaking NTLM hashes. All stored files and temp data are completely wiped on startup and when the session ends.

---

## Pairing & Security Model

```
[Desktop: Start Session]
       │
       ├─── Single-Use QR Pairing Secret (Random 16-byte hex) ───► Scanned by Mobile Camera
       │    (Consumed immediately on first pair)                         │
       │                                                                 ▼
       │                                                       GET /mobile/{secret}
       │                                                    (Fixed-time comparison)
       │                                                                 │
       └─── 6-Digit OTP PIN (30s auto-refresh)                           ▼
            (Immediate single-use consumption,               Hands phone its own device token
             per-client IP 5-attempt rate-limit lockout)     via HttpOnly session cookie
```

### 1. Dual-Factor Pairing
- **QR Code Pairing**: The QR code encodes a single-use random secret (`/mobile/{secret}`). Accessing this URL verifies the secret in constant time via `CryptographicOperations.FixedTimeEquals`, creates an authenticated session, issues a unique device token, immediately consumes the secret (`_pairingSecret = null`) so it cannot be reused, and redirects to `/mobile`. When "Connect Again" is clicked, a fresh secret is generated.
- **6-Digit PIN Pairing**: Users typing the address manually enter a 6-digit one-time PIN. The PIN auto-refreshes every 30 seconds. Upon successful pairing, the PIN is immediately consumed and refreshed. Concurrency is limited with a semaphore, and failed attempts are tracked per client IP. After 5 incorrect attempts from an IP, PIN pairing is paused for that IP for 2 minutes to prevent brute-force guessing over Wi-Fi without locking out legitimate users.

### 2. Device Token Enforcement
- Every `/api/*`, `/preview/*`, `/download/*`, `/api/folder.zip`, and `/ws` request requires a valid device token (passed via `Authorization: Bearer <token>` header or `filee_token` cookie; query string tokens are strictly rejected to prevent history/Referer leakage).
- Requests lacking a valid token are rejected with `401 Unauthorized` or closed WebSocket connection.
- Desktop users can view connected devices and click **Disconnect** at any time to instantly revoke the device token and terminate its connection.

### 3. Received File Execution Safety & Neutral Storage
- Files received from mobile devices are never blindly opened with `UseShellExecute`.
- Safe file types (images, PDF, plain text, audio, and video) open directly in their respective default applications, determined strictly by a sanitized file extension on the PC side. Risky formats like `.svg` and `.xml` are excluded from direct execution.
- Executables, scripts (`.exe`, `.bat`, `.cmd`, `.ps1`, `.sh`, `.vbs`, etc.), shortcuts, and unknown file types are revealed and selected in the system file manager (Windows Explorer, macOS Finder, or Linux file manager) rather than executed.
- Neutral file storage: files not on the safe allowlist are written to disk as `{id}.bin` so Windows Explorer cannot automatically inspect malicious shell shortcuts (`.scf`, `.library-ms`, `.url`) and trigger SMB / NTLM credential leaks. Their original human-readable names are preserved safely in session metadata.
- Received files are tagged with security attributes:
  - **Windows**: Tagged with Mark of the Web (`Zone.Identifier: [ZoneTransfer] ZoneId=3` - Internet zone).
  - **macOS**: Tagged with `com.apple.quarantine`.

### 4. Direct Streaming & Quota Protection
- **Asynchronous Direct ZIP Streaming**: The `/api/folder.zip` endpoint streams compressed entries asynchronously directly to the HTTP response stream (`await ZipArchive.CreateAsync(context.Response.Body, ...)`), avoiding buffering gigabytes in memory and avoiding synchronous I/O blocks.
- **Concurrency & Quota Management**: Shared folder storage limit (2 GB) is reserved under lock before writing, preventing quota overflows from concurrent uploads. Individual uploads are capped at 500 MB per file, while default non-upload endpoints enforce a strict 64 KB body size limit.
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

