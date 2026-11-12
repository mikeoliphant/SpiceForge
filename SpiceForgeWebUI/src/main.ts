// SpiceForge's web front end: schematics in tabs, drawn and edited: place components from the library, wire them,
// select, move, rotate, flip, delete, cut, copy and paste, edit properties, undo, and save.

import { ApiClient, RequestError } from "./client";
import { wireNames } from "./connectivity";
import { moveElements, nameAdded } from "./edits";
import { Editor, newEditorState, type EditorState } from "./editor";
import { chooseFile } from "./fileDialog";
import { snap } from "./geometry";
import { LayoutCache } from "./layouts";
import { configureAudio } from "./audio";
import { manageComponentFolders } from "./componentFolders";
import { LibraryDragType, LibraryPanel, WireType } from "./library";
import { PropertiesPanel } from "./properties";
import { renderDocument } from "./render";
import { SimulationPanel } from "./simulation";
import type { CatalogEntry, Element, FileRead, Hello, Point, SchematicDocument, SymbolElement } from "./protocol";
import { applyTheme, currentTheme, initTheme, isTheme, Themes } from "./theme";
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
const tabBar = $<HTMLDivElement>("tabs");
const banner = $<HTMLDivElement>("banner");
const menu = $<HTMLDivElement>("menu");
const themePicker = $<HTMLSelectElement>("theme");
const buttons = {
  new: $<HTMLButtonElement>("new"),
  open: $<HTMLButtonElement>("open"),
  save: $<HTMLButtonElement>("save"),
  saveAs: $<HTMLButtonElement>("save-as"),
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
  simulate: $<HTMLButtonElement>("simulate"),
  audio: $<HTMLButtonElement>("audio"),
  export: $<HTMLButtonElement>("export"),
};

const client = new ApiClient();
const layouts = new LayoutCache(client);
const view = new SchematicView(svg);
const editor = new Editor(svg, drawing, overlay, view, layouts);
const library = new LibraryPanel($("library"), client);
const properties = new PropertiesPanel($("properties"), editor, client, () => library.types);
const simulation = new SimulationPanel({
  client,
  root: $("sim"),
  svg,
  probeLayer: document.getElementById("probes") as unknown as SVGGElement,
  view,
  layouts,
  shown: () => active?.state ?? null,
  status: (text, kind) => setStatus(text, kind),
  changed: () => updateChrome(),
  keyOf: (state) => tabs.find((t) => t.state === state)?.file?.path ?? null,
  display: (componentOf) => {
    editor.displayComponent = componentOf;
    editor.redraw();
  },
});
let hello: Hello | null = null;
let connected = false;
/** The last elements copied, as LiveSPICE clipboard XML: used when the system clipboard can't be read. */
let clipboardFallback: string | null = null;

// ---- Tabs: one per open schematic ----

interface Tab {
  state: EditorState;
  /** The file, once it has one; null while untitled. `modified` is the time we last read or wrote it. */
  file: { path: string; modified: string } | null;
  /** For naming untitled tabs: Untitled 1, Untitled 2... */
  untitled: number;
  /** Where the view was when the tab was last shown. */
  place: SchematicView["place"] | null;
  /** The modified time the server reported, when the file changed on disk since we read or wrote it. */
  changedOnDisk: string | null;
}

let tabs: Tab[] = [];
let active: Tab | null = null;
let untitledCount = 0;

// For scripted tests and the browser console.
Object.assign(window, { spiceforge: { editor, library, layouts, tabs: () => tabs, exportSvg: () => exportSvg() } });

const emptyDocument = (): SchematicDocument => ({ circuit: { Name: "", Description: "", PartNumber: "" }, elements: [] });

function fileName(path: string) {
  return path.split(/[\\/]/).pop()!.replace(/\.(schx|xml)$/i, "");
}

function tabName(tab: Tab) {
  return tab.file ? fileName(tab.file.path) : `Untitled ${tab.untitled}`;
}

