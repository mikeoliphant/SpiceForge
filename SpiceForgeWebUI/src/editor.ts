// The document being edited, its selection and history, and the editing tools. They started as ports of LiveSPICE's
// (UISpec.md, Tools); where LiveSPICE's behaviour didn't make good sense, they now differ (WebSocketApi.md, Editing).
//
// Selection tool:
// - pressing on an element, without Ctrl, selects it (if it wasn't) and starts a move: the selection follows the
//   pointer in grid steps, live, and the whole drag is one undo step (right button or Escape cancels it). Releasing
//   without moving selects just that element;
// - pressing on empty space, or with Ctrl, starts a rubber band. Releasing without moving is a click: Ctrl toggles the
//   element under the pointer; on empty space, a click clears the selection (Ctrl keeps it). Releasing after a drag
//   selects everything the band touches (Ctrl adds to the selection);
// - a double-click adds every symbol of the same type to the selection;
// - the arrow keys rotate (Left counter-clockwise, Right clockwise) and flip (Up, Down) the selection about its centre.
//
// Symbol tool: a gray ghost of the component follows the pointer; the arrow keys rotate and flip it; a click places a
// copy and selects it, then returns to the selection tool unless Ctrl is held.
//
// Wire tool: a drag draws an L-shaped wire, its first leg in the direction the drag starts, joined to what it meets
// on release; then back to the selection tool unless Ctrl is held.
//
// Escape returns to the selection tool from the others.

import { ChangeEdit, CompositeEdit, EditStack, FlipEdit, MoveEdit, PropertyEdit, RotateEdit, flipElements, moveElements, nameAdded, rotateElements, type Edit } from "./edits";
import { boxIntersects, mapBox, mappingOf, normalize, snap, unionBoxes, wireIntersects, type Box } from "./geometry";
import type { LayoutCache } from "./layouts";
import type { ComponentData, Element, Layout, Point, SchematicDocument, SymbolElement } from "./protocol";
import { drawGhost, renderDocument, type Rendered } from "./render";
import type { SchematicView } from "./view";
import { addWire, findWirePath } from "./wires";

const SVG = "http://www.w3.org/2000/svg";

export type Tool =
  | { kind: "select" }
  | { kind: "place"; component: ComponentData; layout: Layout; rotation: number; flip: boolean; at: Point | null }
  | { kind: "wire" };

type Gesture =
  | { kind: "band"; pointer: number; from: Point; to: Point; hit: string | null; additive: boolean }
  | { kind: "move"; pointer: number; ids: Set<string>; last: Point; total: Point; pressed: string }
  | { kind: "wire"; pointer: number; mouse: Point[] };

export class Editor {
  readonly history = new EditStack();
  readonly selection = new Set<string>();
  private doc: SchematicDocument | null = null;
  private rendered: Rendered = { bounds: null, problems: [] };
  private gesture: Gesture | null = null;
  private current: Tool = { kind: "select" };
  private nextId = 0;
  private readonly band: SVGRectElement;
  private readonly ghost: SVGGElement;
  private readonly wirePreview: SVGPolylineElement;

  /** Called after the drawing changes, and after the selection, tool or history does. */
  onChange: () => void = () => {};
  /** Reports a failure the user should see (the status bar). */
  onError: (message: string) => void = () => {};

