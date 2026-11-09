(() => {
  const $ = (id) => document.getElementById(id);
  const picker = $('filePicker');
  const chooseBtn = $('chooseFiles');
  const list = $('fileList');
  const downloadFolderBtn = $('downloadFolder');
  const refreshBtn = $('refreshFiles');

  let allFiles = [];
  let currentPath = '';

  function headers() {
    const tok = window.getFileeToken ? window.getFileeToken() : '';
    const h = {};
    if (tok) {
      h['Authorization'] = 'Bearer ' + tok;
    }
    return h;
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
    const tok = window.getFileeToken ? window.getFileeToken() : '';
    if (!tok) return;

    try {
      const response = await request('/api/files');
      const data = await response.json();
      allFiles = data.files || [];
      const usage = data.usage || { used: 0, limit: 0, files: 0 };
      const storageEl = $('storageUsage');
      if (storageEl) {
        storageEl.textContent = `${formatSize(usage.used)} / ${formatSize(usage.limit)}`;
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
        directFiles.Add ? directFiles.Add(file) : directFiles.push(file);
      }
    }

    const itemCountEl = $('itemCount');
    if (itemCountEl) {
      itemCountEl.textContent = `${dirs.size + directFiles.length} items`;
    }

    if (downloadFolderBtn) {
      downloadFolderBtn.hidden = allFiles.length === 0;
    }

    if (dirs.size === 0 && directFiles.length === 0) {
      list.appendChild(element('div', 'empty-state', 'No shared files in this folder yet.'));
      return;
    }

    // Folders first
    Array.from(dirs).sort().forEach((dirName) => {
      const row = element('div', 'file-row');
      const info = element('div', 'file-info');
      const icon = element('span', 'file-icon', '📁');
      const text = element('div', 'file-meta');
      const name = element('span', 'file-name', dirName);
      const sub = element('span', 'file-sub', 'Folder');

      text.append(name, sub);
      info.append(icon, text);
      row.appendChild(info);

      row.onclick = () => {
        currentPath = prefix ? `${prefix}${dirName}` : dirName;
        render();
      };

      list.appendChild(row);
    });

    // Files
    directFiles.sort((a, b) => a.name.localeCompare(b.name)).forEach((file) => {
      const row = element('div', 'file-row');
      const info = element('div', 'file-info');

      let iconGlyph = '📎';
      if (file.icon === 'image') iconGlyph = '🖼️';
      else if (file.icon === 'pdf') iconGlyph = '📄';
      else if (file.icon === 'audio') iconGlyph = '🎵';
      else if (file.icon === 'video') iconGlyph = '🎬';
      else if (file.icon === 'text') iconGlyph = '📝';

      const icon = element('span', 'file-icon', iconGlyph);
      const text = element('div', 'file-meta');
      const name = element('span', 'file-name', file.name);
      const sub = element('span', 'file-sub', `${formatSize(file.size)} • ${file.sender === 'mobile' ? 'You' : 'PC'}`);

      text.append(name, sub);
      info.append(icon, text);

      const actions = element('div', 'file-actions');
      const tok = window.getFileeToken ? window.getFileeToken() : '';

      // Preview button for images & pdf
      if (file.icon === 'image' || file.icon === 'pdf' || file.icon === 'text') {
        const previewBtn = element('button', 'button button-quiet button-sm', 'View');
        previewBtn.type = 'button';
        previewBtn.onclick = (e) => {
          e.stopPropagation();
          showPreview(file);
        };
        actions.appendChild(previewBtn);
      }

      const downloadLink = element('a', 'button button-secondary button-sm', '↓');
      downloadLink.href = `/download/${encodeURIComponent(file.id)}?token=${encodeURIComponent(tok)}`;
      downloadLink.download = file.name;
      downloadLink.onclick = (e) => e.stopPropagation();
      actions.appendChild(downloadLink);

      row.append(info, actions);
      list.appendChild(row);
    });
  }

  function showPreview(file) {
    const modal = $('previewModal');
    const title = $('previewTitle');
    const content = $('previewContent');
    const tok = window.getFileeToken ? window.getFileeToken() : '';

    if (!modal || !content) return;

    if (title) title.textContent = file.name;
    content.replaceChildren();

    const previewUrl = `/preview/${encodeURIComponent(file.id)}?token=${encodeURIComponent(tok)}`;

    if (file.icon === 'image') {
      const img = document.createElement('img');
      img.src = previewUrl;
      img.alt = file.name;
      img.style.maxWidth = '100%';
      img.style.maxHeight = '70vh';
      img.style.borderRadius = '8px';
      content.appendChild(img);
    } else if (file.icon === 'pdf') {
      const iframe = document.createElement('iframe');
      iframe.src = previewUrl;
      iframe.style.width = '100%';
      iframe.style.height = '70vh';
      iframe.style.border = 'none';
      iframe.style.borderRadius = '8px';
      content.appendChild(iframe);
    } else {
      const iframe = document.createElement('iframe');
      iframe.src = previewUrl;
      iframe.style.width = '100%';
      iframe.style.height = '60vh';
      iframe.style.border = 'none';
      content.appendChild(iframe);
    }

    modal.hidden = false;
    modal.style.display = 'flex';
  }

  const closePreviewBtn = $('closePreview');
  closePreviewBtn?.addEventListener('click', () => {
    const modal = $('previewModal');
    if (modal) {
      modal.hidden = true;
      modal.style.display = 'none';
      const content = $('previewContent');
      if (content) content.replaceChildren();
    }
  });

  async function uploadFiles(fileList) {
    const total = fileList.length;
    if (!total) return;

    const progressBox = $('uploadProgress');
    const progressLabel = $('progressLabel');
    const progressFill = $('progressFill');
    const progressPercent = $('progressPercent');

    if (progressBox) progressBox.hidden = false;

    for (let i = 0; i < total; i++) {
      const file = fileList[i];
      if (progressLabel) progressLabel.textContent = `Uploading ${i + 1} of ${total}: ${file.name}`;
      const pct = Math.round((i / total) * 100);
      if (progressFill) progressFill.style.width = pct + '%';
      if (progressPercent) progressPercent.textContent = pct + '%';

      const formData = new FormData();
      formData.append('file', file);
      formData.append('relativePath', currentPath ? `${currentPath}/${file.name}` : file.name);

      try {
        await request('/api/upload', {
          method: 'POST',
          body: formData
        });
      } catch (err) {
        alert(`Failed to upload ${file.name}: ${err.message}`);
      }
    }

    if (progressFill) progressFill.style.width = '100%';
    if (progressPercent) progressPercent.textContent = '100%';
    setTimeout(() => {
      if (progressBox) progressBox.hidden = true;
    }, 600);

    await refresh();
  }

  chooseBtn?.addEventListener('click', () => {
    if (picker) picker.click();
  });

  picker?.addEventListener('change', () => {
    if (picker.files && picker.files.length) {
      uploadFiles(Array.from(picker.files));
      picker.value = '';
    }
  });

  downloadFolderBtn?.addEventListener('click', () => {
    const tok = window.getFileeToken ? window.getFileeToken() : '';
    location.href = `/api/folder.zip?token=${encodeURIComponent(tok)}`;
  });

  refreshBtn?.addEventListener('click', refresh);
})();
