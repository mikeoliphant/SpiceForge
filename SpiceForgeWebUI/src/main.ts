// SpiceForge's web front end: open a schematic, draw it, and edit it: place components from the library, wire them,
// select, move, rotate, flip, delete, cut, copy and paste, edit properties, undo, and save.

import { ApiClient, RequestError } from "./client";
import { moveElements, nameAdded } from "./edits";
import { snap } from "./geometry";
import { Editor } from "./editor";
import { chooseFile } from "./fileDialog";
import { LayoutCache } from "./layouts";
import { LibraryPanel, WireType } from "./library";
import { PropertiesPanel } from "./properties";
import type { CatalogEntry, Element, FileRead, Hello, Point, SymbolElement } from "./protocol";
import { SchematicView } from "./view";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const svg = document.getElementById("schematic") as unknown as SVGSVGElement;
const drawing = document.getElementById("drawing") as unknown as SVGGElement;
const overlay = document.getElementById("overlay") as unknown as SVGGElement;
const status = $<HTMLSpanElement>("status");
const toolLabel = $<HTMLSpanElement>("tool");
const selectionLabel = $<HTMLSpanElement>("selection");
const pointer = $<HTMLSpanElement>("pointer");
const zoomLabel = $<HTMLSpanElement>("zoom");
const title = $<HTMLSpanElement>("title");
const buttons = {
  open: $<HTMLButtonElement>("open"),
  save: $<HTMLButtonElement>("save"),
  undo: $<HTMLButtonElement>("undo"),
  redo: $<HTMLButtonElement>("redo"),
  cut: $<HTMLButtonElement>("cut"),
  copy: $<HTMLButtonElement>("copy"),
  paste: $<HTMLButtonElement>("paste"),
  remove: $<HTMLButtonElement>("delete"),
  wire: $<HTMLButtonElement>("wire"),
  fit: $<HTMLButtonElement>("fit"),
  zoomIn: $<HTMLButtonElement>("zoom-in"),
  zoomOut: $<HTMLButtonElement>("zoom-out"),
};

const client = new ApiClient();
const layouts = new LayoutCache(client);
const view = new SchematicView(svg);
const editor = new Editor(svg, drawing, overlay, view, layouts);
const library = new LibraryPanel($("library"), client);
const properties = new PropertiesPanel($("properties"), editor, client, () => library.types);
let hello: Hello | null = null;
let connected = false;
let file: { path: string; modified: string } | null = null;
/** The last elements copied, as LiveSPICE clipboard XML: used when the system clipboard can't be read. */
let clipboardFallback: string | null = null;

// For scripted tests and the browser console.
Object.assign(window, { spiceforge: { editor, library, layouts } });

view.onChange = (zoom) => (zoomLabel.textContent = `${Math.round(zoom * 100)}%`);
/** Where the pointer is over the schematic, in its coordinates; null when it's elsewhere. */
let pointerAt: Point | null = null;
view.onPointer = (x, y) => {
  pointer.textContent = `${Math.round(x)}, ${Math.round(y)}`;
  pointerAt = [x, y];
};
svg.addEventListener("pointerleave", () => (pointerAt = null));
editor.onChange = () => {
  updateChrome();
  properties.render();
};
editor.onError = (message) => setStatus(message, "error");
properties.onError = (message) => setStatus(message, "error");
library.onPick = pick;

function setStatus(text: string, kind: "info" | "error" = "info") {
  status.textContent = text;
  status.className = kind;
}

function errorText(e: unknown): string {
  return e instanceof RequestError ? e.error.message : (e as Error).message;
}

function fileName(path: string) {
  return path.split(/[\\/]/).pop()!.replace(/\.(schx|xml)$/i, "");
}

