// Simulating the open schematic (WebSocketApi.md, Simulation): a panel under the canvas with the simulation's state,
// its input, meters, pots and switches, and the scope; a probe tool that clips probes onto wires and terminals; and
// edits sent on as `sim.update`, so the simulation follows the schematic as it changes.

import type { ApiClient } from "./client";
import type { EditorState } from "./editor";
import { mapPoint, mappingOf } from "./geometry";
import type { LayoutCache } from "./layouts";
import type { Point, SchematicDocument, SymbolElement, WireElement } from "./protocol";
import { Scope, formatUnits, type SignalStats } from "./scope";
import type { SchematicView } from "./view";

const SVG = "http://www.w3.org/2000/svg";
const UpdateDelayMs = 400;

interface Pot {
  name: string;
  position: number;
  default: number;
  taper?: string;
  elements: string[];
}

interface Switch {
  name: string;
  position: number;
  positions: number;
  default: number;
  elements: string[];
}

interface Input {
  element: string;
  source: { expression?: string; silence?: boolean; channel?: string };
}

interface Controls {
  pots: Pot[];
  switches: Switch[];
  inputs: Input[];
  gains: { inputDb: number; outputDb: number };
}

type Target = { wire: string } | { element: string; terminal: string };

interface Probe {
  id: string;
  stream: number;
  target: Target;
  node: string;
  color: string;
  label: string;
}

interface SimulationInfo extends Controls {
  sim: string;
  title: string;
  state: string;
  code?: string;
  message?: string;
  probes: Probe[];
}

/** Input signals to choose from, in volts; the expression box takes any signal in t. */
const Presets: { name: string; expression: string | null }[] = [
  { name: "Sine, 220 Hz, 0.5 V", expression: "0.5*Sin(2*Pi*220*t)" },
  { name: "Sine, 440 Hz, 0.1 V", expression: "0.1*Sin(2*Pi*440*t)" },
  { name: "Sine, 1 kHz, 1 V", expression: "Sin(2*Pi*1000*t)" },
  { name: "Sine, 82 Hz (low E), 0.2 V", expression: "0.2*Sin(2*Pi*82.41*t)" },
  { name: "Square, 220 Hz, 0.2 V", expression: "0.2*Sign(Sin(2*Pi*220*t))" },
  { name: "Silence", expression: null },
];

const StateNames: Record<string, string> = {
  building: "Building…",
  rebuilding: "Rebuilding…",
  running: "Running",
  error: "Stopped by an error",
  stopped: "Stopped",
};

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

export interface SimulationHost {
  client: ApiClient;
  /** The panel's element, under the canvas. */
  root: HTMLElement;
  svg: SVGSVGElement;
  /** Where probe flags are drawn, in schematic coordinates, over the drawing. */
  probeLayer: SVGGElement;
  view: SchematicView;
  layouts: LayoutCache;
  /** The editor state shown now, to know whether the simulated schematic is the one on screen. */
  shown: () => EditorState | null;
  status: (text: string, kind?: "info" | "error") => void;
  /** Called when whether it's running or probing changes, for the toolbar. */
  changed: () => void;
}

export class SimulationPanel {
  private info: SimulationInfo | null = null;
  private state: EditorState | null = null;
  private sentVersion = 0;
  private updateTimer = 0;
  private probingNow = false;
  /** Where each probe's flag is drawn, from where it was clipped on. */
  private readonly flags = new Map<string, Point>();
  private readonly scope: Scope;
  private readonly el: {
    title: HTMLElement;
    state: HTMLElement;
    message: HTMLElement;
    probe: HTMLButtonElement;
    restart: HTMLButtonElement;
    stop: HTMLButtonElement;
    preset: HTMLSelectElement;
    expression: HTMLInputElement;
    inputMeter: HTMLElement;
    outputMeter: HTMLElement;
    load: HTMLElement;
    controls: HTMLElement;
    legend: HTMLElement;
  };

