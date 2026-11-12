// The properties panel (LiveSPICE's PropertyGrid): the selected components' properties, grouped by category, edited
// together. With no symbol selected, the schematic's own properties (its name, part number and description, for use as
// a subcircuit). A drop-down narrows a multiple selection to one object, as LiveSPICE's does.
//
// Each edit is checked by the server (values.check), which returns it as the component would hold it ("4.7k" becomes
// "4.7 kΩ"), and is one undo step. A name that another component already has is refused: LiveSPICE would only report
// the clash when the circuit is built.

import type { ApiClient } from "./client";
import { componentNames } from "./edits";
import type { Editor } from "./editor";
import type { Catalog, ComponentData, PropertyDescriptor, SymbolElement, ValueCheck } from "./protocol";

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

/** What the grid edits: components (by their symbols) or the schematic's circuit. */
type Subject = { kind: "symbols"; symbols: SymbolElement[] } | { kind: "circuit" };

export class PropertiesPanel {
  private readonly picker: HTMLSelectElement;
  private readonly grid: HTMLDivElement;
  private readonly help: HTMLDivElement;
  private selectionKey = "";
  /** The one object a multiple selection is narrowed to, by element id; null for all of them. */
  private narrowed: string | null = null;

  /** Reports a refused value. */
  onError: (message: string) => void = () => {};

  constructor(
    root: HTMLElement,
    private readonly editor: Editor,
    private readonly client: ApiClient,
    private readonly types: () => Catalog["types"],
  ) {
    this.picker = h("select", { className: "picker" });
    this.grid = h("div", { className: "grid" });
    this.help = h("div", { className: "help" });
    root.append(h("div", { className: "panel-title" }, "Properties"), this.picker, this.grid, this.help);
    this.picker.addEventListener("change", () => {
      this.narrowed = this.picker.value || null;
      this.render();
    });
  }

  /** Brings the grid up to date with the selection and the document. Leaves it alone while a field is being edited. */
  render() {
    if (this.grid.contains(document.activeElement) && document.activeElement !== document.body) return;
    const doc = this.editor.document;
    const symbols = this.editor.selectedElements().filter((e): e is SymbolElement => e.kind === "symbol");
    const key = symbols.map((s) => s.id).join(",");
    if (key !== this.selectionKey) {
      this.selectionKey = key;
      this.narrowed = null;
    }

    // The picker: "(n objects)" and each object, as LiveSPICE's combo box above its grid.
    this.picker.replaceChildren();
    this.picker.hidden = symbols.length < 2;
    if (symbols.length >= 2) {
      this.picker.append(h("option", { value: "", textContent: `(${symbols.length} objects)` }));
      for (const s of symbols) this.picker.append(h("option", { value: s.id, textContent: label(s, this.types()) }));
      this.picker.value = this.narrowed ?? "";
    }

    if (!doc) {
      this.grid.replaceChildren(h("div", { className: "muted", textContent: "No schematic open." }));
      return;
    }
    const narrowed = symbols.find((s) => s.id === this.narrowed);
    const subject: Subject = narrowed ? { kind: "symbols", symbols: [narrowed] } : symbols.length ? { kind: "symbols", symbols } : { kind: "circuit" };
    this.grid.replaceChildren(...this.rows(subject));
    if (subject.kind === "circuit") this.grid.prepend(h("div", { className: "subject", textContent: "Schematic" }));
    else if (subject.symbols.length === 1) this.grid.prepend(h("div", { className: "subject", textContent: label(subject.symbols[0]!, this.types()) }));
  }

  private descriptors(subject: Subject): PropertyDescriptor[] {
    const types = this.types();
    if (subject.kind === "circuit") return types["circuit"]?.properties ?? [];
    // The properties every selected component has, with the same kind, as a multi-object property grid shows them.
    const lists = [...new Set(subject.symbols.map((s) => s.component.type))].map((t) => types[t]?.properties ?? []);
    const [first, ...rest] = lists;
    return (first ?? []).filter((d) => rest.every((l) => l.some((o) => o.name === d.name && o.kind === d.kind)));
  }

  private rows(subject: Subject): HTMLElement[] {
    const descriptors = this.descriptors(subject);
    if (descriptors.length === 0) return [h("div", { className: "muted", textContent: subject.kind === "symbols" ? "No properties in common." : "Loading…" })];
    const byCategory = new Map<string, PropertyDescriptor[]>();
    for (const d of descriptors) byCategory.set(d.category, [...(byCategory.get(d.category) ?? []), d]);
    const out: HTMLElement[] = [];
    for (const category of [...byCategory.keys()].sort()) {
      out.push(h("div", { className: "category", textContent: category }));
      for (const d of byCategory.get(category)!.sort((a, b) => a.name.localeCompare(b.name))) out.push(this.row(subject, d));
    }
    return out;
  }

