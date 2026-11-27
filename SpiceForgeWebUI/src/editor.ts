// The document being edited, its selection and history, and the editing tools, as UISpec.md (Schematic editor)
// describes them. They started as ports of LiveSPICE's; where its behaviour didn't make good sense, they now differ
// (UISpec.md, Departures from LiveSPICE; WebSocketApi.md, Editing).
//
// Selection tool:
// - pressing on an element, without Ctrl, selects it (if it wasn't) and starts a move: the selection follows the
//   pointer in grid steps, live, and the whole drag is one undo step (right button or Escape cancels it). Releasing
//   without moving selects just that element. Wires with an end on what moves (a moved symbol's terminal, a moved
//   wire's end) follow it, rerouted straight or as an L, or sliding a plain corner along (wires.ts, followerPaths), and joined as the wire tool joins
//   them, in the same undo step. A part those wires reach (at a moved wire's end, or a following wire's fixed end)
//   that connects to nothing else moves too, and its own wires follow it (wires.ts, findTowed). Holding Shift (at the
//   press or during the drag) leaves the wires, and any towed parts, where they are, and
//   heals the wire a moved part sat in (wires.ts, findHeals). A part dropped with two of its terminals on a wire
//   (moved, placed, or dropped from the library) is wired into it: the wire between them goes (wires.ts, insertSymbols).
//   One whose terminal lands on another part's terminal alone, with no wire there, steps a grid step away from that
//   part and is wired to it, if that changes nothing else that connects (wires.ts, spaceApart).
//   Pressing within a few pixels of a wire's free end (one that connects to nothing) drags that end instead, and the
//   wire extends to it: straight, or an L going on in its old direction (wires.ts, extendPath);
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
import type { ComponentData, Element, Layout, Point, SchematicDocument, SymbolElement, WireElement } from "./protocol";
import { drawGhost, renderDocument, selectionBox, type Rendered } from "./render";
import type { SchematicView } from "./view";
import {
  addWire,
  extendPath,
  findFollowers,
  findHeals,
  findTowed,
  findWirePath,
  followerPaths,
  followerWires,
  insertSymbols,
  isFreeEnd,
  spaceApart,
  type Follower,
} from "./wires";
import { computeNodes } from "./connectivity";

const SVG = "http://www.w3.org/2000/svg";

export type Tool =
  | { kind: "select" }
  | { kind: "place"; component: ComponentData; layout: Layout; rotation: number; flip: boolean; at: Point | null }
  | { kind: "wire" };

type Gesture =
  | { kind: "band"; pointer: number; from: Point; to: Point; hit: string | null; additive: boolean }
  | {
      kind: "move";
      pointer: number;
      ids: Set<string>;
      last: Point;
      total: Point;
      pressed: string;
      /** The parts connected only by the moving wires, which move with them unless detached (wires.ts, findTowed). */
      towed: Set<string>;
      /** How far the towed parts have been moved, while they're moved. */
      towedBy: Point | null;
      /** The wires attached to what moves (the towed parts included), which follow it unless detached (Shift held). */
      followers: Follower[];
      detached: boolean;
      /** The wires that close the gaps moved parts leave in wires they sat in, when detached. */
      heals: Point[][];
      /** The followers' rerouted stand-ins (or the heals, detached) while dragging: plain wires, joined properly on
       * release. */
      preview: ChangeEdit | null;
    }
  | { kind: "wire"; pointer: number; mouse: Point[] }
  | {
      kind: "extend";
      pointer: number;
      wire: WireElement;
      /** The end that stays, and the free end being dragged. */
      fixed: Point;
      free: Point;
      /** Where the free end is now. */
      to: Point;
      /** The wire's stand-ins while dragging: plain wires, joined properly on release. */
      preview: ChangeEdit | null;
    };

/** How close to a wire's free end, in screen pixels, a press drags that end rather than the wire. */
const FreeEndPixels = 10;

/** One open document's editing state: what a tab keeps while another is shown. */
export interface EditorState {
  doc: SchematicDocument;
  history: EditStack;
  selection: Set<string>;
}

export function newEditorState(doc: SchematicDocument): EditorState {
  return { doc, history: new EditStack(), selection: new Set() };
}