  constructor(private readonly host: SimulationHost) {
    const canvas = h("canvas", { className: "scope-canvas" });
    this.el = {
      title: h("span", { className: "sim-title" }),
      state: h("span", { className: "sim-state" }),
      message: h("div", { className: "sim-message" }),
      probe: h("button", { textContent: "Probe", title: "Click wires or terminals to see their voltage on the scope (Esc to stop)" }),
      restart: h("button", { textContent: "Restart", title: "Rebuild from the DC operating point" }),
      stop: h("button", { textContent: "Stop", title: "Stop the simulation (Shift+F5)" }),
      preset: h("select", { className: "sim-preset", title: "A test signal for the Input" }),
      expression: h("input", { type: "text", className: "sim-expression", spellcheck: false, title: "The Input's signal, in volts, as an expression in t (seconds). Enter applies it." }),
      inputMeter: h("div", { className: "meter-bar" }),
      outputMeter: h("div", { className: "meter-bar" }),
      load: h("span", { className: "sim-load" }),
      controls: h("div", { className: "sim-controls" }),
      legend: h("div", { className: "scope-legend" }),
    };
    const e = this.el;
    e.preset.append(...Presets.map((p, i) => new Option(p.name, String(i))), new Option("Custom", "custom"));
    host.root.replaceChildren(
      h(
        "div",
        { className: "sim-side" },
        h("div", { className: "sim-head" }, e.title, e.state, h("span", { className: "spacer" }), e.probe, e.restart, e.stop),
        e.message,
        h("div", { className: "sim-row" }, h("label", { textContent: "Input" }), e.preset),
        h("div", { className: "sim-row" }, h("span"), e.expression),
        h(
          "div",
          { className: "sim-row meters" },
          h("label", { textContent: "In" }),
          h("div", { className: "meter", title: "The Input's level, against its full scale" }, e.inputMeter),
          h("label", { textContent: "Out" }),
          h("div", { className: "meter", title: "The Speakers' level, against their full scale" }, e.outputMeter),
          e.load,
        ),
        e.controls,
      ),
      h("div", { className: "sim-scope" }, canvas, e.legend),
    );
    this.scope = new Scope(canvas);
    this.scope.onStats = (stats) => this.showStats(stats);

    e.probe.addEventListener("click", () => this.setProbing(!this.probingNow));
    e.restart.addEventListener("click", () => this.info && void this.call("sim.restart", { sim: this.info.sim }));
    e.stop.addEventListener("click", () => void this.stop());
    e.preset.addEventListener("change", () => {
      const preset = Presets[Number(e.preset.value)];
      if (!preset) return e.expression.focus();
      if (preset.expression) e.expression.value = preset.expression;
      void this.setSource(preset.expression);
    });
    e.expression.addEventListener("keydown", (ev) => {
      if (ev.key === "Enter") void this.setSource(e.expression.value.trim());
    });

    const client = host.client;
    client.onBinary = (frame) => this.scope.receive(frame);
    const mine = (data: unknown): data is { sim: string } => (data as { sim?: string })?.sim === this.info?.sim;
    client.on("sim.state", (d) => mine(d) && this.setState(d as unknown as { state: string; code?: string; message?: string }));
    client.on("sim.controls", (d) => mine(d) && this.setControls(d as unknown as Controls));
    client.on("sim.probes", (d) => mine(d) && this.setProbes((d as unknown as { probes: Probe[] }).probes));
    client.on("sim.levels", (d) => mine(d) && this.setLevels(d as unknown as { inputs: number[]; outputs: number[] }));
    client.on("sim.stats", (d) => mine(d) && this.setStats(d as unknown as { load: number; dropouts: number; rejectedSettings: number }));

    // The probe tool: a click picks the wire or terminal under the pointer, ahead of the editor's own handling.
    host.svg.addEventListener("pointerdown", (ev) => this.probeAt(ev), { capture: true });
    host.root.hidden = true;
  }

  /** Whether a simulation is running (or building). */
  get running() {
    return this.info !== null;
  }

  get probing() {
    return this.probingNow;
  }

