// The Help dialog, opened from the toolbar's Help button, F1 or ?: for now a summary of the keyboard shortcuts and
// mouse gestures. The lists are written out by hand, so a change to a binding (main.ts's keydown handler, editor.ts's
// gestures, view.ts's zoom and pan, simulation.ts's knobs and probes) is mirrored here.

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

/** A group of bindings: each the keys (alternatives separated by " / ", chords by "+") and what they do. */
interface Section {
  title: string;
  rows: [keys: string, action: string][];
}

const Sections: Section[] = [
  {
    title: "Files",
    rows: [
      ["Ctrl+O", "Open a schematic"],
      ["Ctrl+S", "Save"],
      ["Ctrl+Shift+S", "Save under a new name"],
      ["Middle-click a tab", "Close it"],
    ],
  },
  {
    title: "Editing",
    rows: [
      ["Ctrl+Z", "Undo"],
      ["Ctrl+Y / Ctrl+Shift+Z", "Redo"],
      ["Ctrl+X", "Cut"],
      ["Ctrl+C", "Copy"],
      ["Ctrl+V", "Paste"],
      ["Del", "Delete the selection"],
      ["Ctrl+A", "Select all"],
      ["←", "Rotate the selection (or the part being placed) left"],
      ["→", "Rotate it right"],
      ["↑ / ↓", "Flip it"],
      ["Esc", "Cancel: back to the selection tool"],
      ["Ctrl+F", "Find a part by name (Enter for the next, Shift+Enter the previous)"],
    ],
  },
  {
    title: "Placing parts",
    rows: [
      ["W", "Draw a wire"],
      ["G", "Ground"],
      ["P", "Rail"],
      ["R", "Resistor"],
      ["C", "Capacitor"],
      ["L", "Inductor"],
      ["T", "Label"],
      ["Ctrl+G / P / R / L / H", "The same, as in LiveSPICE (Ctrl+H is the inductor too)"],
      ["Ctrl+click", "Place, keeping the tool to place another"],
    ],
  },
  {
    title: "Mouse",
    rows: [
      ["Click", "Select a part or wire (on nothing, clear the selection)"],
      ["Drag", "Move what's under the pointer, with the rest of the selection; attached wires follow"],
      ["Shift+drag", "Move without the attached wires"],
      ["Drag a wire's free end", "Extend the wire"],
      ["Drag on empty space", "Select everything in the rectangle"],
      ["Ctrl+click / Ctrl+drag", "Add to or remove from the selection"],
      ["Double-click a part", "Select every part of its type"],
      ["Right-click", "The context menu; during a drag, cancel it"],
    ],
  },
  {
    title: "View",
    rows: [
      ["Wheel", "Zoom about the pointer"],
      ["+ / −", "Zoom in and out"],
      ["F / 0", "Zoom to fit"],
      ["Middle-drag / Space+drag", "Pan"],
    ],
  },
  {
    title: "Simulation",
    rows: [
      ["F5", "Simulate the schematic shown"],
      ["Shift+F5", "Stop"],
      ["Drag a knob / Wheel over it", "Turn it"],
      ["Double-click a knob", "Back to its default"],
      ["Click a switch", "Its next position"],
      ["Esc", "Leave the probe or pick tool"],
    ],
  },
  {
    title: "Help",
    rows: [["F1 / ?", "This summary"]],
  },
];

/** "Ctrl+Shift+S" as key caps, with the " / " alternatives and "+" chords left as text between them. */
function keyCaps(keys: string): HTMLElement {
  const cell = h("td", { className: "help-keys" });
  keys.split(" / ").forEach((alternative, i) => {
    if (i > 0) cell.append(" / ");
    alternative.split("+").forEach((key, j) => {
      if (j > 0) cell.append("+");
      // A mouse gesture ("Middle-click a tab", "Drag a knob") reads as words, not a key.
      cell.append(/\s/.test(key) || /click|drag|wheel/i.test(key) ? key : h("kbd", { textContent: key }));
    });
  });
  return cell;
}

let open: HTMLDialogElement | null = null;

/** Shows the dialog until it's closed (or brings it back to the front if it's open). */
export function showHelp(): void {
  if (open) return void open.focus();
  const close = h("button", { textContent: "Close", className: "primary" });
  const dialog = h(
    "dialog",
    { className: "file-dialog help-dialog" },
    h("h2", { textContent: "Keyboard shortcuts" }),
    h(
      "div",
      { className: "help-sections" },
      ...Sections.map((s) =>
        h("section", {}, h("h3", { textContent: s.title }), h("table", {}, ...s.rows.map(([keys, action]) => h("tr", {}, keyCaps(keys), h("td", { textContent: action }))))),
      ),
    ),
    h("p", { className: "help-note", textContent: "On a Mac, ⌘ works wherever Ctrl is shown. Toolbar buttons show their shortcut when hovered." }),
    h("div", { className: "buttons" }, close),
  );
  const finish = () => {
    dialog.close();
    dialog.remove();
    open = null;
  };
  close.addEventListener("click", finish);
  dialog.addEventListener("cancel", (e) => {
    e.preventDefault();
    finish();
  });
  document.body.append(dialog);
  open = dialog;
  dialog.showModal();
  close.focus();
}