  constructor(
    private readonly svg: SVGSVGElement,
    private readonly drawing: SVGGElement,
    overlay: SVGGElement,
    private readonly view: SchematicView,
    private readonly layouts: LayoutCache,
  ) {
    this.band = document.createElementNS(SVG, "rect");
    this.band.setAttribute("class", "band");
    this.band.style.display = "none";
    this.ghost = document.createElementNS(SVG, "g");
    this.wirePreview = document.createElementNS(SVG, "polyline");
    this.wirePreview.setAttribute("class", "wire-preview");
    this.wirePreview.style.display = "none";
    overlay.append(this.band, this.ghost, this.wirePreview);
    this.history.onChange = () => this.onChange();

    svg.addEventListener("pointerdown", (e) => this.pointerDown(e));
    svg.addEventListener("pointermove", (e) => this.pointerMove(e));
    svg.addEventListener("pointerup", (e) => this.pointerUp(e));
    svg.addEventListener("pointercancel", () => this.cancel());
    svg.addEventListener("pointerleave", () => this.hideGhost());
    // The right button cancels a gesture in progress, as in LiveSPICE. Pressed while the left is held, it doesn't
    // make a pointerdown (pointer events report a second button as a "chorded" move), but it does make a mousedown.
    svg.addEventListener("mousedown", (e) => e.button === 2 && this.cancel());
    svg.addEventListener("dblclick", (e) => this.doubleClick(e));
    svg.addEventListener("contextmenu", (e) => e.preventDefault());
  }

  get document(): SchematicDocument | null {
    return this.doc;
  }

  get bounds(): Box | null {
    return this.rendered.bounds;
  }

  get problems(): string[] {
    return this.rendered.problems;
  }

  get tool(): Tool["kind"] {
    return this.current.kind;
  }

  /** Starts editing a document, with an empty selection and history. */
  load(doc: SchematicDocument) {
    this.cancel();
    this.setTool({ kind: "select" });
    this.doc = doc;
    this.selection.clear();
    this.history.clear();
    this.redraw();
  }

  redraw() {
    if (!this.doc) return;
    this.rendered = renderDocument(this.drawing, this.doc, this.layouts, this.selection);
    this.onChange();
  }

  // ---- Edits ----

  /** Records an edit that has been applied, drops vanished elements from the selection, and redraws. */
  private commit(edit: Edit) {
    this.history.push(edit);
    this.pruneSelection();
    this.redraw();
  }

  private pruneSelection() {
    if (!this.doc) return;
    const present = new Set(this.doc.elements.map((e) => e.id));
    for (const id of [...this.selection]) if (!present.has(id)) this.selection.delete(id);
  }

  undo() {
    if (this.gesture) return;
    if (this.history.undo()) {
      this.pruneSelection();
      this.redraw();
    }
  }

  redo() {
    if (this.gesture) return;
    if (this.history.redo()) {
      this.pruneSelection();
      this.redraw();
    }
  }

  /** A fresh element id, unused in the document. */
  newId = (): string => {
    const used = new Set(this.doc?.elements.map((e) => e.id));
    let id: string;
    do id = `n${++this.nextId}`;
    while (used.has(id));
    return id;
  };

  /** Adds elements (renamed where their names clash, as LiveSPICE does) as one undo step, and selects them alone.
   * Their layouts must already be cached. */
  addElements(elements: Element[], label: string) {
    if (!this.doc || elements.length === 0) return;
    nameAdded(this.doc, elements);
    const edit = new ChangeEdit(label, this.doc, [], elements);
    edit.apply();
    this.commit(edit);
    this.select(
      elements.map((e) => e.id),
      true,
    );
  }

  /** The bounds of some elements, from their layouts (symbols) and ends (wires); null if none has any. */
  boundsOf(elements: Element[]): Box | null {
    const boxes: Box[] = [];
    for (const e of elements) {
      if (e.kind === "wire") boxes.push(normalize(e.a, e.b));
      else if (e.kind === "symbol") {
        const layout = this.layouts.get(e.component);
        if (layout) boxes.push(mapBox(mappingOf(e), layout.bounds));
      }
    }
    return unionBoxes(boxes);
  }

  /** The selected elements, in document order. */
  selectedElements(): Element[] {
    return this.doc?.elements.filter((e) => this.selection.has(e.id)) ?? [];
  }

  /** Delete: removes the selection, as one undo step. */
  deleteSelection(label = "Delete") {
    const doomed = this.selectedElements();
    if (!this.doc || this.gesture || doomed.length === 0) return;
    const edit = new ChangeEdit(label, this.doc, doomed, []);
    edit.apply();
    this.commit(edit);
  }

