// Which terminals and wires form each node: LiveSPICE's Circuit.Schematic rules, which decide what a drawing means as
// a circuit (WebSocketApi.md, Connectivity). This is part of the file-format contract, not editor behaviour: the server
// builds circuits with LiveSPICE's code, so the client must agree with it exactly (circuit.check is the reference).
//
// - Wires form nodes: two wires connect when an end of either lies on the other, at its end or along it. Wires that
//   only cross don't. Connection is transitive.
// - A symbol terminal connects to the node of the first wire (in document order) that passes through its point. Two
//   terminals touching with no wire there aren't connected.
// - A named wire (Ground is one, named GND) joins every node it touches to the others with its name. The name is its
//   WireName property, or its type's default when the file doesn't store it (older files' grounds don't).

import { mapPoint, mappingOf, pointOnSegment } from "./geometry";
import type { LayoutCache } from "./layouts";
import type { Catalog, ComponentData, Point, SchematicDocument, WireElement } from "./protocol";

export interface Nodes {
  /** Each wire's node, by wire id. */
  wires: Map<string, number>;
  /** Each connected terminal's node, by `${elementId}/${terminalName}`. Unconnected terminals are absent. */
  terminals: Map<string, number>;
  /** How many nodes there are; nodes are numbered from 0. */
  count: number;
}

export const terminalKey = (element: string, terminal: string) => `${element}/${terminal}`;

/** Wire.IsConnectedTo: an end of either lies on the other. */
function wiresConnect(w: WireElement, v: WireElement): boolean {
  return pointOnSegment(v.a, w.a, w.b) || pointOnSegment(v.b, w.a, w.b) || pointOnSegment(w.a, v.a, v.b) || pointOnSegment(w.b, v.a, v.b);
}

/** The named-wire rule from the catalogue's types: a component's WireName, else its type's default; undefined for a
 * component that isn't a named wire. */
export function wireNames(types: Catalog["types"]): (c: ComponentData) => string | undefined {
  return (c) => {
    const descriptor = types[c.type]?.properties.find((p) => p.name === "WireName");
    return descriptor ? (c.properties["WireName"] ?? descriptor.default ?? undefined) : c.properties["WireName"];
  };
}

export function computeNodes(doc: SchematicDocument, layouts: LayoutCache, wireNameOf: (c: ComponentData) => string | undefined): Nodes {
  // A wire whose ends coincide isn't one: LiveSPICE loads it as a warning symbol, which connects nothing.
  const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire" && (e.a[0] !== e.b[0] || e.a[1] !== e.b[1]));

  // Union-find over the wires, then over named-wire names.
  const parent = wires.map((_, i) => i);
  const find = (i: number): number => (parent[i] === i ? i : (parent[i] = find(parent[i]!)));
  const union = (i: number, j: number) => {
    const [a, b] = [find(i), find(j)];
    if (a !== b) parent[b] = a;
  };
  for (let i = 0; i < wires.length; i++) for (let j = i + 1; j < wires.length; j++) if (wiresConnect(wires[i]!, wires[j]!)) union(i, j);

  // Each symbol terminal attaches to the first wire through its point (Schematic.NodeAt).
  const wireAt = (p: Point) => wires.findIndex((w) => pointOnSegment(p, w.a, w.b));
  const attached: { key: string; wire: number }[] = [];
  const named = new Map<string, number>();
  for (const e of doc.elements) {
    if (e.kind !== "symbol") continue;
    const layout = layouts.get(e.component);
    if (!layout) continue;
    const m = mappingOf(e);
    for (const t of layout.terminals) {
      const w = wireAt(mapPoint(m, t.at));
      if (w < 0) continue;
      attached.push({ key: terminalKey(e.id, t.name), wire: w });
      // Schematic.MergeNode: a node touching a named wire takes its name, so same-named nodes are one.
      const name = wireNameOf(e.component);
      if (name !== undefined) {
        const first = named.get(name);
        if (first === undefined) named.set(name, w);
        else union(first, w);
      }
    }
  }

  // Number the nodes densely.
  const numbers = new Map<number, number>();
  const nodeOf = (wire: number) => {
    const root = find(wire);
    let n = numbers.get(root);
    if (n === undefined) numbers.set(root, (n = numbers.size));
    return n;
  };
  const result: Nodes = { wires: new Map(), terminals: new Map(), count: 0 };
  wires.forEach((w, i) => result.wires.set(w.id, nodeOf(i)));
  // (A zero-length wire has no node, and the server doesn't list it either.)
  for (const a of attached) result.terminals.set(a.key, nodeOf(a.wire));
  result.count = numbers.size;
  return result;
}