export class Editor {
  private state: EditorState | null = null;
  // Stand-ins while no document is attached, so callers never see null.
  private readonly idleHistory = new EditStack();
  private readonly idleSelection = new Set<string>();
  private rendered: Rendered = { bounds: null, problems: [] };
  private gesture: Gesture | null = null;
  private current: Tool = { kind: "select" };
  private nextId = 0;
  private readonly band: SVGRectElement;
  private readonly ghost: SVGGElement;
  private readonly wirePreview: SVGPolylineElement;

  /** Called after the drawing changes, and after the selection, tool or history does. */
  onChange: () => void = () => {};

  /** A stand-in component to draw a symbol as, for display only, or null for its own: the simulation shows switches
   * and pots at their live positions this way, without editing the document. */
  displayComponent: ((s: SymbolElement) => ComponentData | null) | null = null;
  /** Reports a failure the user should see (the status bar). */
  onError: (message: string) => void = () => {};
  /** The named-wire rule for connectivity (wireNames, from the catalogue's types). */
  wireNameOf: (c: ComponentData) => string | undefined = (c) => c.properties["WireName"];

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

    // A press on the schematic takes focus from a field being edited (the properties panel's), committing it: the
    // gestures prevent the default, which would otherwise have moved focus.
    svg.addEventListener("pointerdown", (e) => {
      const active = document.activeElement;
      if (active instanceof HTMLInputElement || active instanceof HTMLSelectElement || active instanceof HTMLTextAreaElement) active.blur();

      this.pointerDown(e);
    });
    svg.addEventListener("pointermove", (e) => this.pointerMove(e));
    svg.addEventListener("pointerup", (e) => this.pointerUp(e));
    svg.addEventListener("pointercancel", () => this.cancel());
    svg.addEventListener("pointerleave", () => this.hideGhost());
    // The right button cancels a gesture in progress, as in LiveSPICE. Pressed while the left is held, it doesn't
    // make a pointerdown (pointer events report a second button as a "chorded" move), but it does make a mousedown.
    svg.addEventListener("mousedown", (e) => e.button === 2 && this.cancel());
    svg.addEventListener("dblclick", (e) => this.doubleClick(e));
    svg.addEventListener("contextmenu", (e) => e.preventDefault());
    // Shift pressed or released during a move detaches or reattaches the wires, without waiting for the pointer.
    const shiftChanged = (e: KeyboardEvent) => {
      if (e.key === "Shift") this.setDetached(e.shiftKey);
    };
    window.addEventListener("keydown", shiftChanged);
    window.addEventListener("keyup", shiftChanged);
  }

  private get doc(): SchematicDocument | null {
    return this.state?.doc ?? null;
  }

  get document(): SchematicDocument | null {
    return this.doc;
  }

  get history(): EditStack {
    return this.state?.history ?? this.idleHistory;
  }

  get selection(): Set<string> {
    return this.state?.selection ?? this.idleSelection;
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

  /** Edits another document (a tab's), or none. Its selection and history come with it. */
  attach(state: EditorState | null) {
    this.cancel();
    this.setTool({ kind: "select" });
    if (this.state) this.state.history.onChange = () => {};
    this.state = state;
    if (state) state.history.onChange = () => this.onChange();
    this.redraw();
  }

  redraw() {
    if (!this.doc) {
      this.drawing.replaceChildren();
      this.rendered = { bounds: null, problems: [] };
      this.onChange();
      return;
    }
    const titles = this.wireTitles(this.doc);
    const shown = this.displayComponent;
    this.rendered = renderDocument(this.drawing, this.doc, this.layouts, this.selection, (id) => titles.get(id) ?? "", (s) => shown?.(s) ?? s.component);
    this.onChange();
  }

  /** Each wire's tooltip: its node, by the named wires on it ("GND") or else a number, and what the node connects. */
  private wireTitles(doc: SchematicDocument): Map<string, string> {
    const nodes = computeNodes(doc, this.layouts, this.wireNameOf);
    const names: Set<string>[] = Array.from({ length: nodes.count }, () => new Set());
    const members: string[][] = Array.from({ length: nodes.count }, () => []);
    const byId = new Map(doc.elements.map((e) => [e.id, e]));
    for (const [key, n] of nodes.terminals) {
      const [id, terminal] = [key.slice(0, key.indexOf("/")), key.slice(key.indexOf("/") + 1)];
      const e = byId.get(id);
      if (e?.kind !== "symbol") continue;
      const wireName = this.wireNameOf(e.component);
      if (wireName !== undefined) names[n]!.add(wireName);
      else members[n]!.push(`${e.component.properties["Name"] ?? id}.${terminal}`);
    }
    const titles = new Map<string, string>();
    for (const [id, n] of nodes.wires) {
      const label = names[n]!.size ? [...names[n]!].join(", ") : `Node ${n + 1}`;
      const list = members[n]!.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
      const shown = list.length > 8 ? [...list.slice(0, 8), `and ${list.length - 8} more`] : list;
      titles.set(id, shown.length ? `${label}
Connects ${shown.join(", ")}` : label);
    }
    return titles;
  }

  /**
   * A right-click on the schematic, before its context menu opens: a placing or wiring tool is left, and nothing more
   * happens (false); otherwise an unselected element under the pointer becomes the selection, and the menu can open
   * (true).
   */
  prepareContextMenu(e: MouseEvent): boolean {
    if (!this.doc || this.gesture) return false;
    if (this.current.kind !== "select") {
      this.setTool({ kind: "select" });
      return false;
    }
    const hit = this.hitAt(e);
    if (hit && !this.selection.has(hit)) this.select([hit], true);
    return true;
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

  /** Places a copy of a component at a point of the window, as a drop from the library does: selected, with the
   * selection tool back. */
  dropComponent(component: ComponentData, layout: Layout, clientX: number, clientY: number) {
    if (!this.doc) return;
    this.cancel();
    const at = snap(this.view.toSchematic(clientX, clientY));
    void this.place({ kind: "place", component, layout, rotation: 0, flip: false, at }, at, false);
  }

  /** Selects exactly these elements (as Find does). */
  selectOnly(ids: Iterable<string>) {
    this.select(ids, true);
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
    return layout !== undefined && boxIntersects(selectionBox(s, layout), rect);
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
    const free = hit && !ctrl ? this.freeEndAt(hit, e) : null;
    if (free) {
      // Pressed by a wire's free end: that end follows the pointer, and the wire extends to it.
      this.select([free.wire.id], true);
      this.gesture = { kind: "extend", pointer: e.pointerId, wire: free.wire, fixed: free.fixed, free: free.free, to: free.free, preview: null };
    } else if (hit && !ctrl) {
      // Select-and-drag in one gesture: an unselected element becomes the selection first.
      if (!this.selection.has(hit)) this.select([hit], true);
      const ids = new Set(this.selection);
      const towed = new Set(findTowed(this.doc, this.layouts, ids));
      const followers = findFollowers(this.doc, this.layouts, new Set([...ids, ...towed]));
      const heals = findHeals(this.doc, this.layouts, ids);
      this.gesture = {
        kind: "move",
        pointer: e.pointerId,
        ids,
        last: at,
        total: [0, 0],
        pressed: hit,
        towed,
        towedBy: null,
        followers,
        detached: e.shiftKey,
        heals,
        preview: null,
      };
      this.svg.classList.add("moving");
    } else {
      this.gesture = { kind: "band", pointer: e.pointerId, from: at, to: at, hit, additive: ctrl };
    }
  }

  /** SymbolTool.MouseDown: places a copy of the ghost, wired in if it lands on a wire, selects it, and returns to the
   * selection tool unless Ctrl is held. The copy's layout is fetched first: a renamed copy's differs (its name is
   * drawn). */
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

    // The document may have been closed while the layout was fetched.
    const doc = this.doc;
    if (!doc) return;

    const at0 = symbol.position;
    const build = ([dx, dy]: Point): Edit[] => {
      symbol.position = [at0[0] + dx, at0[1] + dy];

      const add = new ChangeEdit("Add", doc, [], [symbol]);
      add.apply();

      const inserted = insertSymbols(doc, this.layouts, [symbol], new Set(), this.newId);

      return inserted ? [add, inserted] : [add];
    };

    const edits = this.spaceApart(new Set([symbol.id]), build);
    this.commit(edits.length === 1 ? edits[0]! : new CompositeEdit("Add", edits));

    this.select([symbol.id], true);

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
      const detached = e.shiftKey;
      const by: Point = [at[0] - g.last[0], at[1] - g.last[1]];
      if (by[0] === 0 && by[1] === 0) {
        this.setDetached(detached);
        return;
      }
      this.unfollow(g);
      moveElements(this.doc, g.ids, by);
      g.total = [g.total[0] + by[0], g.total[1] + by[1]];
      g.last = at;
      g.detached = detached;
      this.follow(g);
      this.redraw();
    } else if (g.kind === "extend") {
      if (at[0] === g.to[0] && at[1] === g.to[1]) return;
      g.to = at;
      this.unextend(g);
      this.extend(g);
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
      if (g.total[0] !== 0 || g.total[1] !== 0) this.finishMove(g);
      // A click (no drag) on an element of a larger selection narrows the selection to it.
      else this.select([g.pressed], true);
    } else if (g.kind === "extend") {
      this.finishExtend(g);
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
      this.unfollow(g);
      moveElements(this.doc, g.ids, [-g.total[0], -g.total[1]]);
      this.redraw();
    } else if (g.kind === "extend" && g.preview) {
      this.unextend(g);
      this.redraw();
    }
  }

  // ---- Extending a wire by its free end ----

  /** The wire `hit` names, if the press is within FreeEndPixels of an end of it that connects to nothing (the nearer
   * end, if both are close). */
  private freeEndAt(hit: string, e: PointerEvent): { wire: WireElement; fixed: Point; free: Point } | null {
    const wire = this.doc?.elements.find((x): x is WireElement => x.id === hit && x.kind === "wire");
    if (!this.doc || !wire) return null;
    const [x, y] = this.view.toSchematic(e.clientX, e.clientY);
    const distance = (p: Point) => Math.hypot(p[0] - x, p[1] - y) * this.view.Zoom;
    const [near, far] = distance(wire.a) <= distance(wire.b) ? [wire.a, wire.b] : [wire.b, wire.a];
    if (distance(near) > FreeEndPixels || !isFreeEnd(this.doc, this.layouts, wire, near)) return null;
    return { wire, fixed: far, free: near };
  }

  /** Puts the wire's stand-ins, reaching to where its free end is now, in place of it. */
  private extend(g: Extract<Gesture, { kind: "extend" }>) {
    const path = extendPath(g.fixed, g.free, g.to);
    if (!this.doc || !path || (g.to[0] === g.free[0] && g.to[1] === g.free[1])) return;
    const stand: Element[] = [];
    for (let i = 0; i + 1 < path.length; i++) stand.push({ id: this.newId(), kind: "wire", a: path[i]!, b: path[i + 1]! });
    g.preview = new ChangeEdit("", this.doc, [g.wire], stand);
    g.preview.apply();
  }

  private unextend(g: Extract<Gesture, { kind: "extend" }>) {
    g.preview?.revert();
    g.preview = null;
  }

  /** Ends an extension as one undo step: the wire replaced by its new path, joined as the wire tool joins wires, and
   * the new pieces selected (a wire it merged with comes back as one of them). A release where it started, or on the
   * wire's other end, changes nothing. */
  private finishExtend(g: Extract<Gesture, { kind: "extend" }>) {
    this.unextend(g);
    const path = extendPath(g.fixed, g.free, g.to);
    if (!this.doc || !path || (g.to[0] === g.free[0] && g.to[1] === g.free[1])) {
      this.redraw();
      return;
    }
    const before = new Set(this.doc.elements.map((x) => x.id));
    const removed = new ChangeEdit("Extend", this.doc, [g.wire], []);
    removed.apply();
    const added = addWire(this.doc, this.layouts, path, this.newId);
    this.commit(new CompositeEdit("Extend", added ? [removed, added] : [removed]));
    this.select(
      this.doc.elements.filter((x) => x.kind === "wire" && !before.has(x.id)).map((x) => x.id),
      true,
    );
  }

  // ---- Wires following a move ----

  /** Puts the followers' rerouted stand-ins in place of them, for the move so far; or, detached, the heals. */
  private follow(g: Extract<Gesture, { kind: "move" }>) {
    if (!this.doc || (g.total[0] === 0 && g.total[1] === 0)) return;

    if (g.detached) {
      if (g.heals.length === 0) return;

      const heals: Element[] = g.heals.map(([a, b]) => ({ id: this.newId(), kind: "wire", a: a!, b: b! }));
      g.preview = new ChangeEdit("", this.doc, [], heals);
      g.preview.apply();
      return;
    }

    if (g.towed.size > 0) {
      moveElements(this.doc, g.towed, g.total);
      g.towedBy = g.total;
    }

    if (g.followers.length === 0) return;

    const stand: Element[] = [];
    for (const path of g.followers.flatMap((f) => followerPaths(f, g.total))) {
      for (let i = 0; i + 1 < path.length; i++) {
        const [a, b] = [path[i]!, path[i + 1]!];
        if (a[0] !== b[0] || a[1] !== b[1]) stand.push({ id: this.newId(), kind: "wire", a, b });
      }
    }
    g.preview = new ChangeEdit("", this.doc, g.followers.flatMap(followerWires), stand);
    g.preview.apply();
  }

  /** Takes the stand-ins out, and the followers and the towed parts back where they were. */
  private unfollow(g: Extract<Gesture, { kind: "move" }>) {
    g.preview?.revert();
    g.preview = null;

    if (g.towedBy && this.doc) moveElements(this.doc, g.towed, [-g.towedBy[0], -g.towedBy[1]]);
    g.towedBy = null;
  }

  /** Shift, pressed or released during a move: the wires let go of what moves, or follow it again. */
  private setDetached(detached: boolean) {
    const g = this.gesture;
    if (g?.kind !== "move" || g.detached === detached) return;
    this.unfollow(g);
    g.detached = detached;
    this.follow(g);
    this.redraw();
  }

  /** Ends a move as one undo step: the move; the followers rerouted and joined as the wire tool joins wires, or,
   * detached, the gaps healed; and the moved parts wired into any wire they now lie on. */
  private finishMove(g: Extract<Gesture, { kind: "move" }>) {
    const doc = this.doc;
    if (!doc) return;

    this.unfollow(g);

    // Back where the move began, so it can be built from there (and built again, spaced apart).
    moveElements(doc, g.ids, [-g.total[0], -g.total[1]]);

    // The towed parts move with the rest, unless detached.
    const towed = !g.detached && g.towed.size > 0;
    const moving = towed ? new Set([...g.ids, ...g.towed]) : g.ids;

    const build = ([dx, dy]: Point): Edit[] => {
      const total: Point = [g.total[0] + dx, g.total[1] + dy];
      const move = new MoveEdit(doc, moving, total);
      move.apply();

      const edits: Edit[] = [move];
      if (g.detached) {
        edits.push(...g.heals.flatMap((path) => addWire(doc, this.layouts, path, this.newId) ?? []));
      } else if (g.followers.length > 0) {
        const removed = new ChangeEdit("Move", doc, g.followers.flatMap(followerWires), []);
        removed.apply();
        edits.push(removed);

        const paths = g.followers.flatMap((f) => followerPaths(f, total));
        edits.push(...paths.flatMap((path) => addWire(doc, this.layouts, path, this.newId) ?? []));
      }

      const moved = doc.elements.filter((e): e is SymbolElement => e.kind === "symbol" && moving.has(e.id));
      const inserted = insertSymbols(doc, this.layouts, moved, moving, this.newId);
      if (inserted) edits.push(inserted);

      return edits;
    };

    const edits = this.spaceApart(moving, build);
    this.commit(edits.length === 1 ? edits[0]! : new CompositeEdit("Move", edits));
  }

  /** Builds a move or placement, spaced a step apart from a part it would only touch (wires.ts, spaceApart). */
  private spaceApart(moving: ReadonlySet<string>, build: (by: Point) => Edit[]): Edit[] {
    return spaceApart(this.doc!, this.layouts, this.wireNameOf, moving, build, this.newId);
  }
}
