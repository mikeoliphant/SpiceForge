// Draws a document as SVG, from the server's default artwork, the way LiveSPICE's SymbolControl and WireControl draw
// it (WebSocketApi.md, Mapping symbols): 1-unit round-capped strokes, upright text, terminals red when unconnected.

import { mapBox, mapPoint, mapVector, mappingOf, onAnyWire, unionBoxes, type Box, type Mapping } from "./geometry";
import type { LayoutCache } from "./layouts";
import type { Edge, Layout, Point, SchematicDocument, SymbolElement, WireElement } from "./protocol";

const SVG = "http://www.w3.org/2000/svg";

/** LiveSPICE's edge colours (SchematicControls.ElementControl): wire is dark blue, green is lime. */
export const EdgeColors: Record<Edge, string> = {
  wire: "#00008b",
  black: "#000000",
  gray: "#808080",
  red: "#ff0000",
  green: "#00ff00",
  blue: "#0000ff",
  yellow: "#ffff00",
  cyan: "#00ffff",
  magenta: "#ff00ff",
  orange: "#ffa500",
};

const TerminalSize = 2;
const WireTerminalSize = TerminalSize * 0.9;
const FontSize = 12;
const FontFamily = '"Segoe UI", system-ui, sans-serif';
const TextScale = { small: 0.5, normal: 1, large: 1.5 } as const;
const Alignments = { near: 0, center: 0.5, far: 1 } as const;

function el<K extends keyof SVGElementTagNameMap>(name: K, attributes: Record<string, string | number> = {}): SVGElementTagNameMap[K] {
  const e = document.createElementNS(SVG, name);
  for (const [k, v] of Object.entries(attributes)) e.setAttribute(k, String(v));
  return e;
}

// Text is measured once per string and size, with the same font the SVG uses.
const measurer = document.createElement("canvas").getContext("2d")!;
const measured = new Map<string, { width: number; ascent: number; height: number }>();
function measure(text: string, size: number) {
  const key = `${size}|${text}`;
  let m = measured.get(key);
  if (!m) {
    measurer.font = `${size}px ${FontFamily}`;
    const metrics = measurer.measureText(text);
    const ascent = metrics.fontBoundingBoxAscent ?? size * 0.9;
    const descent = metrics.fontBoundingBoxDescent ?? size * 0.25;
    m = { width: metrics.width, ascent, height: ascent + descent };
    measured.set(key, m);
  }
  return m;
}

export interface Rendered {
  /** The bounds of everything drawn, for Zoom Fit. */
  bounds: Box | null;
  /** Symbols with no layout from the server, or a placeholder one. */
  problems: string[];
}

/** LiveSPICE's selection colour, DodgerBlue. */
const SelectedColor = "#1e90ff";

/** Replaces the contents of `root` with the document's drawing, with the elements in `selected` drawn selected. */
export function renderDocument(
  root: SVGGElement,
  doc: SchematicDocument,
  layouts: LayoutCache,
  selected: ReadonlySet<string> = new Set(),
  wireTitle: (id: string) => string = () => "",
): Rendered {
  root.replaceChildren();
  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire");
  const symbols = doc.elements.filter((e): e is SymbolElement => e.kind === "symbol");
  const boxes: Box[] = [];
  const problems: string[] = [];

  // Wires underneath, as LiveSPICE layers them.
  const wireLayer = el("g", { class: "wires" });
  for (const w of wires) {
    wireLayer.append(drawWire(w, selected.has(w.id), wireTitle(w.id)));
    boxes.push({ min: [Math.min(w.a[0], w.b[0]), Math.min(w.a[1], w.b[1])], max: [Math.max(w.a[0], w.b[0]), Math.max(w.a[1], w.b[1])] });
  }

  const symbolLayer = el("g", { class: "symbols" });
  for (const s of symbols) {
    const layout = layouts.get(s.component);
    if (!layout) {
      problems.push(`${s.component.properties["Name"] ?? s.id}: no layout`);
      continue;
    }
    if (layout.message) problems.push(`${s.component.properties["Name"] ?? s.id}: ${layout.message}`);
    const m = mappingOf(s);
    const box = mapBox(m, layout.bounds);
    boxes.push(box);
    symbolLayer.append(drawSymbol(s, layout, m, box, wires, selected.has(s.id)));
  }

  root.append(wireLayer, symbolLayer);
  return { bounds: unionBoxes(boxes), problems };
}

