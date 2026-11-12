// The wire tool's routing, and its joining, which keeps LiveSPICE's SchematicEditor.AddWire: overlapping wires merge
// into one, and a new wire is split wherever it meets a terminal or ends on another wire, so the schematic stays tidy.
// (Connectivity doesn't depend on the splitting: LiveSPICE connects a wire ending anywhere on another.)

import { ChangeEdit, CompositeEdit, type Edit } from "./edits";
import { boxIntersects, mapBox, mapPoint, mappingOf, pointOnSegment, wireIntersects, type Box } from "./geometry";
import type { LayoutCache } from "./layouts";
import type { Element, Point, SchematicDocument, WireElement } from "./protocol";

/**
 * The wire tool's route: from the drag's first point to its last, straight if they line up, else an L whose first
 * leg follows the direction the drag started in (sideways first, horizontal first). LiveSPICE instead picks whichever
 * corner lies closer to every point the pointer passed through, by a distance its own comment calls wrong; following
 * the start is simpler to predict.
 */
export function findWirePath(mouse: Point[]): Point[] {
  const a = mouse[0]!;
  const b = mouse[mouse.length - 1]!;
  if (a[0] === b[0] || a[1] === b[1]) return [a, b];
  const first = mouse.find((p) => p[0] !== a[0] || p[1] !== a[1]) ?? b;
  const horizontalFirst = Math.abs(first[0] - a[0]) >= Math.abs(first[1] - a[1]);
  return horizontalFirst ? [a, [b[0], a[1]], b] : [a, [a[0], b[1]], b];
}

/** Wire.PointOnLine: on the infinite line through x1 and x2. */
function pointOnLine(x: Point, x1: Point, x2: Point): boolean {
  return (x2[0] - x1[0]) * (x[1] - x1[1]) === (x[0] - x1[0]) * (x2[1] - x1[1]);
}

/** Where an element's terminals are, in the schematic: a wire's two ends, or a symbol's mapped terminals. */
function terminalsOf(e: Element, layouts: LayoutCache): Point[] {
  if (e.kind === "wire") return [e.a, e.b];
  if (e.kind !== "symbol") return [];
  const layout = layouts.get(e.component);
  const m = mappingOf(e);
  return layout ? layout.terminals.map((t) => mapPoint(m, t.at)) : [];
}

/** SchematicControl.InRect: the symbols whose bounds, then the wires whose segments, touch the rectangle. */
function inRect(doc: SchematicDocument, layouts: LayoutCache, rect: Box): Element[] {
  const symbols = doc.elements.filter((e) => {
    if (e.kind !== "symbol") return false;
    const layout = layouts.get(e.component);
    return layout !== undefined && boxIntersects(mapBox(mappingOf(e), layout.bounds), rect);
  });
  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire" && wireIntersects(e, rect));
  return [...symbols, ...wires];
}

/**
 * SchematicEditor.AddWire for one horizontal or vertical segment from A to B, applied to the document now and
 * returned as an edit. Wires collinear with the segment and overlapping it are merged with it into one span; the span
 * is then split at every terminal on it (a symbol's, or the end of a wire that isn't collinear), and at A or B where
 * one of them lies on a crossing wire's line and the other doesn't. So a wire connects wherever it meets a terminal or
 * ends on another wire, and never merely by crossing one.
 */
function addWireSegment(doc: SchematicDocument, layouts: LayoutCache, A: Point, B: Point, newId: () => string): Edit | null {
  if (A[0] === B[0] && A[1] === B[1]) return null;

  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire");
  const overlapping = wires.filter(
    (i) =>
      pointOnLine(A, i.a, i.b) &&
      pointOnLine(B, i.a, i.b) &&
      (pointOnSegment(A, i.a, i.b) || pointOnSegment(B, i.a, i.b) || pointOnSegment(i.a, A, B) || pointOnSegment(i.b, A, B)),
  );

  const a: Point = [
    Math.min(...overlapping.map((i) => Math.min(i.a[0], i.b[0])), Math.min(A[0], B[0])),
    Math.min(...overlapping.map((i) => Math.min(i.a[1], i.b[1])), Math.min(A[1], B[1])),
  ];
  const b: Point = [
    Math.max(...overlapping.map((i) => Math.max(i.a[0], i.b[0])), Math.max(A[0], B[0])),
    Math.max(...overlapping.map((i) => Math.max(i.a[1], i.b[1])), Math.max(A[1], B[1])),
  ];

  const terminals: Point[] = [a, b];
  for (const i of inRect(doc, layouts, { min: [a[0] - 1, a[1] - 1], max: [b[0] + 1, b[1] + 1] })) {
    const ts = terminalsOf(i, layouts);
    // Every terminal between a and b, unless it's on a wire lying along a and b.
    for (const t of ts)
      if (pointOnSegment(t, a, b) && (i.kind !== "wire" || ts.some((k) => !pointOnLine(k, a, b)))) terminals.push(t);
    // A wire whose line A lies on, but B doesn't (or the reverse): don't merge across that point.
    if (i.kind === "wire") {
      if (pointOnLine(A, i.a, i.b) && !pointOnLine(B, i.a, i.b)) terminals.push(A);
      else if (pointOnLine(B, i.a, i.b) && !pointOnLine(A, i.a, i.b)) terminals.push(B);
    }
  }
  terminals.sort((t1, t2) => (t1[0] === t2[0] ? t1[1] - t2[1] : t1[0] - t2[0]));

  const added: WireElement[] = [];
  for (let i = 0; i + 1 < terminals.length; i++) {
    const [p, q] = [terminals[i]!, terminals[i + 1]!];
    if (p[0] !== q[0] || p[1] !== q[1]) added.push({ id: newId(), kind: "wire", a: p, b: q });
  }
  const edit = new ChangeEdit("Wire", doc, overlapping, added);
  edit.apply();
  return edit;
}

/** SchematicEditor.AddWire for a path: each segment in turn, as one undo step. Null if it added nothing. */
export function addWire(doc: SchematicDocument, layouts: LayoutCache, path: Point[], newId: () => string): Edit | null {
  const edits: Edit[] = [];
  for (let i = 0; i + 1 < path.length; i++) {
    const edit = addWireSegment(doc, layouts, path[i]!, path[i + 1]!, newId);
    if (edit) edits.push(edit);
  }
  return edits.length > 0 ? new CompositeEdit("Wire", edits) : null;
}
