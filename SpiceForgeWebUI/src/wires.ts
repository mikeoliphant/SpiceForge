// The wire tool's routing, and its joining, which keeps LiveSPICE's SchematicEditor.AddWire: overlapping wires merge
// into one, and a new wire is split wherever it meets a terminal or ends on another wire, so the schematic stays tidy.
// (Connectivity doesn't depend on the splitting: LiveSPICE connects a wire ending anywhere on another.)

import { ChangeEdit, CompositeEdit, type Edit } from "./edits";
import { boxIntersects, Grid, mapBox, mapPoint, mappingOf, onAnyWire, pointOnSegment, wireIntersects, type Box } from "./geometry";
import { computeNodes, terminalKey } from "./connectivity";
import type { LayoutCache } from "./layouts";
import type { ComponentData, Element, Point, SchematicDocument, SymbolElement, WireElement } from "./protocol";

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
export function terminalsOf(e: Element, layouts: LayoutCache): Point[] {
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

/** A wire with an end on something being moved, which that end follows. */
export interface Follower {
  wire: WireElement;
  /** Where the wire was when the move began. */
  a: Point;
  b: Point;
  /** Which ends move. */
  movesA: boolean;
  movesB: boolean;
  /** When the fixed end is a plain corner, the wire turning there, and its far end: the corner slides with the move,
   * so that wire lengthens or shortens rather than gaining a leg alongside it. */
  elbow?: { wire: WireElement; far: Point };
  /** When the moving end is a T on a moving wire's length (not at a moved point): the follower that wire belongs to,
   * or null for a wire moving whole, with that wire's ends when the move began. The end slides to wherever the wire
   * now runs (teeTarget). */
  tee?: { host: Follower | null; a: Point; b: Point };
}

const same = (p: Point, q: Point) => p[0] === q[0] && p[1] === q[1];
const horizontal = (a: Point, b: Point) => a[1] === b[1] && a[0] !== b[0];
const vertical = (a: Point, b: Point) => a[0] === b[0] && a[1] !== b[1];

/** The wires a follower takes out of the document: itself, and its elbow's wire. */
export function followerWires(f: Follower): WireElement[] {
  return f.elbow ? [f.wire, f.elbow.wire] : [f.wire];
}

/** The wires not being moved with an end on one of the points that are (terminals of moved symbols, ends of moved
 * wires): the wires a move drags along. Then the tees: wires, not moving or following, with an end on the length of
 * a moving or following wire (not at its ends), which keep that end on it. Each follower that's horizontal or
 * vertical, with one end moving, gets its elbow when its fixed end is a plain corner: exactly one other wire touches
 * that point, ending there, at a right angle, and neither moving nor following; and no symbol has a terminal there. */
export function findFollowers(doc: SchematicDocument, layouts: LayoutCache, moving: ReadonlySet<string>): Follower[] {
  const points: Point[] = doc.elements.filter((e) => moving.has(e.id)).flatMap((e) => terminalsOf(e, layouts));
  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire");
  const followers: Follower[] = [];
  for (const e of wires) {
    if (moving.has(e.id)) continue;
    const movesA = points.some((p) => same(p, e.a));
    const movesB = points.some((p) => same(p, e.b));
    if (movesA || movesB) followers.push({ wire: e, a: e.a, b: e.b, movesA, movesB });
  }

  const symbolTerminals = doc.elements.filter((e) => e.kind === "symbol").flatMap((e) => terminalsOf(e, layouts));
  const taken = new Set<WireElement>(followers.map((f) => f.wire));
  addElbows(followers, wires, moving, symbolTerminals, taken);

  // The wires that move, with the follower each belongs to (null: moving whole).
  const hosts: { wire: WireElement; follower: Follower | null }[] = [
    ...wires.filter((w) => moving.has(w.id)).map((wire) => ({ wire, follower: null })),
    ...followers.flatMap((f) => followerWires(f).map((wire) => ({ wire, follower: f }))),
  ];
  const onLength = (p: Point, w: WireElement) => pointOnSegment(p, w.a, w.b) && !same(p, w.a) && !same(p, w.b);
  const tees: Follower[] = [];
  for (const e of wires) {
    if (moving.has(e.id) || taken.has(e)) continue;

    const hostA = hosts.find((h) => onLength(e.a, h.wire));
    const hostB = hosts.find((h) => onLength(e.b, h.wire));
    if (!hostA && !hostB) continue;

    // A wire between two wires moving whole moves whole too.
    if (hostA && hostB && !hostA.follower && !hostB.follower) {
      tees.push({ wire: e, a: e.a, b: e.b, movesA: true, movesB: true });
      continue;
    }

    const host = (hostA ?? hostB)!;
    tees.push({ wire: e, a: e.a, b: e.b, movesA: host === hostA, movesB: host !== hostA, tee: { host: host.follower, a: host.wire.a, b: host.wire.b } });
  }

  for (const t of tees) taken.add(t.wire);
  addElbows(tees, wires, moving, symbolTerminals, taken);

  return [...followers, ...tees];
}

/** Gives followers their elbows (see findFollowers), claiming each elbow's wire in `taken`. */
function addElbows(followers: Follower[], wires: WireElement[], moving: ReadonlySet<string>, symbolTerminals: Point[], taken: Set<WireElement>) {
  const following = new Set(taken);
  for (const f of followers) {
    if (f.movesA === f.movesB) continue;
    const [from, corner] = f.movesA ? [f.a, f.b] : [f.b, f.a];
    if (!horizontal(from, corner) && !vertical(from, corner)) continue;
    if (symbolTerminals.some((t) => same(t, corner))) continue;
    const touching = wires.filter((w) => w !== f.wire && pointOnSegment(corner, w.a, w.b));
    if (touching.length !== 1) continue;
    const w = touching[0]!;
    if (moving.has(w.id) || following.has(w) || taken.has(w)) continue;
    const far = same(w.a, corner) ? w.b : same(w.b, corner) ? w.a : null;
    if (!far) continue;
    // At a right angle: one horizontal, the other vertical.
    if (!(horizontal(from, corner) ? vertical(corner, far) : horizontal(corner, far))) continue;
    f.elbow = { wire: w, far };
    taken.add(w);
  }
}

/**
 * Where a tee's end goes once the wire it sat on runs along `paths`: where it was, if that's still on the wire; else
 * the nearest point where the tee's own line meets the wire, so it stretches or shortens straight; else the nearest
 * point of the wire, where an L will reach it.
 */
function teeTarget(p: Point, q: Point, paths: Point[][]): Point {
  const segments = paths.flatMap((path) => path.slice(1).map((b, i): [Point, Point] => [path[i]!, b]));
  if (segments.some(([a, b]) => pointOnSegment(p, a, b))) return p;

  let best: Point | null = null;
  let distance = Infinity;
  const consider = (x: Point) => {
    const d = Math.hypot(x[0] - p[0], x[1] - p[1]);
    if (d < distance) [best, distance] = [x, d];
  };
  const clamp = (v: number, a: number, b: number) => Math.min(Math.max(v, Math.min(a, b)), Math.max(a, b));

  // Along the tee's own line: k is the coordinate it keeps, v the one that varies.
  if (horizontal(p, q) || vertical(p, q)) {
    const k = p[0] === q[0] ? 0 : 1;
    const v = 1 - k;
    const point = (kv: number, vv: number): Point => (k === 0 ? [kv, vv] : [vv, kv]);
    for (const [a, b] of segments) {
      if (a[v] === b[v] && p[k] >= Math.min(a[k], b[k]) && p[k] <= Math.max(a[k], b[k])) consider(point(p[k], a[v]));
      else if (a[k] === p[k] && b[k] === p[k]) consider(point(p[k], clamp(p[v], a[v], b[v])));
    }
  }
  if (best) return best;

  for (const [a, b] of segments) consider([clamp(p[0], a[0], b[0]), clamp(p[1], a[1], b[1])]);

  return best ?? p;
}

/**
 * The parts a move tows along: each symbol not moving with a terminal at the far end of a moving wire (either end of
 * a moved wire, the fixed end of a follower), if that's its only connection: it connects to no other part (its other
 * terminals may be unconnected, or wired to nothing but the move). Its own wires then follow it.
 */
export function findTowed(doc: SchematicDocument, layouts: LayoutCache, moving: ReadonlySet<string>): string[] {
  const points: Point[] = doc.elements.filter((e) => moving.has(e.id)).flatMap((e) => terminalsOf(e, layouts));
  const ends: Point[] = [];
  for (const e of doc.elements) {
    if (e.kind !== "wire") continue;

    if (moving.has(e.id)) {
      ends.push(e.a, e.b);
      continue;
    }

    const movesA = points.some((p) => same(p, e.a));
    const movesB = points.some((p) => same(p, e.b));
    if (movesA !== movesB) ends.push(movesA ? e.b : e.a);
  }

  const reached = doc.elements.filter((e) => e.kind === "symbol" && !moving.has(e.id) && terminalsOf(e, layouts).some((t) => ends.some((p) => same(p, t))));

  return reached.filter((e) => !reachesOthers(doc, layouts, e, moving, points)).map((e) => e.id);
}

/**
 * Whether any of a part's terminals connects to a part outside the move: directly (a terminal at the same point), or
 * through wires not moving. The search stops at the moving terminals, so the wires the part is towed by lead nowhere
 * further; a wire touching another along its length, or a terminal on a wire's middle, connects, as in LiveSPICE.
 */
function reachesOthers(doc: SchematicDocument, layouts: LayoutCache, part: Element, moving: ReadonlySet<string>, stops: Point[]): boolean {
  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire" && !moving.has(e.id));
  const own = terminalsOf(part, layouts);
  const reached = new Set<WireElement>();
  const visited: Point[] = [];
  const frontier: Point[] = [...own];
  while (frontier.length > 0) {
    const p = frontier.pop()!;
    if (visited.some((v) => same(v, p))) continue;

    visited.push(p);
    if (stops.some((s) => same(s, p))) continue;

    for (const w of wires) {
      if (reached.has(w) || !pointOnSegment(p, w.a, w.b)) continue;

      reached.add(w);
      frontier.push(w.a, w.b);
      // Wires ending along this one's length connect to it too.
      for (const v of wires) for (const end of [v.a, v.b]) if (v !== w && pointOnSegment(end, w.a, w.b)) frontier.push(end);
    }
  }

  const others = doc.elements.filter((e) => e.kind === "symbol" && e !== part && !moving.has(e.id));
  const connects = (t: Point) => own.some((o) => same(o, t)) || [...reached].some((w) => pointOnSegment(t, w.a, w.b));

  return others.some((e) => terminalsOf(e, layouts).some(connects));
}

/**
 * Where a follower runs after a move by `by`: paths from its moved end to its fixed one. A wire with both ends moving
 * moves whole. A wire with an elbow keeps its direction, and the corner slides across with the moved end, so the
 * elbow's wire lengthens or shortens to it. Otherwise a horizontal or vertical wire stays straight if its ends still
 * line up, and else becomes an L whose first leg leaves the moved end in the wire's old direction (so it still meets
 * the terminal as it did) and turns at the fixed end's line. A slanted wire just stretches. A tee's moved end goes
 * where its host wire now runs (teeTarget), and routes from there the same way.
 */
export function followerPaths(f: Follower, [dx, dy]: Point): Point[][] {
  const shift = (p: Point): Point => [p[0] + dx, p[1] + dy];
  if (f.movesA && f.movesB) return [[shift(f.a), shift(f.b)]];
  const [from, to] = f.movesA ? [f.a, f.b] : [f.b, f.a];
  const tee = f.tee;
  const moved = tee ? teeTarget(from, to, tee.host ? followerPaths(tee.host, [dx, dy]) : [[shift(tee.a), shift(tee.b)]]) : shift(from);
  if (f.elbow) {
    const corner: Point = from[1] === to[1] ? [to[0], moved[1]] : [moved[0], to[1]];
    return [[moved, corner], [corner, f.elbow.far]];
  }
  if (moved[0] === to[0] || moved[1] === to[1]) return [[moved, to]];
  if (from[1] === to[1]) return [[moved, [to[0], moved[1]], to]];
  if (from[0] === to[0]) return [[moved, [moved[0], to[1]], to]];
  return [[moved, to]];
}

/** The pairs of a symbol's terminals that line up horizontally or vertically, farthest apart first. */
function alignedPairs(terminals: Point[]): [Point, Point][] {
  const pairs: [Point, Point][] = [];
  for (let i = 0; i < terminals.length; i++) {
    for (let j = i + 1; j < terminals.length; j++) {
      const [p, q] = [terminals[i]!, terminals[j]!];
      if (horizontal(p, q) || vertical(p, q)) pairs.push([p, q]);
    }
  }

  const length = ([p, q]: [Point, Point]) => Math.abs(q[0] - p[0]) + Math.abs(q[1] - p[1]);

  return pairs.sort((x, y) => length(y) - length(x));
}

/**
 * The wires that heal the gaps a detached move (Shift held) leaves: for each moving symbol sitting in a wire, a path
 * between the two terminals it sat on, where they were. A symbol sits in a wire between two terminals that line up
 * when each has a wire, not moving, ending there and going on along their line, away from the other. A symbol with
 * more than two terminals in line heals its widest such span only, once.
 */
export function findHeals(doc: SchematicDocument, layouts: LayoutCache, moving: ReadonlySet<string>): Point[][] {
  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire" && !moving.has(e.id));
  const leaves = (p: Point, q: Point) =>
    wires.some((w) => {
      const far = same(w.a, p) ? w.b : same(w.b, p) ? w.a : null;
      return far !== null && pointOnLine(far, p, q) && (far[0] - p[0]) * (q[0] - p[0]) + (far[1] - p[1]) * (q[1] - p[1]) < 0;
    });

  const heals: Point[][] = [];
  for (const s of doc.elements) {
    if (s.kind !== "symbol" || !moving.has(s.id)) continue;

    const healed: [Point, Point][] = [];
    for (const [p, q] of alignedPairs(terminalsOf(s, layouts))) {
      if (healed.some(([a, b]) => pointOnSegment(p, a, b) && pointOnSegment(q, a, b))) continue;
      if (!leaves(p, q) || !leaves(q, p)) continue;

      heals.push([p, q]);
      healed.push([p, q]);
    }
  }

  return heals;
}

/**
 * Takes the span between two of a symbol's terminals, lined up, out of the wires along it, so the symbol sits in the
 * wire rather than being shorted by it: the wires along the line must cover the whole span, and nothing else may
 * connect inside it (another wire's end, or another symbol's terminal), or it changes nothing. Wires in `moving` are
 * left alone. Applied now, and returned as an edit; null if it changed nothing.
 */
function cutSpan(doc: SchematicDocument, layouts: LayoutCache, p: Point, q: Point, own: Point[], moving: ReadonlySet<string>, newId: () => string): Edit | null {
  const axis = p[1] === q[1] ? 0 : 1;
  const lo = Math.min(p[axis], q[axis]);
  const hi = Math.max(p[axis], q[axis]);
  const span = (w: WireElement): Point => [Math.min(w.a[axis], w.b[axis]), Math.max(w.a[axis], w.b[axis])];
  const at = (v: number): Point => (axis === 0 ? [v, p[1]] : [p[0], v]);
  const inside = (x: Point) => pointOnSegment(x, p, q) && !same(x, p) && !same(x, q);

  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire");
  const along = wires.filter((w) => !moving.has(w.id) && !same(w.a, w.b) && pointOnLine(w.a, p, q) && pointOnLine(w.b, p, q));

  let reach = lo;
  for (const [s, e] of along.map(span).sort((x, y) => x[0] - y[0])) {
    if (s > reach) break;
    reach = Math.max(reach, e);
  }
  if (reach < hi) return null;

  const others = wires.filter((w) => !along.includes(w));
  if (others.some((w) => inside(w.a) || inside(w.b))) return null;

  const symbols = doc.elements.filter((e) => e.kind === "symbol");
  if (symbols.flatMap((e) => terminalsOf(e, layouts)).some((t) => inside(t) && !own.some((o) => same(o, t)))) return null;

  const removed: WireElement[] = [];
  const added: WireElement[] = [];
  for (const w of along) {
    const [s, e] = span(w);
    if (e <= lo || s >= hi) continue;

    removed.push(w);
    if (s < lo) added.push({ id: newId(), kind: "wire", a: at(s), b: at(lo) });
    if (e > hi) added.push({ id: newId(), kind: "wire", a: at(hi), b: at(e) });
  }
  if (removed.length === 0) return null;

  const edit = new ChangeEdit("Insert", doc, removed, added);
  edit.apply();

  return edit;
}

/**
 * Wires symbols in where they lie on a wire: for each pair of a symbol's terminals that line up, widest first, the
 * wire between them is taken out (cutSpan), so the wire now runs into one terminal and out of the other. Wires in
 * `moving` (moved with the symbols) are left alone. Applied now, as one edit; null if nothing changed.
 */
export function insertSymbols(doc: SchematicDocument, layouts: LayoutCache, symbols: SymbolElement[], moving: ReadonlySet<string>, newId: () => string): Edit | null {
  const edits: Edit[] = [];
  for (const s of symbols) {
    const own = terminalsOf(s, layouts);
    for (const [p, q] of alignedPairs(own)) {
      const edit = cutSpan(doc, layouts, p, q, own, moving, newId);
      if (edit) edits.push(edit);
    }
  }

  return edits.length > 0 ? new CompositeEdit("Insert", edits) : null;
}

/** A moved part's terminal sitting on another part's terminal with no wire there, which LiveSPICE doesn't connect. */
export interface Touch {
  at: Point;
  moved: SymbolElement;
  other: SymbolElement;
  /** The two terminals' names. */
  movedTerminal: string;
  otherTerminal: string;
}

/** A symbol's terminals, by name, where they are in the schematic. */
function namedTerminals(s: SymbolElement, layouts: LayoutCache): { name: string; at: Point }[] {
  const layout = layouts.get(s.component);
  const m = mappingOf(s);
  return layout ? layout.terminals.map((t) => ({ name: t.name, at: mapPoint(m, t.at) })) : [];
}

/** The touches after a move or placement: each terminal of a symbol in `moving` that lies on the terminal of a symbol
 * not moving, where no wire passes. One per pair of terminals. */
export function findTouches(doc: SchematicDocument, layouts: LayoutCache, moving: ReadonlySet<string>): Touch[] {
  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire");
  const symbols = doc.elements.filter((e): e is SymbolElement => e.kind === "symbol");
  const touches: Touch[] = [];
  for (const moved of symbols) {
    if (!moving.has(moved.id)) continue;

    for (const mine of namedTerminals(moved, layouts)) {
      if (onAnyWire(mine.at, wires)) continue;

      for (const other of symbols) {
        if (moving.has(other.id)) continue;

        for (const theirs of namedTerminals(other, layouts)) {
          if (same(theirs.at, mine.at)) touches.push({ at: mine.at, moved, other, movedTerminal: mine.name, otherTerminal: theirs.name });
        }
      }
    }
  }

  return touches;
}

/** One grid step moving `moved` away from `other`, along the axis their centres differ most on (left of, above, and so
 * on). A tie goes sideways: equal-sized parts meeting end to end at a right angle always differ equally. Null when the
 * centres coincide. */
export function awayFrom(moved: SymbolElement, other: SymbolElement, layouts: LayoutCache): Point | null {
  const centre = (s: SymbolElement): Point | null => {
    const layout = layouts.get(s.component);
    if (!layout) return null;

    // Doubled, to stay in integers.
    const b = mapBox(mappingOf(s), layout.bounds);
    return [b.min[0] + b.max[0], b.min[1] + b.max[1]];
  };

  const [m, o] = [centre(moved), centre(other)];
  if (!m || !o) return null;

  const [dx, dy] = [m[0] - o[0], m[1] - o[1]];
  if (dx !== 0 && Math.abs(dx) >= Math.abs(dy)) return [Math.sign(dx) * Grid, 0];
  if (dy !== 0) return [0, Math.sign(dy) * Grid];
  return null;
}

/** What connects to what: each node's terminals, as a sorted list of keys, whatever the wires' ids. */
function partition(doc: SchematicDocument, layouts: LayoutCache, wireNameOf: (c: ComponentData) => string | undefined): Set<string> {
  const nodes = computeNodes(doc, layouts, wireNameOf);
  const groups = new Map<number, string[]>();
  for (const [key, n] of nodes.terminals) {
    const group = groups.get(n);
    if (group) group.push(key);
    else groups.set(n, [key]);
  }

  return new Set([...groups.values()].map((g) => JSON.stringify(g.sort())));
}

/** Undoes applied edits, last first. */
function revertAll(edits: Edit[]) {
  for (const e of [...edits].reverse()) e.revert();
}

/**
 * Builds a move or placement (`build` applies it, offset by the step it's given, and returns its edits). If that
 * leaves exactly one moved terminal sitting on another part's terminal with no wire there, which LiveSPICE wouldn't
 * connect, it's built again one grid step away from that part (awayFrom), with a wire across the gap. The spaced
 * version is kept only if joining that pair is the only difference it makes to what connects to what; otherwise the
 * move is built as it was asked for. Returns the edits it applied.
 */
export function spaceApart(
  doc: SchematicDocument,
  layouts: LayoutCache,
  wireNameOf: (c: ComponentData) => string | undefined,
  moving: ReadonlySet<string>,
  build: (by: Point) => Edit[],
  newId: () => string,
): Edit[] {
  const edits = build([0, 0]);

  const touches = findTouches(doc, layouts, moving);
  if (touches.length !== 1) return edits;

  const touch = touches[0]!;
  const by = awayFrom(touch.moved, touch.other, layouts);
  if (!by) return edits;

  const expected = partition(doc, layouts, wireNameOf);
  expected.add(JSON.stringify([terminalKey(touch.moved.id, touch.movedTerminal), terminalKey(touch.other.id, touch.otherTerminal)].sort()));

  revertAll(edits);

  const spaced = build(by);
  const gap = addWire(doc, layouts, [touch.at, [touch.at[0] + by[0], touch.at[1] + by[1]]], newId);
  if (gap) spaced.push(gap);

  const after = partition(doc, layouts, wireNameOf);
  if (after.size === expected.size && [...after].every((p) => expected.has(p))) return spaced;

  revertAll(spaced);

  return build([0, 0]);
}

/** Whether a wire's end connects to nothing: no other wire touches it (at an end or along it), and no symbol has a
 * terminal there. */
export function isFreeEnd(doc: SchematicDocument, layouts: LayoutCache, wire: WireElement, end: Point): boolean {
  for (const e of doc.elements) {
    if (e === wire) continue;
    if (e.kind === "wire" && pointOnSegment(end, e.a, e.b)) return false;
    if (e.kind === "symbol" && terminalsOf(e, layouts).some((t) => same(t, end))) return false;
  }
  return true;
}

/**
 * Where a wire runs when its free end is dragged to `to`, from its other end, which stays put: straight if `to` lines
 * up with that end, else an L whose first leg goes on in the wire's old direction and turns to `to`. A slanted wire
 * just swings to `to`. Null when `to` is the fixed end, which would leave no wire.
 */
export function extendPath(fixed: Point, free: Point, to: Point): Point[] | null {
  if (same(to, fixed)) return null;
  if (to[0] === fixed[0] || to[1] === fixed[1]) return [fixed, to];
  if (fixed[1] === free[1]) return [fixed, [to[0], fixed[1]], to];
  if (fixed[0] === free[0]) return [fixed, [fixed[0], to[1]], to];
  return [fixed, to];
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
