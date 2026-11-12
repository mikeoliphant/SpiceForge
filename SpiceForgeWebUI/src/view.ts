// Zoom and pan of the schematic SVG, through its viewBox. As LiveSPICE's SchematicViewer: the wheel zooms 1.1× per
// notch about the pointer, the buttons 1.5×, at most 8×; the middle button pans; Zoom Fit frames everything with a
// 40-unit margin. Holding Space makes the left button pan too, for trackpads, which have no middle button.

import type { Box } from "./geometry";

export const MaxZoom = 8;
export const MinZoom = 1 / 16;

export class SchematicView {
  /** Screen pixels per schematic unit. */
  private zoom = 1;
  /** The schematic point at the centre of the view. */
  private cx = 0;
  private cy = 0;
  private drag: { x: number; y: number; cx: number; cy: number; pointer: number } | null = null;
  private space = false;

  /** Called after any change of zoom or position, with the new zoom. */
  onChange: (zoom: number) => void = () => {};
  /** Called as the pointer moves, with the schematic point under it. */
  onPointer: (x: number, y: number) => void = () => {};

  constructor(private readonly svg: SVGSVGElement) {
    new ResizeObserver(() => this.apply()).observe(svg);
    svg.addEventListener("wheel", (e) => {
      e.preventDefault();
      this.zoomAt(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX, e.clientY);
    }, { passive: false });
    window.addEventListener("keydown", (e) => {
      if (e.code === "Space" && !(e.target instanceof HTMLInputElement)) {
        e.preventDefault();
        if (!this.space) svg.classList.add("pan-ready");
        this.space = true;
      }
    });
    window.addEventListener("keyup", (e) => {
      if (e.code === "Space") {
        this.space = false;
        svg.classList.remove("pan-ready");
      }
    });
    window.addEventListener("blur", () => {
      this.space = false;
      svg.classList.remove("pan-ready");
    });
    svg.addEventListener("pointerdown", (e) => {
      if (e.button === 1 || (e.button === 0 && this.space)) {
        e.preventDefault();
        e.stopImmediatePropagation();
        this.drag = { x: e.clientX, y: e.clientY, cx: this.cx, cy: this.cy, pointer: e.pointerId };
        svg.setPointerCapture(e.pointerId);
        svg.classList.add("panning");
      }
    });
    svg.addEventListener("pointermove", (e) => {
      const [x, y] = this.toSchematic(e.clientX, e.clientY);
      this.onPointer(x, y);
      if (this.drag && e.pointerId === this.drag.pointer) {
        this.cx = this.drag.cx - (e.clientX - this.drag.x) / this.zoom;
        this.cy = this.drag.cy - (e.clientY - this.drag.y) / this.zoom;
        this.apply();
      }
    });
    const end = (e: PointerEvent) => {
      if (this.drag && e.pointerId === this.drag.pointer) {
        this.drag = null;
        svg.classList.remove("panning");
      }
    };
    svg.addEventListener("pointerup", end);
    svg.addEventListener("pointercancel", end);
    svg.addEventListener("auxclick", (e) => e.button === 1 && e.preventDefault());
  }

  get Zoom(): number {
    return this.zoom;
  }

  /** Where the view is, to restore later (each tab keeps its own). */
  get place(): { zoom: number; cx: number; cy: number } {
    return { zoom: this.zoom, cx: this.cx, cy: this.cy };
  }

  set place(p: { zoom: number; cx: number; cy: number }) {
    this.zoom = p.zoom;
    this.cx = p.cx;
    this.cy = p.cy;
    this.apply();
  }

  /** Whether a pointer press now pans rather than going to the editor. */
  get panning(): boolean {
    return this.space || this.drag !== null;
  }

  /** The schematic point under a screen point. */
  toSchematic(clientX: number, clientY: number): [number, number] {
    const r = this.svg.getBoundingClientRect();
    return [this.cx + (clientX - r.left - r.width / 2) / this.zoom, this.cy + (clientY - r.top - r.height / 2) / this.zoom];
  }

  /** Zooms by a factor, keeping the schematic point under (clientX, clientY) where it is. */
  zoomAt(factor: number, clientX?: number, clientY?: number) {
    const r = this.svg.getBoundingClientRect();
    const sx = clientX ?? r.left + r.width / 2;
    const sy = clientY ?? r.top + r.height / 2;
    const [x, y] = this.toSchematic(sx, sy);
    this.zoom = Math.min(MaxZoom, Math.max(MinZoom, this.zoom * factor));
    this.cx = x - (sx - r.left - r.width / 2) / this.zoom;
    this.cy = y - (sy - r.top - r.height / 2) / this.zoom;
    this.apply();
  }

  /** Frames a box, as Zoom Fit does: everything with a margin, or the area around the origin when empty. */
  fit(bounds: Box | null, margin = 40) {
    const box = bounds ?? { min: [-100, -100], max: [100, 100] };
    const r = this.svg.getBoundingClientRect();
    const w = box.max[0] - box.min[0] + 2 * margin;
    const h = box.max[1] - box.min[1] + 2 * margin;
    if (r.width > 0 && r.height > 0) this.zoom = Math.min(MaxZoom, Math.max(MinZoom, Math.min(r.width / w, r.height / h)));
    this.cx = (box.min[0] + box.max[0]) / 2;
    this.cy = (box.min[1] + box.max[1]) / 2;
    this.apply();
  }

  private apply() {
    const r = this.svg.getBoundingClientRect();
    const w = r.width / this.zoom;
    const h = r.height / this.zoom;
    this.svg.setAttribute("viewBox", `${this.cx - w / 2} ${this.cy - h / 2} ${w} ${h}`);
    this.onChange(this.zoom);
  }
}
