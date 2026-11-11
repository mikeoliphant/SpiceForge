// The document being edited, its selection and history, and LiveSPICE's selection tool (UISpec.md, Tools;
// LiveSPICE/Controls/Editor/SelectionTool.cs and MoveTool.cs):
//
// - pressing on a selected element, without Ctrl, starts a move: the selection follows the pointer in grid steps,
//   live, and the whole drag is one undo step (right button or Escape cancels it);
// - otherwise a press starts a rubber band. Releasing without moving is a click: it selects the element under the
//   pointer and nothing else (Ctrl toggles it instead), or, on empty space, clears the selection (Ctrl keeps it).
//   Releasing after a drag selects everything the band touches (Ctrl adds to the selection);
// - a double-click adds every symbol of the same type to the selection;
// - the arrow keys rotate (Left counter-clockwise, Right clockwise) and flip (Up, Down) the selection about its centre.

import { boxIntersects, mapBox, mappingOf, normalize, snap, unionBoxes, wireIntersects, type Box } from "./geometry";
import { EditStack, FlipEdit, MoveEdit, RotateEdit, flipElements, moveElements, rotateElements } from "./edits";
import type { LayoutCache } from "./layouts";
import type { Point, SchematicDocument, SymbolElement } from "./protocol";
import { renderDocument, type Rendered } from "./render";
import type { SchematicView } from "./view";

const SVG = "http://www.w3.org/2000/svg";

type Gesture =
  | { kind: "band"; pointer: number; from: Point; to: Point; hit: string | null; additive: boolean }
  | { kind: "move"; pointer: number; ids: Set<string>; last: Point; total: Point };

export class Editor {
  readonly history = new EditStack();
  readonly selection = new Set<string>();
  private doc: SchematicDocument | null = null;
  private rendered: Rendered = { bounds: null, problems: [] };
  private gesture: Gesture | null = null;
  private readonly band: SVGRectElement;

  /** Called after the drawing changes, and after the selection or history does. */
  onChange: () => void = () => {};

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
    overlay.append(this.band);
    this.history.onChange = () => this.onChange();

    svg.addEventListener("pointerdown", (e) => this.pointerDown(e));
    svg.addEventListener("pointermove", (e) => this.pointerMove(e));
    svg.addEventListener("pointerup", (e) => this.pointerUp(e));
    svg.addEventListener("pointercancel", () => this.cancel());
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

