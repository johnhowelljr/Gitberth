(() => {
    const shownAt = performance.now();
    const minSplashMs = 450;

    // If the app hasn't rendered after a while, say so on the splash instead of spinning forever.
    setTimeout(() => {
        const status = document.getElementById('gb-splash-status');
        if (status) status.textContent = 'Still connecting… check that Gitberth is running.';
    }, 12000);

    window.gitberth = {
        // Called by the layout after its first interactive render: fade the splash out.
        ready() {
            const splash = document.getElementById('gb-splash');
            if (!splash || splash.dataset.leaving) return;
            splash.dataset.leaving = '1';
            const wait = Math.max(0, minSplashMs - (performance.now() - shownAt));
            setTimeout(() => {
                splash.classList.add('gb-splash--hide');
                setTimeout(() => splash.remove(), 350);
            }, wait);
        },

        // Keep log panes pinned to the bottom unless the user scrolls up to read.
        follow(el) {
            if (!el || el.gbBound) return;
            el.gbBound = true;
            el.gbStick = true;
            el.addEventListener('scroll', () => {
                el.gbStick = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
            });
        },

        scrollToBottom(el) {
            if (el && el.gbStick !== false) el.scrollTop = el.scrollHeight;
        },

        // navigator.clipboard needs HTTPS or localhost; fall back to execCommand on plain-HTTP LAN addresses.
        async copy(text) {
            try {
                if (navigator.clipboard && window.isSecureContext) {
                    await navigator.clipboard.writeText(text);
                    return true;
                }
            } catch { /* fall through */ }
            const area = document.createElement('textarea');
            area.value = text;
            area.setAttribute('readonly', '');
            area.style.position = 'fixed';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            let ok = false;
            try { ok = document.execCommand('copy'); } catch { ok = false; }
            area.remove();
            return ok;
        },

        submitForm(id) {
            document.getElementById(id)?.submit();
        },

        // Puts the focus in a field by its id (the search, a field that needs fixing).
        focusIn(id) {
            const el = document.getElementById(id);
            if (!el) return false;
            el.focus();
            if (typeof el.select === 'function' && el.value) el.select();
            return true;
        },

        blur(id) {
            document.getElementById(id)?.blur();
        },

        // Brings a section of a long page into view (the Settings tab's index) and says which one is in view as it scrolls.
        scrollToId(id) {
            const el = document.getElementById(id);
            if (!el) return;
            el.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'start' });
        },

        watchSections(dotnet, ids) {
            if (!('IntersectionObserver' in window)) return;
            const seen = new Map();
            const observer = new IntersectionObserver(entries => {
                for (const e of entries) seen.set(e.target.id, e.isIntersecting ? e.boundingClientRect.top : null);
                const first = ids.find(id => seen.get(id) != null);
                if (first) dotnet.invokeMethodAsync('SectionInView', first).catch(() => { });
            }, { rootMargin: '-80px 0px -55% 0px' });
            for (const id of ids) {
                const el = document.getElementById(id);
                if (el) observer.observe(el);
            }
            return { dispose: () => observer.disconnect() };
        }
    };

    // Ctrl K (or Cmd K) goes to the top bar's search from anywhere; / goes to the page's own search when it has one
    // (the Apps list), else the top bar's, unless you're already typing somewhere.
    document.addEventListener('keydown', e => {
        const typing = e.target instanceof HTMLElement && (e.target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName));
        if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            window.gitberth.focusIn('gb-search-input');
        } else if (e.key === '/' && !typing && !e.ctrlKey && !e.metaKey && !e.altKey) {
            e.preventDefault();
            window.gitberth.focusIn('gb-apps-search') || window.gitberth.focusIn('gb-search-input');
        }
    });

    // ---------- Charts (Shared/TimeChart.razor) ----------
    // Blazor renders each chart's frame (title, keys, ticks, gridlines) and hands its points to set() once, then
    // only the new ones to push(). A point is [time (ms), time label, values[], formatted values[]], one value per
    // series. Marks (deploys, restarts, stops) come in meta.marks as [time, label, detail, tone, time label], and the
    // stretches a container was down in meta.down as [from, to], to null while it lasts; both only when they change.
    // Lines, marks, end dots and the tooltip go in .gb-chart-canvas, which Blazor renders empty and never
    // touches again, so this script owns everything inside it.

    const svgNs = 'http://www.w3.org/2000/svg';

    const add = (parent, tag, className) => {
        const el = parent.appendChild(document.createElement(tag));
        if (className) el.className = className;
        return el;
    };

    // Positions as percentages of the plot: x from the window, y from the axis maximum.
    const xOf = (meta, t) => (t - (meta.end - meta.window)) / meta.window * 100;
    const yOf = (meta, v) => 100 - Math.min(Math.max(v / meta.max, 0), 1) * 100;

    const chart = {
        set(plot, meta, points) {
            plot.gbChart = { meta, points: [], marks: [], down: [] };
            chart.push(plot, meta, points);
        },

        push(plot, meta, points) {
            const c = plot.gbChart ??= { meta, points: [], marks: [], down: [] };
            c.meta = meta;
            if (meta.marks) c.marks = meta.marks;
            if (meta.down) c.down = meta.down;
            for (const p of points) {
                if (!c.points.length || p[0] > c.points[c.points.length - 1][0]) c.points.push(p);
            }
            const first = c.points.findIndex(p => p[0] >= meta.end - meta.window);
            c.points.splice(0, first < 0 ? c.points.length : first);
            draw(plot, c);
            // Keep an open tooltip on the same sample while the chart moves; otherwise the slider sits on the newest one.
            if (plot.gbHoverT !== undefined) {
                const i = c.points.findIndex(p => p[0] === plot.gbHoverT);
                if (i < 0) hidePoint(plot); else showPoint(plot, i);
            } else {
                setSlider(plot, c.points.length - 1);
            }
        }
    };

    // The plot is a slider over the samples (role="slider"): its value text is what a screen reader says at each step.
    function setSlider(plot, index) {
        const c = plot.gbChart;
        if (!c?.points.length) {
            plot.removeAttribute('aria-valuenow');
            plot.setAttribute('aria-valuetext', 'No readings yet');
            return;
        }
        const i = Math.max(0, Math.min(c.points.length - 1, index)), p = c.points[i];
        plot.setAttribute('aria-valuemin', '0');
        plot.setAttribute('aria-valuemax', String(c.points.length - 1));
        plot.setAttribute('aria-valuenow', String(i));
        plot.setAttribute('aria-valuetext', [p[1], ...c.meta.series.map((s, k) => `${s.name} ${p[3][k] ?? 'no reading'}`),
            ...marksAt(c, i).map(m => `${m[2]} at ${m[4]}`)].join(', '));
    }
    window.gitberth.chart = chart;

    // The marks a sample answers for: those nearer to it than to its neighbours, so pointing at a mark's line shows it.
    function marksAt(c, i) {
        const { meta, points } = c;
        const t = points[i][0];
        const after = i > 0 ? (points[i - 1][0] + t) / 2 : meta.end - meta.window;
        const until = i < points.length - 1 ? (t + points[i + 1][0]) / 2 : meta.end;
        return (c.marks ?? []).filter(m => m[0] > after && m[0] <= until);
    }

    function draw(plot, c) {
        const { meta, points } = c;
        const canvas = plot.querySelector('.gb-chart-canvas');
        if (!canvas) return;
        if (!canvas.gbSvg) {
            // Shading and mark lines under the data, the marks' names over it.
            canvas.gbUnder = add(canvas, 'div', 'gb-chart-marks');
            canvas.gbSvg = canvas.appendChild(document.createElementNS(svgNs, 'svg'));
            canvas.gbSvg.setAttribute('viewBox', '0 0 100 100');
            canvas.gbSvg.setAttribute('preserveAspectRatio', 'none');
            canvas.gbSeries = [];
            canvas.gbOver = add(canvas, 'div', 'gb-chart-marks');
        }
        drawMarks(plot, canvas, c);

        meta.series.forEach((s, k) => {
            const el = canvas.gbSeries[k] ??= {
                area: canvas.gbSvg.appendChild(document.createElementNS(svgNs, 'path')),
                line: canvas.gbSvg.appendChild(document.createElementNS(svgNs, 'path')),
                dot: add(canvas, 'span')
            };

            // Runs of points, broken where a value is missing or two samples are too far apart.
            let line = '', area = '', run = [];
            const flush = () => {
                if (run.length > 1) {
                    const d = run.map(([x, y], j) => `${j ? 'L' : 'M'}${x.toFixed(2)} ${y.toFixed(2)}`).join('');
                    line += d;
                    if (meta.area) area += `${d}V100H${run[0][0].toFixed(2)}Z`;
                } else if (run.length === 1) {
                    // A lone sample between two gaps: a zero-length segment, which the round line cap draws as a dot.
                    line += `M${run[0][0].toFixed(2)} ${run[0][1].toFixed(2)}h0`;
                }
                run = [];
            };
            points.forEach((p, j) => {
                const v = p[2][k];
                if (v == null) return flush();
                if (run.length && p[0] - points[j - 1][0] > meta.gap) flush();
                run.push([xOf(meta, p[0]), yOf(meta, v)]);
            });
            flush();
            setPath(el.line, line, 'gb-chart-line gb-series--' + s.slot);
            setPath(el.area, area, 'gb-chart-area gb-series--' + s.slot);

            // A dot on the newest reading.
            const last = points[points.length - 1], v = last?.[2][k];
            el.dot.className = 'gb-chart-dot gb-series--' + s.slot;
            el.dot.style.display = v == null ? 'none' : '';
            if (v != null) {
                el.dot.style.left = xOf(meta, last[0]) + '%';
                el.dot.style.top = yOf(meta, v) + '%';
            }
        });
    }

    // A dashed line at each mark and shading where the container was down; on a card's first chart, the marks' names
    // too, newest first, leaving out any that would run into the one to its right.
    function drawMarks(plot, canvas, { meta, marks, down }) {
        const under = canvas.gbUnder, over = canvas.gbOver;
        under.replaceChildren();
        over.replaceChildren();
        for (const [from, to] of down ?? []) {
            const a = Math.max(0, xOf(meta, from)), b = Math.min(100, xOf(meta, to ?? meta.end));
            if (b <= a) continue;
            const band = add(under, 'div', 'gb-chart-down');
            band.style.left = a + '%';
            band.style.width = (b - a) + '%';
        }
        const width = plot.clientWidth || 300;
        let taken = Infinity;
        for (let i = (marks?.length ?? 0) - 1; i >= 0; i--) {
            const [t, label, , tone] = marks[i];
            const x = xOf(meta, t);
            if (x < 0 || x > 100) continue;
            add(under, 'div', 'gb-chart-mark gb-tone--' + tone).style.left = x + '%';
            if (!meta.labels) continue;
            // Roughly 6px a letter at this size, plus the dot and padding; near the right edge the name goes left of its line.
            const px = x / 100 * width, w = label.length * 6 + 18, left = px + w > width;
            const from = left ? px - w : px;
            if (from + w > taken - 4 || from < 0) continue;
            taken = from;
            const flag = add(over, 'span', 'gb-chart-flag gb-tone--' + tone + (left ? ' gb-chart-flag--left' : ''));
            flag.textContent = label;
            flag.style.left = x + '%';
        }
    }

    function setPath(path, d, className) {
        path.setAttribute('class', className);
        if (d) path.setAttribute('d', d); else path.removeAttribute('d');
    }

    function showPoint(plot, index) {
        const c = plot.gbChart;
        const canvas = plot.querySelector('.gb-chart-canvas');
        if (!canvas || !c?.points.length) return;
        const { meta, points } = c;
        const p = points[Math.max(0, Math.min(points.length - 1, index))];
        plot.gbHoverT = p[0];

        // Built on first use: the crosshair, a dot per series, and the tooltip.
        let layer = canvas.gbHover;
        if (!layer) {
            layer = canvas.gbHover = add(canvas, 'div', 'gb-chart-hover');
            layer.gbCross = add(layer, 'div', 'gb-chart-cross');
            layer.gbDots = meta.series.map(() => add(layer, 'span'));
            layer.gbTip = add(layer, 'div', 'gb-tip');
            layer.gbTime = add(layer.gbTip, 'div', 'gb-tip-time');
            layer.gbRows = meta.series.map(() => {
                const row = add(layer.gbTip, 'div', 'gb-tip-row');
                return { key: add(row, 'span'), value: add(row, 'strong'), name: add(row, 'span') };
            });
            layer.gbNotes = add(layer.gbTip, 'div', 'gb-tip-notes');
        }

        const x = xOf(meta, p[0]);
        layer.gbCross.style.left = x + '%';
        meta.series.forEach((s, k) => {
            const v = p[2][k], dot = layer.gbDots[k], row = layer.gbRows[k];
            dot.className = 'gb-chart-hdot gb-series--' + s.slot;
            dot.style.display = v == null ? 'none' : '';
            if (v != null) {
                dot.style.left = x + '%';
                dot.style.top = yOf(meta, v) + '%';
            }
            // Values lead, names follow; textContent because names come from Docker.
            row.key.className = 'gb-key gb-series--' + s.slot;
            row.value.textContent = p[3][k] ?? '–';
            row.name.textContent = s.name;
        });
        layer.gbTime.textContent = p[1];
        // What happened to the container around this sample.
        layer.gbNotes.replaceChildren();
        for (const m of marksAt(c, points.indexOf(p))) {
            const note = add(layer.gbNotes, 'div', 'gb-tip-note gb-tone--' + m[3]);
            add(note, 'span', 'gb-tip-note-time').textContent = m[4];
            add(note, 'span').textContent = m[2];
        }
        layer.gbTip.style.left = x + '%';
        layer.gbTip.style.transform = x > 55 ? 'translateX(calc(-100% - 12px))' : 'translateX(12px)';
        layer.style.visibility = 'visible';
        setSlider(plot, points.indexOf(p));
    }

    function hidePoint(plot) {
        const layer = plot.querySelector('.gb-chart-canvas')?.gbHover;
        if (layer) layer.style.visibility = 'hidden';
        plot.gbHoverT = undefined;
    }

    const hoveredIndex = plot => {
        const points = plot.gbChart?.points ?? [];
        const i = points.findIndex(p => p[0] === plot.gbHoverT);
        return i < 0 ? points.length - 1 : i;
    };

    // The crosshair snaps to the nearest sample, so the pointer never has to land on the line.
    document.addEventListener('pointermove', e => {
        const plot = e.target.closest?.('.gb-chart-plot');
        const c = plot?.gbChart;
        if (!c?.points.length) return;
        const rect = plot.getBoundingClientRect();
        const t = c.meta.end - c.meta.window + (e.clientX - rect.left) / rect.width * c.meta.window;
        let nearest = 0;
        c.points.forEach((p, n) => { if (Math.abs(p[0] - t) < Math.abs(c.points[nearest][0] - t)) nearest = n; });
        showPoint(plot, nearest);
    });

    document.addEventListener('pointerout', e => {
        const plot = e.target.closest?.('.gb-chart-plot');
        if (plot && !plot.contains(e.relatedTarget) && !plot.matches(':focus-visible')) hidePoint(plot);
    });

    // Keyboard: focus a chart, then use the arrow keys, Home and End to step through the samples.
    document.addEventListener('focusin', e => {
        const plot = e.target;
        if (plot.classList?.contains('gb-chart-plot') && plot.matches(':focus-visible'))
            showPoint(plot, (plot.gbChart?.points.length ?? 0) - 1);
    });

    document.addEventListener('focusout', e => {
        if (e.target.classList?.contains('gb-chart-plot')) hidePoint(e.target);
    });

    document.addEventListener('keydown', e => {
        const plot = e.target;
        if (!plot.classList?.contains('gb-chart-plot')) return;
        const count = plot.gbChart?.points.length ?? 0;
        if (!count) return;
        const i = hoveredIndex(plot);
        const next = { ArrowLeft: i - 1, ArrowRight: i + 1, Home: 0, End: count - 1 }[e.key];
        if (e.key === 'Escape') {
            hidePoint(plot);
        } else if (next !== undefined) {
            e.preventDefault();
            showPoint(plot, next);
        }
    });

    // Put focus back on a control Blazor just moved (it re-creates the element, so the browser loses track of it).
    window.gitberth.focusIn = id => document.getElementById(id)?.querySelector('input, button, a')?.focus();
})();