/** Whether two paths name the same file: either slash, any case (as Windows treats them). */
function samePath(a: string, b: string) {
  const norm = (p: string) => p.replace(/\//g, "\\");
  return norm(a).localeCompare(norm(b), undefined, { sensitivity: "accent" }) === 0;
}

/** An untitled tab nobody has touched: Open reuses it rather than leave it behind. */
function pristine(tab: Tab) {
  return !tab.file && tab.state.doc.elements.length === 0 && !tab.state.history.canUndo;
}

function activate(tab: Tab | null) {
  if (active) active.place = view.place;
  active = tab;
  editor.attach(tab?.state ?? null);
  if (tab?.place) view.place = tab.place;
  else view.fit(editor.bounds);
  renderTabs();
  showBanner();
  updateChrome();
  remember();
  simulation.shownChanged();
}

function addTab(state: EditorState, file: Tab["file"]): Tab {
  const tab: Tab = { state, file, untitled: file ? 0 : ++untitledCount, place: null, changedOnDisk: null };
  tabs.push(tab);
  activate(tab);
  return tab;
}

function newTab() {
  addTab(newEditorState(emptyDocument()), null);
}

function closeTab(tab: Tab) {
  if (tab.state.history.dirty && !confirm(`Discard the changes to ${tabName(tab)}?`)) return;
  if (simulation.simulates(tab.state)) void simulation.stop();
  const index = tabs.indexOf(tab);
  tabs = tabs.filter((t) => t !== tab);
  if (active === tab) {
    active = null;
    activate(tabs[Math.min(index, tabs.length - 1)] ?? null);
  } else renderTabs();
  void watchFiles();
  remember();
}

function renderTabs() {
  tabBar.replaceChildren(
    ...tabs.map((tab) => {
      const label = document.createElement("span");
      label.className = "tab-name";
      label.textContent = `${tabName(tab)}${tab.state.history.dirty ? " •" : ""}`;
      const close = document.createElement("button");
      close.className = "tab-close";
      close.textContent = "×";
      close.title = "Close";
      close.addEventListener("click", (e) => {
        e.stopPropagation();
        closeTab(tab);
      });
      const item = document.createElement("div");
      item.className = tab === active ? "tab active" : "tab";
      item.title = tab.file?.path ?? "Not saved yet";
      item.append(label, close);
      item.addEventListener("click", () => tab !== active && activate(tab));
      item.addEventListener("auxclick", (e) => e.button === 1 && closeTab(tab));
      return item;
    }),
  );
}

/** Keeps the open files for the next visit. */
function remember() {
  localStorage.setItem("spiceforge.openFiles", JSON.stringify(tabs.flatMap((t) => (t.file ? [t.file.path] : []))));
  if (active?.file) localStorage.setItem("spiceforge.activeFile", active.file.path);
}

/** Asks the server to watch the open files, to hear when one changes on disk. */
async function watchFiles() {
  if (!connected) return;
  try {
    await client.request("file.watch", { paths: tabs.flatMap((t) => (t.file ? [t.file.path] : [])) });
  } catch {
    // Watching is a convenience; saving still checks for changes on disk.
  }
}

// ---- Status and chrome ----

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
  simulation.documentChanged();
};
editor.onError = (message) => setStatus(message, "error");
properties.onError = (message) => setStatus(message, "error");
library.onPick = pick;
library.onFolders = () => {
  if (!connected || !hello) return;
  void manageComponentFolders(client, hello.folders.components, hello.folders.user);
};
library.onChanged = () => {
  editor.wireNameOf = wireNames(library.types);
  editor.redraw();
};

function setStatus(text: string, kind: "info" | "error" = "info") {
  status.textContent = text;
  status.className = kind;
}

function errorText(e: unknown): string {
  return e instanceof RequestError ? e.error.message : (e as Error).message;
}

const shownDirty = new Map<Tab, boolean>();

