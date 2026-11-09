(() => {
  const $ = (id) => document.getElementById(id);
  const picker = $('filePicker');
  const list = $('fileList');
  const token = location.pathname.split('/').filter(Boolean).pop();
  let allFiles = [];
  let currentPath = '';
  let busy = false;
  const MAX_FILE_SIZE = 200 * 1024 * 1024;

  function headers() {
    return {
      'x-pairing-token': token,
      'x-device-role': 'mobile',
    };
  }

  function formatSize(bytes) {
    if (!bytes || bytes < 1024) return `${bytes || 0} B`;
    if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`;
    if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
    return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  }

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  async function request(url, options = {}) {
    const response = await fetch(url, {
      ...options,
      headers: { ...headers(), ...(options.headers || {}) }
    });
    if (!response.ok) {
      const data = await response.json().catch(() => ({}));
      throw new Error(data.error || `Request failed (${response.status})`);
    }
    return response;
  }

  async function refresh() {
    if (!token) return;
    try {
      const response = await request('/api/files');
      const data = await response.json();
      allFiles = data.files || [];
      const storage = data.storage || { used: 0, limit: 0, files: 0 };
      const storageEl = $('storageUsage');
      if (storageEl) {
        storageEl.textContent = `${formatSize(storage.used)} / ${formatSize(storage.limit)}`;
      }
      render();
    } catch (error) {
      if (list) list.replaceChildren(element('div', 'empty-state', error.message));
    }
  }

  window.refreshSharedFiles = refresh;

  function renderBreadcrumbs() {
    const nav = $('breadcrumbs');
    if (!nav) return;
    nav.replaceChildren();

    const root = element('button', 'crumb', 'Shared folder');
    root.type = 'button';
    root.onclick = () => { currentPath = ''; render(); };
    nav.appendChild(root);

    let accumulated = '';
    currentPath.split('/').filter(Boolean).forEach((part) => {
      accumulated = accumulated ? `${accumulated}/${part}` : part;
      nav.appendChild(element('span', 'crumb-separator', ' / '));
      const pathValue = accumulated;
      const button = element('button', 'crumb', part);
      button.type = 'button';
      button.onclick = () => { currentPath = pathValue; render(); };
      nav.appendChild(button);
    });
  }

  function render() {
    renderBreadcrumbs();
    if (!list) return;
    list.replaceChildren();

    const prefix = currentPath ? `${currentPath}/` : '';
    const dirs = new Set();
    const directFiles = [];

    for (const file of allFiles) {
      if (!file.relativePath.startsWith(prefix)) continue;
      const remainder = file.relativePath.slice(prefix.length);
      const slash = remainder.indexOf('/');
      if (slash >= 0) {
        dirs.add(remainder.slice(0, slash));
      } else {
        directFiles.push(file);
      }
    }

    const folders = [...dirs].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
    folders.forEach((name) => {
      const folderPath = prefix + name;
      const count = allFiles.filter((file) => file.relativePath.startsWith(`${folderPath}/`)).length;
      const row = element('div', 'file-row');

      const info = element('div', 'file-info');
      const icon = element('span', 'file-type-icon', '📁');
      const nameBtn = element('button', 'file-name-text folder-name', name);
      nameBtn.type = 'button';
      nameBtn.onclick = () => { currentPath = folderPath; render(); };
      info.append(icon, nameBtn);

      const meta = element('span', 'file-meta-sub', `${count} items`);
      row.append(info, meta);
      list.appendChild(row);
    });

    directFiles.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })).forEach((file) => {
      list.appendChild(fileRow(file));
    });

    if (!folders.length && !directFiles.length) {
      const empty = element('div', 'empty-state');
      empty.textContent = currentPath ? 'This folder is empty.' : 'No shared files yet. Tap "Upload files" to start.';
      list.appendChild(empty);
    }

    const count = folders.length + directFiles.length;
    const countEl = $('itemCount');
    if (countEl) countEl.textContent = `${count} item${count === 1 ? '' : 's'}`;

    const dlFolder = $('downloadFolder');
    if (dlFolder) dlFolder.hidden = !currentPath;
    const delFolder = $('deleteFolder');
    if (delFolder) delFolder.hidden = !currentPath;
  }

  function fileRow(file) {
    const row = element('div', 'file-row');
    const info = element('div', 'file-info');

    const ext = (file.name.split('.').pop() || '').toLowerCase();
    const isImage = file.mimeType?.startsWith('image/');
    const iconChar = isImage ? '🖼️' : ext === 'pdf' ? '📄' : '📎';
    const icon = element('span', 'file-type-icon', iconChar);

    const nameWrapper = element('div', '');
    const name = element('div', 'file-name-text', file.name);
    const sub = element('div', 'file-meta-sub', `${formatSize(file.size)} • ${file.sender === 'mobile' ? 'You' : 'PC'}`);
    nameWrapper.append(name, sub);
    info.append(icon, nameWrapper);

    const actions = element('div', 'file-actions');
    if (isImage || file.mimeType === 'application/pdf' || file.mimeType?.startsWith('text/')) {
      const preview = element('button', 'action-button', 'View');
      preview.type = 'button';
      preview.onclick = () => previewFile(file);
      actions.appendChild(preview);
    }

    const download = element('button', 'action-button', '↓');
    download.type = 'button';
    download.title = 'Download';
    download.onclick = () => downloadFile(file);
    actions.appendChild(download);

    const remove = element('button', 'action-button action-delete', '×');
    remove.type = 'button';
    remove.title = 'Delete';
    remove.onclick = () => deleteFile(file);
    actions.appendChild(remove);

    row.append(info, actions);
    return row;
  }

  function progress(show, label = '', percent = 0) {
    const p = $('uploadProgress');
    if (!p) return;
    p.hidden = !show;
    $('progressLabel').textContent = label;
    $('progressPercent').textContent = `${percent}%`;
    $('progressFill').style.width = `${percent}%`;
  }

  async function uploadFiles(files) {
    if (busy || !files.length) return;
    const oversized = Array.from(files).find(f => f.size > MAX_FILE_SIZE);
    if (oversized) {
      alert(`File "${oversized.name}" exceeds the 200 MB limit.`);
      return;
    }

    busy = true;
    progress(true, 'Preparing upload…', 5);

    try {
      const form = new FormData();
      for (const file of files) {
        const relativePath = currentPath ? `${currentPath}/${file.name}` : file.name;
        form.append('files', file);
        form.append('paths', relativePath);
      }

      const xhr = new XMLHttpRequest();
      xhr.open('POST', '/api/files');
      xhr.setRequestHeader('x-pairing-token', token);
      xhr.setRequestHeader('x-device-role', 'mobile');

      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable) {
          const percent = Math.round((e.loaded / e.total) * 100);
          progress(true, `Uploading (${percent}%)…`, percent);
        }
      };

      await new Promise((resolve, reject) => {
        xhr.onload = () => {
          if (xhr.status >= 200 && xhr.status < 300) {
            resolve();
          } else {
            let message = 'Upload failed';
            try { message = JSON.parse(xhr.responseText).error || message; } catch {}
            reject(new Error(message));
          }
        };
        xhr.onerror = () => reject(new Error('Network error during upload'));
        xhr.send(form);
      });

      progress(true, 'Saved!', 100);
      setTimeout(() => progress(false), 1000);
      await refresh();
    } catch (err) {
      alert(err.message || 'Upload failed');
      progress(false);
    } finally {
      busy = false;
      if (picker) picker.value = '';
    }
  }

  function closePreviewModal() {
    const modal = $('previewModal');
    if (modal) {
      modal.hidden = true;
      modal.style.display = 'none';
      const content = $('previewContent');
      if (content) content.replaceChildren();
    }
  }

  $('closePreview')?.addEventListener('click', closePreviewModal);

  $('previewModal')?.addEventListener('click', (e) => {
    if (e.target === $('previewModal')) {
      closePreviewModal();
    }
  });

  async function previewFile(file) {
    const modal = $('previewModal');
    const title = $('previewTitle');
    const content = $('previewContent');
    if (!modal || !content) return;

    title.textContent = file.name;
    content.replaceChildren(element('div', 'muted small', 'Loading preview…'));
    modal.hidden = false;
    modal.style.display = 'flex';

    const previewUrl = `/api/files/${encodeURIComponent(file.id)}/preview?token=${encodeURIComponent(token)}`;

    if (file.mimeType?.startsWith('image/')) {
      const img = document.createElement('img');
      img.src = previewUrl;
      content.replaceChildren(img);
    } else if (file.mimeType?.startsWith('text/')) {
      try {
        const res = await request(previewUrl);
        const text = await res.text();
        const pre = document.createElement('pre');
        pre.textContent = text.slice(0, 50000);
        content.replaceChildren(pre);
      } catch (err) {
        content.replaceChildren(element('div', 'muted small', 'Preview unavailable.'));
      }
    } else if (file.mimeType === 'application/pdf') {
      const iframe = document.createElement('iframe');
      iframe.src = previewUrl;
      iframe.style.width = '100%';
      iframe.style.height = '60vh';
      iframe.style.border = 'none';
      content.replaceChildren(iframe);
    }
  }

  function downloadFile(file) {
    const a = document.createElement('a');
    a.href = `/api/files/${encodeURIComponent(file.id)}/download?token=${encodeURIComponent(token)}`;
    a.download = file.name;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
  }

  async function deleteFile(file) {
    if (!confirm(`Delete "${file.name}"?`)) return;
    try {
      await request(`/api/files/${encodeURIComponent(file.id)}`, { method: 'DELETE' });
      await refresh();
    } catch (err) {
      alert(err.message || 'Could not delete file');
    }
  }

  $('downloadFolder')?.addEventListener('click', () => {
    if (!currentPath) return;
    const a = document.createElement('a');
    a.href = `/api/folder.zip?path=${encodeURIComponent(currentPath)}&token=${encodeURIComponent(token)}`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
  });

  $('deleteFolder')?.addEventListener('click', async () => {
    if (!currentPath || !confirm(`Delete folder "${currentPath}" and all its contents?`)) return;
    try {
      await request(`/api/folder?path=${encodeURIComponent(currentPath)}`, { method: 'DELETE' });
      currentPath = '';
      await refresh();
    } catch (err) {
      alert(err.message || 'Could not delete folder');
    }
  });

  $('chooseFiles')?.addEventListener('click', () => picker?.click());
  picker?.addEventListener('change', () => {
    if (picker.files && picker.files.length) {
      uploadFiles(picker.files);
    }
  });

  $('refreshFiles')?.addEventListener('click', refresh);

  window.addEventListener('filee:files-changed', () => {
    refresh();
  });

  refresh();
})();