/** Brings the toolbar, title and status up to date with the editor. */
function updateChrome() {
  const h = editor.history;
  const dirty = h.dirty;
  const open = editor.document !== null;
  const selected = editor.selection.size;
  buttons.save.disabled = !connected || !file;
  buttons.undo.disabled = !h.canUndo;
  buttons.redo.disabled = !h.canRedo;
  buttons.undo.title = h.undoLabel ? `Undo ${h.undoLabel} (Ctrl+Z)` : "Undo (Ctrl+Z)";
  buttons.redo.title = h.redoLabel ? `Redo ${h.redoLabel} (Ctrl+Y)` : "Redo (Ctrl+Y)";
  buttons.cut.disabled = buttons.copy.disabled = buttons.remove.disabled = selected === 0;
  buttons.paste.disabled = !open || !connected;
  buttons.wire.disabled = !open;
  buttons.wire.classList.toggle("active", editor.tool === "wire");
  const name = file ? fileName(file.path) : "";
  title.textContent = name ? `${name}${dirty ? " •" : ""}` : "";
  title.title = file?.path ?? "";
  document.title = name ? `${dirty ? "• " : ""}${name} - SpiceForge` : "SpiceForge";
  selectionLabel.textContent = selected ? `${selected} selected` : "";
  toolLabel.textContent = editor.tool === "place" ? "Placing (Esc to stop)" : editor.tool === "wire" ? "Wiring (Esc to stop)" : "";
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
    setStatus(errorText(e), "error");
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
    setStatus(errorText(e), "error");
  }
}

// ---- Components and wiring ----

/** A library entry clicked or chosen by its shortcut: the wire tool for the conductor, else placing the component. */
function pick(entry: CatalogEntry) {
  if (!editor.document) return setStatus("Open a schematic first.", "error");
  if (entry.component.type === WireType) editor.startWiring();
  else editor.startPlacing(entry.component, entry.layout);
  svg.focus();
}

// LiveSPICE's shortcuts for the Common components. Browsers keep Ctrl+W and Ctrl+T for themselves, so the plain letters
// work too.
const ShortcutTypes: Record<string, string> = {
  w: WireType,
  g: "Circuit.Ground, Circuit",
  p: "Circuit.Rail, Circuit",
  r: "Circuit.Resistor, Circuit",
  c: "Circuit.Capacitor, Circuit",
  l: "Circuit.Inductor, Circuit",
  t: "Circuit.Label, Circuit",
};
const CtrlShortcutTypes: Record<string, string> = {
  g: "Circuit.Ground, Circuit",
  p: "Circuit.Rail, Circuit",
  r: "Circuit.Resistor, Circuit",
  f: "Circuit.Capacitor, Circuit",
  l: "Circuit.Inductor, Circuit",
  h: "Circuit.Inductor, Circuit",
};

function pickType(type: string) {
  const entry = library.find("Common", type);
  if (entry) pick(entry);
}

// ---- Clipboard: LiveSPICE's format, so copies go between this editor and the desktop app ----

async function copySelection(cut: boolean) {
  const elements = editor.selectedElements();
  if (elements.length === 0) return;
  try {
    const { xml } = await client.request<{ xml: string }>("schx.toXml", { elements });
    clipboardFallback = xml;
    try {
      await navigator.clipboard.writeText(xml);
    } catch {
      // No system clipboard (permission, or an insecure context): the fallback still pastes here.
    }
    if (cut) editor.deleteSelection("Cut");
    setStatus(`${cut ? "Cut" : "Copied"} ${elements.length} element${elements.length === 1 ? "" : "s"}.`);
  } catch (e) {
    setStatus(errorText(e), "error");
  }
}

async function paste(text: string | null) {
  const xml = text?.trim().startsWith("<") ? text : clipboardFallback;
  if (!xml || !editor.document) return;
  try {
    const { elements } = await client.request<{ elements: Element[] }>("schx.fromXml", { xml });
    for (const e of elements) e.id = editor.newId();
    // Rename clashing components first: a renamed component's layout differs (its name is drawn).
    nameAdded(editor.document, elements);
    await layouts.fetch(elements.filter((e): e is SymbolElement => e.kind === "symbol").map((s) => s.component));
    // Centre the paste on the pointer when it's over the schematic (Ctrl+V); otherwise (the toolbar button) put it a
    // little down and right of where it was copied from, so it's never hidden under the original. Either way by whole
    // grid steps, so the elements stay on the grid.
    const bounds = editor.boundsOf(elements);
    if (bounds) {
      const center: Point = [Math.trunc((bounds.min[0] + bounds.max[0]) / 2), Math.trunc((bounds.min[1] + bounds.max[1]) / 2)];
      const by = pointerAt ? snap([pointerAt[0] - center[0], pointerAt[1] - center[1]]) : ([20, 20] as Point);
      moveElements({ circuit: {}, elements }, new Set(elements.map((e) => e.id)), by);
    }
    editor.addElements(elements, "Paste");
  } catch (e) {
    setStatus(`Couldn't paste: ${errorText(e)}`, "error");
  }
}