/** Brings the toolbar, tabs, title and status up to date with the editor. */
function updateChrome() {
  const h = editor.history;
  const open = active !== null;
  const selected = editor.selection.size;
  buttons.save.disabled = buttons.saveAs.disabled = !connected || !open;
  buttons.export.disabled = !open || !editor.bounds;
  buttons.undo.disabled = !h.canUndo;
  buttons.redo.disabled = !h.canRedo;
  buttons.undo.title = h.undoLabel ? `Undo ${h.undoLabel} (Ctrl+Z)` : "Undo (Ctrl+Z)";
  buttons.redo.title = h.redoLabel ? `Redo ${h.redoLabel} (Ctrl+Y)` : "Redo (Ctrl+Y)";
  buttons.cut.disabled = buttons.copy.disabled = buttons.remove.disabled = selected === 0;
  buttons.paste.disabled = !open || !connected;
  buttons.wire.disabled = !open;
  buttons.wire.classList.toggle("active", editor.tool === "wire");
  buttons.simulate.disabled = !connected || !open;
  buttons.audio.disabled = !connected || hello?.capabilities["audio"] !== true;
  buttons.simulate.classList.toggle("active", simulation.simulates(active?.state ?? null));
  buttons.simulate.title = simulation.simulates(active?.state ?? null)
    ? "Simulating this schematic: click to start again from scratch (F5)"
    : "Simulate this schematic (F5)";
  document.title = active ? `${h.dirty ? "• " : ""}${tabName(active)} - SpiceForge` : "SpiceForge";
  selectionLabel.textContent = selected ? `${selected} selected` : "";
  toolLabel.textContent = simulation.picking !== null
    ? `Picking what ${simulation.picking} plays: click a component (Ctrl+click adds; Esc to stop)`
    : simulation.probing
    ? "Probing (Esc to stop)"
    : editor.tool === "place"
      ? "Placing (Esc to stop)"
      : editor.tool === "wire"
        ? "Wiring (Esc to stop)"
        : "";
  // The tab's dirty marker, when it changes.
  if (active && shownDirty.get(active) !== h.dirty) {
    shownDirty.set(active, h.dirty);
    renderTabs();
  }
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

// ---- Files ----

async function readFile(path: string): Promise<{ state: EditorState; file: Tab["file"]; read: FileRead }> {
  const read = await client.request<FileRead>("file.read", { path });
  const symbols = read.document.elements.filter((e): e is SymbolElement => e.kind === "symbol");
  await layouts.fetch(symbols.map((s) => s.component));
  return { state: newEditorState(read.document), file: { path: read.path, modified: read.modified }, read };
}

function reportOpened(read: FileRead) {
  const symbols = read.document.elements.filter((e) => e.kind === "symbol").length;
  const wires = read.document.elements.filter((e) => e.kind === "wire").length;
  const notes = [...read.messages.map((m) => m.message), ...editor.problems];
  setStatus(`${symbols} symbols, ${wires} wires${notes.length ? ` — ${notes.join("; ")}` : ""}`, notes.length ? "error" : "info");
}

/** Opens a file in a tab: its own, if it's open already; an untouched untitled tab; or a new one. */
async function open(path: string) {
  const existing = tabs.find((t) => t.file && samePath(t.file.path, path));
  if (existing) return activate(existing);
  setStatus(`Opening ${path}…`);
  try {
    const { state, file, read } = await readFile(path);
    if (active && pristine(active)) {
      const reused = active;
      reused.state = state;
      reused.file = file;
      reused.place = null;
      active = null;
      activate(reused);
    } else addTab(state, file);
    reportOpened(read);
    void watchFiles();
  } catch (e) {
    setStatus(errorText(e), "error");
  }
}

/** Saves a tab: to its file, asking before overwriting one that changed on disk; else Save As. */
async function save(tab = active) {
  if (!tab) return;
  if (!tab.file) return saveAs(tab);
  const path = tab.file.path;
  const write = (expectModified?: string) =>
    client.request<{ path: string; modified: string }>("file.write", { path, document: tab.state.doc, ...(expectModified ? { expectModified } : {}) });
  try {
    let written;
    try {
      written = await write(tab.file.modified);
    } catch (e) {
      if (!(e instanceof RequestError && e.error.code === "modified_on_disk")) throw e;
      if (!confirm(`${tabName(tab)} has changed on disk since it was opened. Overwrite it?`)) return;
      written = await write();
    }
    saved(tab, written);
  } catch (e) {
    setStatus(errorText(e), "error");
  }
}

/** Save As: a new name for a tab's schematic, asking before replacing an existing file. */
async function saveAs(tab = active) {
  if (!tab) return;
  const folder = tab.file ? tab.file.path.replace(/[\\/][^\\/]*$/, "") : (hello?.folders.user ?? "");
  const name = tab.file ? tab.file.path.split(/[\\/]/).pop()! : `${tabName(tab)}.schx`;
  const path = await chooseFile(client, folder, { mode: "save", name });
  if (!path) return;
  const other = tabs.find((t) => t !== tab && t.file && samePath(t.file.path, path));
  if (other) return setStatus(`${fileName(path)} is open in another tab; close it first.`, "error");
  const write = (overwrite: boolean) =>
    client.request<{ path: string; modified: string }>("file.write", { path, document: tab.state.doc, overwrite });
  try {
    let written;
    try {
      written = await write(false);
    } catch (e) {
      if (!(e instanceof RequestError && e.error.code === "exists")) throw e;
      if (!confirm(`${path} already exists. Replace it?`)) return;
      written = await write(true);
    }
    saved(tab, written);
    void watchFiles();
  } catch (e) {
    setStatus(errorText(e), "error");
  }
}

function saved(tab: Tab, written: { path: string; modified: string }) {
  tab.file = { path: written.path, modified: written.modified };
  tab.changedOnDisk = null;
  tab.state.history.markSaved();
  shownDirty.delete(tab);
  renderTabs();
  showBanner();
  updateChrome();
  remember();
  setStatus(`Saved ${written.path}`);
}

// ---- Files changed on disk ----

client.on("file.changed", (data) => {
  const { path, modified } = data as { path: string; modified: string };
  const tab = tabs.find((t) => t.file && samePath(t.file.path, path));
  if (!tab || !tab.file || tab.file.modified === modified) return;
  tab.changedOnDisk = modified;
  showBanner();
});

/** The banner for the active tab's file having changed on disk: reload it, or keep editing ours (saving then asks
 * before overwriting). */
function showBanner() {
  const tab = active;
  banner.replaceChildren();
  banner.hidden = !tab?.changedOnDisk;
  if (!tab?.changedOnDisk) return;
  const text = document.createElement("span");
  text.textContent = tab.state.history.dirty
    ? `${tabName(tab)} has changed on disk. Reloading it discards your changes.`
    : `${tabName(tab)} has changed on disk.`;
  const reload = document.createElement("button");
  reload.textContent = "Reload";
  reload.addEventListener("click", async () => {
    if (!tab.file) return;
    try {
      const { state, file, read } = await readFile(tab.file.path);
      tab.state = state;
      tab.file = file;
      tab.changedOnDisk = null;
      shownDirty.delete(tab);
      if (active === tab) {
        active = null;
        tab.place = view.place;
        activate(tab);
      }
      reportOpened(read);
    } catch (e) {
      setStatus(errorText(e), "error");
    }
  });
  const keep = document.createElement("button");
  keep.textContent = "Keep mine";
  keep.addEventListener("click", () => {
    tab.changedOnDisk = null;
    showBanner();
  });
  banner.append(text, reload, keep);
}

// ---- Components and wiring ----

/** A library entry clicked or chosen by its shortcut: the wire tool for the conductor, else placing the component. */
function pick(entry: CatalogEntry) {
  if (!editor.document) return setStatus("Open or create a schematic first.", "error");
  if (entry.component.type === WireType) editor.startWiring();
  else editor.startPlacing(entry.component, entry.layout);
  svg.focus();
}

// Shortcuts for the Common components. Browsers keep Ctrl+W and Ctrl+T for themselves, so the plain letters are the
// dependable ones; LiveSPICE's Ctrl shortcuts work where the browser allows.
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

/** Pastes clipboard XML, centred on `at` (or 20 units down and right of where it was copied from, without one). */
async function paste(text: string | null, at: Point | null) {
  const xml = text?.trim().startsWith("<") ? text : clipboardFallback;
  if (!xml || !editor.document) return;
  try {
    const { elements } = await client.request<{ elements: Element[] }>("schx.fromXml", { xml });
    for (const e of elements) e.id = editor.newId();
    // Rename clashing components first: a renamed component's layout differs (its name is drawn).
    nameAdded(editor.document, elements);
    await layouts.fetch(elements.filter((e): e is SymbolElement => e.kind === "symbol").map((s) => s.component));
    // By whole grid steps, so the elements stay on the grid.
    const bounds = editor.boundsOf(elements);
    if (bounds) {
      const center: Point = [Math.trunc((bounds.min[0] + bounds.max[0]) / 2), Math.trunc((bounds.min[1] + bounds.max[1]) / 2)];
      const by = at ? snap([at[0] - center[0], at[1] - center[1]]) : ([20, 20] as Point);
      moveElements({ circuit: {}, elements }, new Set(elements.map((e) => e.id)), by);
    }
    editor.addElements(elements, "Paste");
  } catch (e) {
    setStatus(`Couldn't paste: ${errorText(e)}`, "error");
  }
}

async function pasteFromClipboard(at: Point | null) {
  let text: string | null = null;
  try {
    text = await navigator.clipboard.readText();
  } catch {
    // Reading needs permission; without it, paste what was last copied here.
  }
  await paste(text, at);
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
  void paste(e.clipboardData?.getData("text/plain") ?? null, pointerAt);
});

