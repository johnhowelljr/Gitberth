// The built-in Help's pages (Components/Pages/HelpPage.razor), as a module only they load: their ```mermaid diagrams
// drawn with the copy of mermaid in lib/mermaid (fetched once, on the first page that has one), the heading the address
// names brought into view, "On this page" following the scroll, pictures opening larger, and code blocks' copy buttons.
// show() runs after each page is on the screen (OnAfterRenderAsync, and after an enhanced navigation).

let mermaidUrl = '';
let mermaidReady = null;
let count = 0;
let spy = null;
let userScrolled = false;

// Gitberth's colours (app.css :root) for the diagrams; mermaid derives the rest of its dark theme from them.
const palette = {
    canvas: '#090b10', surface1: '#0f1218', surface2: '#151922', surface3: '#1b2029', surface4: '#222835',
    line: '#2b3240', text1: '#eef1f6', text2: '#aab3c2', text3: '#868fa0', text4: '#5d6676',
    accent: '#4c8dff', accentText: '#8fb6ff', violet: '#8b5cf6', good: '#3ddc97', warn: '#f5c04e', critical: '#ff6b75',
};

function loadMermaid() {
    if (mermaidReady) return mermaidReady;
    mermaidReady = new Promise((resolve, reject) => {
        if (window.mermaid) return resolve(window.mermaid);
        const script = document.createElement('script');
        script.src = mermaidUrl;
        script.async = true;
        script.onload = () => window.mermaid ? resolve(window.mermaid) : reject(new Error('mermaid is missing'));
        script.onerror = () => reject(new Error('mermaid did not load'));
        document.head.appendChild(script);
    }).then(mermaid => {
        const font = getComputedStyle(document.documentElement).getPropertyValue('--gb-sans').trim() || 'Inter, system-ui, sans-serif';
        mermaid.initialize({
            startOnLoad: false,
            theme: 'dark',
            securityLevel: 'strict',
            logLevel: 'fatal',
            suppressErrorRendering: true,
            fontFamily: font,
            themeVariables: {
                darkMode: true, fontFamily: font, fontSize: '13px', background: palette.surface1,
                primaryColor: palette.surface3, primaryBorderColor: palette.accent, primaryTextColor: palette.text1,
                secondaryColor: palette.surface2, secondaryBorderColor: palette.line, secondaryTextColor: palette.text1,
                tertiaryColor: palette.surface4, tertiaryBorderColor: palette.line, tertiaryTextColor: palette.text1,
                mainBkg: palette.surface3, nodeBorder: palette.accent, nodeTextColor: palette.text1, textColor: palette.text2,
                lineColor: palette.text3, edgeLabelBackground: palette.surface2, titleColor: palette.text1,
                clusterBkg: palette.surface2, clusterBorder: palette.line,
                actorBkg: palette.surface3, actorBorder: palette.accent, actorTextColor: palette.text1, actorLineColor: palette.text4,
                signalColor: palette.text2, signalTextColor: palette.text1, labelBoxBkgColor: palette.surface3,
                labelBoxBorderColor: palette.line, labelTextColor: palette.text1, loopTextColor: palette.text2,
                noteBkgColor: palette.surface4, noteTextColor: palette.text1, noteBorderColor: palette.line,
                activationBkgColor: palette.surface4, activationBorderColor: palette.accent, sequenceNumberColor: palette.canvas,
            },
            flowchart: { curve: 'basis', padding: 12 },
            sequence: { mirrorActors: false },
        });
        return mermaid;
    });
    // A failed load is tried again on the next page that has a diagram.
    mermaidReady.catch(() => { mermaidReady = null; });
    return mermaidReady;
}

// Each diagram not drawn yet: drawn as SVG, its source hidden; one that can't be drawn keeps its source, with a note.
async function draw() {
    const blocks = [...document.querySelectorAll('.gb-mermaid:not([data-drawn]):not([data-failed])')];
    if (!blocks.length) return;
    let mermaid;
    try {
        mermaid = await loadMermaid();
        await document.fonts?.ready;
    } catch {
        blocks.forEach(fail);
        return;
    }
    for (const block of blocks) {
        if (!block.isConnected || block.hasAttribute('data-drawn')) continue;
        const source = block.querySelector('.gb-mermaid-src')?.textContent ?? '';
        const id = `gb-mermaid-${++count}`;
        try {
            const { svg, bindFunctions } = await mermaid.render(id, source);
            if (!block.isConnected) continue;
            const holder = document.createElement('div');
            holder.className = 'gb-mermaid-svg';
            holder.innerHTML = svg;
            block.appendChild(holder);
            bindFunctions?.(holder);
            fit(holder.querySelector('svg'));
            block.setAttribute('data-drawn', '');
            // A click (or Enter) opens it larger, over the page.
            block.tabIndex = 0;
            block.setAttribute('role', 'button');
            block.setAttribute('aria-label', 'Open the flow chart larger');
        } catch {
            // mermaid's own scratch elements, when it stopped half way.
            document.getElementById(id)?.remove();
            document.getElementById('d' + id)?.remove();
            fail(block);
        }
    }
}

// A chart as wide as the page allows, but never under 80% of its own size, so its words stay readable: a wider one
// scrolls sideways (and opens larger on a click).
function fit(svg) {
    const width = svg?.viewBox?.baseVal?.width;
    if (!width) return;
    svg.removeAttribute('height');
    svg.style.width = '100%';
    svg.style.maxWidth = Math.ceil(width) + 'px';
    svg.style.minWidth = Math.ceil(width * 0.8) + 'px';
    svg.style.height = 'auto';
}

