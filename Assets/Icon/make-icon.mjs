// Renders SpiceForge.svg into the icon files beside it, with headless Chrome (or Edge):
//   SpiceForge.ico      16, 24, 32, 48, 64, 128 and 256 px, as PNG images inside the .ico (Windows Vista and later)
//   SpiceForge-256.png  for the web editor's favicon and Linux
//   SpiceForge-32.png   the favicon's small size
// Run it after changing the SVG: node Assets/Icon/make-icon.mjs
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const svg = readFileSync(join(here, "SpiceForge.svg"), "utf8");
const sizes = [16, 24, 32, 48, 64, 128, 256];

const browser = [
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "/usr/bin/google-chrome",
  "/usr/bin/chromium",
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
].find((p) => existsSync(p));
if (!browser) throw new Error("No Chrome or Edge found to render the SVG with.");

const port = 19300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), "spiceforge-icon-"));
const chrome = spawn(browser, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--no-first-run", "about:blank"], { stdio: "ignore" });
try {
  let page;
  for (let i = 0; i < 50 && !page; i++) {
    await new Promise((r) => setTimeout(r, 200));
    try {
      page = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === "page");
    } catch {}
  }
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((r) => (ws.onopen = r));
  let id = 0;
  const waiting = new Map();
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    waiting.get(msg.id)?.(msg);
  };
  const send = (method, params = {}) => new Promise((r) => { const i = ++id; waiting.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });

  await send("Emulation.setDefaultBackgroundColorOverride", { color: { r: 0, g: 0, b: 0, a: 0 } });
  const html = `<html><body style="margin:0;background:transparent">${svg.replace("<svg ", '<svg width="100%" height="100%" ')}</body></html>`;
  await send("Page.navigate", { url: "data:text/html;base64," + Buffer.from(html).toString("base64") });
  await new Promise((r) => setTimeout(r, 500));
  const pngs = new Map();
  for (const size of sizes) {
    await send("Emulation.setDeviceMetricsOverride", { width: size, height: size, deviceScaleFactor: 1, mobile: false });
    await new Promise((r) => setTimeout(r, 100));
    const shot = await send("Page.captureScreenshot", { format: "png", clip: { x: 0, y: 0, width: size, height: size, scale: 1 } });
    pngs.set(size, Buffer.from(shot.result.data, "base64"));
  }
  ws.close();

  // ICO: a 6-byte header, a 16-byte entry per image (0 meaning 256 px), then the PNGs.
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  const entries = [];
  let offset = 6 + 16 * sizes.length;
  for (const size of sizes) {
    const png = pngs.get(size);
    const entry = Buffer.alloc(16);
    entry.writeUInt8(size >= 256 ? 0 : size, 0);
    entry.writeUInt8(size >= 256 ? 0 : size, 1);
    entry.writeUInt8(0, 2);
    entry.writeUInt8(0, 3);
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(png.length, 8);
    entry.writeUInt32LE(offset, 12);
    offset += png.length;
    entries.push(entry);
  }
  writeFileSync(join(here, "SpiceForge.ico"), Buffer.concat([header, ...entries, ...sizes.map((s) => pngs.get(s))]));
  writeFileSync(join(here, "SpiceForge-256.png"), pngs.get(256));
  writeFileSync(join(here, "SpiceForge-32.png"), pngs.get(32));
  console.log(`Wrote SpiceForge.ico (${sizes.join(", ")} px), SpiceForge-256.png and SpiceForge-32.png`);
} finally {
  chrome.kill();
  await new Promise((r) => setTimeout(r, 500));
  try { rmSync(profile, { recursive: true, force: true }); } catch {}
}