// ---- Dropping components from the library ----

svg.addEventListener("dragover", (e) => {
  if (!e.dataTransfer?.types.includes(LibraryDragType) || !editor.document) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = "copy";
});

svg.addEventListener("drop", (e) => {
  const id = e.dataTransfer?.getData(LibraryDragType);
  const entry = id ? library.entry(id) : null;
  if (!entry || !editor.document) return;
  e.preventDefault();
  if (entry.component.type === WireType) return pick(entry);
  editor.dropComponent(entry.component, entry.layout, e.clientX, e.clientY);
  svg.focus();
});

// ---- Find (Ctrl+F): parts by name, each selected and brought into view in turn ----

const findBox = $<HTMLDivElement>("find");
const findText = $<HTMLInputElement>("find-text");
const findCount = $<HTMLSpanElement>("find-count");
let findAt = -1;

function findMatches(): SymbolElement[] {
  const doc = editor.document;
  const text = findText.value.trim().toLowerCase();
  if (!doc || !text) return [];
  const name = (s: SymbolElement) => s.component.properties["Name"] ?? "";
  return doc.elements
    .filter((e): e is SymbolElement => e.kind === "symbol")
    .filter((s) => name(s).toLowerCase().includes(text) || (s.component.properties["PartNumber"] ?? "").toLowerCase().includes(text))
    // Exact names first, then those starting with the text, then the rest; each in natural order (R2 before R10).
    .sort((a, b) => rank(a) - rank(b) || name(a).localeCompare(name(b), undefined, { numeric: true }));
  function rank(s: SymbolElement) {
    const n = name(s).toLowerCase();
    return n === text ? 0 : n.startsWith(text) ? 1 : 2;
  }
}