  private bags(subject: Subject): Record<string, string>[] {
    const doc = this.editor.document!;
    return subject.kind === "circuit" ? [doc.circuit] : subject.symbols.map((s) => s.component.properties);
  }

  private row(subject: Subject, d: PropertyDescriptor): HTMLElement {
    const values = this.bags(subject).map((b) => b[d.name] ?? d.default ?? "");
    const same = values.every((v) => v === values[0]);
    const shown = same ? values[0]! : "";
    let field: HTMLInputElement | HTMLSelectElement;
    if (d.kind === "enum" || d.kind === "boolean") {
      const options = d.kind === "boolean" ? ["True", "False"] : (d.values ?? []);
      field = h("select");
      if (!same) field.append(h("option", { value: "", textContent: "" }));
      for (const o of options) field.append(h("option", { value: o, textContent: o }));
      // Booleans are stored as "True" or "False", but compare case-insensitively ("true" in older files).
      field.value = options.find((o) => o.toLowerCase() === shown.toLowerCase()) ?? "";
      field.addEventListener("change", () => void this.commit(subject, d, field.value, field));
    } else {
      const input = h("input", { type: "text", value: shown, placeholder: same ? "" : "(different values)", spellcheck: false });
      field = input;
      input.addEventListener("keydown", (e) => {
        if (e.key === "Enter") input.blur();
        else if (e.key === "Escape") {
          input.value = shown;
          input.classList.remove("invalid");
          input.blur();
        }
      });
      input.addEventListener("change", () => void this.commit(subject, d, input.value, input));
    }
    field.disabled = d.readOnly;
    field.addEventListener("focus", () => (this.help.replaceChildren(h("strong", { textContent: d.name }), h("div", { textContent: d.description ?? "" }))));
    const name = h("label", { textContent: d.name, title: d.description ?? "" });
    // A quantity's value carries its units ("100 Ω"), as LiveSPICE's grid shows it.
    return h("div", { className: "row" }, name, field);
  }

  private async commit(subject: Subject, d: PropertyDescriptor, value: string, field: HTMLInputElement | HTMLSelectElement) {
    const doc = this.editor.document;
    if (!doc || value === "") return;
    const fail = (message: string) => {
      field.classList.add("invalid");
      field.title = message;
      this.onError(`${d.name}: ${message}`);
    };

    if (d.name === "Name" && subject.kind === "symbols") {
      if (subject.symbols.length > 1) return fail("each component needs its own name.");
      const others = componentNames(doc);
      others.delete(subject.symbols[0]!.component.properties["Name"] ?? "");
      if (others.has(value)) return fail(`'${value}' is already the name of another component.`);
    }

    // Each type checks the value its own way (a quantity takes the property's units), so check it once per type.
    const groups = new Map<string, Record<string, string>[]>();
    if (subject.kind === "circuit") groups.set("circuit", [doc.circuit]);
    else for (const s of subject.symbols) groups.set(s.component.type, [...(groups.get(s.component.type) ?? []), s.component.properties]);
    let checked: ValueCheck[];
    try {
      const reply = await this.client.request<{ items: ValueCheck[] }>("values.check", {
        items: [...groups.keys()].map((type) => ({ type, property: d.name, value })),
      });
      checked = reply.items;
    } catch (e) {
      return fail((e as Error).message);
    }
    const bad = checked.find((c) => !c.ok);
    if (bad) return fail(bad.message ?? "not a valid value.");

    field.classList.remove("invalid");
    field.title = "";
    const components: ComponentData[] = subject.kind === "symbols" ? subject.symbols.map((s) => s.component) : [];
    const entries = [...groups.values()].map((targets, i) => ({ targets, value: checked[i]!.value ?? value }));
    // Nothing to do if every value is unchanged.
    if (entries.every((g) => g.targets.every((t) => t[d.name] === g.value))) {
      this.render();
      return;
    }
    (document.activeElement as HTMLElement | null)?.blur();
    await this.editor.setProperty(d.name, entries, components);
    this.render();
  }
}

function label(s: SymbolElement, types: Catalog["types"]): string {
  const type = types[s.component.type]?.name ?? s.component.type.split(",")[0]!.split(".").pop()!;
  const name = s.component.properties["Name"];
  return name ? `${type} ${name}` : type;
}