function drawWire(w: WireElement, isSelected: boolean, title: string): SVGGElement {
  const g = el("g", { class: isSelected ? "wire selected" : "wire" });
  g.dataset["id"] = w.id;
  // A wider, invisible line over the wire, so it can be hit at any zoom.
  g.append(el("line", { class: "hit", x1: w.a[0], y1: w.a[1], x2: w.b[0], y2: w.b[1] }));
  // WireControl: DodgerBlue when selected; its end squares keep the wire colour.
  g.append(el("line", { x1: w.a[0], y1: w.a[1], x2: w.b[0], y2: w.b[1], stroke: isSelected ? SelectedColor : EdgeColors.wire }));
  for (const [x, y] of [w.a, w.b])
    g.append(square([x, y], WireTerminalSize, EdgeColors.wire));
  if (title) {
    const t = el("title");
    t.textContent = title;
    g.append(t);
  }
  return g;
}

function square([x, y]: Point, size: number, color: string): SVGRectElement {
  return el("rect", { x: x - size / 2, y: y - size / 2, width: size, height: size, fill: color, stroke: color, class: "terminal" });
}

function drawSymbol(s: SymbolElement, layout: Layout, m: Mapping, box: Box, wires: WireElement[], isSelected: boolean): SVGGElement {
  const classes = ["symbol"];
  if (s.component.unresolved || layout.message) classes.push("unresolved");
  if (isSelected) classes.push("selected");
  const g = el("g", { class: classes.join(" ") });
  g.dataset["id"] = s.id;

  // A transparent box over the symbol, for hovering, and the dashed outline LiveSPICE draws when highlighted.
  const width = box.max[0] - box.min[0];
  const height = box.max[1] - box.min[1];
  g.append(el("rect", { class: "hit", x: box.min[0], y: box.min[1], width, height }));

  drawLayout(g, layout, m, { text: true, connected: (at) => onAnyWire(at, wires) });
  g.append(el("rect", { class: "outline", x: box.min[0], y: box.min[1], width, height }));
  const title = el("title");
  title.textContent = tooltip(s, layout);
  g.append(title);
  return g;
}

interface LayoutOptions {
  /** Draw every shape in this colour, unfilled, instead of its own (SymbolControl's Pen, for the placement ghost). */
  pen?: string;
  /** Draw the texts. */
  text: boolean;
  /** Draw the terminals, coloured by whether each is connected; omitted, they aren't drawn. */
  connected?: (at: Point) => boolean;
}

/** SymbolControl.DrawLayout: a layout's shapes, mapped into the schematic and appended to `g`. */
function drawLayout(g: SVGGElement, layout: Layout, m: Mapping, options: LayoutOptions) {
  const color = (e: Edge) => options.pen ?? EdgeColors[e];
  const P = (p: Point) => mapPoint(m, p);

  for (const line of layout.lines) {
    const [a, b] = [P(line.a), P(line.b)];
    g.append(el("line", { x1: a[0], y1: a[1], x2: b[0], y2: b[1], stroke: color(line.edge) }));
  }
  for (const r of layout.rectangles) {
    const [a, b] = [P(r.a), P(r.b)];
    g.append(
      el("rect", {
        x: Math.min(a[0], b[0]),
        y: Math.min(a[1], b[1]),
        width: Math.abs(b[0] - a[0]),
        height: Math.abs(b[1] - a[1]),
        stroke: color(r.edge),
        fill: r.fill && !options.pen ? color(r.edge) : "none",
      }),
    );
  }
  for (const e of layout.ellipses) {
    const [a, b] = [P(e.a), P(e.b)];
    g.append(
      el("ellipse", {
        cx: (a[0] + b[0]) / 2,
        cy: (a[1] + b[1]) / 2,
        rx: Math.abs(b[0] - a[0]) / 2,
        ry: Math.abs(b[1] - a[1]) / 2,
        stroke: color(e.edge),
        fill: e.fill && !options.pen ? color(e.edge) : "none",
      }),
    );
  }
  for (const c of layout.curves) {
    if (c.points.length < 2) continue;
    g.append(el("polyline", { points: c.points.map((p) => P(p).join(",")).join(" "), stroke: color(c.edge), fill: "none" }));
  }
  for (const arc of layout.arcs) {
    const start = P([arc.center[0] + Math.cos(arc.start) * arc.radius, arc.center[1] + Math.sin(arc.start) * arc.radius]);
    const end = P([arc.center[0] + Math.cos(arc.end) * arc.radius, arc.center[1] + Math.sin(arc.end) * arc.radius]);
    // The sweep reverses when the mapping mirrors (its determinant, ySign, is negative), as in SymbolControl.
    const clockwise = (arc.direction === "clockwise") !== m.ySign > 0;
    const large = Math.abs(arc.start - arc.end) > Math.PI ? 1 : 0;
    g.append(
      el("path", {
        d: `M ${start[0]} ${start[1]} A ${arc.radius} ${arc.radius} 0 ${large} ${clockwise ? 1 : 0} ${end[0]} ${end[1]}`,
        stroke: color(arc.edge),
        fill: "none",
      }),
    );
  }
  for (const t of options.text ? layout.texts : []) {
    if (!t.text) continue;
    g.append(drawText(m, t.text, t.at, Alignments[t.h], Alignments[t.v], FontSize * TextScale[t.size]));
  }
  for (const terminal of options.connected ? layout.terminals : []) {
    const at = P(terminal.at);
    g.append(square(at, TerminalSize, options.connected!(at) ? EdgeColors.wire : EdgeColors.red));
  }

}