  /** Sets a property, as one undo step: each group's value on its property bags (components', or the circuit's). The
   * layouts the new values need are fetched first. */
  async setProperty(property: string, groups: { targets: Record<string, string>[]; value: string }[], components: ComponentData[]) {
    if (!this.doc || groups.length === 0) return;
    const edits = groups.map((g) => new PropertyEdit(g.targets, property, g.value));
    const edit = edits.length === 1 ? edits[0]! : new CompositeEdit(edits[0]!.label, edits);
    edit.apply();
    try {
      await this.layouts.fetch(components);
    } catch (e) {
      edit.revert();
      this.onError((e as Error).message);
      return;
    }
    this.commit(edit);
  }

  /** Adds a wire along a path of horizontal and vertical segments, joined as LiveSPICE joins it, as one undo step. */
  drawWire(path: Point[]) {
    if (!this.doc) return;
    const edit = addWire(this.doc, this.layouts, path, this.newId);
    if (edit) this.commit(edit);
  }

  // ---- Tools ----

  /** The symbol tool, placing copies of `component`. */
  startPlacing(component: ComponentData, layout: Layout) {
    if (!this.doc) return;
    this.cancel();
    this.setTool({ kind: "place", component, layout, rotation: 0, flip: false, at: null });
  }

  startWiring() {
    if (!this.doc) return;
    this.cancel();
    this.setTool({ kind: "wire" });
  }

  private setTool(tool: Tool) {
    this.current = tool;
    this.svg.dataset["tool"] = tool.kind;
    this.hideGhost();
    this.onChange();
  }

  /** Arrow keys: turn the ghost while placing, or else the selection. +1 is counter-clockwise. */
  rotate(turns: number) {
    const t = this.current;
    if (t.kind === "place") {
      t.rotation += turns;
      this.showGhost();
      return;
    }
    const about = this.selectionCenter();
    if (!this.doc || this.gesture || !about) return;
    const ids = new Set(this.selection);
    rotateElements(this.doc, ids, turns, about);
    this.commit(new RotateEdit(this.doc, ids, turns, about));
  }

  flip() {
    const t = this.current;
    if (t.kind === "place") {
      t.flip = !t.flip;
      this.showGhost();
      return;
    }
    const about = this.selectionCenter();
    if (!this.doc || this.gesture || !about) return;
    const ids = new Set(this.selection);
    flipElements(this.doc, ids, about[1]);
    this.commit(new FlipEdit(this.doc, ids, about[1]));
  }

  selectAll() {
    if (!this.doc) return;
    this.select(
      this.doc.elements.filter((e) => e.kind !== "raw").map((e) => e.id),
      true,
    );
  }

  /** Escape: cancels a gesture in progress; else leaves the symbol or wire tool; else clears the selection. */
  escape() {
    if (this.gesture) this.cancel();
    else if (this.current.kind !== "select") this.setTool({ kind: "select" });
    else this.select([], true);
  }

  /** SelectionTool.GetSelectionCenter: the middle of the selection's bounds, in integers (C#'s division truncates
   * toward zero), snapped to the grid. Null with nothing selected. */
  private selectionCenter(): Point | null {
    const bounds = this.boundsOf(this.selectedElements());
    if (!bounds) return null;
    return snap([Math.trunc((bounds.min[0] + bounds.max[0]) / 2), Math.trunc((bounds.min[1] + bounds.max[1]) / 2)]);
  }

  // ---- Hit testing ----

  /** The element under the pointer, by the SVG it drew (symbols are drawn over wires, so they win). By position, not
   * the event's target: while the pointer is captured, clicks are aimed at the SVG itself. */
  private hitAt(e: MouseEvent): string | null {
    const target = document.elementFromPoint(e.clientX, e.clientY);
    return (target?.closest?.("#drawing [data-id]") as SVGElement | null)?.dataset["id"] ?? null;
  }