  /** Whether this editor state is the schematic being simulated. */
  simulates(state: EditorState | null) {
    return state !== null && state === this.state;
  }

  /** Simulates a schematic, replacing any simulation already running. */
  async start(state: EditorState, title: string) {
    clearTimeout(this.updateTimer);
    if (this.info) await this.host.client.request("sim.stop", { sim: this.info.sim }).catch(() => {});
    this.reset();
    this.host.status(`Simulating ${title}…`);
    try {
      const info = await this.host.client.request<SimulationInfo>("sim.start", { document: state.doc, title, replace: true });
      this.info = info;
      this.state = state;
      this.sentVersion = state.history.version;
      this.host.root.hidden = false;
      this.el.title.textContent = title;
      this.setState(info);
      this.setControls(info);
      this.setProbes(info.probes);
      await this.host.client.request("sim.scope", { sim: info.sim, enabled: true });
      this.host.status(`Simulating ${title}.`);
    } catch (e) {
      this.host.status(`Couldn't simulate: ${(e as Error).message}`, "error");
    }
    this.host.changed();
  }

  async stop() {
    clearTimeout(this.updateTimer);
    const info = this.info;
    this.reset();
    this.host.changed();
    if (info) await this.host.client.request("sim.stop", { sim: info.sim }).catch(() => {});
  }

  private reset() {
    this.info = null;
    this.state = null;
    this.flags.clear();
    this.scope.clear();
    this.scope.setSignals([]);
    this.setProbing(false);
    this.host.root.hidden = true;
    this.el.controls.replaceChildren();
    this.el.legend.replaceChildren();
    this.el.message.textContent = "";
    this.drawFlags();
  }

  /** The connection closed: the simulation went with the server's state. */
  disconnected() {
    clearTimeout(this.updateTimer);
    this.reset();
    this.host.changed();
  }

  setProbing(on: boolean) {
    on &&= this.info !== null;
    if (on === this.probingNow) return;
    this.probingNow = on;
    this.el.probe.classList.toggle("active", on);
    this.host.svg.classList.toggle("probing", on);
    this.host.changed();
  }

  /** Called after any change to the shown document: an edit to the simulated one is sent on, after a pause. */
  documentChanged() {
    if (!this.info || !this.state || this.state.history.version === this.sentVersion) return;
    clearTimeout(this.updateTimer);
    this.updateTimer = window.setTimeout(() => void this.sendUpdate(), UpdateDelayMs);
    this.drawFlags();
  }

  /** Called when another tab is shown: flags only show on the simulated schematic. */
  shownChanged() {
    this.drawFlags();
  }

  private async sendUpdate() {
    const info = this.info, state = this.state;
    if (!info || !state) return;
    const version = state.history.version;
    try {
      await this.host.client.request("sim.update", { sim: info.sim, document: state.doc });
      this.sentVersion = version;
      this.el.message.textContent = "";
    } catch (e) {
      // It keeps running the last version that built.
      this.sentVersion = version;
      this.el.message.textContent = `Still simulating the last version that built: ${(e as Error).message.replace(/^sim\.update: /, "")}`;
    }
  }

  private async call(op: string, args: Record<string, unknown>) {
    try {
      return await this.host.client.request(op, args);
    } catch (e) {
      this.host.status((e as Error).message, "error");
      return null;
    }
  }

  // ---- What the server says ----

  private setState(s: { state: string; code?: string; message?: string }) {
    if (!this.info) return;
    this.info.state = s.state;
    this.el.state.textContent = StateNames[s.state] ?? s.state;
    this.el.state.dataset["state"] = s.state;
    if (s.state === "error") this.el.message.textContent = s.code === "diverged" ? "The simulation diverged." : (s.message ?? "");
    else if (s.state === "running" && this.el.message.textContent?.startsWith("The simulation")) this.el.message.textContent = "";
    if (s.state === "stopped") void this.stop();
  }

