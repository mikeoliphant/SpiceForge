# SpiceForgeWebUI

SpiceForge's web front end: plain TypeScript with no runtime dependencies, bundled by esbuild and served by
`SpiceForgeServer` from the same origin as its WebSocket API (see `DesignDocs/WebSocketApi.md`).

So far it opens a schematic and draws it: LiveSPICE's symbols from the server's catalogue layouts, wires, and
terminals (red when unconnected). You can select, move, rotate and flip elements, undo and redo, and save.

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
| Ctrl+A | Selects everything |
| Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z) | Undo, redo |
| Ctrl+S | Saves, asking before overwriting a file that changed on disk |
| Ctrl+O | Open… browses the server's files |
| Wheel | Zooms about the pointer |
| Middle drag, or Space + drag | Pans |
| F or 0, + and − | Fit, zoom in, zoom out |

The title shows • while there are unsaved changes, and the page asks before closing them.

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
| `src/editor.ts` | The document, its selection and the selection tool's gestures |
| `src/edits.ts` | Edits (move, rotate, flip) and the undo history |
| `src/fileDialog.ts` | The Open dialog, on `fs.list` and `fs.recent` |
| `src/main.ts` | The page: toolbar, status, opening and saving files |
| `static/` | `index.html` and `style.css`, copied to `dist/` |
