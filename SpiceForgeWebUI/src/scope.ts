// The scope (WebSocketApi.md, Probes and the scope): the server streams each probe's samples in binary frames; this keeps
// the last 0.25 s of each and draws them, triggered on the first probe's rising crossing so a steady waveform stands
// still, over about three of its periods. Scale, statistics and the time base are worked out here, as the spec asks.

const History = 0.25;

/** One probe's recent samples, in a ring. */
class Trace {
  readonly samples: Float32Array;
  /** The stream index of the next sample to arrive. */
  end = 0;

  constructor(size: number) {
    this.samples = new Float32Array(size);
  }

  write(first: number, values: Float32Array) {
    const size = this.samples.length;
    // A gap (a dropped frame) leaves zeros rather than splicing the two sides together.
    if (first > this.end) for (let i = this.end; i < Math.min(first, this.end + size); i++) this.samples[i % size] = 0;
    for (let i = 0; i < values.length; i++) this.samples[(first + i) % size] = values[i]!;
    this.end = first + values.length;
  }

  /** The samples from stream index `from` for `count`, or fewer if they're no longer kept. */
  read(from: number, count: number): Float32Array {
    const size = this.samples.length;
    const start = Math.max(from, this.end - size);
    const out = new Float32Array(Math.max(0, Math.min(count, this.end - start)));
    for (let i = 0; i < out.length; i++) out[i] = this.samples[(start + i) % size]!;
    return out;
  }
}

export interface ScopeSignal {
  stream: number;
  color: string;
  label: string;
}

export interface SignalStats {
  peakToPeak: number;
  frequency: number | null;
}

export class Scope {
  private readonly traces = new Map<number, Trace>();
  private signals: ScopeSignal[] = [];
  private rate = 48000;
  private dirty = false;
  private frame = 0;

  /** Called after each draw, with each signal's statistics over what's shown. */
  onStats: (stats: Map<number, SignalStats>) => void = () => {};

  constructor(private readonly canvas: HTMLCanvasElement) {
    new ResizeObserver(() => this.invalidate()).observe(canvas);
  }

  setSignals(signals: ScopeSignal[]) {
    this.signals = signals;
    for (const stream of [...this.traces.keys()]) if (!signals.some((s) => s.stream === stream)) this.traces.delete(stream);
    this.invalidate();
  }

  clear() {
    this.traces.clear();
    this.invalidate();
  }

  /** Takes a binary frame (WebSocketApi.md, Binary frames). */
  receive(buffer: ArrayBuffer) {
    const view = new DataView(buffer);
    if (view.getUint8(0) !== 1 || view.getUint8(1) !== 1) return;
    const first = Number(view.getBigUint64(8, true));
    this.rate = view.getFloat32(16, true);
    const count = view.getUint16(20, true);
    let at = 24;
    for (let s = 0; s < count; s++) {
      const stream = view.getUint32(at, true);
      const n = view.getUint32(at + 4, true);
      const values = new Float32Array(buffer, at + 8, n);
      let trace = this.traces.get(stream);
      if (!trace) this.traces.set(stream, (trace = new Trace(Math.ceil(this.rate * History))));
      trace.write(first, values);
      at += 8 + 4 * n;
    }
    this.invalidate();
  }

  private invalidate() {
    if (this.dirty) return;
    this.dirty = true;
    this.frame = requestAnimationFrame(() => {
      this.dirty = false;
      this.draw();
    });
  }

  dispose() {
    cancelAnimationFrame(this.frame);
  }

  private draw() {
    const canvas = this.canvas;
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(canvas.clientWidth * ratio));
    const height = Math.max(1, Math.round(canvas.clientHeight * ratio));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    const g = canvas.getContext("2d")!;
    const css = getComputedStyle(document.documentElement);
    const token = (name: string) => css.getPropertyValue(name).trim();
    g.fillStyle = token("--canvas");
    g.fillRect(0, 0, width, height);

    const shown = this.signals.filter((s) => this.traces.has(s.stream));
    const stats = new Map<number, SignalStats>();
    const font = `${11 * ratio}px "Segoe UI", system-ui, sans-serif`;
    g.font = font;
    if (shown.length === 0) {
      g.fillStyle = token("--muted");
      g.textAlign = "center";
      g.textBaseline = "middle";
      g.fillText(this.signals.length ? "Waiting for samples…" : "Probe a wire or terminal to see its voltage here.", width / 2, height / 2);
      this.onStats(stats);
      return;
    }