  /** SchematicControl.InRect: the symbols whose bounds and the wires whose segments touch the rectangle. */
  private inRect(rect: Box): string[] {
    if (!this.doc) return [];
    const ids: string[] = [];
    for (const e of this.doc.elements) {
      if (e.kind === "wire" ? wireIntersects(e, rect) : e.kind === "symbol" && this.symbolIntersects(e, rect)) ids.push(e.id);
    }
    return ids;
  }

  private symbolIntersects(s: SymbolElement, rect: Box): boolean {
    const layout = this.layouts.get(s.component);
    return layout !== undefined && boxIntersects(mapBox(mappingOf(s), layout.bounds), rect);
  }

  // ---- Selection ----

  /** SchematicControl.Select: `only` deselects everything else; `toggle` flips the given elements instead of selecting
   * them. */
  private select(ids: Iterable<string>, only: boolean, toggle = false) {
    const given = new Set(ids);
    let changed = false;
    if (only)
      for (const id of [...this.selection])
        if (!given.has(id)) {
          this.selection.delete(id);
          changed = true;
        }
    for (const id of given) {
      if (toggle && this.selection.has(id)) this.selection.delete(id);
      else if (!this.selection.has(id)) this.selection.add(id);
      else continue;
      changed = true;
    }
    if (changed) this.redraw();
  }

  // ---- The ghost ----

  private ghostSymbol(t: Extract<Tool, { kind: "place" }>, at: Point): SymbolElement {
    return { id: "", kind: "symbol", position: at, rotation: t.rotation, flip: t.flip, component: t.component };
  }

  private showGhost() {
    const t = this.current;
    if (t.kind !== "place" || !t.at) return this.hideGhost();
    drawGhost(this.ghost, t.layout, mappingOf(this.ghostSymbol(t, t.at)));
  }

  private hideGhost() {
    this.ghost.replaceChildren();
  }

  // ---- The gestures ----

  private point(e: PointerEvent): Point {
    return snap(this.view.toSchematic(e.clientX, e.clientY));
  }

  private pointerDown(e: PointerEvent) {
    if (!this.doc || this.view.panning || e.button !== 0 || this.gesture) return;
    e.preventDefault();
    const at = this.point(e);
    const ctrl = e.ctrlKey || e.metaKey;
    const tool = this.current;
    if (tool.kind === "place") {
      void this.place(tool, at, ctrl);
      return;
    }
    this.svg.setPointerCapture(e.pointerId);
    if (tool.kind === "wire") {
      this.gesture = { kind: "wire", pointer: e.pointerId, mouse: [at] };
      return;
    }
    const hit = this.hitAt(e);
    if (hit && !ctrl) {
      // Select-and-drag in one gesture: an unselected element becomes the selection first.
      if (!this.selection.has(hit)) this.select([hit], true);
      this.gesture = { kind: "move", pointer: e.pointerId, ids: new Set(this.selection), last: at, total: [0, 0], pressed: hit };
      this.svg.classList.add("moving");
    } else {
      this.gesture = { kind: "band", pointer: e.pointerId, from: at, to: at, hit, additive: ctrl };
    }
  }

  /** SymbolTool.MouseDown: places a copy of the ghost, selects it, and returns to the selection tool unless Ctrl is
   * held. The copy's layout is fetched first: a renamed copy's differs (its name is drawn). */
  private async place(tool: Extract<Tool, { kind: "place" }>, at: Point, keepTool: boolean) {
    if (!this.doc) return;
    const symbol: SymbolElement = {
      ...this.ghostSymbol(tool, at),
      id: this.newId(),
      component: structuredClone(tool.component),
    };
    nameAdded(this.doc, [symbol]);
    try {
      await this.layouts.fetch([symbol.component]);
    } catch (e) {
      this.onError((e as Error).message);
      return;
    }
    this.addElements([symbol], "Add");
    if (!keepTool) this.setTool({ kind: "select" });
  }