  private setControls(c: Controls) {
    if (!this.info) return;
    Object.assign(this.info, { pots: c.pots, switches: c.switches, inputs: c.inputs, gains: c.gains });
    const box = this.el.controls;
    // Built once per set of controls; after that only values change, so a knob being dragged isn't replaced.
    const key = JSON.stringify([c.pots.map((p) => p.name), c.switches.map((s) => [s.name, s.positions])]);
    if (box.dataset["key"] !== key) {
      box.dataset["key"] = key;
      box.replaceChildren(
        ...c.pots.map((pot) => {
          const slider = h("input", { type: "range", min: "0", max: "1", step: "0.001", title: `${pot.name}: drag to turn; double-click for its schematic position` });
          slider.dataset["pot"] = pot.name;
          const value = h("span", { className: "value" });
          slider.addEventListener("input", () => {
            value.textContent = percent(Number(slider.value));
            this.info && this.host.client.notify("sim.set", { sim: this.info.sim, pots: { [pot.name]: Number(slider.value) } });
          });
          slider.addEventListener("dblclick", () => {
            const original = this.info?.pots.find((p) => p.name === pot.name)?.default ?? pot.default;
            slider.value = String(original);
            slider.dispatchEvent(new Event("input"));
          });
          return h("div", { className: "control pot" }, h("label", { textContent: pot.name, title: pot.taper ? `${pot.taper} taper` : "" }), slider, value);
        }),
        ...c.switches.map((sw) => {
          const button = h("button", { className: "switch", title: `${sw.name}: click for the next position` });
          button.dataset["switch"] = sw.name;
          button.addEventListener("click", () => this.info && void this.call("sim.click", { sim: this.info.sim, switch: sw.name }));
          return h("div", { className: "control" }, h("label", { textContent: sw.name }), button);
        }),
      );
      if (c.pots.length + c.switches.length === 0) box.append(h("div", { className: "muted", textContent: "No pots or switches." }));
    }
    for (const pot of c.pots) {
      const slider = box.querySelector<HTMLInputElement>(`input[data-pot="${CSS.escape(pot.name)}"]`);
      if (!slider) continue;
      if (document.activeElement !== slider) slider.value = String(pot.position);
      slider.nextElementSibling!.textContent = percent(Number(slider.value));
    }
    for (const sw of c.switches) {
      const button = box.querySelector<HTMLButtonElement>(`button[data-switch="${CSS.escape(sw.name)}"]`);
      if (button) button.textContent = `${sw.position + 1} of ${sw.positions}`;
    }

    // The input source.
    const source = c.inputs[0]?.source;
    const hasInput = c.inputs.length > 0;
    this.el.preset.disabled = this.el.expression.disabled = !hasInput;
    if (!hasInput) {
      this.el.expression.value = "";
      this.el.expression.placeholder = "This circuit has no Input.";
    } else if (document.activeElement !== this.el.expression) {
      this.el.expression.value = source?.expression ?? "";
      this.el.expression.placeholder = source?.silence ? "Silence" : "";
      const preset = Presets.findIndex((p) => (p.expression ?? null) === (source?.expression ?? null));
      this.el.preset.value = preset >= 0 ? String(preset) : "custom";
    }
  }

  private async setSource(expression: string | null) {
    const info = this.info;
    const input = info?.inputs[0];
    if (!info || !input) return;
    const source = expression ? { expression } : { silence: true };
    try {
      await this.host.client.request("sim.setInput", { sim: info.sim, element: input.element, source });
      this.el.expression.classList.remove("invalid");
    } catch (e) {
      this.el.expression.classList.add("invalid");
      this.host.status((e as Error).message.replace(/^sim\.setInput: /, ""), "error");
    }
  }

  private setProbes(probes: Probe[]) {
    if (!this.info) return;
    this.info.probes = probes;
    for (const id of [...this.flags.keys()]) if (!probes.some((p) => p.id === id)) this.flags.delete(id);
    this.scope.setSignals(probes.map((p) => ({ stream: p.stream, color: p.color, label: p.label })));
    this.el.legend.replaceChildren(
      ...probes.map((p) => {
        const remove = h("button", { className: "remove", textContent: "×", title: "Remove this probe" });
        remove.addEventListener("click", () => this.info && void this.call("sim.removeProbes", { sim: this.info.sim, probes: [p.id] }));
        const item = h("div", { className: "legend-item" }, h("span", { className: `swatch b-${p.color}` }), h("span", { className: "name", textContent: this.probeName(p) }), h("span", { className: "stats" }), remove);
        item.dataset["stream"] = String(p.stream);
        return item;
      }),
    );
    this.drawFlags();
  }

