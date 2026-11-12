// Simulating the open schematic (WebSocketApi.md, Simulation): a panel under the canvas with the simulation's state,
// its input, meters, pots and switches, and the scope; a probe tool that clips probes onto wires and terminals; and
// edits sent on as `sim.update`, so the simulation follows the schematic as it changes.

import type { ApiClient } from "./client";
import type { EditorState } from "./editor";
import { boxIntersects, mapBox, mapPoint, mappingOf, type Box } from "./geometry";
import type { LayoutCache } from "./layouts";
import type { ComponentData, Point, SchematicDocument, SymbolElement, WireElement } from "./protocol";
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
  settings: Record<string, unknown>;
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

/** The scope's time and voltage scales: null fits them to the signals. */
const TimeScales: (number | null)[] = [null, 0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02];
const VoltScales: (number | null)[] = [null, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10];

/** How far a vertical drag turns a knob end to end, in pixels. */
const KnobTravel = 150;

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
  /** Where to remember a schematic's simulation setup (probes, scope, input, controls): its file, or null. */
  keyOf: (state: EditorState) => string | null;
  /** Draws symbols as these stand-ins, for display only (null: as they are), and redraws. */
  display: (componentOf: ((s: SymbolElement) => ComponentData | null) | null) => void;
}

/** What's remembered of a schematic's simulation between runs and reloads, in this browser. */
interface Remembered {
  probes: { target: Target; at: Point | null }[];
  timeScale: number;
  voltScale: number;
  source: { expression?: string; silence?: boolean } | null;
  pots: Record<string, number>;
  switches: Record<string, number>;
}

const RememberPrefix = "spiceforge.sim:";
/** The simulation this browser tab is showing, kept for a reload: the server carries on running it. */
const RunningKey = "spiceforge.simulation";

function readJson<T>(storage: Storage, key: string): T | null {
  try {
    const text = storage.getItem(key);
    return text ? (JSON.parse(text) as T) : null;
  } catch {
    return null;
  }
}

function writeJson(storage: Storage, key: string, value: unknown) {
  try {
    if (value === null) storage.removeItem(key);
    else storage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage may be full or unavailable; remembering is a convenience.
  }
}