function showFind() {
  if (!editor.document) return;
  findBox.hidden = false;
  findText.focus();
  findText.select();
}

function hideFind() {
  findBox.hidden = true;
  svg.focus();
}

function findNext(step: number) {
  const matches = findMatches();
  findText.classList.toggle("invalid", findText.value.trim() !== "" && matches.length === 0);
  if (matches.length === 0) {
    findCount.textContent = findText.value.trim() ? "No match" : "";
    findAt = -1;
    return;
  }
  findAt = (((findAt + step) % matches.length) + matches.length) % matches.length;
  const found = matches[findAt]!;
  findCount.textContent = `${findAt + 1} of ${matches.length}`;
  editor.selectOnly([found.id]);
  const box = editor.boundsOf([found]);
  if (box) view.place = { zoom: Math.max(view.Zoom, 1), cx: (box.min[0] + box.max[0]) / 2, cy: (box.min[1] + box.max[1]) / 2 };
}

findText.addEventListener("input", () => {
  findAt = -1;
  findNext(1);
});
findText.addEventListener("keydown", (e) => {
  if (e.key === "Enter") (e.preventDefault(), findNext(e.shiftKey ? -1 : 1));
  else if (e.key === "Escape") (e.preventDefault(), hideFind());
});
$<HTMLButtonElement>("find-close").addEventListener("click", hideFind);

// ---- Export: the drawing as an image, in the theme's colours, without selection or hover marks ----

const SvgNamespace = "http://www.w3.org/2000/svg";

/** The schematic as a standalone SVG: drawn afresh without selection, with each shape's computed style written
 * onto it, so it looks the same outside the page. */