  /** A probe's name: the node's, unless it's one of LiveSPICE's anonymous ones, then what it's clipped to. */
  private probeName(p: Probe): string {
    if (!p.node.startsWith("_")) return p.node;
    const doc = this.state?.doc;
    if ("element" in p.target) {
      const target = p.target;
      const symbol = doc?.elements.find((e) => e.id === target.element);
      const name = symbol?.kind === "symbol" ? symbol.component.properties["Name"] : undefined;
      return name ? `${name}.${target.terminal}` : p.label;
    }
    return p.label;
  }

  private showStats(stats: Map<number, SignalStats>) {
    for (const item of this.el.legend.querySelectorAll<HTMLElement>(".legend-item")) {
      const s = stats.get(Number(item.dataset["stream"]));
      item.querySelector(".stats")!.textContent = s ? `${formatUnits(s.peakToPeak, "V")}pp${s.frequency ? `, ${formatFrequency(s.frequency)}` : ""}` : "";
    }
  }

  private setLevels(l: { inputs: number[]; outputs: number[] }) {
    meter(this.el.inputMeter, l.inputs[0] ?? 0);
    meter(this.el.outputMeter, l.outputs[0] ?? 0);
  }

  private setStats(s: { load: number; dropouts: number; rejectedSettings: number }) {
    const load = `${Math.round(s.load * 100)}% of real time`;
    this.el.load.textContent = load;
    this.el.load.title = `Processing load: ${load}. Dropouts: ${s.dropouts}. Settings it couldn't solve: ${s.rejectedSettings}.`;
    this.el.load.classList.toggle("high", s.load > 0.8);
  }

  // ---- Probes on the schematic ----

  private probeAt(e: PointerEvent) {
    if (!this.probingNow || e.button !== 0 || !this.info || !this.state) return;
    e.stopPropagation();
    e.preventDefault();
    if (this.host.shown() !== this.state) {
      this.host.status("The probe works on the schematic being simulated: switch to its tab.", "error");
      return;
    }
    const found = this.targetAt(e.clientX, e.clientY);
    if (!found) {
      this.host.status("Click a wire or a terminal to probe it.");
      return;
    }
    const info = this.info;
    void this.host.client
      .request<Probe>("sim.probe", { sim: info.sim, target: found.target })
      .then((probe) => {
        this.flags.set(probe.id, found.at);
        this.drawFlags();
      })
      .catch((err) => this.host.status((err as Error).message.replace(/^sim\.probe: /, ""), "error"));
  }

  /** The wire or terminal under a point of the window, with where on it to put the flag. */
  private targetAt(clientX: number, clientY: number): { target: Target; at: Point } | null {
    const doc = this.state!.doc;
    const hit = document.elementFromPoint(clientX, clientY)?.closest<SVGElement>("#drawing [data-id]");
    const element = hit && doc.elements.find((x) => x.id === hit.dataset["id"]);
    if (!element) return null;
    const at = this.host.view.toSchematic(clientX, clientY);
    if (element.kind === "wire") return { target: { wire: element.id }, at: nearestOnWire(element, at) };
    if (element.kind !== "symbol") return null;
    const terminal = this.nearestTerminal(element, at);
    return terminal ? { target: { element: element.id, terminal: terminal.name }, at: terminal.at } : null;
  }

  private nearestTerminal(symbol: SymbolElement, at: Point): { name: string; at: Point } | null {
    const layout = this.host.layouts.get(symbol.component);
    if (!layout || layout.terminals.length === 0) return null;
    const m = mappingOf(symbol);
    let best: { name: string; at: Point } | null = null;
    let distance = Infinity;
    for (const t of layout.terminals) {
      const p = mapPoint(m, t.at);
      const d = Math.hypot(p[0] - at[0], p[1] - at[1]);
      if (d < distance) (distance = d), (best = { name: t.name, at: p });
    }
    return best;
  }

