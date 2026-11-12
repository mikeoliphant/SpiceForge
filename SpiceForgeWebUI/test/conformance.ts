// The editor's contract with the file format (WebSocketApi.md, The document): whatever the client does to a
// schematic, its idea of which terminals and wires connect must be LiveSPICE's. The server's circuit.check runs
// LiveSPICE's own Circuit.Schematic, so it's the reference.
//
// For every schematic under SPICEFORGE_TESTS, this compares the client's nodes (connectivity.ts) with the server's: once
// as loaded, then after each of a series of random edits made with the editor's own functions (rotate, flip and move
// random selections, add wires between random points, delete). Edits are meant to break connections as often as they
// make them; both sides must agree either way. That checks the symbol mapping, rotation and flip encodings, wire
// joining, and the connectivity rules together.
//
// Run by `npm test` with SPICEFORGE_HOST (ws://127.0.0.1:port), SPICEFORGE_TOKEN and SPICEFORGE_TESTS set; normally
// from `SpiceForge.Validate server`, which starts a server for it.

import { ApiClient } from "../src/client";
import { computeNodes, terminalKey, wireNames } from "../src/connectivity";
import { ChangeEdit, flipElements, moveElements, rotateElements } from "../src/edits";
import { mapBox, mapPoint, mappingOf, normalize, snap, unionBoxes, type Box } from "../src/geometry";
import { LayoutCache } from "../src/layouts";
import type { Catalog, ComponentData, Element, FileRead, FolderListing, Point, SchematicDocument, SymbolElement } from "../src/protocol";
import { addWire } from "../src/wires";

declare const process: { env: Record<string, string | undefined>; exit(code: number): never };

const RoundsPerSchematic = 8;