function fail(block) {
    if (block.hasAttribute('data-failed')) return;
    block.setAttribute('data-failed', '');
    const note = document.createElement('p');
    note.className = 'gb-mermaid-note';
    note.textContent = 'This flow chart couldn’t be drawn here, so its source is shown instead.';
    block.insertBefore(note, block.firstChild);
}

// The heading the address names (#own-lan-addresses), under the top bar.
function toHash() {
    const id = decodeURIComponent(location.hash.slice(1));
    if (id) document.getElementById(id)?.scrollIntoView({ block: 'start' });
}

// "On this page": the first heading in view is marked.
function watchHeadings() {
    spy?.disconnect();
    spy = null;
    const links = [...document.querySelectorAll('.gb-helptoc a[data-toc]')];
    if (!links.length || !('IntersectionObserver' in window)) return;
    const seen = new Set();
    spy = new IntersectionObserver(entries => {
        for (const e of entries) {
            if (e.isIntersecting) seen.add(e.target.id);
            else seen.delete(e.target.id);
        }
        const first = links.find(a => seen.has(a.dataset.toc));
        if (first) links.forEach(a => a.classList.toggle('on', a === first));
    }, { rootMargin: '-72px 0px -62% 0px' });
    for (const a of links) {
        const heading = document.getElementById(a.dataset.toc);
        if (heading) spy.observe(heading);
    }
}

export async function show(url) {
    if (url) mermaidUrl = url;
    userScrolled = false;
    toHash();
    watchHeadings();
    await draw();
    // Drawn diagrams change the page's height: back to the heading, unless the reader has moved on.
    if (!userScrolled) toHash();
}

for (const type of ['wheel', 'touchmove', 'keydown']) {
    window.addEventListener(type, () => { userScrolled = true; }, { passive: true });
}

// After an enhanced navigation (a page rendered on the server), the same.
window.Blazor?.addEventListener?.('enhancedload', () => {
    if (document.querySelector('.gb-doc')) show();
});

// ---- The viewer: a picture or a flow chart larger, over the page; a click, Esc or the close button puts it away ----

// content goes in the viewer (and back with putBack when it closes); the focus returns to from.
function view(content, label, from, putBack) {
    const box = document.createElement('div');
    box.className = 'gb-lightbox';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-modal', 'true');
    box.setAttribute('aria-label', label);
    box.appendChild(content);
    const close = box.appendChild(document.createElement('button'));
    close.type = 'button';
    close.className = 'gb-lightbox-close';
    close.setAttribute('aria-label', 'Close');
    close.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>';
    box.appendChild(document.createElement('div')).className = 'gb-lightbox-cap';
    box.lastChild.textContent = label;
    const done = () => {
        putBack?.();
        box.remove();
        document.removeEventListener('keydown', onKey, true);
        from.focus();
    };
    const onKey = e => {
        if (e.key !== 'Escape') return;
        e.stopPropagation();
        e.preventDefault();
        done();
    };
    box.addEventListener('click', done);
    document.addEventListener('keydown', onKey, true);
    document.body.appendChild(box);
    close.focus();
}

function openPicture(link) {
    const small = link.querySelector('img');
    const big = document.createElement('img');
    big.src = link.href;
    big.alt = small?.alt ?? '';
    view(big, small?.alt || 'Picture', link);
}

// The chart itself moves into the viewer and back (a copy would repeat its ids, which its own styles use), at its own
// size or up to half as large again when the window has room, never wider than the window.
function openChart(block) {
    const svg = block.querySelector('.gb-mermaid-svg svg');
    if (!svg) return;
    const holder = svg.parentElement;
    const kept = svg.getAttribute('style');
    const width = svg.viewBox?.baseVal?.width;
    if (width) {
        svg.style.width = Math.ceil(Math.max(width, Math.min(width * 1.5, window.innerWidth - 140))) + 'px';
        svg.style.minWidth = '0';
        svg.style.maxWidth = '100%';
    }
    const frame = document.createElement('div');
    frame.className = 'gb-lightbox-chart';
    frame.appendChild(svg);
    view(frame, 'Flow chart', block, () => {
        if (kept === null) svg.removeAttribute('style');
        else svg.setAttribute('style', kept);
        holder.appendChild(svg);
    });
}

document.addEventListener('click', e => {
    if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
    const link = e.target.closest?.('.gb-doc a.gb-doc-zoom');
    if (link) {
        e.preventDefault();
        openPicture(link);
        return;
    }
    const chart = e.target.closest?.('.gb-doc .gb-mermaid[data-drawn]');
    if (chart && !e.target.closest('a')) openChart(chart);
}, true);

document.addEventListener('keydown', e => {
    const chart = e.target.closest?.('.gb-doc .gb-mermaid[data-drawn]');
    if (!chart || e.target !== chart || (e.key !== 'Enter' && e.key !== ' ')) return;
    e.preventDefault();
    openChart(chart);
});

// ---- Code blocks: Copy ----

document.addEventListener('click', async e => {
    const button = e.target.closest?.('.gb-doc [data-doc-copy]');
    if (!button) return;
    const code = button.closest('.gb-doc-code')?.querySelector('pre')?.innerText ?? '';
    const copied = await window.gitberth?.copy?.(code);
    button.classList.toggle('gb-copybtn--done', !!copied);
    setTimeout(() => button.classList.remove('gb-copybtn--done'), 1400);
});
