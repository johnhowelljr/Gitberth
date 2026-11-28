// The app tools of builder B: uploaded folders (New app's Upload a folder, an app's Upload a new version), and later the
// terminal and the files. Each sends straight to Gitberth (never through the page's own connection), with the sign-out
// form's antiforgery token first (App.razor), so Gitberth checks it before it reads a byte of a file.
(() => {
    const token = () => document.querySelector('#gb-logout input[name="__RequestVerificationToken"]')?.value ?? '';
    const archive = /\.(zip|tar|tgz|tar\.gz)$/i;
    const maxUpload = 2 * 1024 * 1024 * 1024;

    // What a folder upload leaves out (Gitberth leaves the same out again): Git's folder, installed packages and caches
    // the build makes again, and macOS's .DS_Store.
    function leftOut(path) {
        const parts = path.split('/');
        for (let i = 0; i < parts.length; i++) {
            const p = parts[i];
            if (p === '.git' || p === 'node_modules' || p === '.venv' || p === 'venv' || p === '__pycache__') return true;
            if (p === '.next' && parts[i + 1] === 'cache') return true;
        }
        return parts[parts.length - 1] === '.DS_Store';
    }

    // Every file of a dropped folder with its path in it ("site/css/app.css"), its left-out folders never walked.
    async function walk(entry, into) {
        const path = entry.fullPath.replace(/^\/+/, '');
        if (leftOut(path)) return;
        if (entry.isFile) {
            const file = await new Promise((ok, fail) => entry.file(ok, fail));
            into.push({ file, path });
            return;
        }
        const reader = entry.createReader();
        for (;;) {
            const batch = await new Promise((ok, fail) => reader.readEntries(ok, fail));
            if (batch.length === 0) break;
            for (const child of batch) await walk(child, into);
        }
    }

    let sending = null;

    function send(form, url, total, dotnet) {
        const xhr = new XMLHttpRequest();
        sending = xhr;
        let last = 0;
        xhr.upload.onprogress = e => {
            const now = performance.now();
            if (now - last < 250 && e.loaded < e.total) return;
            last = now;
            dotnet.invokeMethodAsync('Uploading', e.loaded, e.lengthComputable ? e.total : total).catch(() => { });
        };
        const end = aborted => {
            if (sending === xhr) sending = null;
            const ok = !aborted && xhr.status >= 200 && xhr.status < 300;
            dotnet.invokeMethodAsync('Uploaded', ok, aborted, xhr.status, (xhr.responseText || '').slice(0, 2000)).catch(() => { });
        };
        xhr.onload = () => end(false);
        xhr.onerror = () => end(false);
        xhr.onabort = () => end(true);
        xhr.open('POST', url);
        xhr.send(form);
    }

    function refuse(dotnet, sentence) {
        dotnet.invokeMethodAsync('Refused', sentence).catch(() => { });
    }

    function sendArchive(file, url, dotnet) {
        if (!archive.test(file.name)) return refuse(dotnet, 'Upload a .zip, .tar, .tar.gz or .tgz file, or choose a folder.');
        if (file.size > maxUpload) return refuse(dotnet, 'The file is larger than 2 GiB, the most Gitberth takes.');
        const form = new FormData();
        form.append('__RequestVerificationToken', token());
        form.append('kind', 'archive');
        form.append('file', file, file.name);
        dotnet.invokeMethodAsync('Starting', file.name, file.size).catch(() => { });
        send(form, url, file.size, dotnet);
    }

    function sendFolder(files, url, dotnet) {
        const kept = files.filter(f => !leftOut(f.path));
        if (kept.length === 0) return refuse(dotnet, files.length === 0 ? 'The folder is empty.' : 'Every file of the folder is one Gitberth leaves out (.git, node_modules and the like): there\'s nothing to deploy.');
        const total = kept.reduce((n, f) => n + f.file.size, 0);
        if (total > maxUpload) return refuse(dotnet, 'The folder is larger than 2 GiB, the most Gitberth takes.');
        if (kept.length > 200000) return refuse(dotnet, 'The folder has more than 200,000 files.');
        const form = new FormData();
        form.append('__RequestVerificationToken', token());
        form.append('kind', 'folder');
        for (const f of kept) form.append('file', f.file, f.path);
        dotnet.invokeMethodAsync('Starting', kept[0].path.split('/')[0], total).catch(() => { });
        send(form, url, total, dotnet);
    }

    window.gitberthUploads = {
        // A drop zone and its two pickers: a drop of one folder or one archive, or either picker's choice, is sent to url.
        // The component hears Starting(name, size), Uploading(sent, total), Uploaded(ok, aborted, status, answer) and
        // Refused(sentence).
        bind(zoneId, fileId, folderId, url, dotnet) {
            const zone = document.getElementById(zoneId);
            const fileInput = document.getElementById(fileId);
            const folderInput = document.getElementById(folderId);
            if (!zone || zone.gbUpload) return;
            zone.gbUpload = { url, dotnet };
            const busy = () => sending !== null;
            fileInput?.addEventListener('change', () => {
                const file = fileInput.files?.[0];
                fileInput.value = '';
                if (file && !busy()) sendArchive(file, zone.gbUpload.url, dotnet);
            });
            folderInput?.addEventListener('change', () => {
                const files = Array.from(folderInput.files ?? []).map(file => ({ file, path: file.webkitRelativePath || file.name }));
                folderInput.value = '';
                if (files.length > 0 && !busy()) sendFolder(files, zone.gbUpload.url, dotnet);
            });
            zone.addEventListener('dragover', e => {
                e.preventDefault();
                zone.classList.add('gb-dropzone--over');
            });
            zone.addEventListener('dragleave', () => zone.classList.remove('gb-dropzone--over'));
            zone.addEventListener('drop', async e => {
                e.preventDefault();
                zone.classList.remove('gb-dropzone--over');
                if (busy()) return;
                const items = Array.from(e.dataTransfer?.items ?? []).filter(i => i.kind === 'file');
                const entries = items.map(i => i.webkitGetAsEntry?.()).filter(Boolean);
                if (entries.length === 1 && entries[0].isDirectory) {
                    const files = [];
                    try {
                        await walk(entries[0], files);
                    } catch {
                        return refuse(dotnet, 'The browser couldn\'t read that folder. Choose it with Choose a folder instead.');
                    }
                    return sendFolder(files, zone.gbUpload.url, dotnet);
                }
                const dropped = Array.from(e.dataTransfer?.files ?? []);
                if (dropped.length === 1) return sendArchive(dropped[0], zone.gbUpload.url, dotnet);
                refuse(dotnet, 'Drop one folder, or one .zip, .tar, .tar.gz or .tgz file.');
            });
        },

        // Opens a picker by its input's id (the buttons' click).
        pick(inputId) {
            document.getElementById(inputId)?.click();
        },

        cancel() {
            sending?.abort();
        }
    };

    // ---- The terminal (B3): xterm.js, loaded the first time a terminal opens, on Gitberth's WebSocket ----

    let xtermLoading = null;

    function loadXterm() {
        if (window.Terminal && window.FitAddon) return Promise.resolve();
        if (xtermLoading) return xtermLoading;
        const script = src => new Promise((ok, fail) => {
            const s = document.createElement('script');
            s.src = src;
            s.onload = ok;
            s.onerror = () => fail(new Error('The terminal\'s script didn\'t load: reload the page.'));
            document.head.appendChild(s);
        });
        if (!document.getElementById('gb-xterm-css')) {
            const link = document.createElement('link');
            link.id = 'gb-xterm-css';
            link.rel = 'stylesheet';
            link.href = 'lib/xterm/xterm.css';
            document.head.appendChild(link);
        }
        xtermLoading = script('lib/xterm/xterm.js').then(() => script('lib/xterm/addon-fit.js'));
        return xtermLoading;
    }

    const terminals = new Map();

    function theme() {
        const css = getComputedStyle(document.documentElement);
        const v = name => css.getPropertyValue(name).trim();
        return {
            background: v('--gb-inset') || '#07090c', foreground: v('--gb-text-1') || '#eef1f6', cursor: v('--gb-accent-text') || '#8fb6ff',
            selectionBackground: 'rgba(76, 141, 255, .35)',
        };
    }

    window.gitberthTerminal = {
        // Opens a terminal in the element and connects it to /terminal with the one-time ticket. The component hears
        // Connected(), Ended(sentence or null) and nothing of what's typed.
        async open(elementId, ticket, dotnet) {
            this.close(elementId);
            const el = document.getElementById(elementId);
            if (!el) return;
            try {
                await loadXterm();
            } catch (e) {
                dotnet.invokeMethodAsync('Ended', e.message).catch(() => { });
                return;
            }
            el.innerHTML = '';
            const term = new window.Terminal({
                cursorBlink: true, fontFamily: getComputedStyle(document.documentElement).getPropertyValue('--gb-mono').trim() || 'monospace',
                fontSize: 13, scrollback: 5000, theme: theme(), convertEol: false,
            });
            const fit = new window.FitAddon.FitAddon();
            term.loadAddon(fit);
            term.open(el);
            try { fit.fit(); } catch { }
            const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
            const ws = new WebSocket(`${scheme}//${location.host}/terminal?ticket=${encodeURIComponent(ticket)}`);
            ws.binaryType = 'arraybuffer';
            const encoder = new TextEncoder();
            let said = null;
            const size = () => {
                try { fit.fit(); } catch { }
                if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
            };
            ws.onopen = () => {
                size();
                term.focus();
                dotnet.invokeMethodAsync('Connected').catch(() => { });
            };
            ws.onmessage = e => {
                if (typeof e.data !== 'string') {
                    term.write(new Uint8Array(e.data));
                    return;
                }
                try {
                    const m = JSON.parse(e.data);
                    said = m.type === 'exit'
                        ? (m.code === null || m.code === undefined ? 'The shell ended.' : `The shell ended (exit code ${m.code}).`)
                        : m.message;
                    if (said) term.write(`\r\n\x1b[2m${said}\x1b[0m\r\n`);
                } catch { }
            };
            ws.onclose = e => {
                const t = terminals.get(elementId);
                if (t?.ws === ws) {
                    t.observer.disconnect();
                    terminals.delete(elementId);
                }
                term.options.disableStdin = true;
                const why = said ?? (e.code === 1000 ? 'The terminal closed.' : 'The connection to Gitberth was lost.');
                dotnet.invokeMethodAsync('Ended', why).catch(() => { });
            };
            term.onData(d => {
                if (ws.readyState === WebSocket.OPEN) ws.send(encoder.encode(d));
            });
            term.onBinary(d => {
                if (ws.readyState !== WebSocket.OPEN) return;
                const bytes = new Uint8Array(d.length);
                for (let i = 0; i < d.length; i++) bytes[i] = d.charCodeAt(i) & 255;
                ws.send(bytes);
            });
            let pending = 0;
            const observer = new ResizeObserver(() => {
                clearTimeout(pending);
                pending = setTimeout(size, 100);
            });
            observer.observe(el);
            terminals.set(elementId, { term, ws, observer });
        },

        // Closes the element's terminal (Disconnect, or the tab left): the shell is hung up.
        close(elementId) {
            const t = terminals.get(elementId);
            if (!t) return;
            terminals.delete(elementId);
            t.observer.disconnect();
            try { t.ws.close(1000, 'closed'); } catch { }
        },

        focus(elementId) {
            terminals.get(elementId)?.term.focus();
        }
    };

    // ---- An app's files (B3): uploads into the folder shown, and downloads, each with a one-time URL from the page ----

    function uploadFiles(files, zone) {
        const state = zone.gbFiles;
        if (!state || files.length === 0 || state.busy) return;
        state.busy = true;
        const { dotnet } = state;
        dotnet.invokeMethodAsync('UploadUrl').then(url => {
            const form = new FormData();
            for (const f of files) form.append('file', f, f.name);
            const xhr = new XMLHttpRequest();
            const end = () => {
                state.busy = false;
                dotnet.invokeMethodAsync('Uploaded', xhr.status >= 200 && xhr.status < 300, xhr.status, (xhr.responseText || '').slice(0, 4000)).catch(() => { });
            };
            xhr.onload = end;
            xhr.onerror = end;
            xhr.open('POST', url);
            xhr.send(form);
        }).catch(() => { state.busy = false; });
    }

    window.gitberthFiles = {
        // The Files tab's drop zone and its file picker: what's dropped or chosen goes into the folder shown.
        bind(zoneId, inputId, dotnet) {
            const zone = document.getElementById(zoneId);
            const input = document.getElementById(inputId);
            if (!zone) return;
            if (zone.gbFiles) {
                zone.gbFiles.dotnet = dotnet;
                return;
            }
            zone.gbFiles = { dotnet, busy: false };
            input?.addEventListener('change', () => {
                const files = Array.from(input.files ?? []);
                input.value = '';
                uploadFiles(files, zone);
            });
            zone.addEventListener('dragover', e => {
                e.preventDefault();
                zone.classList.add('gb-dropzone--over');
            });
            zone.addEventListener('dragleave', () => zone.classList.remove('gb-dropzone--over'));
            zone.addEventListener('drop', e => {
                e.preventDefault();
                zone.classList.remove('gb-dropzone--over');
                const files = Array.from(e.dataTransfer?.items ?? [])
                    .filter(i => i.kind === 'file' && !i.webkitGetAsEntry?.()?.isDirectory)
                    .map(i => i.getAsFile()).filter(Boolean);
                uploadFiles(files, zone);
            });
        },

        unbind(zoneId) {
            const zone = document.getElementById(zoneId);
            if (zone) zone.gbFiles = null;
        },

        pick(inputId) {
            document.getElementById(inputId)?.click();
        },

        // A download: the browser saves what the one-time URL answers.
        download(url) {
            const a = document.createElement('a');
            a.href = url;
            a.rel = 'noopener';
            a.download = '';
            document.body.appendChild(a);
            a.click();
            a.remove();
        }
    };
})();
