(() => {
  const $ = (id) => document.getElementById(id);
  const status = $('status');
  const dot = $('dot');
  const messages = $('messages');
  const input = $('input');
  const send = $('send');
  const pasteBtn = $('pasteClipboard');

  const pairingView = $('pairingView');
  const appView = $('appView');
  const pinForm = $('pinForm');
  const pinInput = $('pinInput');
  const pinError = $('pinError');
  const pinSubmit = $('pinSubmit');

  let socket;
  let reconnectTimer;
  let ended = false;

  // Tabs
  const tabBtnText = $('tabBtnText');
  const tabBtnFiles = $('tabBtnFiles');
  const tabBtnMirror = $('tabBtnMirror');
  const tabText = $('tabText');
  const tabFiles = $('tabFiles');
  const tabMirror = $('tabMirror');

  function switchTab(target) {
    [tabBtnText, tabBtnFiles, tabBtnMirror].forEach(b => b?.classList.remove('active'));
    [tabText, tabFiles, tabMirror].forEach(p => p?.classList.remove('active'));

    if (target === 'tabText') {
      tabBtnText?.classList.add('active');
      tabText?.classList.add('active');
    } else if (target === 'tabFiles') {
      tabBtnFiles?.classList.add('active');
      tabFiles?.classList.add('active');
      if (window.refreshSharedFiles) window.refreshSharedFiles();
    } else if (target === 'tabMirror') {
      tabBtnMirror?.classList.add('active');
      tabMirror?.classList.add('active');
    }
  }

  tabBtnText?.addEventListener('click', () => switchTab('tabText'));
  tabBtnFiles?.addEventListener('click', () => switchTab('tabFiles'));
  tabBtnMirror?.addEventListener('click', () => switchTab('tabMirror'));

  // Screen Mirroring
  let mirrorStream = null;
  let mirrorSocket = null;
  let mirrorInterval = null;
  const startMirrorBtn = $('startMirrorBtn');
  const stopMirrorBtn = $('stopMirrorBtn');
  const mirrorVideo = $('mirrorVideo');
  const mirrorStatusText = $('mirrorStatusText');

  startMirrorBtn?.addEventListener('click', async () => {
    try {
      if (!navigator.mediaDevices || !navigator.mediaDevices.getDisplayMedia) {
        alert('Screen capture is not supported on this browser.');
        return;
      }

      mirrorStream = await navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: 25 }
      });

      mirrorVideo.srcObject = mirrorStream;
      await mirrorVideo.play();

      const canvas = document.createElement('canvas');
      const ctx = canvas.getContext('2d');
      const track = mirrorStream.getVideoTracks()[0];
      const s = track.getSettings();
      canvas.width = Math.min(s.width || 720, 720);
      canvas.height = Math.min(s.height || 1280, 1280);

      const wsProto = location.protocol === 'https:' ? 'wss://' : 'ws://';
      mirrorSocket = new WebSocket(`${wsProto}${location.host}/ws/mirror`);
      mirrorSocket.binaryType = 'arraybuffer';

      mirrorSocket.onopen = () => {
        if (mirrorStatusText) mirrorStatusText.textContent = 'Broadcasting screen live to PC…';
        startMirrorBtn.hidden = true;
        stopMirrorBtn.hidden = false;

        mirrorInterval = setInterval(() => {
          if (mirrorSocket && mirrorSocket.readyState === WebSocket.OPEN && mirrorVideo.videoWidth > 0) {
            ctx.drawImage(mirrorVideo, 0, 0, canvas.width, canvas.height);
            canvas.toBlob((blob) => {
              if (blob && mirrorSocket && mirrorSocket.readyState === WebSocket.OPEN) {
                blob.arrayBuffer().then((buffer) => mirrorSocket.send(buffer));
              }
            }, 'image/jpeg', 0.65);
          }
        }, 1000 / 25);
      };

      track.onended = () => stopMirroring();
      mirrorSocket.onclose = () => stopMirroring();
    } catch (err) {
      if (mirrorStatusText) mirrorStatusText.textContent = 'Could not start screen sharing: ' + err.message;
    }
  });

  function stopMirroring() {
    if (mirrorInterval) { clearInterval(mirrorInterval); mirrorInterval = null; }
    if (mirrorSocket) { mirrorSocket.close(); mirrorSocket = null; }
    if (mirrorStream) {
      mirrorStream.getTracks().forEach(t => t.stop());
      mirrorStream = null;
    }
    if (startMirrorBtn) startMirrorBtn.hidden = false;
    if (stopMirrorBtn) stopMirrorBtn.hidden = true;
    if (mirrorStatusText) mirrorStatusText.textContent = 'Screen mirroring stopped.';
  }

  stopMirrorBtn?.addEventListener('click', stopMirroring);

  function getDeviceName() {
    const ua = navigator.userAgent || '';
    if (/iPhone/i.test(ua)) return 'iPhone';
    if (/iPad/i.test(ua)) return 'iPad';
    if (/Android/i.test(ua)) {
      if (/Samsung/i.test(ua)) return 'Samsung Phone';
      if (/Pixel/i.test(ua)) return 'Google Pixel';
      return 'Android Device';
    }
    if (/Macintosh/i.test(ua)) return 'Mac';
    if (/Windows/i.test(ua)) return 'Windows PC';
    return 'Mobile Device';
  }
  window.getDeviceName = getDeviceName;

  function setStatus(text, online) {
    if (status) status.textContent = text;
    if (dot) dot.classList.toggle('online', Boolean(online));
  }

  function addMessage(item) {
    const empty = messages.querySelector('.empty-state');
    if (empty) empty.remove();

    const isMine = item.from === 'mobile';
    const bubble = document.createElement('div');
    bubble.className = `bubble ${isMine ? 'mine' : ''}`;

    const header = document.createElement('div');
    header.className = 'bubble-header';

    const meta = document.createElement('span');
    meta.className = 'meta';
    const sender = isMine ? 'You' : 'PC';
    const time = new Date(item.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    meta.textContent = `${sender} • ${time}`;

    const copyBtn = document.createElement('button');
    copyBtn.className = 'copy-button';
    copyBtn.type = 'button';
    copyBtn.textContent = 'Copy';
    copyBtn.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(item.text);
        copyBtn.textContent = 'Copied!';
        copyBtn.classList.add('copied');
        setTimeout(() => {
          copyBtn.textContent = 'Copy';
          copyBtn.classList.remove('copied');
        }, 2000);
      } catch {
        const ta = document.createElement('textarea');
        ta.value = item.text;
        document.body.appendChild(ta);
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
        copyBtn.textContent = 'Copied!';
        setTimeout(() => { copyBtn.textContent = 'Copy'; }, 2000);
      }
    });

    header.append(meta, copyBtn);

    const body = document.createElement('div');
    body.className = 'bubble-body';
    body.textContent = item.text;

    bubble.append(header, body);
    messages.appendChild(bubble);
    messages.scrollTop = messages.scrollHeight;
  }

  function endUI(reason) {
    ended = true;
    clearTimeout(reconnectTimer);
    setStatus(reason || 'Session ended', false);
    if (send) send.disabled = true;
    if (input) input.disabled = true;
    if (pasteBtn) pasteBtn.disabled = true;
  }

  function connect() {
    if (ended) return;

    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const deviceName = encodeURIComponent(getDeviceName());
    socket = new WebSocket(`${scheme}//${location.host}/ws?deviceName=${deviceName}`);

    socket.onopen = () => {
      setStatus('Connected to PC', true);
    };

    socket.onmessage = ({ data: raw }) => {
      let data;
      try { data = JSON.parse(raw); } catch { return; }

      if (data.type === 'init') {
        messages.replaceChildren();
        if (!data.messages || !data.messages.length) {
          messages.innerHTML = '<div class="empty-state">No messages in this session yet.</div>';
        } else {
          data.messages.forEach(addMessage);
        }
      } else if (data.type === 'message') {
        addMessage(data.message);
      } else if (data.type === 'files-changed') {
        if (window.refreshSharedFiles) window.refreshSharedFiles();
      } else if (data.type === 'device-disconnected') {
        showPairingView();
      } else if (data.type === 'session-ended') {
        endUI(data.reason || 'Session ended');
      }
    };

    socket.onclose = (event) => {
      if (event.code === 4401 || event.code === 4001) {
        showPairingView();
        return;
      }
      if (!ended) {
        setStatus('Reconnecting…', false);
        reconnectTimer = setTimeout(connect, 2000);
      }
    };

    socket.onerror = () => socket.close();
  }

  function showPairingView() {
    if (pairingView) pairingView.hidden = false;
    if (appView) appView.hidden = true;
    setStatus('Pairing required', false);
    if (pinInput) {
      pinInput.value = '';
      pinInput.focus();
    }
  }

  function showAppView() {
    if (pairingView) pairingView.hidden = true;
    if (appView) appView.hidden = false;
    connect();
    if (window.refreshSharedFiles) window.refreshSharedFiles();
  }

  async function checkAuthAndStart() {
    try {
      const res = await fetch('/api/auth/check');
      if (res.ok) {
        showAppView();
      } else {
        showPairingView();
      }
    } catch {
      showPairingView();
    }
  }

  pinForm?.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (pinError) pinError.hidden = true;

    const rawPin = (pinInput?.value || '').replace(/\s+/g, '');
    if (rawPin.length < 6) {
      if (pinError) {
        pinError.textContent = 'Please enter a 6-digit PIN.';
        pinError.hidden = false;
      }
      return;
    }

    if (pinSubmit) pinSubmit.disabled = true;

    try {
      const res = await fetch('/api/pair/pin', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin: rawPin, deviceName: getDeviceName() })
      });

      const data = await res.json().catch(() => ({}));
      if (res.ok && data.token) {
        showAppView();
      } else if (res.status === 429) {
        if (pinError) {
          pinError.textContent = `Too many wrong attempts. Locked out for ${data.remainingSeconds || 120}s.`;
          pinError.hidden = false;
        }
      } else {
        if (pinError) {
          pinError.textContent = 'Invalid PIN. Please check your PC screen.';
          pinError.hidden = false;
        }
      }
    } catch (err) {
      if (pinError) {
        pinError.textContent = 'Could not connect to PC. Ensure both devices are on Wi-Fi.';
        pinError.hidden = false;
      }
    } finally {
      if (pinSubmit) pinSubmit.disabled = false;
    }
  });

  function sendMessage() {
    const text = (input?.value || '').trim();
    if (!text) return;

    if (socket?.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify({ type: 'text', text }));
      if (input) {
        input.value = '';
        input.focus();
      }
    } else {
      // Fallback HTTP POST (cookies sent automatically)
      fetch('/api/send', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ text })
      }).then(() => {
        if (input) {
          input.value = '';
          input.focus();
        }
      }).catch(() => {
        alert('Could not send message. Connection lost.');
      });
    }
  }

  send?.addEventListener('click', sendMessage);
  input?.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      sendMessage();
    }
  });

  pasteBtn?.addEventListener('click', async () => {
    try {
      if (navigator.clipboard && navigator.clipboard.readText) {
        const text = await navigator.clipboard.readText();
        if (text && input) {
          input.value = input.value ? input.value + '\n' + text : text;
          input.focus();
          return;
        }
      }
    } catch (_) { }

    const text = prompt('Paste your copied text here:');
    if (text && input) {
      input.value = input.value ? input.value + '\n' + text : text;
      input.focus();
    }
  });

  // Start auth check and load localized strings
  loadStrings();
  checkAuthAndStart();

  async function loadStrings() {
    try {
      const res = await fetch('/api/strings');
      if (!res.ok) return;
      const s = await res.json();
      if (s['connect.tab_text'] && tabBtnText) tabBtnText.textContent = s['connect.tab_text'];
      if (s['connect.tab_shared_folder'] && tabBtnFiles) tabBtnFiles.textContent = s['connect.tab_shared_folder'];
      if (s['connect.input_placeholder'] && input) input.placeholder = s['connect.input_placeholder'];
      if (s['connect.paste_clipboard'] && pasteBtn) pasteBtn.textContent = s['connect.paste_clipboard'];
      if (s['connect.send'] && send) send.textContent = s['connect.send'];
      const uploadBtn = $('chooseFiles');
      if (s['connect.upload_files'] && uploadBtn) uploadBtn.textContent = s['connect.upload_files'];
      if (s['connect.title'] && pairingView) {
        const h2 = pairingView.querySelector('h2');
        if (h2) h2.textContent = s['connect.title'];
      }
      if (s['connect.manual_instruction_pin'] && pairingView) {
        const p = pairingView.querySelector('.muted.small');
        if (p) p.textContent = s['connect.manual_instruction_pin'];
      }
      if (s['connect.send'] && pinSubmit) pinSubmit.textContent = s['connect.send'];
    } catch (_) { }
  }
})();