  /** Starts editing a new document, with an empty selection and history. */
  load(doc: SchematicDocument) {
    this.cancel();
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

  undo() {
    if (this.gesture) return;
    if (this.history.undo()) this.redraw();
  }

  redo() {
    if (this.gesture) return;
    if (this.history.redo()) this.redraw();
  }

  selectAll() {
    if (!this.doc) return;
    this.select(this.doc.elements.filter((e) => e.kind !== "raw").map((e) => e.id), true);
  }

  /** Rotates the selection by quarter turns (+1 counter-clockwise) about its centre: SelectionTool.Rotate. */
  rotate(turns: number) {
    const about = this.selectionCenter();
    if (!this.doc || this.gesture || !about) return;
    const ids = new Set(this.selection);
    rotateElements(this.doc, ids, turns, about);
    this.history.push(new RotateEdit(this.doc, ids, turns, about));
    this.redraw();
  }

  /** Flips the selection over the horizontal line through its centre: SelectionTool.Flip. */
  flip() {
    const about = this.selectionCenter();
    if (!this.doc || this.gesture || !about) return;
    const ids = new Set(this.selection);
    flipElements(this.doc, ids, about[1]);
    this.history.push(new FlipEdit(this.doc, ids, about[1]));
    this.redraw();
  }

  /** SelectionTool.GetSelectionCenter: the middle of the selection's bounds, in integers (C#'s division truncates
   * toward zero), snapped to the grid. Null with nothing selected. */
  private selectionCenter(): Point | null {
    if (!this.doc) return null;
    const boxes: Box[] = [];
    for (const e of this.doc.elements) {
      if (!this.selection.has(e.id)) continue;
      if (e.kind === "wire") boxes.push(normalize(e.a, e.b));
      else if (e.kind === "symbol") {
        const layout = this.layouts.get(e.component);
        if (layout) boxes.push(mapBox(mappingOf(e), layout.bounds));
      }
    }
    const bounds = unionBoxes(boxes);
    if (!bounds) return null;
    return snap([Math.trunc((bounds.min[0] + bounds.max[0]) / 2), Math.trunc((bounds.min[1] + bounds.max[1]) / 2)]);
  }

  /** Escape: cancels a gesture in progress, or else clears the selection. */
  escape() {
    if (this.gesture) this.cancel();
    else this.select([], true);
  }

  // ---- Hit testing ----

  /** The element under the pointer, by the SVG it drew (symbols are drawn over wires, so they win). By position, not
   * the event's target: while the pointer is captured, clicks are aimed at the SVG itself. */
  private hitAt(e: MouseEvent): string | null {
    const target = document.elementFromPoint(e.clientX, e.clientY);
    return (target?.closest?.("[data-id]") as SVGElement | null)?.dataset["id"] ?? null;
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

  // ---- The gestures ----

  private point(e: PointerEvent): Point {
    return snap(this.view.toSchematic(e.clientX, e.clientY));
  }

  private pointerDown(e: PointerEvent) {
    if (!this.doc || this.view.panning || e.button !== 0 || this.gesture) return;
    e.preventDefault();
    this.svg.setPointerCapture(e.pointerId);
    const at = this.point(e);
    const hit = this.hitAt(e);
    const ctrl = e.ctrlKey || e.metaKey;
    if (hit && this.selection.has(hit) && !ctrl) {
      this.gesture = { kind: "move", pointer: e.pointerId, ids: new Set(this.selection), last: at, total: [0, 0] };
      this.svg.classList.add("moving");
    } else {
      this.gesture = { kind: "band", pointer: e.pointerId, from: at, to: at, hit, additive: ctrl };
    }
  }

  private pointerMove(e: PointerEvent) {
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
    } else {
      g.to = at;
      const r = normalize(g.from, g.to);
      Object.assign(this.band.style, { display: g.from[0] === g.to[0] && g.from[1] === g.to[1] ? "none" : "" });
      this.band.setAttribute("x", String(r.min[0]));
      this.band.setAttribute("y", String(r.min[1]));
      this.band.setAttribute("width", String(r.max[0] - r.min[0]));
      this.band.setAttribute("height", String(r.max[1] - r.min[1]));
    }
  }

  private pointerUp(e: PointerEvent) {
    const g = this.gesture;
    if (!g || e.pointerId !== g.pointer || e.button !== 0) return;
    this.gesture = null;
    this.svg.classList.remove("moving");
    this.band.style.display = "none";
    if (!this.doc) return;
    if (g.kind === "move") {
      if (g.total[0] !== 0 || g.total[1] !== 0) this.history.push(new MoveEdit(this.doc, g.ids, g.total));
      return;
    }
    if (g.from[0] === g.to[0] && g.from[1] === g.to[1]) {
      // A click: ToggleSelect of the element under the pointer (or of nothing, which clears the selection).
      this.select(g.hit ? [g.hit] : [], !g.additive, true);
    } else {
      this.select(this.inRect(normalize(g.from, g.to)), !g.additive);
    }
  }

  private doubleClick(e: MouseEvent) {
    if (!this.doc || e.button !== 0) return;
    const hit = this.hitAt(e);
    const symbol = this.doc.elements.find((x): x is SymbolElement => x.id === hit && x.kind === "symbol");
    if (!symbol) return;
    const type = symbol.component.type;
    this.select(this.doc.elements.filter((x) => x.kind === "symbol" && x.component.type === type).map((x) => x.id), false);
  }

  /** Abandons a gesture: a move goes back where it started; a rubber band selects nothing. */
  private cancel() {
    const g = this.gesture;
    if (!g) return;
    this.gesture = null;
    this.svg.classList.remove("moving");
    this.band.style.display = "none";
    if (g.kind === "move" && this.doc && (g.total[0] !== 0 || g.total[1] !== 0)) {
      moveElements(this.doc, g.ids, [-g.total[0], -g.total[1]]);
      this.redraw();
    }
  }
}