  /** Where a probe's flag goes: on its terminal, wherever the symbol is now; on its wire where it was clipped, or the
   * wire's middle if that's no longer on it. */
  private flagPoint(probe: Probe, doc: SchematicDocument): Point | null {
    const target = probe.target;
    if ("element" in target) {
      const symbol = doc.elements.find((e): e is SymbolElement => e.id === target.element && e.kind === "symbol");
      if (!symbol) return null;
      const layout = this.host.layouts.get(symbol.component);
      const t = layout?.terminals.find((x) => x.name === target.terminal);
      return t ? mapPoint(mappingOf(symbol), t.at) : null;
    }
    const wire = doc.elements.find((e): e is WireElement => e.id === target.wire && e.kind === "wire");
    if (!wire) return null;
    const clipped = this.flags.get(probe.id);
    if (clipped && Math.hypot(...sub(nearestOnWire(wire, clipped), clipped)) < 0.5) return clipped;
    return [(wire.a[0] + wire.b[0]) / 2, (wire.a[1] + wire.b[1]) / 2];
  }

  private drawFlags() {
    const layer = this.host.probeLayer;
    layer.replaceChildren();
    if (!this.info || !this.state || this.host.shown() !== this.state) return;
    for (const probe of this.info.probes) {
      const at = this.flagPoint(probe, this.state.doc);
      if (!at) continue;
      // A probe's flag: a dot on the node and a numbered pennant up and to the right, in the probe's colour.
      const g = document.createElementNS(SVG, "g");
      g.setAttribute("class", "probe-flag");
      const [x, y] = at;
      const parts: [string, Record<string, string | number>][] = [
        ["line", { x1: x, y1: y, x2: x + 8, y2: y - 12, class: `s-${probe.color}` }],
        ["circle", { cx: x, cy: y, r: 2, class: `f-${probe.color} s-${probe.color}` }],
        ["rect", { x: x + 8, y: y - 20, width: 12, height: 9, rx: 1.5, class: `f-${probe.color} s-${probe.color}` }],
        ["text", { x: x + 14, y: y - 13.2, class: "probe-number" }],
      ];
      for (const [tag, attributes] of parts) {
        const el = document.createElementNS(SVG, tag);
        for (const [k, v] of Object.entries(attributes)) el.setAttribute(k, String(v));
        if (tag === "text") el.textContent = String(probe.stream);
        g.append(el);
      }
      const title = document.createElementNS(SVG, "title");
      title.textContent = `${this.probeName(probe)} (${probe.label})`;
      g.append(title);
      layer.append(g);
    }
  }
}

function sub(a: Point, b: Point): Point {
  return [a[0] - b[0], a[1] - b[1]];
}

function nearestOnWire(w: WireElement, p: Point): Point {
  const [dx, dy] = sub(w.b, w.a);
  const length = dx * dx + dy * dy;
  const t = length ? Math.min(1, Math.max(0, ((p[0] - w.a[0]) * dx + (p[1] - w.a[1]) * dy) / length)) : 0;
  return [w.a[0] + t * dx, w.a[1] + t * dy];
}

function percent(v: number) {
  return `${Math.round(v * 100)}%`;
}

function formatFrequency(f: number) {
  return f >= 1000 ? `${(f / 1000).toPrecision(3)} kHz` : `${f.toPrecision(3)} Hz`;
}

/** A level meter: linear peak against full scale, coloured as LiveSPICE's lamps (WebSocketApi.md, Monitoring). */
function meter(bar: HTMLElement, peak: number) {
  bar.style.width = `${Math.min(1, peak) * 100}%`;
  bar.dataset["level"] = peak < 0.5 ? "ok" : peak < 0.75 ? "warm" : peak < 0.95 ? "hot" : "clip";
}
