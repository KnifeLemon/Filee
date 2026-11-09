(() => {
  const $ = (id) => document.getElementById(id);
  const status = $('status');
  const dot = $('dot');
  const messages = $('messages');
  const input = $('input');
  const send = $('send');
  const pasteBtn = $('pasteClipboard');
  const token = location.pathname.split('/').filter(Boolean).pop();

  let socket;
  let reconnectTimer;
  let ended = false;

  // Tabs
  const tabBtnText = $('tabBtnText');
  const tabBtnFiles = $('tabBtnFiles');
  const tabText = $('tabText');
  const tabFiles = $('tabFiles');

  function switchTab(target) {
    if (target === 'tabText') {
      tabBtnText.classList.add('active');
      tabBtnFiles.classList.remove('active');
      tabText.classList.add('active');
      tabFiles.classList.remove('active');
    } else {
      tabBtnFiles.classList.add('active');
      tabBtnText.classList.remove('active');
      tabFiles.classList.add('active');
      tabText.classList.remove('active');
      if (window.refreshSharedFiles) window.refreshSharedFiles();
    }
  }

  tabBtnText.addEventListener('click', () => switchTab('tabText'));
  tabBtnFiles.addEventListener('click', () => switchTab('tabFiles'));

  function getDeviceName() {
    const ua = navigator.userAgent || '';
    if (/iPhone/i.test(ua)) return 'iPhone';
    if (/iPad/i.test(ua)) return 'iPad';
    if (/Android/i.test(ua)) {
      const match = ua.match(/;\s*([^;]+)\s*Build/);
      if (match && match[1]) return match[1].trim();
      return 'Android Device';
    }
    if (/Macintosh/i.test(ua)) return 'Mac';
    if (/Windows/i.test(ua)) return 'Windows PC';
    return 'Mobile Device';
  }

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
        // Fallback for older webviews
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
    if (ended || !token) return;
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const deviceName = encodeURIComponent(getDeviceName());
    socket = new WebSocket(`${scheme}//${location.host}/ws?token=${encodeURIComponent(token)}&role=mobile&deviceName=${deviceName}`);

    socket.onopen = () => {
      setStatus('Connected to PC', true);
    };

    socket.onmessage = ({ data: raw }) => {
      let data;
      try { data = JSON.parse(raw); } catch { return; }

      if (data.type === 'initial-state') {
        messages.replaceChildren();
        if (!data.history || !data.history.length) {
          messages.innerHTML = '<div class="empty-state">No messages in this session yet.</div>';
        } else {
          data.history.forEach(addMessage);
        }
      } else if (data.type === 'new-message') {
        addMessage(data.message);
      } else if (data.type === 'files-changed') {
        window.dispatchEvent(new CustomEvent('filee:files-changed', { detail: data }));
      } else if (data.type === 'session-ended') {
        endUI(data.reason || 'Session ended');
      }
    };

    socket.onclose = (event) => {
      if (event.code === 4001) return endUI('Session ended');
      if (!ended) {
        setStatus('Reconnecting…', false);
        reconnectTimer = setTimeout(connect, 2000);
      }
    };

    socket.onerror = () => socket.close();
  }

  function sendMessage() {
    if (socket?.readyState !== WebSocket.OPEN) {
      alert('Connection lost. Please wait for reconnection.');
      return;
    }
    const text = input.value.trim();
    if (!text) return;
    socket.send(JSON.stringify({ type: 'text', text }));
    input.value = '';
    input.focus();
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
        if (text) {
          input.value = input.value ? input.value + '\n' + text : text;
          input.focus();
          return;
        }
      }
    } catch (_) { }

    const text = prompt('Paste your copied text here:');
    if (text) {
      input.value = input.value ? input.value + '\n' + text : text;
    }
    input.focus();
  });

  window.fileeSessionToken = token;
  connect();
})();