/** The placement ghost (SymbolTool's overlay): gray, without text, its terminals red as they're unconnected. */
export function drawGhost(root: SVGGElement, layout: Layout, m: Mapping) {
  root.replaceChildren();
  const g = el("g", { class: "ghost" });
  drawLayout(g, layout, m, { pen: EdgeColors.gray, text: false, connected: () => false });
  root.append(g);
}

/** A library icon (LiveSPICE's LayoutControl): the symbol unrotated, without text or terminals, fitted to a box. */
export function drawIcon(layout: Layout, size: number): SVGSVGElement {
  const svg = el("svg", { width: size, height: size, class: "icon" });
  const m = { cos: 1, sin: 0, ySign: -1, px: 0, py: 0 };
  const box = mapBox(m, layout.bounds);
  const pad = 2;
  const w = Math.max(box.max[0] - box.min[0], 1);
  const h = Math.max(box.max[1] - box.min[1], 1);
  const span = Math.max(w, h) + 2 * pad;
  const cx = (box.min[0] + box.max[0]) / 2;
  const cy = (box.min[1] + box.max[1]) / 2;
  svg.setAttribute("viewBox", `${cx - span / 2} ${cy - span / 2} ${span} ${span}`);
  const g = el("g");
  drawLayout(g, layout, m, { text: false });
  svg.append(g);
  return svg;
}

// SymbolControl.DrawLayout's text placement: upright text, its box placed from the alignment, with the alignment
// offsets taken through the mapping (so a rotated symbol's text keeps to the right side of its anchor).
function drawText(m: Mapping, text: string, at: Point, h: number, v: number, size: number): SVGGElement {
  const { width, ascent, height } = measure(text, size);
  const p = mapPoint(m, at);
  const d1 = mapVector(m, [-h, 1 - v]);
  const d2 = mapVector(m, [-(1 - h), v]);
  const left = Math.min(p[0] + d1[0] * width, p[0] - d2[0] * width);
  const top = Math.min(p[1] + d1[1] * height, p[1] - d2[1] * height);
  const g = el("g", { class: "text" });
  g.append(el("rect", { class: "text-box", x: left, y: top, width, height }));
  const t = el("text", { x: left, y: top + ascent, "font-size": size });
  t.textContent = text;
  g.append(t);
  return g;
}

// LiveSPICE's symbol tooltip: the component, then each stored property (the document only stores those that differ
// from their defaults).
function tooltip(s: SymbolElement, layout: Layout): string {
  const type = s.component.type.split(",")[0]!.split(".").pop();
  const name = s.component.properties["Name"] ?? "";
  const lines = [`${type} ${name}`.trim()];
  for (const [k, v] of Object.entries(s.component.properties)) if (k !== "Name" && k !== "Description") lines.push(`${k} = ${v}`);
  if (s.component.unresolved) lines.push(`Couldn't be loaded: ${s.component.unresolved.message}`);
  else if (layout.message) lines.push(layout.message);
  return lines.join("\n");
}