export class SimulationPanel {
  private info: SimulationInfo | null = null;
  private state: EditorState | null = null;
  private sentVersion = 0;
  private updateTimer = 0;
  private probingNow = false;
  /** Where each probe's flag is drawn, from where it was clipped on. */
  private readonly flags = new Map<string, Point>();
  private rememberTimer = 0;
  private displayTimer = 0;
  /** Live positions to draw switches and pots at, by element id, while simulating. */
  private readonly shownAs = new Map<string, ComponentData>();
  /** The knob being dragged on the schematic, if any. */
  private turning: { pot: string; pointer: number; startY: number; start: number } | null = null;
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
    oversample: HTMLSelectElement;
    cap: HTMLSelectElement;
    resampling: HTMLSelectElement;
    timeScale: HTMLSelectElement;
    voltScale: HTMLSelectElement;
    freeze: HTMLButtonElement;
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
      oversample: h("select", { title: "Internal steps per sample: more is more accurate, and slower" }),
      cap: h("select", { title: "Newton's iteration cap per step" }),
      resampling: h("select", { title: "How samples are converted to and from the internal steps" }),
      timeScale: h("select", { title: "Time per division" }),
      voltScale: h("select", { title: "Volts per division" }),
      freeze: h("button", { textContent: "Freeze", title: "Hold the display still" }),
    };
    const e = this.el;
    e.preset.append(...Presets.map((p, i) => new Option(p.name, String(i))), new Option("Custom", "custom"));
    e.oversample.append(...[1, 2, 4, 8, 16].map((n) => new Option(`${n}×`, String(n))));
    e.cap.append(...[8, 16, 32, 64, 127].map((n) => new Option(`cap ${n}`, String(n))));
    e.resampling.append(new Option("sinc", "sinc"), new Option("linear", "linear"));
    e.timeScale.append(...TimeScales.map((t, i) => new Option(t ? `${formatUnits(t, "s")}/div` : "Auto time", String(i))));
    e.voltScale.append(...VoltScales.map((v, i) => new Option(v ? `${formatUnits(v, "V")}/div` : "Auto volts", String(i))));
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
        h("div", { className: "sim-row settings" }, h("label", { textContent: "Engine" }), h("div", { className: "sim-settings" }, e.oversample, e.cap, e.resampling)),
      ),
      h("div", { className: "sim-scope" }, canvas, h("div", { className: "scope-side" }, h("div", { className: "scope-bar" }, e.timeScale, e.voltScale, e.freeze), e.legend)),
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
    for (const select of [e.oversample, e.cap, e.resampling]) select.addEventListener("change", () => void this.configure());
    e.timeScale.addEventListener("change", () => {
      this.scope.timePerDivision = TimeScales[Number(e.timeScale.value)] ?? null;
      this.scope.refresh();
      this.remember();
    });
    e.voltScale.addEventListener("change", () => {
      this.scope.voltsPerDivision = VoltScales[Number(e.voltScale.value)] ?? null;
      this.scope.refresh();
      this.remember();
    });
    e.freeze.addEventListener("click", () => {
      this.scope.frozen = !this.scope.frozen;
      e.freeze.classList.toggle("active", this.scope.frozen);
    });

    const client = host.client;
    client.onBinary = (frame) => this.scope.receive(frame);
    const mine = (data: unknown): data is { sim: string } => (data as { sim?: string })?.sim === this.info?.sim;
    client.on("sim.state", (d) => mine(d) && this.setState(d as unknown as { state: string; code?: string; message?: string }));
    client.on("sim.controls", (d) => mine(d) && this.setControls(d as unknown as Controls));
    client.on("sim.probes", (d) => mine(d) && this.setProbes((d as unknown as { probes: Probe[] }).probes));
    client.on("sim.levels", (d) => mine(d) && this.setLevels(d as unknown as { inputs: number[]; outputs: number[] }));
    client.on("sim.stats", (d) => mine(d) && this.setStats(d as unknown as { load: number; dropouts: number; rejectedSettings: number; cappedSteps: number | null }));

    // A knob being turned on the schematic: followed on the window, without pointer capture, which an SVG group
    // didn't keep past the first move in testing.
    window.addEventListener("pointermove", (ev) => {
      const t = this.turning;
      if (t && t.pointer === ev.pointerId) this.setPot(t.pot, t.start + (t.startY - ev.clientY) / KnobTravel);
    });
    const release = (ev: PointerEvent) => {
      if (this.turning?.pointer === ev.pointerId) this.turning = null;
    };
    window.addEventListener("pointerup", release);
    window.addEventListener("pointercancel", release);

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
      this.show(info, state, title);
      await this.host.client.request("sim.scope", { sim: info.sim, enabled: true });
      await this.restore();
      this.host.status(`Simulating ${title}.`);
    } catch (e) {
      this.host.status(`Couldn't simulate: ${(e as Error).message}`, "error");
    }
    this.host.changed();
  }

  /** Shows a simulation in the panel, for a schematic. */
  private show(info: SimulationInfo, state: EditorState, title: string) {
    this.info = info;
    this.state = state;
    this.sentVersion = state.history.version;
    this.host.root.hidden = false;
    this.el.title.textContent = title;
    this.setState(info);
    this.showSettings(info.settings);
    this.setControls(info);
    this.setProbes(info.probes);
    writeJson(sessionStorage, RunningKey, { sim: info.sim, key: this.host.keyOf(state) });
  }

  /**
   * After a reload: if the simulation this browser tab was showing still runs on the server, and its schematic is
   * open again, shows it again, sending the schematic as it is now (probes and controls carry over on the server).
   */
  async resume(stateFor: (key: string) => { state: EditorState; title: string } | null) {
    const running = readJson<{ sim: string; key: string | null }>(sessionStorage, RunningKey);
    if (!running?.key || this.info) return;
    const found = stateFor(running.key);
    let info: SimulationInfo;
    try {
      info = await this.host.client.request<SimulationInfo>("sim.get", { sim: running.sim });
    } catch {
      writeJson(sessionStorage, RunningKey, null);
      return;
    }
    if (!found) return;
    this.show(info, found.state, found.title);
    // Flags go where they were clipped on, as remembered.
    const saved = readJson<Remembered>(localStorage, RememberPrefix + running.key);
    for (const probe of info.probes) {
      const at = saved?.probes.find((p) => JSON.stringify(p.target) === JSON.stringify(probe.target))?.at;
      if (at) this.flags.set(probe.id, at);
    }
    if (saved) this.restoreScales(saved);
    this.drawFlags();
    this.host.changed();
    try {
      await this.host.client.request("sim.scope", { sim: info.sim, enabled: true });
      await this.host.client.request("sim.update", { sim: info.sim, document: found.state.doc });
      this.host.status(`Simulating ${found.title} (carried on from before the reload).`);
    } catch (e) {
      this.el.message.textContent = `Still simulating the last version that built: ${(e as Error).message.replace(/^sim\.update: /, "")}`;
    }
  }

  // ---- Remembering a schematic's setup ----

  /** Saves this schematic's probes, scope scales, input and controls, a moment after the last change. */
  private remember() {
    clearTimeout(this.rememberTimer);
    this.rememberTimer = window.setTimeout(() => {
      const info = this.info, state = this.state;
      const key = state && this.host.keyOf(state);
      if (!info || !key) return;
      const remembered: Remembered = {
        probes: info.probes.map((p) => ({ target: p.target, at: this.flags.get(p.id) ?? null })),
        timeScale: Number(this.el.timeScale.value),
        voltScale: Number(this.el.voltScale.value),
        source: info.inputs[0]?.source ?? null,
        pots: Object.fromEntries(info.pots.map((p) => [p.name, p.position])),
        switches: Object.fromEntries(info.switches.map((s) => [s.name, s.position])),
      };
      writeJson(localStorage, RememberPrefix + key, remembered);
    }, 300);
  }

  private restoreScales(saved: Remembered) {
    this.el.timeScale.value = String(saved.timeScale ?? 0);
    this.el.voltScale.value = String(saved.voltScale ?? 0);
    this.scope.timePerDivision = TimeScales[saved.timeScale] ?? null;
    this.scope.voltsPerDivision = VoltScales[saved.voltScale] ?? null;
    this.scope.refresh();
  }

  /** Puts back what was remembered of this schematic: what no longer applies (a pot renamed, a wire deleted) is
   * skipped. */
  private async restore() {
    const info = this.info, state = this.state;
    const key = state && this.host.keyOf(state);
    const saved = key ? readJson<Remembered>(localStorage, RememberPrefix + key) : null;
    if (!info || !saved) return;
    this.restoreScales(saved);
    const pots = Object.fromEntries(Object.entries(saved.pots ?? {}).filter(([name]) => info.pots.some((p) => p.name === name)));
    const switches = Object.fromEntries(
      Object.entries(saved.switches ?? {}).filter(([name, position]) => info.switches.some((s) => s.name === name && position < s.positions)),
    );
    if (Object.keys(pots).length + Object.keys(switches).length > 0)
      await this.host.client.request("sim.set", { sim: info.sim, pots, switches }).catch(() => {});
    const input = info.inputs[0];
    if (input && saved.source && (saved.source.expression || saved.source.silence))
      await this.host.client.request("sim.setInput", { sim: info.sim, element: input.element, source: saved.source }).catch(() => {});
    for (const p of saved.probes ?? []) {
      try {
        const probe = await this.host.client.request<Probe>("sim.probe", { sim: info.sim, target: p.target });
        if (p.at) this.flags.set(probe.id, p.at);
      } catch {
        // Its wire or terminal is gone.
      }
    }
    this.drawFlags();
  }

  async stop() {
    clearTimeout(this.updateTimer);
    const info = this.info;
    this.reset();
    this.host.changed();
    writeJson(sessionStorage, RunningKey, null);
    if (info) await this.host.client.request("sim.stop", { sim: info.sim }).catch(() => {});
  }

  private reset() {
    this.info = null;
    this.shownAs.clear();
    clearTimeout(this.displayTimer);
    this.host.display(null);
    this.turning = null;
    this.scope.frozen = false;
    this.el.freeze.classList.remove("active");
    this.state = null;
    this.flags.clear();
    this.scope.clear();
    this.scope.setSignals([]);
    this.setProbing(false);
    this.host.root.hidden = true;
    this.el.controls.replaceChildren();
    // So the next simulation builds its sliders afresh, even of the same circuit.
    delete this.el.controls.dataset["key"];
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
          slider.addEventListener("input", () => this.setPot(pot.name, Number(slider.value)));
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
    // The knobs and buttons on the schematic follow, unless one is being turned there.
    if (!this.turning) this.drawFlags();
    this.showLive();
    this.remember();

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

  /**
   * Draws the simulated schematic's switches at their live positions, and its pots' wipers at theirs, without
   * editing the document: the editor draws stand-in components, whose layouts are fetched first. Throttled, so a
   * knob being dragged doesn't fetch a layout per step.
   */
  private showLive() {
    if (this.displayTimer) return;
    this.displayTimer = window.setTimeout(() => {
      this.displayTimer = 0;
      void this.applyLive();
    }, 120);
  }

  private async applyLive() {
    const info = this.info, state = this.state;
    if (!info || !state) return;
    const live = new Map<string, ComponentData>();
    const symbolOf = (id: string) => state.doc.elements.find((e): e is SymbolElement => e.id === id && e.kind === "symbol");
    const put = (id: string, property: string, value: string) => {
      const symbol = symbolOf(id);
      if (!symbol || symbol.component.properties[property] === value) return;
      live.set(id, { ...symbol.component, properties: { ...symbol.component.properties, [property]: value } });
    };
    for (const sw of info.switches) for (const id of sw.elements) put(id, "Position", String(sw.position));
    // A wiper as the symbol prints it, to two places, so dragging fetches a handful of layouts, not hundreds.
    for (const pot of info.pots) for (const id of pot.elements) if (symbolOf(id)?.component.properties["Wipe"] !== undefined) put(id, "Wipe", String(Number(pot.position.toFixed(2))));
    try {
      await this.host.layouts.fetch([...live.values()]);
    } catch {
      return;
    }
    if (this.info !== info) return;
    this.shownAs.clear();
    for (const [id, component] of live) this.shownAs.set(id, component);
    this.host.display(this.shownAs.size ? (s) => (this.host.shown() === this.state ? this.shownAs.get(s.id) ?? null : null) : null);
  }

  /** Turns a pot, from the panel's slider or a knob on the schematic: both show it, and the server gets it. */
  private setPot(name: string, position: number) {
    const info = this.info;
    const pot = info?.pots.find((p) => p.name === name);
    if (!info || !pot) return;
    position = Math.min(1, Math.max(0, position));
    pot.position = position;
    const slider = this.el.controls.querySelector<HTMLInputElement>(`input[data-pot="${CSS.escape(name)}"]`);
    if (slider) {
      if (document.activeElement !== slider) slider.value = String(position);
      slider.nextElementSibling!.textContent = percent(position);
    }
    for (const knob of this.host.probeLayer.querySelectorAll<SVGGElement>(".sim-knob")) if (knob.dataset["pot"] === name) turnKnob(knob, pot);
    this.host.client.notify("sim.set", { sim: info.sim, pots: { [name]: position } });
    this.showLive();
    this.remember();
  }

  private showSettings(settings: Record<string, unknown>) {
    this.el.oversample.value = String(settings["oversample"] ?? 4);
    this.el.cap.value = String(settings["iterationCap"] ?? 32);
    this.el.resampling.value = String(settings["resampling"] ?? "sinc");
  }

  /** The engine settings changed: the simulation rebuilds with them. */
  private async configure() {
    const info = this.info;
    if (!info) return;
    const settings = {
      ...info.settings,
      oversample: Number(this.el.oversample.value),
      iterationCap: Number(this.el.cap.value),
      resampling: this.el.resampling.value,
    };
    try {
      await this.host.client.request("sim.configure", { sim: info.sim, settings });
      info.settings = settings;
    } catch (e) {
      this.host.status((e as Error).message, "error");
      this.showSettings(info.settings);
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
      this.remember();
    } catch (e) {
      this.el.expression.classList.add("invalid");
      this.host.status((e as Error).message.replace(/^sim\.setInput: /, ""), "error");
    }
  }

  private setProbes(probes: Probe[]) {
    if (!this.info) return;
    this.info.probes = probes;
    this.remember();
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

  private setStats(s: { load: number; dropouts: number; rejectedSettings: number; cappedSteps: number | null }) {
    const load = `${Math.round(s.load * 100)}% of real time`;
    const capped = s.cappedSteps ? `, ${s.cappedSteps} capped` : "";
    this.el.load.textContent = load + capped;
    this.el.load.title =
      `Processing load: ${load}. Newton steps that hit the iteration cap in the last second: ${s.cappedSteps ?? 0} ` +
      `(raise the cap if this stays high). Dropouts: ${s.dropouts}. Settings it couldn't solve: ${s.rejectedSettings}.`;
    this.el.load.classList.toggle("high", s.load > 0.8 || (s.cappedSteps ?? 0) > 0);
  }

  // ---- Probes on the schematic ----

  private probeAt(e: PointerEvent) {
    if (!this.probingNow || e.button !== 0 || !this.info || !this.state) return;
    if ((e.target as Element | null)?.closest?.(".sim-knob, .sim-switch")) return;
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
        this.remember();
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

  /** Knobs beside the pots and buttons beside the switches of the simulated schematic, to turn and click there. */
  private drawControls(layer: SVGGElement, doc: SchematicDocument) {
    const info = this.info!;
    // What a control shouldn't cover: every symbol, every wire, the probe flags, and the controls already placed.
    const symbols = doc.elements.filter((e): e is SymbolElement => e.kind === "symbol");
    const wires = doc.elements.filter((e): e is WireElement => e.kind === "wire");
    const boxOf = (s: SymbolElement): Box | null => {
      const layout = this.host.layouts.get(this.shownAs.get(s.id) ?? s.component) ?? this.host.layouts.get(s.component);
      return layout ? mapBox(mappingOf(s), layout.bounds) : null;
    };
    const symbolBoxes = new Map(symbols.map((s) => [s.id, boxOf(s)] as const));
    const taken: Box[] = [...this.flags.values()].map(([x, y]) => ({ min: [x - 2, y - 21], max: [x + 21, y + 2] }));
    // Labels aren't in a symbol's bounds: their boxes as drawn, in schematic coordinates, the symbol's own included.
    const labels: Box[] = [...document.querySelectorAll<SVGRectElement>("#drawing .text-box")].map((r) => {
      const x = Number(r.getAttribute("x")), y = Number(r.getAttribute("y"));
      return { min: [x, y], max: [x + Number(r.getAttribute("width")), y + Number(r.getAttribute("height"))] };
    });
    const around = (c: Point, r: number): Box => ({ min: [c[0] - r, c[1] - r], max: [c[0] + r, c[1] + r] });
    const cost = (box: Box, own: string) => {
      let n = 0;
      for (const [id, b] of symbolBoxes) if (id !== own && b && boxIntersects(b, box)) n += 2;
      for (const w of wires) if (segmentHitsBox(w.a, w.b, box)) n += 3;
      for (const t of taken) if (boxIntersects(t, box)) n += 4;
      for (const l of labels) if (boxIntersects(l, box)) n += 3;
      return n;
    };
    // Places a control of half-size r beside a symbol: the first of the preferred sides that's clear, or else the
    // side with the least in the way.
    type Side = "right" | "left" | "above" | "below" | "above-right" | "below-right" | "above-left" | "below-left";
    const place = (id: string, r: number, sides: Side[]): Point | null => {
      const box = symbolBoxes.get(id);
      if (!box) return null;
      const midX = (box.min[0] + box.max[0]) / 2, midY = (box.min[1] + box.max[1]) / 2, gap = r + 3;
      const at: Record<Side, Point> = {
        right: [box.max[0] + gap, midY],
        left: [box.min[0] - gap, midY],
        above: [midX, box.min[1] - gap],
        below: [midX, box.max[1] + gap],
        "above-right": [box.max[0] + gap, box.min[1] - gap],
        "below-right": [box.max[0] + gap, box.max[1] + gap],
        "above-left": [box.min[0] - gap, box.min[1] - gap],
        "below-left": [box.min[0] - gap, box.max[1] + gap],
      };
      let best: Point | null = null, bestCost = Infinity;
      for (const side of sides) {
        const c = cost(around(at[side]!, r + 1), id);
        if (c < bestCost) (best = at[side]!), (bestCost = c);
        if (c === 0) break;
      }
      if (best) taken.push(around(best, r + 1));
      return best;
    };
    for (const pot of info.pots)
      for (const id of pot.elements) {
        const at = place(id, 8, ["right", "left", "above", "below", "above-right", "below-right", "above-left", "below-left"]);
        if (at) layer.append(this.knob(pot, at));
      }
    for (const sw of info.switches)
      for (const id of sw.elements) {
        const at = place(id, 7, ["above", "below", "right", "left", "above-right", "below-right", "above-left", "below-left"]);
        if (at) layer.append(this.switchButton(sw, at));
      }
  }

  private knob(pot: Pot, [x, y]: Point): SVGGElement {
    const g = svg("g", { class: "sim-knob", transform: `translate(${x} ${y})` });
    g.dataset["pot"] = pot.name;
    g.append(svg("circle", { r: 7, class: "knob-body" }), svg("line", { x1: 0, y1: 0, x2: 0, y2: -6, class: "knob-pointer" }), svg("title"));
    turnKnob(g, pot);
    const position = () => this.info?.pots.find((p) => p.name === pot.name)?.position ?? pot.position;
    // The drag is followed on the window (see the constructor), so it carries on wherever the pointer goes.
    g.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      e.stopPropagation();
      e.preventDefault();
      this.turning = { pot: pot.name, pointer: e.pointerId, startY: e.clientY, start: position() };
    });
    g.addEventListener("dblclick", (e) => {
      e.stopPropagation();
      this.setPot(pot.name, this.info?.pots.find((p) => p.name === pot.name)?.default ?? pot.default);
    });
    g.addEventListener(
      "wheel",
      (e) => {
        // A scroll over a knob turns it, rather than zooming the view.
        e.preventDefault();
        e.stopPropagation();
        this.setPot(pot.name, position() - Math.sign(e.deltaY) * 0.02);
      },
      { passive: false },
    );
    return g;
  }

  private switchButton(sw: Switch, [x, y]: Point): SVGGElement {
    const g = svg("g", { class: "sim-switch", transform: `translate(${x} ${y})` });
    const label = svg("text", { x: 0, y: 2.6, class: "switch-label" });
    label.textContent = String(sw.position + 1);
    const title = svg("title");
    title.textContent = `${sw.name}: position ${sw.position + 1} of ${sw.positions}. Click for the next.`;
    g.append(svg("rect", { x: -7, y: -5, width: 14, height: 10, rx: 2, class: "switch-body" }), label, title);
    g.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      e.stopPropagation();
      e.preventDefault();
      if (this.info) void this.call("sim.click", { sim: this.info.sim, switch: sw.name });
    });
    return g;
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
    this.drawControls(layer, this.state.doc);
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