function exportSvg(): { text: string; width: number; height: number } | null {
  const doc = editor.document;
  const bounds = editor.bounds;
  if (!doc || !bounds) return null;
  const margin = 20;
  const x = bounds.min[0] - margin, y = bounds.min[1] - margin;
  const width = bounds.max[0] - bounds.min[0] + 2 * margin, height = bounds.max[1] - bounds.min[1] + 2 * margin;

  // Drawn inside the page's SVG for a moment, so the stylesheet applies to it.
  const scratch = document.createElementNS(SvgNamespace, "g") as SVGGElement;
  scratch.style.visibility = "hidden";
  svg.append(scratch);
  renderDocument(scratch, doc, layouts);
  scratch.querySelectorAll(".hit, .outline, .text-box, title").forEach((e) => e.remove());
  // What the stylesheet hides (an unselected Label's corner lines) stays hidden: display isn't among the styles copied.
  scratch.querySelectorAll("*").forEach((e) => getComputedStyle(e).display === "none" && e.remove());
  const copy = scratch.cloneNode(true) as SVGGElement;
  const properties = ["stroke", "fill", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-dasharray", "font-family", "font-size", "font-weight"];
  const originals = [...scratch.querySelectorAll("*")];
  const copies = [...copy.querySelectorAll("*")];
  originals.forEach((original, i) => {
    const style = getComputedStyle(original);
    const target = copies[i] as SVGElement;
    target.removeAttribute("class");
    target.setAttribute("style", properties.map((p) => `${p}:${style.getPropertyValue(p)}`).join(";"));
  });
  scratch.remove();
  copy.removeAttribute("style");

  const out = document.createElementNS(SvgNamespace, "svg");
  out.setAttribute("xmlns", SvgNamespace);
  out.setAttribute("viewBox", `${x} ${y} ${width} ${height}`);
  out.setAttribute("width", String(width));
  out.setAttribute("height", String(height));
  const background = document.createElementNS(SvgNamespace, "rect");
  for (const [k, v] of Object.entries({ x, y, width, height, fill: getComputedStyle(document.documentElement).getPropertyValue("--canvas").trim() }))
    background.setAttribute(k, String(v));
  out.append(background, copy);
  return { text: new XMLSerializer().serializeToString(out), width, height };
}

function download(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

async function exportImage(kind: "svg" | "png") {
  const image = exportSvg();
  if (!image || !active) return;
  const base = tabName(active).replace(/[\\/:*?"<>|]/g, "_");
  const svgBlob = new Blob([image.text], { type: "image/svg+xml" });
  if (kind === "svg") return download(svgBlob, `${base}.svg`);
  // PNG: the SVG drawn onto a canvas at twice its size, for a sharp image.
  const scale = 2;
  const img = new Image();
  const url = URL.createObjectURL(svgBlob);
  try {
    img.src = url;
    await img.decode();
    const canvas = document.createElement("canvas");
    canvas.width = Math.ceil(image.width * scale);
    canvas.height = Math.ceil(image.height * scale);
    canvas.getContext("2d")!.drawImage(img, 0, 0, canvas.width, canvas.height);
    const png = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, "image/png"));
    if (png) download(png, `${base}.png`);
  } catch (e) {
    setStatus(`Couldn't make the PNG: ${errorText(e)}`, "error");
  } finally {
    URL.revokeObjectURL(url);
  }
}

buttons.export.addEventListener("click", () => {
  const r = buttons.export.getBoundingClientRect();
  showMenu(r.left, r.bottom + 2, [
    { label: "SVG image", run: () => void exportImage("svg") },
    { label: "PNG image", run: () => void exportImage("png") },
  ]);
});

// ---- The context menu ----

interface MenuItem {
  label: string;
  keys?: string;
  enabled?: boolean;
  run: () => void;
}

function hideMenu() {
  menu.hidden = true;
}

function showMenu(x: number, y: number, items: (MenuItem | "-")[]) {
  menu.replaceChildren(
    ...items.map((item) => {
      if (item === "-") {
        const line = document.createElement("div");
        line.className = "menu-separator";
        return line;
      }
      const button = document.createElement("button");
      button.className = "menu-item";
      button.disabled = item.enabled === false;
      const label = document.createElement("span");
      label.textContent = item.label;
      const keys = document.createElement("span");
      keys.className = "keys";
      keys.textContent = item.keys ?? "";
      button.append(label, keys);
      button.addEventListener("click", () => {
        hideMenu();
        item.run();
      });
      return button;
    }),
  );
  menu.hidden = false;
  // Keep it on screen.
  const r = menu.getBoundingClientRect();
  menu.style.left = `${Math.max(0, Math.min(x, innerWidth - r.width - 4))}px`;
  menu.style.top = `${Math.max(0, Math.min(y, innerHeight - r.height - 4))}px`;
}

svg.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  if (!editor.prepareContextMenu(e)) return;
  const at = view.toSchematic(e.clientX, e.clientY);
  const selected = editor.selection.size > 0;
  const h = editor.history;
  showMenu(e.clientX, e.clientY, [
    { label: "Cut", keys: "Ctrl+X", enabled: selected, run: () => void copySelection(true) },
    { label: "Copy", keys: "Ctrl+C", enabled: selected, run: () => void copySelection(false) },
    { label: "Paste here", keys: "Ctrl+V", enabled: connected, run: () => void pasteFromClipboard(at) },
    { label: "Delete", keys: "Del", enabled: selected, run: () => editor.deleteSelection() },
    "-",
    { label: "Rotate left", keys: "←", enabled: selected, run: () => editor.rotate(1) },
    { label: "Rotate right", keys: "→", enabled: selected, run: () => editor.rotate(-1) },
    { label: "Flip", keys: "↑", enabled: selected, run: () => editor.flip() },
    "-",
    { label: "Select all", keys: "Ctrl+A", run: () => editor.selectAll() },
    { label: h.undoLabel ? `Undo ${h.undoLabel}` : "Undo", keys: "Ctrl+Z", enabled: h.canUndo, run: () => editor.undo() },
    { label: h.redoLabel ? `Redo ${h.redoLabel}` : "Redo", keys: "Ctrl+Y", enabled: h.canRedo, run: () => editor.redo() },
  ]);
});
window.addEventListener("pointerdown", (e) => !menu.contains(e.target as Node) && hideMenu(), true);
window.addEventListener("blur", hideMenu);
svg.addEventListener("wheel", hideMenu);

