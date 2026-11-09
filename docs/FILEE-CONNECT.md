# Filee Connect

**Filee Connect** provides an instant, peer-to-peer Wi-Fi bridge between your PC and mobile devices (iPhone, iPad, Android). It enables seamless wireless note/text exchange and local file transfers without relying on external cloud servers or third-party accounts.

---

## Architecture & Privacy

- **100% Local Network (LAN)**: Communication runs entirely over your Wi-Fi router using an embedded high-performance ASP.NET Core Kestrel HTTP/WebSocket server.
- **Zero Cloud Footprint**: Data never leaves your local network. No external servers, logins, or tracking.
- **Ephemeral Security Tokens**: Every session generates an isolated, cryptographically random pairing token (`_token`) required for all HTTP and WebSocket requests.
- **Local Storage Quota**: The shared workspace enforces a local 2 GB storage limit (`SharedFolderManager.MaxTotalStorage`) with path-traversal prevention.

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
   [QR Code Modal] ───────────────► Mobile Camera Scan
 (Wi-Fi Link & Instructions)               │
          │                                │ (Device Detected)
          ▼                                ▼
[Filee Connect Desktop Window] ◄────── [Mobile Web Client]
 • Quick Notes / Clipboard              • Clean Light Mode UI
 • Shared Folder Workspace              • Real-time WebSockets
```

### 1. Triggering Filee Connect
- **Global Shortcut**: Press <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Left Click</kbd> anywhere on your screen (inside any application window or desktop, except on files where file conversions take priority).
- **Customizable**: Go to **Settings → Shortcuts**. The **"Filee Connect Server Shortcut"** section allows configuring custom modifier keys, mouse buttons, and triggers.

### 2. QR Code Pairing Modal
- Choosing **Filee Connect** from the Donut toolbar launches the centered **QR Code Modal** (`FileeConnectQrWindow`).
- Displays a high-contrast QR code generated on-the-fly (`QRCoder`), direct Wi-Fi URL, and network guidance:
  - *Please make sure both devices are on the same Wi-Fi network.*
  - *Scan with your default Camera or QR scanner app.*
  - *Trust the local HTTP connection.*

### 3. Automatic Transition
- As soon as your phone camera opens the link, the desktop automatically detects the connection.
- The QR Code Modal closes automatically, and the primary **Filee Connect Window** appears.
- Header displays **FileeConnect** on the left and **`{Device Name} Connected`** with an active green status dot on the right.

---

## Features

### Quick Notes & Text Exchange
- **Desktop Composer**: Type notes or click **Paste Clipboard Text** to send text or URLs directly to your phone.
- **Mobile Composer**: Write messages on your phone or paste mobile clipboard text to send to your PC.
- **History & One-Click Copy**: Both PC and mobile retain message history with instant copy buttons and delivery timestamps.

### Shared Folder Workspace
- **Drag & Drop / Upload**: Upload individual files or entire folders from your PC or mobile device.
- **Instant Preview**: Directly view images, text files, and PDFs on mobile or desktop.
- **Folder ZIP Download**: Download entire shared directories as a single `.zip` archive.
- **Live Sync**: Changes (uploads, deletions) sync in real-time across both screens via WebSockets.

### Clean Light-Mode Mobile Client
- The mobile web client (`mobile.html`) is designed with a persistent, clean **Light Mode** palette and features the official vector **Filee** logo.