async function pasteFromClipboard() {
  let text: string | null = null;
  try {
    text = await navigator.clipboard.readText();
  } catch {
    // Reading needs permission; without it, paste what was last copied here.
  }
  await paste(text);
}

const editingText = (target: EventTarget | null) =>
  target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement;

document.addEventListener("copy", (e) => {
  if (editingText(document.activeElement) || editor.selection.size === 0) return;
  e.preventDefault();
  void copySelection(false);
});
document.addEventListener("cut", (e) => {
  if (editingText(document.activeElement) || editor.selection.size === 0) return;
  e.preventDefault();
  void copySelection(true);
});
document.addEventListener("paste", (e) => {
  if (editingText(document.activeElement) || !editor.document) return;
  e.preventDefault();
  void paste(e.clipboardData?.getData("text/plain") ?? null);
});

// ---- Buttons and keys ----

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
buttons.cut.addEventListener("click", () => void copySelection(true));
buttons.copy.addEventListener("click", () => void copySelection(false));
buttons.paste.addEventListener("click", () => void pasteFromClipboard());
buttons.remove.addEventListener("click", () => editor.deleteSelection());
buttons.wire.addEventListener("click", () => (editor.tool === "wire" ? editor.escape() : editor.startWiring()));
buttons.fit.addEventListener("click", () => view.fit(editor.bounds));
buttons.zoomIn.addEventListener("click", () => view.zoomAt(1.5));
buttons.zoomOut.addEventListener("click", () => view.zoomAt(1 / 1.5));

window.addEventListener("keydown", (e) => {
  if (editingText(e.target) || document.querySelector("dialog[open]")) return;
  const ctrl = e.ctrlKey || e.metaKey;
  const key = e.key.toLowerCase();
  const handled = () => e.preventDefault();
  if (ctrl && key === "o") (handled(), buttons.open.click());
  else if (ctrl && key === "s") (handled(), void save());
  else if (ctrl && key === "z" && !e.shiftKey) (handled(), editor.undo());
  else if (ctrl && (key === "y" || (key === "z" && e.shiftKey))) (handled(), editor.redo());
  else if (ctrl && key === "a") (handled(), editor.selectAll());
  else if (ctrl && !e.shiftKey && !e.altKey && CtrlShortcutTypes[key]) (handled(), pickType(CtrlShortcutTypes[key]!));
  else if (e.key === "Escape") editor.escape();
  else if (e.key === "Delete") (handled(), editor.deleteSelection());
  else if (ctrl || e.altKey) return;
  // SelectionTool.KeyDown: Left and Right rotate, Up and Down flip (the ghost, while placing).
  else if (e.key === "ArrowLeft") (handled(), editor.rotate(1));
  else if (e.key === "ArrowRight") (handled(), editor.rotate(-1));
  else if (e.key === "ArrowUp" || e.key === "ArrowDown") (handled(), editor.flip());
  else if (ShortcutTypes[key]) (handled(), pickType(ShortcutTypes[key]!));
  else if (e.key === "+" || e.key === "=") view.zoomAt(1.5);
  else if (e.key === "-") view.zoomAt(1 / 1.5);
  else if (e.key === "0" || key === "f") view.fit(editor.bounds);
});

window.addEventListener("beforeunload", (e) => {
  if (editor.history.dirty) e.preventDefault();
});

async function start() {
  updateChrome();
  properties.render();
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
  try {
    await library.load();
  } catch (e) {
    setStatus(`Couldn't load the components: ${errorText(e)}`, "error");
  }
  properties.render();
  const requested = new URLSearchParams(location.search).get("file") ?? localStorage.getItem("spiceforge.lastFile");
  if (requested) await open(requested);
}

view.fit(null);
void start();
