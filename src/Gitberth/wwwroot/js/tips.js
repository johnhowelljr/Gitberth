// Tips: a card that explains a label of the UI (Components/Shared/Tip.razor and the shared components' Tip parameter).
// A trigger is any element with data-tip="<key>" (a tip from the catalog, Help/Tips/*.yaml) or data-tip-text="<words>"
// (a short note of its own); data-tip-detail adds a live line. Everything is delegated from document, so triggers Blazor
// renders or re-renders later just work. The catalog comes once (help-tips.json, cached by version), so hovering never
// waits on the server.
//   - Hover: the card opens after 200 ms and stays while the pointer is on the trigger or the card (a short grace between
//     them), so its links can be clicked.
//   - Keyboard focus: it opens at once. Enter on a tip's own trigger (dotted words, an info button) follows "Learn more".
//   - Touch: a tap opens it (a button's or a link's click still happens); an info button's tap toggles it.
//   - Esc, a scroll, a click elsewhere, typing in the trigger or a navigation closes it.
// One card for the whole page, appended to body (role="tooltip"), above the trigger if there's room, else below, kept
// inside the viewport, with an arrow at the trigger.
(() => {
    const catalogUrl = document.currentScript?.dataset.catalog;
    const TRIGGER = '[data-tip], [data-tip-text]';
    const OPEN_DELAY = 200;
    const SWITCH_DELAY = 70;
    const GRACE = 180;
    const MARGIN = 8;
    const GAP = 10;

    let catalog = null;
    let loading = null;
    let card = null;
    let current = null;
    let wanted = null;
    let openTimer = 0;
    let closeTimer = 0;
    let watchTimer = 0;
    let openedAt = '';
    let openedTime = 0;
    let lastPointer = 'mouse';
    let lastPointerTime = -1e9;
    const describedBy = new WeakMap();

    // ---- the catalog ----

    function load() {
        if (catalog || !catalogUrl) return Promise.resolve(catalog);
        loading ??= fetch(catalogUrl, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
            .then(r => r.ok && (r.headers.get('content-type') || '').includes('json') ? r.json() : null)
            .then(json => { catalog = json?.tips ? json : null; return catalog; })
            .catch(() => null)
            .finally(() => { loading = null; });
        return loading;
    }

    // ---- the card ----

    const el = (tag, className, text) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    };

    const svg = path => {
        const ns = 'http://www.w3.org/2000/svg';
        const icon = document.createElementNS(ns, 'svg');
        icon.setAttribute('viewBox', '0 0 24 24');
        icon.setAttribute('aria-hidden', 'true');
        icon.setAttribute('focusable', 'false');
        const p = icon.appendChild(document.createElementNS(ns, 'path'));
        p.setAttribute('d', path);
        return icon;
    };

    const INFO = 'M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z';

    function ensureCard() {
        if (card?.isConnected) return card;
        card = el('div', 'gb-tipcard');
        card.id = 'gb-tipcard';
        card.setAttribute('role', 'tooltip');
        card.hidden = true;
        card.addEventListener('pointerenter', () => clearTimeout(closeTimer));
        card.addEventListener('pointerleave', e => {
            if (!(current && e.relatedTarget instanceof Node && current.contains(e.relatedTarget))) scheduleClose();
        });
        // A link in the card (Learn more, or one in its text) leads somewhere else: the card goes with it.
        card.addEventListener('click', e => {
            if (e.target.closest?.('a')) setTimeout(close, 0);
        });
        document.body.appendChild(card);
        return card;
    }

    // What the card says: the tip's title, text, default and example, the live line, and "Learn more"; or, for a note of
    // its own (data-tip-text), just its words. False when there's nothing to show (a key the catalog doesn't have).
    function fill(trigger) {
        const key = trigger.getAttribute('data-tip');
        const words = trigger.getAttribute('data-tip-text');
        const detail = trigger.getAttribute('data-tip-detail');
        const tip = key ? catalog?.tips?.[key] : null;
        if (!tip && !words) return false;
        card.replaceChildren();
        card.classList.toggle('gb-tipcard--plain', !tip);
        if (tip) {
            const head = el('div', 'gb-tipcard-head');
            const badge = head.appendChild(el('span', 'gb-tipcard-icon'));
            badge.appendChild(svg(INFO));
            head.appendChild(el('span', 'gb-tipcard-title', tip.title));
            card.appendChild(head);
            // Safe HTML: escaped and marked up by Gitberth itself (TipText), from its own catalog.
            const text = card.appendChild(el('div', 'gb-tipcard-text'));
            text.innerHTML = tip.html;
            if (tip.default || tip.example) {
                const facts = card.appendChild(el('dl', 'gb-tipcard-facts'));
                if (tip.default) facts.append(el('dt', null, 'Default'), el('dd', null, tip.default));
                if (tip.example) facts.append(el('dt', null, 'Example'), el('dd', null, tip.example));
            }
        } else {
            card.appendChild(el('div', 'gb-tipcard-text', words));
        }
        if (detail) card.appendChild(el('div', 'gb-tipcard-detail', detail));
        if (tip?.more) {
            const foot = card.appendChild(el('div', 'gb-tipcard-foot'));
            const more = foot.appendChild(el('a', 'gb-tipcard-more', 'Learn more'));
            more.href = tip.more;
            if (tip.moreTitle) more.setAttribute('aria-label', `Learn more: ${tip.moreTitle}`);
            more.appendChild(el('span', 'gb-tipcard-go', ' →')).setAttribute('aria-hidden', 'true');
            if (tip.moreTitle) foot.appendChild(el('span', 'gb-tipcard-page', tip.moreTitle));
        }
        card.appendChild(el('span', 'gb-tipcard-arrow')).setAttribute('aria-hidden', 'true');
        return true;
    }

    // Above the trigger when it fits, else below; shifted along to stay inside the viewport; the arrow at the trigger.
    function place(trigger) {
        const rects = trigger.getClientRects();
        if (!rects.length) return false;
        const first = rects[0];
        const last = rects[rects.length - 1];
        const vw = document.documentElement.clientWidth;
        const vh = document.documentElement.clientHeight;
        card.style.left = '0px';
        card.style.top = '0px';
        const cw = card.offsetWidth;
        const ch = card.offsetHeight;
        let side = 'top';
        let top = first.top - GAP - ch;
        if (top < MARGIN && (last.bottom + GAP + ch <= vh - MARGIN || vh - last.bottom > first.top)) {
            side = 'bottom';
            top = last.bottom + GAP;
        }
        top = Math.max(MARGIN, Math.min(top, vh - MARGIN - ch));
        const at = side === 'top' ? first : last;
        const cx = at.left + at.width / 2;
        const left = Math.round(Math.max(MARGIN, Math.min(cx - cw / 2, vw - MARGIN - cw)));
        const arrow = Math.round(Math.max(16, Math.min(cx - left, cw - 16)));
        card.style.left = left + 'px';
        card.style.top = Math.round(top) + 'px';
        card.dataset.side = side;
        card.style.setProperty('--gb-tip-arrow', arrow + 'px');
        return true;
    }

    function describe(trigger) {
        const own = trigger.getAttribute('aria-describedby');
        describedBy.set(trigger, own);
        trigger.setAttribute('aria-describedby', own ? `${own} gb-tipcard` : 'gb-tipcard');
    }

    function undescribe(trigger) {
        if (!describedBy.has(trigger)) return;
        const own = describedBy.get(trigger);
        describedBy.delete(trigger);
        if (own) trigger.setAttribute('aria-describedby', own);
        else trigger.removeAttribute('aria-describedby');
    }

    async function open(trigger) {
        clearTimeout(openTimer);
        clearTimeout(closeTimer);
        wanted = trigger;
        if (trigger === current && card && !card.hidden) return;
        if (!catalog && trigger.hasAttribute('data-tip')) await load();
        // The catalog took a moment: the pointer or the focus may have moved on meanwhile.
        if (wanted !== trigger || !trigger.isConnected) return;
        ensureCard();
        if (!fill(trigger)) {
            close();
            return;
        }
        if (current && current !== trigger) undescribe(current);
        current = trigger;
        card.classList.remove('on');
        card.hidden = false;
        if (!place(trigger)) {
            close();
            return;
        }
        describe(trigger);
        requestAnimationFrame(() => { if (current === trigger) card.classList.add('on'); });
        // Gone with its trigger (Blazor re-rendered it away) or with the page (a navigation).
        openedAt = location.href;
        openedTime = performance.now();
        clearInterval(watchTimer);
        watchTimer = setInterval(() => {
            if (!current?.isConnected || location.href !== openedAt) close();
        }, 250);
    }

    function close() {
        clearTimeout(openTimer);
        clearTimeout(closeTimer);
        clearInterval(watchTimer);
        if (current) undescribe(current);
        current = null;
        wanted = null;
        if (card) {
            card.classList.remove('on');
            card.hidden = true;
        }
    }

    function scheduleClose() {
        clearTimeout(closeTimer);
        closeTimer = setTimeout(close, GRACE);
    }

    const isOpen = () => !!(current && card && !card.hidden);

    // ---- pointer ----

    // A press anywhere else closes the card; so does one on a button or a link that has a tip, whose own action (a menu,
    // another page) takes over, and its card doesn't come back until the pointer has left it. Words with a tip and info
    // buttons keep it.
    let pressed = null;
    document.addEventListener('pointerdown', e => {
        lastPointer = e.pointerType || 'mouse';
        lastPointerTime = performance.now();
        const trigger = e.target.closest?.(TRIGGER);
        if (trigger && !trigger.matches('.gb-tipped, .gb-tipicon') && !card?.contains(e.target)) {
            pressed = trigger;
            clearTimeout(openTimer);
        }
        if (!isOpen() || card.contains(e.target)) return;
        if (!current.contains(e.target) || !current.matches('.gb-tipped, .gb-tipicon')) close();
    }, true);

    document.addEventListener('pointerover', e => {
        if (e.pointerType === 'touch') return;
        if (card?.contains(e.target)) {
            clearTimeout(closeTimer);
            return;
        }
        const trigger = e.target.closest?.(TRIGGER);
        if (!trigger || trigger === pressed) return;
        if (trigger === current) {
            clearTimeout(closeTimer);
            return;
        }
        clearTimeout(openTimer);
        openTimer = setTimeout(() => open(trigger), isOpen() ? SWITCH_DELAY : OPEN_DELAY);
    });

    document.addEventListener('pointerout', e => {
        if (e.pointerType === 'touch') return;
        const trigger = e.target.closest?.(TRIGGER);
        if (!trigger) return;
        const to = e.relatedTarget;
        if (to instanceof Node && (trigger.contains(to) || card?.contains(to))) return;
        if (trigger === pressed) pressed = null;
        clearTimeout(openTimer);
        if (wanted === trigger && trigger !== current) wanted = null;
        if (trigger === current) scheduleClose();
    });

    // A tap opens a tip without stopping what the tap does (a second tap on an info button closes it), except on a button
    // that opens a menu (aria-haspopup), which the card would cover; a click on an info button opens its card at once.
    document.addEventListener('click', e => {
        const trigger = e.target.closest?.(TRIGGER);
        if (!trigger || card?.contains(e.target)) return;
        const touch = lastPointer === 'touch';
        if (trigger.classList.contains('gb-tipicon')) {
            if (touch && trigger === current && isOpen()) close();
            else open(trigger);
        } else if (touch && trigger !== current && !trigger.matches('[aria-haspopup]')) {
            open(trigger);
        }
    });

    // ---- keyboard ----

    // Focus from the keyboard opens the card at once; focus that follows a click or a tap leaves it to the pointer.
    document.addEventListener('focusin', e => {
        const trigger = e.target.closest?.(TRIGGER);
        if (!trigger || card?.contains(e.target)) return;
        if (performance.now() - lastPointerTime < 500) return;
        let keyboard = true;
        try { keyboard = e.target.matches(':focus-visible'); } catch { /* an old browser: treat it as the keyboard */ }
        if (keyboard) open(trigger);
    });

    document.addEventListener('focusout', e => {
        if (!current || !current.contains(e.target)) return;
        const to = e.relatedTarget;
        if (to instanceof Node && (current.contains(to) || card?.contains(to))) return;
        close();
    });

    document.addEventListener('keydown', e => {
        if (!isOpen()) return;
        if (e.key === 'Escape') {
            // Only the card: a dialog or a menu under it stays open until the next Esc.
            e.stopPropagation();
            close();
        } else if (e.key === 'Enter' && current.matches('.gb-tipped, .gb-tipicon') && current.contains(e.target)) {
            const more = card.querySelector('.gb-tipcard-more');
            if (!more) return;
            e.preventDefault();
            more.click();
        }
    }, true);

    // Typing in a field that has a tip, a scroll or a resize: the card was in the way, or is no longer by its trigger.
    document.addEventListener('input', e => { if (isOpen() && current.contains(e.target)) close(); }, true);
    document.addEventListener('scroll', e => {
        if (!isOpen() || (e.target instanceof Node && card.contains(e.target))) return;
        // The scroll that brings a newly focused trigger into view moves the card along with it instead.
        if (performance.now() - openedTime < 200) place(current);
        else close();
    }, true);
    window.addEventListener('resize', () => { if (isOpen()) close(); });
    window.addEventListener('popstate', () => { if (isOpen()) close(); });

    // The catalog comes as soon as the page has a tip, while the browser is idle, so the first hover is instant.
    const prefetch = () => { if (document.querySelector('[data-tip]')) load(); };
    const idle = window.requestIdleCallback ?? (fn => setTimeout(fn, 400));
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => idle(prefetch));
    else idle(prefetch);

    window.gitberth = window.gitberth || {};
    window.gitberth.tips = { open: t => open(t), close, load };
})();