// ---- Buttons and keys ----

buttons.new.addEventListener("click", newTab);
buttons.open.addEventListener("click", async () => {
  const from = active?.file?.path ?? localStorage.getItem("spiceforge.activeFile") ?? hello?.folders.user ?? "";
  const folder = /\.(schx|xml)$/i.test(from) ? from.replace(/[\\/][^\\/]*$/, "") : from;
  const path = await chooseFile(client, folder);
  if (path) await open(path);
});
buttons.save.addEventListener("click", () => void save());
buttons.saveAs.addEventListener("click", () => void saveAs());
buttons.undo.addEventListener("click", () => editor.undo());
buttons.redo.addEventListener("click", () => editor.redo());
buttons.cut.addEventListener("click", () => void copySelection(true));
buttons.copy.addEventListener("click", () => void copySelection(false));
buttons.paste.addEventListener("click", () => void pasteFromClipboard(null));
buttons.remove.addEventListener("click", () => editor.deleteSelection());
buttons.wire.addEventListener("click", () => (editor.tool === "wire" ? editor.escape() : editor.startWiring()));
buttons.simulate.addEventListener("click", () => simulate());
buttons.audio.addEventListener("click", () => void configureAudio(client).catch((e) => setStatus(`Couldn't open the audio settings: ${errorText(e)}`, "error")));

/** Simulates the schematic shown, replacing any simulation running. */
function simulate() {
  if (!active || !connected) return;
  editor.escape();
  void simulation.start(active.state, tabName(active));
}
buttons.fit.addEventListener("click", () => view.fit(editor.bounds));
buttons.zoomIn.addEventListener("click", () => view.zoomAt(1.5));
buttons.zoomOut.addEventListener("click", () => view.zoomAt(1 / 1.5));

window.addEventListener("keydown", (e) => {
  if (editingText(e.target) || document.querySelector("dialog[open]")) return;
  const ctrl = e.ctrlKey || e.metaKey;
  const key = e.key.toLowerCase();
  const handled = () => e.preventDefault();
  if (!menu.hidden && e.key === "Escape") return hideMenu();
  if (e.key === "F5") {
    // F5 simulates, as in LiveSPICE, rather than reloading the page; Shift+F5 stops.
    handled();
    if (e.shiftKey) void simulation.stop();
    else simulate();
    return;
  }
  if (e.key === "Escape" && simulation.picking !== null) return simulation.setPicking(null);
  if (e.key === "Escape" && simulation.probing) return simulation.setProbing(false);
  if (ctrl && key === "o") (handled(), buttons.open.click());
  else if (ctrl && key === "f") (handled(), showFind());
  else if (ctrl && key === "s" && e.shiftKey) (handled(), void saveAs());
  else if (ctrl && key === "s") (handled(), void save());
  else if (ctrl && key === "z" && !e.shiftKey) (handled(), editor.undo());
  else if (ctrl && (key === "y" || (key === "z" && e.shiftKey))) (handled(), editor.redo());
  else if (ctrl && key === "a") (handled(), editor.selectAll());
  else if (ctrl && !e.shiftKey && !e.altKey && CtrlShortcutTypes[key]) (handled(), pickType(CtrlShortcutTypes[key]!));
  else if (e.key === "Escape") editor.escape();
  else if (e.key === "Delete") (handled(), editor.deleteSelection());
  else if (ctrl || e.altKey) return;
  else if (e.key === "ArrowLeft") (handled(), editor.rotate(1));
  else if (e.key === "ArrowRight") (handled(), editor.rotate(-1));
  else if (e.key === "ArrowUp" || e.key === "ArrowDown") (handled(), editor.flip());
  else if (ShortcutTypes[key]) (handled(), pickType(ShortcutTypes[key]!));
  else if (e.key === "+" || e.key === "=") view.zoomAt(1.5);
  else if (e.key === "-") view.zoomAt(1 / 1.5);
  else if (e.key === "0" || key === "f") view.fit(editor.bounds);
});