  private pointerMove(e: PointerEvent) {
    const tool = this.current;
    if (tool.kind === "place" && !this.view.panning) {
      tool.at = this.point(e);
      this.showGhost();
    }
    const g = this.gesture;
    if (!g || e.pointerId !== g.pointer || !this.doc) return;
    if (e.buttons & 2) {
      // The right button, pressed during the gesture (see the mousedown handler).
      this.cancel();
      return;
    }
    const at = this.point(e);
    if (g.kind === "move") {
      const by: Point = [at[0] - g.last[0], at[1] - g.last[1]];
      if (by[0] === 0 && by[1] === 0) return;
      moveElements(this.doc, g.ids, by);
      g.total = [g.total[0] + by[0], g.total[1] + by[1]];
      g.last = at;
      this.redraw();
    } else if (g.kind === "wire") {
      const last = g.mouse[g.mouse.length - 1]!;
      if (last[0] !== at[0] || last[1] !== at[1]) g.mouse.push(at);
      this.wirePreview.setAttribute("points", findWirePath(g.mouse).map((p) => p.join(",")).join(" "));
      this.wirePreview.style.display = "";
    } else {
      g.to = at;
      const r = normalize(g.from, g.to);
      this.band.style.display = g.from[0] === g.to[0] && g.from[1] === g.to[1] ? "none" : "";
      this.band.setAttribute("x", String(r.min[0]));
      this.band.setAttribute("y", String(r.min[1]));
      this.band.setAttribute("width", String(r.max[0] - r.min[0]));
      this.band.setAttribute("height", String(r.max[1] - r.min[1]));
    }
  }

  private pointerUp(e: PointerEvent) {
    const g = this.gesture;
    if (!g || e.pointerId !== g.pointer || e.button !== 0) return;
    this.endGesture();
    if (!this.doc) return;
    if (g.kind === "move") {
      if (g.total[0] !== 0 || g.total[1] !== 0) this.history.push(new MoveEdit(this.doc, g.ids, g.total));
      // A click (no drag) on an element of a larger selection narrows the selection to it.
      else this.select([g.pressed], true);
    } else if (g.kind === "wire") {
      // WireTool.MouseUp: the routed path, joined to what it meets.
      this.drawWire(findWirePath(g.mouse));
      if (!(e.ctrlKey || e.metaKey)) this.setTool({ kind: "select" });
    } else if (g.from[0] === g.to[0] && g.from[1] === g.to[1]) {
      // A click: ToggleSelect of the element under the pointer (or of nothing, which clears the selection).
      this.select(g.hit ? [g.hit] : [], !g.additive, true);
    } else {
      this.select(this.inRect(normalize(g.from, g.to)), !g.additive);
    }
  }

  private doubleClick(e: MouseEvent) {
    if (!this.doc || e.button !== 0 || this.current.kind !== "select") return;
    const hit = this.hitAt(e);
    const symbol = this.doc.elements.find((x): x is SymbolElement => x.id === hit && x.kind === "symbol");
    if (!symbol) return;
    const type = symbol.component.type;
    this.select(
      this.doc.elements.filter((x) => x.kind === "symbol" && x.component.type === type).map((x) => x.id),
      false,
    );
  }

  private endGesture() {
    this.gesture = null;
    this.svg.classList.remove("moving");
    this.band.style.display = "none";
    this.wirePreview.style.display = "none";
  }

  /** Abandons a gesture: a move goes back where it started; a rubber band selects nothing; a wire isn't drawn. */
  private cancel() {
    const g = this.gesture;
    if (!g) return;
    this.endGesture();
    if (g.kind === "move" && this.doc && (g.total[0] !== 0 || g.total[1] !== 0)) {
      moveElements(this.doc, g.ids, [-g.total[0], -g.total[1]]);
      this.redraw();
    }
  }
}
