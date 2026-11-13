// The side panels' widths: the workspace's edges between the schematic and the library (on its left) and the
// properties (on its right) are handles. Dragging one sets that panel's width, which this browser remembers, and a
// double-click restores its default. Each width is a CSS variable on the workspace that its grid's columns use; the
// handles sit in the workspace, over the boundaries, rather than in the panels, where they'd cover the scrollbars.

/** One side panel's sizing. */
export interface PanelWidth {
  panel: HTMLElement;
  /** Which side of the schematic the panel is on: its handle is on its edge facing the schematic. */
  side: "left" | "right";
  /** The workspace's CSS variable holding the width, which the stylesheet defaults to `defaultWidth`. */
  variable: string;
  /** Where the dragged width is remembered. */
  key: string;
  defaultWidth: number;
  min: number;
  max: number;
  /** The handle's tooltip. */
  title: string;
}

/** The schematic's least width, whatever the panels' widths. */
const MinCanvasWidth = 300;

/** Makes the side panels resizable; each keeps the schematic its room beside the other. */
export function makePanelsResizable(workspace: HTMLElement, panels: PanelWidth[]) {
  const width = (p: PanelWidth) => p.panel.getBoundingClientRect().width;
  const setters = panels.map((p) => {
    const handle = document.createElement("div");
    handle.className = `panel-splitter ${p.side}`;
    handle.title = p.title;
    handle.style.setProperty("--splitter-at", `var(${p.variable}, ${p.defaultWidth}px)`);
    workspace.append(handle);

    const others = () => panels.filter((q) => q !== p).reduce((sum, q) => sum + width(q), 0);
    const clamp = (w: number) => Math.round(Math.max(p.min, Math.min(w, p.max, window.innerWidth - others() - MinCanvasWidth)));
    const setWidth = (w: number, save: boolean) => {
      workspace.style.setProperty(p.variable, `${clamp(w)}px`);
      if (!save) return;
      try {
        localStorage.setItem(p.key, String(clamp(w)));
      } catch {
        // Not remembered, then.
      }
    };
    let saved = NaN;
    try {
      saved = Number(localStorage.getItem(p.key) ?? NaN);
    } catch {
      // Unavailable storage: the default width.
    }
    if (Number.isFinite(saved)) setWidth(saved, false);

    // Dragging toward the schematic widens the panel: rightward for the left panel, leftward for the right one.
    const sign = p.side === "left" ? 1 : -1;
    let drag: { pointer: number; startX: number; start: number } | null = null;
    handle.addEventListener("pointerdown", (ev) => {
      if (ev.button !== 0) return;
      ev.preventDefault();
      handle.setPointerCapture(ev.pointerId);
      drag = { pointer: ev.pointerId, startX: ev.clientX, start: width(p) };
      handle.classList.add("dragging");
    });
    handle.addEventListener("pointermove", (ev) => {
      if (drag?.pointer === ev.pointerId) setWidth(drag.start + sign * (ev.clientX - drag.startX), false);
    });
    const end = (ev: PointerEvent) => {
      if (drag?.pointer !== ev.pointerId) return;
      drag = null;
      handle.classList.remove("dragging");
      setWidth(width(p), true);
    };
    handle.addEventListener("pointerup", end);
    handle.addEventListener("pointercancel", end);
    handle.addEventListener("dblclick", () => setWidth(p.defaultWidth, true));
    return () => setWidth(width(p), false);
  });
  // A smaller window mustn't leave the panels crowding out the schematic.
  window.addEventListener("resize", () => setters.forEach((set) => set()));
}