/** A small seeded generator (mulberry32), so a failure can be reproduced. */
function random(seed: number) {
  return () => {
    seed |= 0;
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

interface ServerNode {
  name: string;
  terminals: { element: string; terminal: string }[];
  wires: string[];
}

/** A node partition as a set of canonical strings: each node's sorted members. */
function partition(groups: string[][]): Set<string> {
  return new Set(groups.filter((g) => g.length > 0).map((g) => [...g].sort().join(" ")));
}

function clientPartition(doc: SchematicDocument, layouts: LayoutCache, wireNameOf: (c: ComponentData) => string | undefined): Set<string> {
  const nodes = computeNodes(doc, layouts, wireNameOf);
  const groups: string[][] = Array.from({ length: nodes.count }, () => []);
  for (const [id, n] of nodes.wires) groups[n]!.push(`wire:${id}`);
  for (const [key, n] of nodes.terminals) groups[n]!.push(key);
  return partition(groups);
}

function serverPartition(nodes: ServerNode[]): Set<string> {
  return partition(nodes.map((n) => [...n.wires.map((w) => `wire:${w}`), ...n.terminals.map((t) => terminalKey(t.element, t.terminal))]));
}

function difference(a: Set<string>, b: Set<string>): string[] {
  return [...a].filter((x) => !b.has(x));
}

async function main() {
  const host = process.env["SPICEFORGE_HOST"];
  const token = process.env["SPICEFORGE_TOKEN"];
  const tests = process.env["SPICEFORGE_TESTS"];
  if (!host || !token || !tests) {
    console.log("Set SPICEFORGE_HOST, SPICEFORGE_TOKEN and SPICEFORGE_TESTS.");
    process.exit(2);
  }
  const client = new ApiClient();
  await client.connect(token, host);
  await client.request("hello", { protocol: 1, client: "conformance" });
  const layouts = new LayoutCache(client);
  const catalog = await client.request<Catalog>("catalog.get");
  const wireNameOf = wireNames(catalog.types);

  // Every schematic under the tests folder, through the server's own file listing.
  const files: string[] = [];
  const walk = async (dir: string) => {
    const listing = await client.request<FolderListing>("fs.list", { path: dir, extensions: [".schx"] });
    for (const e of listing.entries) {
      const path = `${listing.path}${listing.path.endsWith("\\") || listing.path.endsWith("/") ? "" : listing.path.includes("\\") ? "\\" : "/"}${e.name}`;
      if (e.kind === "dir") await walk(path);
      else files.push(path);
    }
  };
  await walk(tests);

  let comparisons = 0;
  let failures = 0;
  let nextId = 0;
  const newId = () => `t${++nextId}`;

  for (const [index, file] of files.entries()) {
    const name = file.split(/[\\/]/).slice(-2).join("/");
    const read = await client.request<FileRead>("file.read", { path: file });
    const doc = read.document;
    const components = () => doc.elements.filter((e): e is SymbolElement => e.kind === "symbol").map((s) => s.component);
    await layouts.fetch(components());
    const rand = random(8370 + index);
    const pick = <T>(xs: T[]): T => xs[Math.floor(rand() * xs.length)]!;

    const compare = async (step: string) => {
      const check = await client.request<{ nodes: ServerNode[] }>("circuit.check", { document: doc });
      const server = serverPartition(check.nodes);
      const ours = clientPartition(doc, layouts, wireNameOf);
      comparisons++;
      const missing = difference(server, ours);
      const extra = difference(ours, server);
      if (missing.length || extra.length) {
        failures++;
        console.log(`FAIL ${name}, ${step}:`);
        for (const m of missing.slice(0, 3)) console.log(`  server only: ${m}`);
        for (const x of extra.slice(0, 3)) console.log(`  client only: ${x}`);
        return false;
      }
      return true;
    };

    if (!(await compare("as loaded"))) continue;

    // Points worth wiring between: wire ends and symbol terminals.
    const anchors = (): Point[] => {
      const points: Point[] = [];
      for (const e of doc.elements) {
        if (e.kind === "wire") points.push(e.a, e.b);
        else if (e.kind === "symbol") {
          const layout = layouts.get(e.component);
          if (layout) for (const t of layout.terminals) points.push(mapPoint(mappingOf(e), t.at));
        }
      }
      return points;
    };
    const boundsOf = (elements: Element[]): Box | null =>
      unionBoxes(
        elements.flatMap((e) => {
          if (e.kind === "wire") return [normalize(e.a, e.b)];
          const layout = e.kind === "symbol" ? layouts.get(e.component) : undefined;
          return layout && e.kind === "symbol" ? [mapBox(mappingOf(e), layout.bounds)] : [];
        }),
      );

    for (let round = 0; round < RoundsPerSchematic; round++) {
      const editable = doc.elements.filter((e) => e.kind !== "raw");
      if (editable.length === 0) break;
      const chosen = editable.filter(() => rand() < 0.3);
      if (chosen.length === 0) chosen.push(pick(editable));
      const ids = new Set(chosen.map((e) => e.id));
      const bounds = boundsOf(chosen);
      const centre: Point = bounds ? snap([Math.trunc((bounds.min[0] + bounds.max[0]) / 2), Math.trunc((bounds.min[1] + bounds.max[1]) / 2)]) : [0, 0];
      const op = pick(["rotate", "rotate", "flip", "flip", "move", "wire", "wire", "delete"]);
      let step = op;
      if (op === "rotate") {
        const turns = pick([1, -1, 2]);
        rotateElements(doc, ids, turns, centre);
        step = `rotate ${turns} of ${ids.size}`;
      } else if (op === "flip") {
        flipElements(doc, ids, centre[1]);
        step = `flip of ${ids.size}`;
      } else if (op === "move") {
        const by: Point = [5 * Math.round((rand() - 0.5) * 8), 5 * Math.round((rand() - 0.5) * 8)];
        moveElements(doc, ids, by);
        step = `move of ${ids.size} by ${by}`;
      } else if (op === "wire") {
        const points = anchors();
        if (points.length < 2) continue;
        const a = pick(points);
        const b = pick(points);
        const path: Point[] = a[0] === b[0] || a[1] === b[1] ? [a, b] : [a, rand() < 0.5 ? [b[0], a[1]] : [a[0], b[1]], b];
        addWire(doc, layouts, path, newId);
        step = `wire ${JSON.stringify(path)}`;
      } else {
        new ChangeEdit("Delete", doc, chosen, []).apply();
        step = `delete of ${ids.size}`;
      }
      if (!(await compare(`round ${round + 1}, ${step}`))) break;
    }
  }

  console.log(`${files.length} schematics, ${comparisons} comparisons with circuit.check, ${failures} failure(s).`);
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((e) => {
  console.log(`conformance: ${(e as Error).message}`);
  process.exit(1);
});
