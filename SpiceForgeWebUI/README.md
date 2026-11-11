# SpiceForgeWebUI

SpiceForge's web front end: plain TypeScript with no runtime dependencies, bundled by esbuild and served by
`SpiceForgeServer` from the same origin as its WebSocket API (see `DesignDocs/WebSocketApi.md`).

So far it opens a schematic and draws it: LiveSPICE's symbols from the server's catalogue layouts, wires, terminals
(red when unconnected), zoom and pan.

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

In the page: Open… (Ctrl+O) browses the server's files; the wheel zooms about the pointer; dragging pans; Fit (F or 0)
frames the schematic; + and − zoom.

## Layout

| File | What it does |
|---|---|
| `src/protocol.ts` | The API's message, document and layout types |
| `src/client.ts` | The WebSocket client: requests by id, events |
| `src/geometry.ts` | LiveSPICE's symbol mapping and point-on-wire test, exactly |
| `src/layouts.ts` | Symbol layouts from the server, cached by type, properties and body |
| `src/render.ts` | Draws a document as SVG, as LiveSPICE draws it |
| `src/view.ts` | Zoom and pan |
| `src/fileDialog.ts` | The Open dialog, on `fs.list` and `fs.recent` |
| `src/main.ts` | The page: toolbar, status, opening files |
| `static/` | `index.html` and `style.css`, copied to `dist/` |
