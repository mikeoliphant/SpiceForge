// The audio dialog (WebSocketApi.md, Audio; UISpec.md, Audio configuration): the driver, the device, its channels, rate
// and buffer size, the driver's own control panel, and a test that loops the inputs to the outputs, with level meters
// and a scope of the summed input. The server applies, saves and moves a running simulation to what's chosen here.

import type { ApiClient } from "./client";
import { RequestError } from "./client";
import { Scope } from "./scope";

/** `audio.state`. */
export interface AudioState {
  open: boolean;
  driver: string;
  device?: string | null;
  sampleRate?: number;
  bufferSize?: number;
  inputs?: string[];
  outputs?: string[];
  inputLatency?: number;
  outputLatency?: number;
  /** Running on the internal clock, although a device was configured. */
  fallback?: boolean;
  /** Whether a simulation or the test is running on it: a JACK client stays open without. */
  running?: boolean;
  /** JACK: SpiceForge's ports, the channel each was opened for, and what each is connected to now. */
  connections?: { port: string; channel: string; connected: string[] }[];
  error?: string;
}

/** `audio.getConfig` and `audio.setConfig`: null fields are the device's defaults. */
export interface AudioConfig {
  driver: string;
  device: string | null;
  inputs: string[] | null;
  outputs: string[] | null;
  sampleRate: number | null;
  bufferSize: number | null;
  state: AudioState;
  /** The configuration last used with each driver, by driver name. */
  byDriver?: Record<string, Omit<AudioConfig, "state" | "byDriver">>;
}

interface DeviceInfo {
  name: string;
  inputs: string[];
  outputs: string[];
  sampleRates: number[];
  bufferSizes: number[];
  defaultSampleRate: number;
  defaultBufferSize: number;
  controlPanel: boolean;
}

interface Drivers {
  drivers: { name: string; devices: { name: string }[] }[];
}

/** The name of the driver that has no device: the server's own clock. */
export const ClockDriver = "None";

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

function messageOf(e: unknown): string {
  return e instanceof RequestError ? e.error.message : (e as Error).message;
}

function meter(bar: HTMLElement, peak: number) {
  bar.style.width = `${Math.min(1, peak) * 100}%`;
  bar.dataset["level"] = peak < 0.5 ? "ok" : peak < 0.75 ? "warm" : peak < 0.95 ? "hot" : "clip";
}

export function formatRate(hz: number) {
  return hz % 1000 === 0 ? `${hz / 1000} kHz` : `${(hz / 1000).toFixed(1)} kHz`;
}

function formatMs(samples: number, rate: number) {
  return `${((samples / rate) * 1000).toFixed(1)} ms`;
}

/** One line on what the audio is doing, for the dialog and the simulation panel. */
export function describeAudio(state: AudioState): string {
  if (!state.open) return state.driver === ClockDriver ? "No audio device: the internal clock" : `${state.device ?? state.driver}, closed until something runs`;
  const rate = state.sampleRate ?? 0;
  const where = state.driver === ClockDriver ? "Internal clock (no audio)" : `${state.device}`;
  let text = `${where}, ${formatRate(rate)}, ${state.bufferSize} samples (${formatMs(state.bufferSize ?? 0, rate)})`;
  if (state.driver !== ClockDriver && (state.inputLatency || state.outputLatency))
    text += `, ${formatMs((state.inputLatency ?? 0) + (state.outputLatency ?? 0), rate)} round trip`;
  if (state.fallback) text += `. The device couldn't be used: ${state.error ?? "it failed"}`;
  // Switched to no device after one failed while running (unplugged, say): why.
  else if (state.error && state.driver === ClockDriver) text += `. The audio device failed, so it switched to none: ${state.error}`;
  return text;
}

/** Whether the audio line should warn: running on the clock instead of a configured device, or since one failed. */
export function audioWarning(state: AudioState): boolean {
  return !!state.fallback || (!!state.open && !!state.error);
}