window.addEventListener("beforeunload", (e) => {
  if (tabs.some((t) => t.state.history.dirty)) e.preventDefault();
});

// ---- Starting up ----

function storedList(key: string): string[] {
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? "[]") as unknown;
    return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
  } catch {
    return [];
  }
}

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
    simulation.disconnected();
    buttons.open.disabled = buttons.new.disabled = true;
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
  buttons.open.disabled = buttons.new.disabled = false;
  updateChrome();
  setStatus(`Connected to ${hello.server.name} ${hello.server.version}.`);
  // The server's copy of the theme wins over this browser's, which may be older or cleared.
  try {
    const settings = await client.request<{ client?: unknown }>("settings.get");
    if (isTheme((settings.client as Record<string, unknown> | null)?.["theme"])) adoptSettings(settings);
    else {
      clientSettings = (settings.client as Record<string, unknown> | null) ?? {};
      if (currentTheme() !== "system") themePicker.dispatchEvent(new Event("change"));
    }
  } catch {
    // The theme stays as this browser has it.
  }
  try {
    await library.load();
    editor.wireNameOf = wireNames(library.types);
    editor.redraw();
  } catch (e) {
    setStatus(`Couldn't load the components: ${errorText(e)}`, "error");
  }
  properties.render();

  // Reopen what was open last time, plus any file the address names; the active one last, so it ends up shown.
  const requested = new URLSearchParams(location.search).get("file");
  let files = storedList("spiceforge.openFiles");
  const legacy = localStorage.getItem("spiceforge.lastFile");
  if (files.length === 0 && legacy) files = [legacy];
  if (requested && !files.some((f) => samePath(f, requested))) files.push(requested);
  const shown = requested ?? localStorage.getItem("spiceforge.activeFile");
  for (const f of files) if (!shown || !samePath(f, shown)) await open(f);
  if (shown && files.some((f) => samePath(f, shown))) await open(shown);
  if (tabs.length === 0) newTab();

  // A simulation this browser tab was showing before a reload carries on, if its schematic is open again.
  await simulation.resume((key) => {
    const tab = tabs.find((t) => t.file && samePath(t.file.path, key));
    return tab ? { state: tab.state, title: tabName(tab) } : null;
  });
}

// ---- The theme: chosen here, kept in this browser and in the server's client settings ----

initTheme();
themePicker.append(...Themes.map((t) => new Option(t.name, t.id)));
themePicker.value = currentTheme();

/** The server's opaque client settings, which the theme shares with whatever else is kept there. */
let clientSettings: Record<string, unknown> = {};

function adoptSettings(settings: { client?: unknown }) {
  clientSettings = settings.client && typeof settings.client === "object" ? (settings.client as Record<string, unknown>) : {};
  const theme = clientSettings["theme"];
  if (isTheme(theme) && theme !== currentTheme()) {
    applyTheme(theme);
    themePicker.value = theme;
  }
}

themePicker.addEventListener("change", () => {
  if (!isTheme(themePicker.value)) return;
  applyTheme(themePicker.value);
  svg.focus();
  if (!connected) return;
  clientSettings = { ...clientSettings, theme: themePicker.value };
  client.request("settings.set", { client: clientSettings }).catch((e) => setStatus(`Couldn't save the theme: ${errorText(e)}`, "error"));
});

// Another window changing the theme changes it here too.
client.on("settings.changed", (data) => adoptSettings(data as { client?: unknown }));

view.fit(null);
void start();
