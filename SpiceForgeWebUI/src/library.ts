// The component library panel (LiveSPICE's ComponentLibrary): the catalogue's categories as expanders of components,
// each its symbol icon and name, a filter that replaces them with a flat list of matching components, and a refresh
// button. The names matter: a library's specializations of one generic component (its diodes, op-amps, transistors,
// tubes) share that component's symbol, so their icons alone can't tell them apart. A compact view of icons alone can
// be chosen instead, and is remembered. Clicking a component starts placing it (the wire entry starts the wire tool).

import type { ApiClient } from "./client";
import type { Catalog, CatalogCategory, CatalogEntry } from "./protocol";
import { drawIcon } from "./render";

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

/** The conductor, which LiveSPICE's library turns into the wire tool rather than a symbol. */
export const WireType = "Circuit.Conductor, Circuit";

/** The drag data type of a library entry, carrying its id. */
export const LibraryDragType = "application/x-spiceforge-entry";

/** Where the choice of the compact, icons-only view is remembered. */
const CompactKey = "spiceforge.libraryCompact";

function readCompact(): boolean {
  try {
    return localStorage.getItem(CompactKey) === "true";
  } catch {
    return false;
  }
}

export class LibraryPanel {
  private catalog: Catalog | null = null;
  private readonly filter: HTMLInputElement;
  private readonly tree: HTMLDivElement;
  private readonly flat: HTMLDivElement;
  private readonly expanded = new Map<string, boolean>();
  private compact = readCompact();

  /** Called when a component is clicked. */
  onPick: (entry: CatalogEntry) => void = () => {};

  /** Called when the folders button is clicked, to manage the user's component folders. */
  onFolders: () => void = () => {};

  /** Called after the catalogue changes (reloaded here, or by the server for every client). */
  onChanged: () => void = () => {};

  constructor(
    root: HTMLElement,
    private readonly client: ApiClient,
  ) {
    this.filter = h("input", { type: "search", placeholder: "Filter", className: "filter", spellcheck: false });
    const refresh = h("button", { textContent: "↻", title: "Reload the component libraries" });
    const folders = h("button", { textContent: "…", title: "Component folders: add your own libraries" });
    // A grid of four squares, in the text's colour: font glyphs for this (▦) come out as coloured emoji on some systems.
    const view = h("button", { className: "view-toggle" });
    view.innerHTML = '<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true" fill="currentColor"><rect x="0" y="0" width="5" height="5"/><rect x="7" y="0" width="5" height="5"/><rect x="0" y="7" width="5" height="5"/><rect x="7" y="7" width="5" height="5"/></svg>';
    const showView = () => {
      view.title = this.compact ? "Show component names" : "Show icons only";
      view.classList.toggle("active", this.compact);
      root.classList.toggle("compact", this.compact);
    };
    showView();
    view.addEventListener("click", () => {
      this.compact = !this.compact;
      try {
        localStorage.setItem(CompactKey, String(this.compact));
      } catch {
        // Not remembered, then; the view still changes.
      }
      showView();
    });
    this.tree = h("div", { className: "tree" });
    this.flat = h("div", { className: "flat" });
    root.append(h("div", { className: "panel-title" }, "Components"), h("div", { className: "library-bar" }, this.filter, view, refresh, folders), this.tree, this.flat);
    this.filter.addEventListener("input", () => this.render());
    this.filter.addEventListener("keydown", (e) => {
      if (e.key === "Escape") {
        this.filter.value = "";
        this.render();
      }
    });
    refresh.addEventListener("click", () => void this.load(true));
    folders.addEventListener("click", () => this.onFolders());
    // The server reloads the catalogue for everyone when the component folders change, or on another's refresh.
    client.on("catalog.changed", (data) => {
      this.catalog = data as Catalog;
      this.render();
      this.onChanged();
    });
  }

  /** An entry by its id, as a drag from the library carries it. */
  entry(id: string): CatalogEntry | null {
    return this.catalog?.entries[id] ?? null;
  }

  /** The catalogue's entries, once loaded. */
  get entries(): Record<string, CatalogEntry> {
    return this.catalog?.entries ?? {};
  }

  get types(): Catalog["types"] {
    return this.catalog?.types ?? {};
  }

  /** The first entry in a category with this component type (for the shortcut keys). */
  find(categoryName: string, type: string): CatalogEntry | null {
    const category = this.catalog?.categories.find((c) => c.name === categoryName);
    const id = category?.entries.find((id) => this.catalog!.entries[id]!.component.type === type);
    return id ? this.catalog!.entries[id]! : null;
  }

  async load(refresh = false) {
    this.tree.replaceChildren(h("div", { className: "muted", textContent: "Loading components…" }));
    this.catalog = await this.client.request<Catalog>(refresh ? "catalog.refresh" : "catalog.get");
    this.render();
    this.onChanged();
  }

  private render() {
    if (!this.catalog) return;
    const text = this.filter.value.trim().toUpperCase();
    this.tree.hidden = text !== "";
    this.flat.hidden = text === "";
    if (text === "") {
      this.tree.replaceChildren(...this.catalog.categories.map((c) => this.category(c, "")));
    } else {
      // LiveSPICE's filter: every component whose name contains the text, in one list.
      const seen = new Set<string>();
      const matches: HTMLElement[] = [];
      for (const [id, entry] of Object.entries(this.catalog.entries)) {
        const key = `${entry.name}|${entry.component.type}|${JSON.stringify(entry.component.properties)}`;
        if (!entry.name.toUpperCase().includes(text) || seen.has(key)) continue;
        seen.add(key);
        matches.push(this.item(id, entry));
      }
      this.flat.replaceChildren(matches.length ? h("div", { className: "icons" }, ...matches) : h("div", { className: "muted", textContent: "No components match." }));
    }
  }

  private category(c: CatalogCategory, path: string): HTMLElement {
    const key = `${path}/${c.name}`;
    const details = h("details", { className: "category", open: this.expanded.get(key) ?? c.expanded });
    details.addEventListener("toggle", () => this.expanded.set(key, details.open));
    const grid = h("div", { className: "icons" }, ...c.entries.map((id) => this.item(id, this.catalog!.entries[id]!)));
    details.append(h("summary", { textContent: c.name }), grid, ...c.children.map((child) => this.category(child, key)));
    return details;
  }

  /** A component's button: its icon and name (the name hidden by the compact view's CSS, and kept in the tooltip). */
  private item(id: string, entry: CatalogEntry): HTMLElement {
    const tip = entry.description ? `${entry.name}\n${entry.description}` : entry.name;
    const button = h("button", { className: "entry", title: tip });
    button.dataset["entry"] = id;
    button.append(drawIcon(entry.layout, 18), h("span", { className: "name", textContent: entry.name }));
    button.addEventListener("click", () => this.onPick(entry));
    // Dragged onto the canvas, it's placed where it's dropped.
    button.draggable = true;
    button.addEventListener("dragstart", (e) => {
      e.dataTransfer?.setData(LibraryDragType, id);
      if (e.dataTransfer) e.dataTransfer.effectAllowed = "copy";
    });
    return button;
  }
}
