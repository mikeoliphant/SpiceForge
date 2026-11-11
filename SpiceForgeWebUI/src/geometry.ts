// LiveSPICE's schematic geometry, exactly (WebSocketApi.md, Mapping symbols and Connectivity). Integer arithmetic:
// these decide which terminals connect, so they must match LiveSPICE to the unit.

import type { Point, SymbolElement, WireElement } from "./protocol";

/** cos(r·90°) for an integer r, as LiveSPICE's Element.Cos. */
export function cos90(r: number): number {
  return [1, 0, -1, 0][((r % 4) + 4) % 4]!;
}

/** sin(r·90°) = cos((r − 1)·90°). */
export function sin90(r: number): number {
  return cos90(r - 1);
}

/** The linear part of a symbol's mapping from its local coordinates (y up) to the schematic's (y down). */
export interface Mapping {
  readonly cos: number;
  readonly sin: number;
  /** +1 if flipped, −1 if not: the local y axis's sign before rotating. Also the mapping's determinant. */
  readonly ySign: number;
  readonly px: number;
  readonly py: number;
}

export function mappingOf(symbol: SymbolElement): Mapping {
  return {
    cos: cos90(symbol.rotation),
    sin: sin90(symbol.rotation),
    ySign: symbol.flip ? 1 : -1,
    px: symbol.position[0],
    py: symbol.position[1],
  };
}

/** Symbol.MapToGlobal: a local point to the schematic. */
export function mapPoint(m: Mapping, [x, y]: Point): Point {
  const y1 = m.ySign * y;
  return [x * m.cos + y1 * m.sin + m.px, y1 * m.cos - x * m.sin + m.py];
}

/** The mapping without its translation, for directions and offsets. */
export function mapVector(m: Mapping, [x, y]: Point): Point {
  const y1 = m.ySign * y;
  return [x * m.cos + y1 * m.sin, y1 * m.cos - x * m.sin];
}

export interface Box {
  min: Point;
  max: Point;
}

/** The schematic bounds of a local box: the bounds of its four mapped corners. */
export function mapBox(m: Mapping, box: Box): Box {
  const corners = [
    mapPoint(m, [box.min[0], box.min[1]]),
    mapPoint(m, [box.max[0], box.min[1]]),
    mapPoint(m, [box.min[0], box.max[1]]),
    mapPoint(m, [box.max[0], box.max[1]]),
  ];
  return {
    min: [Math.min(...corners.map((c) => c[0])), Math.min(...corners.map((c) => c[1]))],
    max: [Math.max(...corners.map((c) => c[0])), Math.max(...corners.map((c) => c[1]))],
  };
}

export function unionBoxes(boxes: Box[]): Box | null {
  if (boxes.length === 0) return null;
  return {
    min: [Math.min(...boxes.map((b) => b.min[0])), Math.min(...boxes.map((b) => b.min[1]))],
    max: [Math.max(...boxes.map((b) => b.max[0])), Math.max(...boxes.map((b) => b.max[1]))],
  };
}

/** Wire.PointOnSegment: x inside the segment's bounding box and on its line. */
export function pointOnSegment(x: Point, a: Point, b: Point): boolean {
  const inBox =
    x[0] >= Math.min(a[0], b[0]) && x[0] <= Math.max(a[0], b[0]) && x[1] >= Math.min(a[1], b[1]) && x[1] <= Math.max(a[1], b[1]);
  return inBox && (b[0] - a[0]) * (x[1] - a[1]) === (x[0] - a[0]) * (b[1] - a[1]);
}

/** Whether a terminal at this schematic point is connected: some wire passes through it (Schematic.NodeAt). */
export function onAnyWire(x: Point, wires: readonly WireElement[]): boolean {
  return wires.some((w) => pointOnSegment(x, w.a, w.b));
}
