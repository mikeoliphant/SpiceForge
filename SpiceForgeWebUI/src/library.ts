// The component library panel (LiveSPICE's ComponentLibrary): the catalogue's categories as expanders of symbol icons,
// a filter that replaces them with a flat list of matching components, and a refresh button. Clicking a component
// starts placing it (the wire entry starts the wire tool).

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

export class LibraryPanel {
  private catalog: Catalog | null = null;
  private readonly filter: HTMLInputElement;
  private readonly tree: HTMLDivElement;
  private readonly flat: HTMLDivElement;
  private readonly expanded = new Map<string, boolean>();

  /** Called when a component is clicked. */
  onPick: (entry: CatalogEntry) => void = () => {};

  constructor(
    root: HTMLElement,
    private readonly client: ApiClient,
  ) {
    this.filter = h("input", { type: "search", placeholder: "Filter", className: "filter", spellcheck: false });
    const refresh = h("button", { textContent: "↻", title: "Reload the component libraries" });
    this.tree = h("div", { className: "tree" });
    this.flat = h("div", { className: "flat" });
    root.append(h("div", { className: "panel-title" }, "Components"), h("div", { className: "library-bar" }, this.filter, refresh), this.tree, this.flat);
    this.filter.addEventListener("input", () => this.render());
    this.filter.addEventListener("keydown", (e) => {
      if (e.key === "Escape") {
        this.filter.value = "";
        this.render();
      }
    });
    refresh.addEventListener("click", () => void this.load(true));
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
        matches.push(this.item(id, entry, true));
      }
      this.flat.replaceChildren(...(matches.length ? matches : [h("div", { className: "muted", textContent: "No components match." })]));
    }
  }

  private category(c: CatalogCategory, path: string): HTMLElement {
    const key = `${path}/${c.name}`;
    const details = h("details", { className: "category", open: this.expanded.get(key) ?? c.expanded });
    details.addEventListener("toggle", () => this.expanded.set(key, details.open));
    const grid = h("div", { className: "icons" }, ...c.entries.map((id) => this.item(id, this.catalog!.entries[id]!, false)));
    details.append(h("summary", { textContent: c.name }), grid, ...c.children.map((child) => this.category(child, key)));
    return details;
  }

  private item(id: string, entry: CatalogEntry, withName: boolean): HTMLElement {
    const tip = entry.description ? `${entry.name}\n${entry.description}` : entry.name;
    const button = h("button", { className: withName ? "entry named" : "entry", title: tip });
    button.dataset["entry"] = id;
    button.append(drawIcon(entry.layout, 22));
    if (withName) button.append(h("span", { textContent: entry.name }));
    button.addEventListener("click", () => this.onPick(entry));
    return button;
  }
}