    // The time base: about three periods of the first signal, triggered on its rising crossing of its mean.
    const end = Math.min(...shown.map((s) => this.traces.get(s.stream)!.end));
    const kept = Math.floor(this.rate * History);
    const reference = this.traces.get(shown[0]!.stream)!.read(end - kept, kept);
    const period = estimatePeriod(reference, this.rate);
    const span = Math.round(clamp(period ? 3 * period : 0.02, 0.002, 0.1) * this.rate);
    let start = end - span;
    if (period) {
      const mean = average(reference);
      // The latest rising crossing that leaves a whole span after it.
      for (let i = reference.length - span - 1; i > 0; i--)
        if (reference[i - 1]! < mean && reference[i]! >= mean) {
          start = end - kept + i;
          break;
        }
    }

    const data = shown.map((s) => ({ signal: s, values: this.traces.get(s.stream)!.read(start, span) }));
    // One vertical scale for every signal: a "nice" number of volts per division, eight divisions high.
    let peak = 0;
    for (const { values } of data) for (const v of values) peak = Math.max(peak, Math.abs(v));
    const perDivision = niceStep(Math.max(peak, 1e-6) / 4);
    const divisions = 8;
    const yOf = (v: number) => height / 2 - (v / (perDivision * divisions)) * height;

    // The grid: ten divisions across, eight down.
    g.strokeStyle = token("--chrome-border");
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 1; i < 10; i++) {
      const x = Math.round((i * width) / 10) + 0.5;
      g.moveTo(x, 0);
      g.lineTo(x, height);
    }
    for (let i = 1; i < divisions; i++) {
      const y = Math.round((i * height) / divisions) + 0.5;
      g.moveTo(0, y);
      g.lineTo(width, y);
    }
    g.stroke();
    g.strokeStyle = token("--muted");
    g.beginPath();
    g.moveTo(0, Math.round(height / 2) + 0.5);
    g.lineTo(width, Math.round(height / 2) + 0.5);
    g.stroke();

    // The traces.
    g.lineWidth = 1.5 * ratio;
    g.lineJoin = "round";
    for (const { signal, values } of data) {
      g.strokeStyle = token(`--edge-${signal.color}`) || signal.color;
      g.beginPath();
      for (let i = 0; i < values.length; i++) {
        const x = (i / Math.max(1, span - 1)) * width;
        const y = yOf(values[i]!);
        if (i === 0) g.moveTo(x, y);
        else g.lineTo(x, y);
      }
      g.stroke();
      let min = Infinity, max = -Infinity;
      for (const v of values) (min = Math.min(min, v)), (max = Math.max(max, v));
      const own = estimatePeriod(this.traces.get(signal.stream)!.read(end - kept, kept), this.rate);
      stats.set(signal.stream, { peakToPeak: values.length ? max - min : 0, frequency: own ? 1 / own : null });
    }

    // The scales.
    g.fillStyle = token("--muted");
    g.textAlign = "left";
    g.textBaseline = "top";
    const pad = 4 * ratio;
    g.fillText(`${formatUnits(perDivision, "V")}/div   ${formatUnits(span / this.rate / 10, "s")}/div`, pad, pad);
    this.onStats(stats);
  }
}

function clamp(v: number, lo: number, hi: number) {
  return Math.min(hi, Math.max(lo, v));
}

function average(values: Float32Array) {
  let sum = 0;
  for (const v of values) sum += v;
  return values.length ? sum / values.length : 0;
}

/** The period, in seconds, from the spacing of rising crossings of the mean, or null if it doesn't look periodic. */
export function estimatePeriod(values: Float32Array, rate: number): number | null {
  if (values.length < 16) return null;
  const mean = average(values);
  let min = Infinity, max = -Infinity;
  for (const v of values) (min = Math.min(min, v)), (max = Math.max(max, v));
  // Hysteresis of a tenth of the swing, so noise near the mean doesn't count as crossings.
  const band = (max - min) / 10;
  if (!(band > 1e-9)) return null;
  const crossings: number[] = [];
  let armed = false;
  for (let i = 1; i < values.length; i++) {
    if (values[i]! < mean - band) armed = true;
    else if (armed && values[i - 1]! < mean && values[i]! >= mean) {
      // Interpolated between the two samples, for a steadier estimate.
      const a = values[i - 1]!, b = values[i]!;
      crossings.push(i - 1 + (mean - a) / (b - a));
      armed = false;
    }
  }
  if (crossings.length < 3) return null;
  const spacing = (crossings.at(-1)! - crossings[0]!) / (crossings.length - 1);
  return spacing / rate;
}

/** 1, 2 or 5 times a power of ten, at least `v`. */
function niceStep(v: number) {
  const power = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 2, 5, 10]) if (m * power >= v) return m * power;
  return 10 * power;
}

export function formatUnits(v: number, unit: string) {
  const a = Math.abs(v);
  const [scale, prefix] = a >= 1 ? [1, ""] : a >= 1e-3 ? [1e3, "m"] : a >= 1e-6 ? [1e6, "µ"] : [1e9, "n"];
  const n = v * scale;
  return `${Number(n.toPrecision(3))} ${prefix}${unit}`;
}
