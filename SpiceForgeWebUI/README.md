# SpiceForgeWebUI

SpiceForge's web front end: plain TypeScript with no runtime dependencies, bundled by esbuild and served by
`SpiceForgeServer` from the same origin as its WebSocket API (see `DesignDocs/WebSocketApi.md`).

It opens a schematic and draws it: LiveSPICE's symbols from the server's catalogue layouts, wires, and terminals (red
when unconnected). You can edit it as in LiveSPICE's editor:
- place components from the library;
- draw wires;
- select, move, rotate and flip elements;
- delete, cut, copy and paste them;
- edit properties;
- undo and redo, and save.

## Building

Node.js is needed to build, not to run: it runs the TypeScript compiler (type checking) and esbuild (bundling). The
result is static files in `dist/`.

```
npm install
npm run build      # type-check, then bundle into dist/
npm run watch      # rebuild on every change (no type check; run `npm run check` for that)
```

## Running

Start the server, which finds `dist/` in the repository while developing (or `wwwroot` beside itself, or
`--webRoot <folder>`), and open the address it prints:

```
dotnet run --project SpiceForgeServer
# Front end: http://127.0.0.1:8370/?token=...
```

`--open true` opens it in the default browser. The page takes the token from its address and keeps it for reloads.
`?file=<path>` opens a schematic straight away; otherwise the last one opened is reopened.

## Using it

The window has the component library on the left, the schematic in the middle, and the properties of the selection
(or of the schematic itself, with nothing selected) on the right.

### Placing and wiring

| Action | Does |
|---|---|
| Click a component in the library | Places it: a gray ghost follows the pointer, and a click places a copy and selects it |
| Ctrl+click while placing | Places a copy and keeps placing |
| Left, Right, Up, Down while placing | Rotate or flip the ghost |
| Wire button, W, or the library's wire | Wire tool: drag to draw an L-shaped wire along the pointer's path |
| Ctrl while releasing a wire | Keeps the wire tool for another |
| Escape | Leaves the tool |
| G, P, R, C, L, T | Place a ground, rail, resistor, capacitor, inductor or label |

A component whose name is taken is renamed, as LiveSPICE renames it (R1 becomes the first free R number). A wire
joins what it meets as in LiveSPICE: overlapping wires merge, and it connects wherever it ends on a wire or passes
through a terminal, but not where it merely crosses another wire.

LiveSPICE's own Ctrl shortcuts (Ctrl+G, Ctrl+P, Ctrl+R, Ctrl+F, Ctrl+L, Ctrl+H) work too. Browsers keep Ctrl+W and
Ctrl+T for themselves, so the plain letters are the dependable ones.

### Selecting and editing

Selecting and moving follow LiveSPICE's selection tool:

| Action | Does |
|---|---|
| Click an element | Selects it alone |
| Ctrl+click | Toggles it, keeping the rest |
| Click empty space | Clears the selection (with Ctrl, keeps it) |
| Drag from empty space, or from an unselected element | Rubber band: selects everything it touches (with Ctrl, adds to the selection) |
| Double-click a symbol | Adds every symbol of its type |
| Drag a selected element | Moves the selection in 5-unit grid steps, as one undo step |
| Right-click or Escape during a drag | Cancels it |
| Left, Right arrows | Rotate the selection 90° counter-clockwise, clockwise, about its centre |
| Up, Down arrows | Flip the selection over its centre line |
| Escape | Clears the selection |
| Delete | Deletes the selection |
| Ctrl+X, Ctrl+C, Ctrl+V | Cut, copy, paste, in LiveSPICE's clipboard format, so copies go to and from the desktop app. Pasted elements keep their positions, and clashing names are renumbered |
| Ctrl+A | Selects everything |
| Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z) | Undo, redo |
| Ctrl+S | Saves, asking before overwriting a file that changed on disk |
| Ctrl+O | Open… browses the server's files |
| Wheel | Zooms about the pointer |
| Middle drag, or Space + drag | Pans |
| F or 0, + and − | Fit, zoom in, zoom out |

The title shows • while there are unsaved changes, and the page asks before closing them.

### Properties

The panel shows the properties the selected components have in common, by category. A property whose values differ
is blank. With several selected, a drop-down above narrows editing to one of them. A value is checked by the server
when you press Enter or leave the field, and stored as the component would hold it ("4.7k" becomes "4.7 kΩ"). A value
that doesn't parse is outlined in red, with the reason in the status bar, and so is a name another component already
has. Each change is one undo step.

Flipping works as in LiveSPICE, including a quirk: a symbol turned 90° flips about its own axis, which is then
vertical, so after a flip its terminals can come off their wires. Rotating by 180° first, or flipping before
rotating, avoids it.

## Layout

| File | What it does |
|---|---|
| `src/protocol.ts` | The API's message, document and layout types |
| `src/client.ts` | The WebSocket client: requests by id, events |
| `src/geometry.ts` | LiveSPICE's symbol mapping and point-on-wire test, exactly |
| `src/layouts.ts` | Symbol layouts from the server, cached by type, properties and body |
| `src/render.ts` | Draws a document as SVG, as LiveSPICE draws it |
| `src/view.ts` | Zoom and pan |
| `src/editor.ts` | The document, its selection, and the selection, symbol and wire tools |
| `src/edits.ts` | Edits (move, rotate, flip, add and remove, properties), naming, and the undo history |
| `src/wires.ts` | The wire tool's routing and joining, ported from LiveSPICE's SchematicEditor |
| `src/library.ts` | The component library panel |
| `src/properties.ts` | The properties panel |
| `src/fileDialog.ts` | The Open dialog, on `fs.list` and `fs.recent` |
| `src/main.ts` | The page: toolbar, status, opening and saving files |
| `static/` | `index.html` and `style.css`, copied to `dist/` |
