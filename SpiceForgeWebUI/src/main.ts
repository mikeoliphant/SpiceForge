// SpiceForge's web front end: open a schematic, draw it, select and move its elements, undo, and save.

import { ApiClient, RequestError } from "./client";
import { Editor } from "./editor";
import { chooseFile } from "./fileDialog";
import { LayoutCache } from "./layouts";
import type { FileRead, Hello, SymbolElement } from "./protocol";
import { SchematicView } from "./view";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const svg = document.getElementById("schematic") as unknown as SVGSVGElement;
const drawing = document.getElementById("drawing") as unknown as SVGGElement;
const overlay = document.getElementById("overlay") as unknown as SVGGElement;
const status = $<HTMLSpanElement>("status");
const selectionLabel = $<HTMLSpanElement>("selection");
const pointer = $<HTMLSpanElement>("pointer");
const zoomLabel = $<HTMLSpanElement>("zoom");
const title = $<HTMLSpanElement>("title");
const buttons = {
  open: $<HTMLButtonElement>("open"),
  save: $<HTMLButtonElement>("save"),
  undo: $<HTMLButtonElement>("undo"),
  redo: $<HTMLButtonElement>("redo"),
  fit: $<HTMLButtonElement>("fit"),
  zoomIn: $<HTMLButtonElement>("zoom-in"),
  zoomOut: $<HTMLButtonElement>("zoom-out"),
};

const client = new ApiClient();
const layouts = new LayoutCache(client);
const view = new SchematicView(svg);
const editor = new Editor(svg, drawing, overlay, view, layouts);
let hello: Hello | null = null;
let connected = false;
let file: { path: string; modified: string } | null = null;

view.onChange = (zoom) => (zoomLabel.textContent = `${Math.round(zoom * 100)}%`);
view.onPointer = (x, y) => (pointer.textContent = `${Math.round(x)}, ${Math.round(y)}`);
editor.onChange = updateChrome;

function setStatus(text: string, kind: "info" | "error" = "info") {
  status.textContent = text;
  status.className = kind;
}

function fileName(path: string) {
  return path.split(/[\\/]/).pop()!.replace(/\.(schx|xml)$/i, "");
}

/** Brings the toolbar, title and selection count up to date with the editor. */
function updateChrome() {
  const h = editor.history;
  const dirty = h.dirty;
  buttons.save.disabled = !connected || !file;
  buttons.undo.disabled = !h.canUndo;
  buttons.redo.disabled = !h.canRedo;
  buttons.undo.title = h.undoLabel ? `Undo ${h.undoLabel} (Ctrl+Z)` : "Undo (Ctrl+Z)";
  buttons.redo.title = h.redoLabel ? `Redo ${h.redoLabel} (Ctrl+Y)` : "Redo (Ctrl+Y)";
  const name = file ? fileName(file.path) : "";
  title.textContent = name ? `${name}${dirty ? " •" : ""}` : "";
  title.title = file?.path ?? "";
  document.title = name ? `${dirty ? "• " : ""}${name} - SpiceForge` : "SpiceForge";
  const n = editor.selection.size;
  selectionLabel.textContent = n ? `${n} selected` : "";
}

/** The token comes in the URL the server prints (?token=...); it's kept for reloads, then dropped from the address bar. */
function token(): string | null {
  const fromUrl = new URLSearchParams(location.search).get("token");
  if (fromUrl) {
    sessionStorage.setItem("spiceforge.token", fromUrl);
    const url = new URL(location.href);
    url.searchParams.delete("token");
    history.replaceState(null, "", url);
    return fromUrl;
  }
  return sessionStorage.getItem("spiceforge.token");
}

/** Asks before discarding unsaved changes; true if it's fine to go on. */
function discardOk(): boolean {
  return !editor.history.dirty || confirm(`Discard the changes to ${file ? fileName(file.path) : "this schematic"}?`);
}

async function open(path: string) {
  setStatus(`Opening ${path}…`);
  try {
    const read = await client.request<FileRead>("file.read", { path });
    const symbols = read.document.elements.filter((e): e is SymbolElement => e.kind === "symbol");
    await layouts.fetch(symbols.map((s) => s.component));
    file = { path: read.path, modified: read.modified };
    editor.load(read.document);
    view.fit(editor.bounds);
    localStorage.setItem("spiceforge.lastFile", read.path);
    const wires = read.document.elements.filter((e) => e.kind === "wire").length;
    const notes = [...read.messages.map((m) => m.message), ...editor.problems];
    setStatus(`${symbols.length} symbols, ${wires} wires${notes.length ? ` — ${notes.join("; ")}` : ""}`, notes.length ? "error" : "info");
  } catch (e) {
    setStatus(e instanceof RequestError ? e.error.message : (e as Error).message, "error");
  }
  updateChrome();
}