/** Shows the audio dialog until it's closed. */
export async function configureAudio(client: ApiClient): Promise<void> {
  const [config, drivers] = await Promise.all([client.request<AudioConfig>("audio.getConfig"), client.request<Drivers>("audio.devices")]);
  let byDriver = config.byDriver ?? {};

  const driver = h("select", { title: "The kind of audio device. None runs simulations on the server's own clock, with no audio." });
  const device = h("select", { title: "The device (for ASIO, the driver)" });
  const panel = h("button", { textContent: "Control panel…", title: "The driver's own settings window, which opens on the server's machine. Many ASIO drivers set their buffer size only here." });
  const inputs = h("div", { className: "channels" });
  const outputs = h("div", { className: "channels" });
  const rate = h("select", { title: "The sample rate. A change rebuilds a running simulation." });
  const buffer = h("select", { title: "Samples per buffer: smaller is less latency, and less time to process each one" });
  const state = h("div", { className: "audio-state" });
  const connections = h("div", { className: "audio-connections", title: "SpiceForge's JACK ports, as connected now (including from a patchbay)" });
  const error = h("div", { className: "error" });
  const test = h("button", { textContent: "Test", title: "Plays the chosen signal on every output, with the settings above (they're applied first)" });
  const signal = h("select", { title: "What the test plays: a tone, or what arrives on the inputs. The scope and the In meters show the inputs either way." });
  signal.append(new Option("Tone, 440 Hz", "tone"), new Option("Inputs, looped back", "inputs"));
  const inputMeters = h("div", { className: "audio-meters" });
  const outputMeters = h("div", { className: "audio-meters" });
  const canvas = h("canvas", { className: "scope-canvas" });
  const cancel = h("button", { textContent: "Cancel" });
  const ok = h("button", { textContent: "OK", className: "primary", title: "Applies the settings, and saves them" });
  const row = (label: string, ...content: Node[]) => h("div", { className: "audio-row" }, h("label", { textContent: label }), ...content);
  const dialog = h(
    "dialog",
    { className: "file-dialog audio-dialog" },
    h("h2", { textContent: "Audio" }),
    row("Driver", driver),
    row("Device", h("div", { className: "audio-device" }, device, panel)),
    h("div", { className: "audio-columns" }, h("div", {}, h("h3", { textContent: "Inputs" }), inputs), h("div", {}, h("h3", { textContent: "Outputs" }), outputs)),
    row("Rate", rate),
    row("Buffer", buffer),
    state,
    connections,
    h(
      "div",
      { className: "audio-test" },
      h("div", { className: "audio-test-side" }, test, signal, h("h3", { textContent: "In" }), inputMeters, h("h3", { textContent: "Out" }), outputMeters),
      h("div", { className: "audio-scope" }, canvas),
    ),
    error,
    h("div", { className: "buttons" }, cancel, ok),
  );
  document.body.append(dialog);

  const scope = new Scope(canvas);
  scope.setSignals([{ stream: 0, color: "green", label: "The inputs, summed" }]);
  let info: DeviceInfo | null = null;
  let testing = false;
  let busy = false;
  // The channels chosen on the device shown, by name; null until its info arrives.
  let chosenInputs: string[] | null = config.inputs;
  let chosenOutputs: string[] | null = config.outputs;

  driver.append(...drivers.drivers.map((d) => new Option(d.name, d.name)));
  driver.value = drivers.drivers.some((d) => d.name === config.driver) ? config.driver : ClockDriver;

  const fillDevices = (wanted: string | null) => {
    const list = drivers.drivers.find((d) => d.name === driver.value)?.devices ?? [];
    device.replaceChildren(...list.map((d) => new Option(d.name, d.name)));
    if (list.length === 0) device.append(new Option("(none installed)", ""));
    device.value = wanted && list.some((d) => d.name === wanted) ? wanted : (list[0]?.name ?? "");
  };

  const channelList = (box: HTMLElement, names: string[], chosen: string[], kind: string) => {
    box.replaceChildren(
      ...names.map((name) => {
        const check = h("input", { type: "checkbox", checked: chosen.some((c) => c.toLowerCase() === name.toLowerCase()) });
        check.dataset["channel"] = name;
        check.addEventListener("change", () => {
          const picked = [...box.querySelectorAll<HTMLInputElement>("input:checked")].map((c) => c.dataset["channel"]!);
          if (kind === "input") chosenInputs = picked;
          else chosenOutputs = picked;
        });
        return h("label", { className: "channel", title: name }, check, h("span", { textContent: name }));
      }),
    );
    if (names.length === 0) box.append(h("div", { className: "muted", textContent: "None" }));
  };

  const showInfo = (wantedRate: number | null, wantedBuffer: number | null) => {
    const i = info;
    rate.replaceChildren();
    buffer.replaceChildren();
    if (!i) {
      channelList(inputs, [], [], "input");
      channelList(outputs, [], [], "output");
      panel.hidden = true;
      return;
    }
    // A device's defaults: its first input, its first two outputs (as the server takes a null list).
    chosenInputs = (chosenInputs ?? i.inputs.slice(0, 1)).filter((c) => i.inputs.some((n) => n.toLowerCase() === c.toLowerCase()));
    chosenOutputs = (chosenOutputs ?? i.outputs.slice(0, 2)).filter((c) => i.outputs.some((n) => n.toLowerCase() === c.toLowerCase()));
    channelList(inputs, i.inputs, chosenInputs, "input");
    channelList(outputs, i.outputs, chosenOutputs, "output");
    rate.append(new Option(`The device's (${formatRate(i.defaultSampleRate)})`, ""), ...i.sampleRates.map((r) => new Option(formatRate(r), String(r))));
    rate.value = wantedRate && i.sampleRates.includes(wantedRate) ? String(wantedRate) : "";
    const ms = (n: number) => formatMs(n, Number(rate.value) || i.defaultSampleRate);
    buffer.append(
      new Option(`The device's (${i.defaultBufferSize}, ${ms(i.defaultBufferSize)})`, ""),
      ...i.bufferSizes.map((b) => new Option(`${b} samples (${ms(b)})`, String(b))),
    );
    buffer.value = wantedBuffer && i.bufferSizes.includes(wantedBuffer) ? String(wantedBuffer) : "";
    panel.hidden = !i.controlPanel;
  };

  const loadInfo = async (wantedRate: number | null, wantedBuffer: number | null) => {
    info = null;
    error.textContent = "";
    showInfo(null, null);
    if (!device.value) return;
    const asked = `${driver.value}/${device.value}`;
    state.textContent = "Reading the device…";
    try {
      const got = await client.request<DeviceInfo>("audio.deviceInfo", { driver: driver.value, device: device.value });
      if (asked !== `${driver.value}/${device.value}`) return;
      info = got;
      showInfo(wantedRate, wantedBuffer);
    } catch (e) {
      if (asked === `${driver.value}/${device.value}`) error.textContent = messageOf(e);
    }
    void refreshState();
  };

  const refreshState = async () => {
    try {
      showState((await client.request<AudioConfig>("audio.getConfig")).state);
    } catch {
      // The dialog still works; the line just isn't current.
    }
  };

  const showState = (s: AudioState) => {
    state.textContent = `Now: ${describeAudio(s)}`;
    state.classList.toggle("warning", audioWarning(s));
    // JACK: each port's live connections, whoever made them.
    connections.replaceChildren(
      ...(s.open && s.connections ? s.connections : []).map((c) => {
        const input = /:in_\d+$/.test(c.port);
        const others = c.connected.length > 0 ? c.connected.join(", ") : "nothing";
        return h("div", { textContent: input ? `${c.port} ← ${others}` : `${c.port} → ${others}` });
      }),
    );
  };

  // JACK's ports come and go as programs start and stop: refresh the lists, quietly, keeping what's chosen.
  let refreshing = 0;
  const refreshInfo = () => {
    clearTimeout(refreshing);
    refreshing = window.setTimeout(async () => {
      if (busy || testing || !info || !device.value) return;
      const asked = `${driver.value}/${device.value}`;
      try {
        const got = await client.request<DeviceInfo>("audio.deviceInfo", { driver: driver.value, device: device.value });
        if (asked !== `${driver.value}/${device.value}` || !info) return;
        const same = (a: string[], b: string[]) => a.length === b.length && a.every((x, i) => x === b[i]);
        if (same(got.inputs, info.inputs) && same(got.outputs, info.outputs)) return;
        info = got;
        showInfo(rate.value ? Number(rate.value) : null, buffer.value ? Number(buffer.value) : null);
        enable();
      } catch {
        // The lists stay as they were.
      }
    }, 300);
  };

  const enable = () => {
    for (const control of [driver, device, rate, buffer, signal, ...dialog.querySelectorAll<HTMLInputElement>(".channels input")])
      control.disabled = testing || busy;
    panel.disabled = busy;
    ok.disabled = test.disabled = busy;
    test.classList.toggle("active", testing);
    test.textContent = testing ? "Stop test" : "Test";
  };

  /** Applies the form with audio.setConfig. Returns whether it took. */
  const apply = async (): Promise<boolean> => {
    error.textContent = "";
    busy = true;
    enable();
    try {
      const applied = await client.request<AudioConfig>("audio.setConfig", {
        driver: driver.value,
        device: device.value || null,
        // A device without channels (the clock) keeps the defaults rather than saving empty lists.
        inputs: info && info.inputs.length > 0 ? chosenInputs : null,
        outputs: info && info.outputs.length > 0 ? chosenOutputs : null,
        sampleRate: rate.value ? Number(rate.value) : null,
        bufferSize: buffer.value ? Number(buffer.value) : null,
      });
      showState(applied.state);
      byDriver = applied.byDriver ?? byDriver;
      return true;
    } catch (e) {
      error.textContent = messageOf(e);
      return false;
    } finally {
      busy = false;
      enable();
    }
  };

  const setTesting = async (on: boolean) => {
    error.textContent = "";
    if (on && !(await apply())) return;
    try {
      await client.request("audio.test", on ? { enabled: true, signal: signal.value } : { enabled: false });
      testing = on;
      if (!on) {
        scope.clear();
        inputMeters.replaceChildren();
        outputMeters.replaceChildren();
      }
    } catch (e) {
      error.textContent = messageOf(e);
    }
    enable();
  };

  const levels = (box: HTMLElement, peaks: number[], names: string[]) => {
    if (box.childElementCount !== peaks.length)
      box.replaceChildren(
        ...peaks.map((_, i) => h("div", { className: "audio-meter", title: names[i] ?? "" }, h("div", { className: "meter" }, h("div", { className: "meter-bar" })))),
      );
    peaks.forEach((p, i) => meter(box.children[i]!.querySelector<HTMLElement>(".meter-bar")!, p));
  };

  let lastState: AudioState = config.state;
  const stops = [
    client.on("audio.state", (d) => {
      lastState = d as AudioState;
      showState(lastState);
      if (lastState.driver === "JACK" && driver.value === "JACK") refreshInfo();
    }),
    client.on("audio.levels", (d) => {
      if (!testing) return;
      const l = d as { inputs: number[]; outputs: number[] };
      levels(inputMeters, l.inputs, lastState.inputs ?? []);
      levels(outputMeters, l.outputs, lastState.outputs ?? []);
    }),
    client.onFrame((frame, source) => source === 0 && testing && scope.receive(frame)),
  ];

  // Another driver starts from what was last used with it (its device, channels, rate and buffer size), as the server
  // does; a remembered device that's gone gives way to the first.
  driver.addEventListener("change", () => {
    const last = byDriver[driver.value];
    chosenInputs = last?.inputs ?? null;
    chosenOutputs = last?.outputs ?? null;
    fillDevices(last?.device ?? null);
    if (last?.device && device.value !== last.device) chosenInputs = chosenOutputs = null;
    void loadInfo(last?.sampleRate ?? null, last?.bufferSize ?? null);
  });
  device.addEventListener("change", () => {
    chosenInputs = chosenOutputs = null;
    void loadInfo(null, null);
  });
  rate.addEventListener("change", () => showInfo(rate.value ? Number(rate.value) : null, buffer.value ? Number(buffer.value) : null));
  panel.addEventListener("click", async () => {
    error.textContent = "";
    try {
      await client.request("audio.controlPanel", { driver: driver.value, device: device.value });
      // The driver may have changed its rate or buffer sizes.
      await loadInfo(rate.value ? Number(rate.value) : null, buffer.value ? Number(buffer.value) : null);
    } catch (e) {
      error.textContent = messageOf(e);
    }
  });
  test.addEventListener("click", () => void setTesting(!testing));

  showState(config.state);
  fillDevices(config.device);
  enable();
  void loadInfo(config.sampleRate, config.bufferSize);

  return new Promise((resolve) => {
    const finish = async (save: boolean) => {
      if (save && !(await apply())) return;
      if (testing) await client.request("audio.test", { enabled: false }).catch(() => {});
      for (const stop of stops) stop();
      scope.dispose();
      dialog.close();
      dialog.remove();
      resolve();
    };
    cancel.addEventListener("click", () => void finish(false));
    ok.addEventListener("click", () => void finish(true));
    dialog.addEventListener("cancel", (e) => {
      e.preventDefault();
      void finish(false);
    });
    dialog.showModal();
  });
}
