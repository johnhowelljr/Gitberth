"""Frames a raw screenshot for the docs, as the 1.0 docs' pictures are: a window shot gets a slim browser frame (traffic
lights, and the page's address at 192.168.50.10:8080), a detail shot rounded corners; both get a soft shadow on a
transparent background, so they sit well on GitHub's light and dark themes and in Gitberth's Help. Drawn by the rig's
browser at twice the pixels, as the raw shot is, and saved as WebP (quality 90, as the 1.0 pictures)."""
import base64
import html

HOST = "192.168.50.10:8080"
QUALITY = 90
DPR = 2

PAGE = """<!doctype html><html><head><meta charset="utf-8"><style>
@font-face { font-family: "Inter"; font-weight: 400 700; src: url("__FONT__") format("woff2"); }
html, body { margin: 0; background: transparent; }
body { padding: 36px; display: inline-block; font-family: "Inter", "Segoe UI", system-ui, sans-serif; }
.win { border-radius: 12px; overflow: hidden; background: #0b0f15; border: 1px solid rgba(255,255,255,.10);
       box-shadow: 0 22px 48px rgba(15,23,42,.28), 0 4px 12px rgba(15,23,42,.16); }
.bar { height: 38px; display: flex; align-items: center; gap: 8px; padding: 0 14px; background: #161b23;
       border-bottom: 1px solid rgba(255,255,255,.07); position: relative; }
.dot { width: 12px; height: 12px; border-radius: 50%; }
.url { position: absolute; left: 50%; transform: translateX(-50%); min-width: 340px; max-width: 60%; height: 24px;
       border-radius: 7px; background: #0d1117; color: #9aa4b2; font-size: 12.5px; line-height: 24px; text-align: center;
       padding: 0 14px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; border: 1px solid rgba(255,255,255,.06); }
.detail { border-radius: 10px; overflow: hidden; border: 1px solid rgba(255,255,255,.10);
          box-shadow: 0 14px 34px rgba(15,23,42,.26), 0 3px 10px rgba(15,23,42,.14); }
img { display: block; }
</style></head><body>__BODY__</body></html>"""


def body_for(kind, width, height, path, data_url):
    img = f'<img src="{data_url}" width="{width}" height="{height}">'
    if kind == "window":
        url = html.escape(HOST + (path or "/"))
        return (f'<div class="win"><div class="bar"><span class="dot" style="background:#ff5f57"></span>'
                f'<span class="dot" style="background:#febc2e"></span><span class="dot" style="background:#28c840"></span>'
                f'<span class="url">{url}</span></div>{img}</div>')
    return f'<div class="detail">{img}</div>'


class Framer:
    """One page in a context of its own (twice the pixels, a transparent background) that frames picture after picture."""

    def __init__(self, browser, font_url):
        self.context = browser.new_context(viewport={"width": 1600, "height": 1100}, device_scale_factor=DPR)
        self.page = self.context.new_page()
        self.cdp = self.context.new_cdp_session(self.page)
        self.cdp.send("Emulation.setDefaultBackgroundColorOverride", {"color": {"r": 0, "g": 0, "b": 0, "a": 0}})
        self.font_url = font_url

    def frame(self, png, kind, width, height, path):
        """The framed picture, as WebP bytes. width and height are the raw shot's size in CSS pixels (it has twice that)."""
        data_url = "data:image/png;base64," + base64.b64encode(png).decode()
        w, h = int(width) + 120, int(height) + 160
        self.page.set_viewport_size({"width": w, "height": h})
        # A capture beyond the viewport draws with the capturing session's own metrics, not the context's: without these
        # it comes out at one pixel per CSS pixel, half the raw shot's.
        self.cdp.send("Emulation.setDeviceMetricsOverride", {"width": w, "height": h, "deviceScaleFactor": DPR, "mobile": False})
        self.page.set_content(PAGE.replace("__FONT__", self.font_url).replace("__BODY__", body_for(kind, width, height, path, data_url)))
        self.page.evaluate("Promise.all([...document.images].map(i => i.decode()))")
        self.page.evaluate("document.fonts.ready.then(() => true)")
        r = self.page.evaluate("(() => { const r = document.body.getBoundingClientRect(); return {w: r.width, h: r.height}; })()")
        shot = self.cdp.send("Page.captureScreenshot", {"format": "webp", "quality": QUALITY, "captureBeyondViewport": True,
                                                        "clip": {"x": 0, "y": 0, "width": r["w"], "height": r["h"], "scale": 1}})
        return base64.b64decode(shot["data"])

    def close(self):
        self.context.close()