/** Writes the document back to its file. If the file changed on disk since it was read, asks before overwriting. */
async function save() {
  const doc = editor.document;
  if (!doc || !file) return;
  const write = (expectModified?: string) =>
    client.request<{ path: string; modified: string }>("file.write", { path: file!.path, document: doc, ...(expectModified ? { expectModified } : {}) });
  try {
    let written;
    try {
      written = await write(file.modified);
    } catch (e) {
      if (!(e instanceof RequestError && e.error.code === "modified_on_disk")) throw e;
      if (!confirm(`${fileName(file.path)} has changed on disk since it was opened. Overwrite it?`)) return;
      written = await write();
    }
    file = { path: written.path, modified: written.modified };
    editor.history.markSaved();
    setStatus(`Saved ${written.path}`);
  } catch (e) {
    setStatus(e instanceof RequestError ? e.error.message : (e as Error).message, "error");
  }
}

buttons.open.addEventListener("click", async () => {
  if (!discardOk()) return;
  const last = localStorage.getItem("spiceforge.lastFile");
  const from = file?.path ?? last ?? hello?.folders.user ?? "";
  const folder = /\.(schx|xml)$/i.test(from) ? from.replace(/[\\/][^\\/]*$/, "") : from;
  const path = await chooseFile(client, folder);
  if (path) await open(path);
});
buttons.save.addEventListener("click", () => void save());
buttons.undo.addEventListener("click", () => editor.undo());
buttons.redo.addEventListener("click", () => editor.redo());
buttons.fit.addEventListener("click", () => view.fit(editor.bounds));
buttons.zoomIn.addEventListener("click", () => view.zoomAt(1.5));
buttons.zoomOut.addEventListener("click", () => view.zoomAt(1 / 1.5));

window.addEventListener("keydown", (e) => {
  if (e.target instanceof HTMLInputElement || document.querySelector("dialog[open]")) return;
  const ctrl = e.ctrlKey || e.metaKey;
  const key = e.key.toLowerCase();
  const handled = () => e.preventDefault();
  if (ctrl && key === "o") (handled(), buttons.open.click());
  else if (ctrl && key === "s") (handled(), void save());
  else if (ctrl && key === "z" && !e.shiftKey) (handled(), editor.undo());
  else if (ctrl && (key === "y" || (key === "z" && e.shiftKey))) (handled(), editor.redo());
  else if (ctrl && key === "a") (handled(), editor.selectAll());
  else if (e.key === "Escape") editor.escape();
  else if (ctrl || e.altKey) return;
  // SelectionTool.KeyDown: Left and Right rotate, Up and Down flip.
  else if (e.key === "ArrowLeft") (handled(), editor.rotate(1));
  else if (e.key === "ArrowRight") (handled(), editor.rotate(-1));
  else if (e.key === "ArrowUp" || e.key === "ArrowDown") (handled(), editor.flip());
  else if (e.key === "+" || e.key === "=") view.zoomAt(1.5);
  else if (e.key === "-") view.zoomAt(1 / 1.5);
  else if (e.key === "0" || key === "f") view.fit(editor.bounds);
});

window.addEventListener("beforeunload", (e) => {
  if (editor.history.dirty) e.preventDefault();
});

async function start() {
  updateChrome();
  const t = token();
  if (!t) {
    setStatus("No token: open the address SpiceForgeServer printed when it started.", "error");
    return;
  }
  client.onClose = (reason) => {
    connected = false;
    buttons.open.disabled = true;
    updateChrome();
    setStatus(`Disconnected from the server: ${reason}`, "error");
  };
  try {
    await client.connect(t);
    hello = await client.request<Hello>("hello", { protocol: 1, client: "SpiceForgeWebUI" });
  } catch (e) {
    setStatus(`${(e as Error).message} Is SpiceForgeServer running, and is the token current?`, "error");
    return;
  }
  connected = true;
  buttons.open.disabled = false;
  updateChrome();
  setStatus(`Connected to ${hello.server.name} ${hello.server.version}. Open a schematic (Ctrl+O).`);
  const requested = new URLSearchParams(location.search).get("file") ?? localStorage.getItem("spiceforge.lastFile");
  if (requested) await open(requested);
}

view.fit(null);
void start();
