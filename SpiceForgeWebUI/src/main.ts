// SpiceForge's web front end. First milestone: connect to SpiceForgeServer, open a schematic, and draw it.

import { ApiClient, RequestError } from "./client";
import { chooseFile } from "./fileDialog";
import { LayoutCache } from "./layouts";
import type { FileRead, Hello, SchematicDocument, SymbolElement } from "./protocol";
import { renderDocument } from "./render";
import { SchematicView } from "./view";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const svg = document.getElementById("schematic") as unknown as SVGSVGElement;
const drawing = document.getElementById("drawing") as unknown as SVGGElement;
const status = $<HTMLSpanElement>("status");
const pointer = $<HTMLSpanElement>("pointer");
const zoomLabel = $<HTMLSpanElement>("zoom");
const title = $<HTMLSpanElement>("title");
const openButton = $<HTMLButtonElement>("open");
const fitButton = $<HTMLButtonElement>("fit");
const zoomIn = $<HTMLButtonElement>("zoom-in");
const zoomOut = $<HTMLButtonElement>("zoom-out");

const client = new ApiClient();
const layouts = new LayoutCache(client);
const view = new SchematicView(svg);
let hello: Hello | null = null;
let current: { path: string; document: SchematicDocument; bounds: ReturnType<typeof renderDocument>["bounds"] } | null = null;

view.onChange = (zoom) => (zoomLabel.textContent = `${Math.round(zoom * 100)}%`);
view.onPointer = (x, y) => (pointer.textContent = `${Math.round(x)}, ${Math.round(y)}`);

function setStatus(text: string, kind: "info" | "error" = "info") {
  status.textContent = text;
  status.className = kind;
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

async function open(path: string) {
  setStatus(`Opening ${path}…`);
  try {
    const read = await client.request<FileRead>("file.read", { path });
    const symbols = read.document.elements.filter((e): e is SymbolElement => e.kind === "symbol");
    await layouts.fetch(symbols.map((s) => s.component));
    const rendered = renderDocument(drawing, read.document, layouts);
    current = { path: read.path, document: read.document, bounds: rendered.bounds };
    view.fit(rendered.bounds);
    const name = read.path.split(/[\\/]/).pop()!.replace(/\.(schx|xml)$/i, "");
    title.textContent = name;
    title.title = read.path;
    document.title = `${name} - SpiceForge`;
    localStorage.setItem("spiceforge.lastFile", read.path);
    const wires = read.document.elements.length - symbols.length;
    const notes = [...read.messages.map((m) => m.message), ...rendered.problems];
    setStatus(`${symbols.length} symbols, ${wires} wires${notes.length ? ` — ${notes.join("; ")}` : ""}`, notes.length ? "error" : "info");
  } catch (e) {
    setStatus(e instanceof RequestError ? e.error.message : (e as Error).message, "error");
  }
}

openButton.addEventListener("click", async () => {
  const last = localStorage.getItem("spiceforge.lastFile");
  const folder = current?.path ?? last ?? hello?.folders.user ?? "";
  const start = folder.endsWith(".schx") || folder.endsWith(".xml") ? folder.replace(/[\\/][^\\/]*$/, "") : folder;
  const path = await chooseFile(client, start);
  if (path) await open(path);
});
fitButton.addEventListener("click", () => view.fit(current?.bounds ?? null));
zoomIn.addEventListener("click", () => view.zoomAt(1.5));
zoomOut.addEventListener("click", () => view.zoomAt(1 / 1.5));
window.addEventListener("keydown", (e) => {
  if (e.target instanceof HTMLInputElement) return;
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "o") {
    e.preventDefault();
    openButton.click();
  } else if (e.key === "+" || e.key === "=") view.zoomAt(1.5);
  else if (e.key === "-") view.zoomAt(1 / 1.5);
  else if (e.key === "0" || e.key.toLowerCase() === "f") fitButton.click();
});

async function start() {
  const t = token();
  if (!t) {
    setStatus("No token: open the address SpiceForgeServer printed when it started.", "error");
    return;
  }
  client.onClose = (reason) => {
    setStatus(`Disconnected from the server: ${reason}`, "error");
    openButton.disabled = true;
  };
  try {
    await client.connect(t);
    hello = await client.request<Hello>("hello", { protocol: 1, client: "SpiceForgeWebUI" });
  } catch (e) {
    setStatus(`${(e as Error).message} Is SpiceForgeServer running, and is the token current?`, "error");
    return;
  }
  openButton.disabled = false;
  setStatus(`Connected to ${hello.server.name} ${hello.server.version}. Open a schematic (Ctrl+O).`);
  const last = localStorage.getItem("spiceforge.lastFile");
  const requested = new URLSearchParams(location.search).get("file");
  if (requested ?? last) await open((requested ?? last)!);
}

view.fit(null);
void start();