function svg<K extends keyof SVGElementTagNameMap>(tag: K, attributes: Record<string, string | number> = {}): SVGElementTagNameMap[K] {
  const e = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attributes)) e.setAttribute(k, String(v));
  return e;
}

/** Points a knob at a pot's position: 0 at seven o'clock, 1 at five, as on a real pot. */
function turnKnob(knob: SVGGElement, pot: Pot) {
  knob.querySelector(".knob-pointer")!.setAttribute("transform", `rotate(${-135 + 270 * pot.position})`);
  knob.querySelector("title")!.textContent = `${pot.name}: ${percent(pot.position)}. Drag up or down, or scroll, to turn; double-click for its schematic position.`;
}

/** Whether a segment passes through a box: clipped against each side in turn (Liang-Barsky). */
function segmentHitsBox(a: Point, b: Point, box: Box): boolean {
  const d: Point = [b[0] - a[0], b[1] - a[1]];
  let t0 = 0, t1 = 1;
  const edges: [number, number][] = [
    [-d[0], a[0] - box.min[0]],
    [d[0], box.max[0] - a[0]],
    [-d[1], a[1] - box.min[1]],
    [d[1], box.max[1] - a[1]],
  ];
  for (const [p, q] of edges) {
    if (p === 0) {
      if (q < 0) return false;
    } else {
      const t = q / p;
      if (p < 0) t0 = Math.max(t0, t);
      else t1 = Math.min(t1, t);
      if (t0 > t1) return false;
    }
  }
  return true;
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
