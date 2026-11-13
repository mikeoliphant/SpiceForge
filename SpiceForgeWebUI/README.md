# SpiceForgeWebUI

SpiceForge's web front end: plain TypeScript with no runtime dependencies, bundled by esbuild and served by
`SpiceForgeServer` from the same origin as its WebSocket API (see `DesignDocs/WebSocketApi.md`).

It opens schematics in tabs and draws them: LiveSPICE's symbols from the server's catalogue layouts, wires, and
terminals (red when unconnected). You can:
- place components from the library;
- draw wires;
- select, move, rotate and flip elements;
- delete, cut, copy and paste them;
- edit properties;
- undo and redo;
- create new schematics, save, Save As, and export the drawing as SVG or PNG;
- find parts by name;
- simulate, with pots, switches, a test signal at the Input, and probes on a scope.

## Building

Node.js is needed to build, not to run: it runs the TypeScript compiler (type checking) and esbuild (bundling). The
result is static files in `dist/`.

```
npm install
npm run build      # type-check, then bundle into dist/
npm run watch      # rebuild on every change (no type check; run `npm run check` for that)
npm test           # the conformance test, against a running server (see below)
```

## Running

Start the server, which finds `dist/` in the repository while developing (or `wwwroot` beside itself, or
`--webRoot <folder>`), and open the address it prints:

```
dotnet run -c Release --project SpiceForgeServer
# Front end: http://127.0.0.1:8370/?token=...
```

Build it in Release: a Debug build runs SpiceForge's audio code without JIT optimizations, and simulates four to five
times slower (the SD-1 at 25 to 27% of real time, against 6% in Release). It says so when it starts.

`--open true` opens it in the default browser, and `--open app` in a window of its own (Edge's or Chrome's app mode),
stopping the server a few seconds after the window closes. The page takes the token from its address and keeps it for
reloads. `?file=<path>` opens a schematic straight away; otherwise the last one opened is reopened.

## Packaging

`SpiceForge.Package` builds the packages into `artifacts/`: the server, self-contained (no .NET to install), with this
editor, LiveSPICE's component libraries and its example schematics. They start the editor in a window of its own.

```
dotnet run -c Release --project SpiceForge.Package [-- --version 0.1.0] [--rid win-x64,linux-x64] [--skipWeb]
```

- **Windows:** `SpiceForge-<version>-win-x64.zip`, portable, and `SpiceForge-<version>-setup.exe`, built when
  [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed. The installer adds a Start menu entry, copies the
  examples to `Documents\LiveSPICE\Examples` (keeping any already there), and installs for the current user or for
  everyone. The server has no console window there, and logs to `server.log` in its settings folder.
- **Linux:** `SpiceForge-<version>-linux-x64.tar.gz`. Unpack it and run `SpiceForge/SpiceForgeServer`.
- **macOS:** build from source for now (`dotnet run`, above). Distributing it needs Apple's signing and notarization,
  and macOS audio needs a CoreAudio driver SpiceForge doesn't have yet (only JACK).

Packages are unsigned: Windows SmartScreen asks before the installer or the zip's server runs for the first time.

On GitHub, the **Build** workflow (`.github/workflows/build.yml`) runs on pull requests, or by hand: it builds the
solution (Windows), runs `SpiceForge.Validate server` on Windows and Linux, and `processor` on Linux. The **Release**
workflow, run by hand with a version, runs the server check, builds the three packages, and creates a GitHub release
tagged `v<version>` with them attached and notes generated from the commits.

The app's icon is `Assets/Icon/SpiceForge.svg`. `node Assets/Icon/make-icon.mjs` renders it (with headless Chrome or
Edge) into the `.ico` the server's executable and the installer carry, and the PNGs this editor uses as its favicons
(which its own window shows) and the Linux package includes as `spiceforge.png`.

## Using it

The window has the component library on the left, the schematic in the middle, and the properties of the selection
(or of the schematic itself, with nothing selected) on the right.

### Themes

The Theme menu at the right of the toolbar picks the colours: System (follows the operating system's light or dark
setting), Light (LiveSPICE's look), Dark, Dim, Paper, or High contrast. The choice is kept by the server with your other
client settings, so it follows you to another browser, and in this browser, so the page opens in it without a flash.

Each theme is one block of colour tokens in `static/themes.css`, covering the page and the schematic: the canvas, text,
wires, terminals, selection, and the ten colours symbols are drawn in. The drawing itself names no colours, only
classes (`s-red` for a red stroke, `terminal unconnected`), so a new theme needs no code.

### Finding, exporting, and component folders

- **Find (Ctrl+F)** looks for parts by name or part number as you type, selects the first and brings it into view;
  Enter and Shift+Enter step through the rest.
- **Export…** saves the drawing as an SVG or PNG image, in the theme's colours, without selection or hover marks.
- **The library** shows each component's icon and name, as LiveSPICE's does. Its ▦ button switches to a compact view
  of icons alone (names in the tooltips), and the choice is remembered.
- **Drag the library's or the properties' edge** by the schematic to resize that panel (remembered); double-click the
  edge for its default width.
- **Drag** a component from the library onto the canvas to place it where it's dropped.
- **The library's … button** lists the component folders, and adds or removes your own. LiveSPICE component libraries
  (`.xml`) in them appear in the library for every window, as soon as they're added.

### Files and tabs

| Action | Does |
|---|---|
| New | A new, untitled schematic in a new tab |
| Open… (Ctrl+O) | Browses the server's files; for a file already open, asks whether to open another copy (a tab of its own, saving to the same file) or reload the open one from disk |
| Save (Ctrl+S) | Saves, asking before overwriting a file that changed on disk; an untitled schematic goes to Save As |
| Save As… (Ctrl+Shift+S) | Saves under a new name or folder, asking before replacing a file |
| Click a tab, × or middle-click | Switches to it, or closes it (asking if it has unsaved changes) |

Browsers keep Ctrl+N and Ctrl+W for themselves, so New and closing a tab have no shortcuts.

Each tab keeps its own undo history, selection and view. The page reopens the tabs that were open, and asks before
closing with unsaved changes. When an open file changes on disk, a banner offers to reload it, or to keep your
version. Saving then asks before overwriting.

### Placing and wiring

| Action | Does |
|---|---|
| Click a component in the library | Places it: a gray ghost follows the pointer, and a click places a copy and selects it |
| Ctrl+click while placing | Places a copy and keeps placing |
| Left, Right, Up, Down while placing | Rotate or flip the ghost |
| Wire button, W, or the library's wire | Wire tool: drag to draw an L-shaped wire, its first leg in the direction the drag starts |
| Ctrl while releasing a wire | Keeps the wire tool for another |
| Escape | Leaves the tool |
| G, P, R, C, L, T | Place a ground, rail, resistor, capacitor, inductor or label |

A component whose name is taken is renamed (R1 becomes the first free R number). A wire joins what it meets:
overlapping wires merge, and it connects wherever it ends on a wire or passes through a terminal, but not where it
merely crosses another wire.

LiveSPICE's own Ctrl shortcuts (Ctrl+G, Ctrl+P, Ctrl+R, Ctrl+F, Ctrl+L, Ctrl+H) work too. Browsers keep Ctrl+W and
Ctrl+T for themselves, so the plain letters are the dependable ones.

### Selecting and editing

Editing started from LiveSPICE's editor and keeps what works there (the arrow keys, Escape, double-click). Where its
behaviour didn't make good sense, this differs. The file format is the one thing it never departs from: see
`DesignDocs/WebSocketApi.md`, Editing.

| Action | Does |
|---|---|
| Click an element | Selects it alone (in a larger selection, narrows the selection to it) |
| Ctrl+click | Toggles it, keeping the rest |
| Click empty space | Clears the selection (with Ctrl, keeps it) |
| Drag an element | Selects it, if it wasn't, and moves the selection with it, in 5-unit grid steps, as one undo step |
| Drag from empty space, or with Ctrl | Rubber band: selects everything it touches (with Ctrl, adds to the selection) |
| Double-click a symbol | Adds every symbol of its type |
| Right-click or Escape during a drag | Cancels it |
| Left, Right arrows | Rotate the selection 90° counter-clockwise, clockwise, about its centre |
| Up, Down arrows | Flip the selection over its centre line; rotated parts flip properly and stay connected |
| Escape | Clears the selection |
| Delete | Deletes the selection |
| Right-click | A menu: cut, copy, paste here, delete, rotate, flip, select all, undo, redo (on an unselected part, it selects it first) |
| Hover a wire | Its node, by name (GND) or number, and what it connects |
| Ctrl+X, Ctrl+C, Ctrl+V | Cut, copy, paste, in LiveSPICE's clipboard format, so copies go to and from the desktop app. A paste lands under the pointer (from the toolbar, just below and right of the original), and clashing names are renumbered |
| Ctrl+A | Selects everything |
| Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z) | Undo, redo |
| Ctrl+S | Saves, asking before overwriting a file that changed on disk |
| Ctrl+O | Open… browses the server's files |
| Wheel | Zooms about the pointer |
| Middle drag, or Space + drag | Pans |
| F or 0, + and − | Fit, zoom in, zoom out |

### Properties

The panel shows the properties the selected components have in common, by category. A property whose values differ
is blank. With several selected, a drop-down above narrows editing to one of them. A value is checked by the server
when you press Enter or leave the field, and stored as the component would hold it ("4.7k" becomes "4.7 kΩ"). A value
that doesn't parse is outlined in red, with the reason in the status bar, and so is a name another component already
has. Each change is one undo step.


### Simulating

Simulate (F5) runs the schematic shown on the server; Shift+F5 or Stop ends it. Pressing F5 again starts over. A panel
opens under the canvas. Drag its top edge to make it taller or shorter (the browser remembers the height), or
double-click the edge to fit it to the controls:

| Part | Does |
|---|---|
| Input | What drives the Input: one of the audio device's open inputs, a preset signal in volts, or any expression in `t` (Enter applies it) |
| Audio | The device it plays on, its rate, buffer and round-trip latency; Configure… opens the audio settings |
| Out | A row per open output channel: what it plays, and its level. Pick, then click a two-terminal component (a resistor, capacitor, diode…) to play the voltage across it, in volts; Ctrl+click adds more to the sum. Speakers puts the channel back |
| In, Out | Level meters against the Input's and Speakers' full scale; then the processing load |
| Pots, switches | Sliders and buttons in the panel, and knobs and buttons on the schematic itself: drag a knob up or down, or scroll over it; double-click for the schematic's position; click a switch for its next position |
| Engine | Oversampling, Newton's iteration cap and resampling; changing them rebuilds |
| Probe | Click wires or terminals to clip probes on (Esc to stop); each gets a numbered flag in its colour |
| Scope | The probes' voltages, triggered on the first so a steady wave stands still, with peak-to-peak and frequency; time and volts per division (Auto fits them), and Freeze |

The load shows how much of real time the simulation takes, and how many Newton steps hit the iteration cap in the
last second: if that stays above zero, raise the cap.

While it runs, switches are drawn at their live positions and pots show their live wiper values, without changing
the file: Stop draws them as saved again. Knobs and switch buttons go beside their symbols, wherever is clearest of
wires, labels and other symbols.

Each schematic's simulation setup (its probes, the scope's scales, the input, and the pot and switch positions) is
remembered in the browser and put back the next time it's simulated. Reloading the page doesn't stop a simulation:
the server carries on, and the page picks it up again once the schematic is open.

Editing the simulated schematic updates the simulation as you go: it rebuilds a moment after the last change, and
keeps running the last version that built while an edit leaves it broken.

### Audio

Audio… (in the toolbar, or Configure… in the simulation panel) chooses where simulations play: an ASIO device, the
JACK server (whose channels are other programs' ports, such as `system:capture_1`, to connect SpiceForge's to; its
client stays in the graph while JACK is chosen, so connections made in a patchbay last, and the dialog shows each
port's connections as they are), or None, the server's own clock, where nothing plays and the output only drives the Out meter and the scope. Choose the
device's input and output channels, its rate and buffer size (or the device's own), or open the driver's control
panel. Test loops the inputs to the outputs, with level meters and a scope of the summed input. OK applies and saves
the settings, and moves a running simulation to them. A device that can't be opened (a USB interface that isn't
plugged in) leaves simulations running on the clock, and the panel says why.

## The conformance test

`npm test` checks the editor's contract with the file format: on every schematic in LiveSPICE's `Tests` folder, the
client's connectivity must equal the server's `circuit.check`, as loaded and after random rotations, flips, moves,
wires and deletions made with the editor's own functions. It needs a running server, given by `SPICEFORGE_HOST`
(`ws://127.0.0.1:port`), `SPICEFORGE_TOKEN` and `SPICEFORGE_TESTS` (the folder). `SpiceForge.Validate server` starts a
server and runs it.

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
| `src/wires.ts` | The wire tool's routing, and joining (LiveSPICE's AddWire) |
| `src/connectivity.ts` | Which terminals and wires form each node: LiveSPICE's rules, part of the contract |
| `test/conformance.ts` | The conformance test |
| `src/library.ts` | The component library panel |
| `src/panelWidth.ts` | Resizing the side panels by dragging their edges |
| `src/properties.ts` | The properties panel |
| `src/fileDialog.ts` | The Open and Save As dialogs, on `fs.list` and `fs.recent` |
| `src/theme.ts` | The colour theme: the picker's choice, kept locally and in the server's client settings |
| `src/simulation.ts` | The simulation panel: controls, input, meters, the probe tool and flags, live updates |
| `src/componentFolders.ts` | The component folders dialog |
| `src/scope.ts` | The scope: binary frames into per-probe history, triggered and drawn on a canvas |
| `src/audio.ts` | The audio dialog: driver, device, channels, rate and buffer size, and the test |
| `src/main.ts` | The page: toolbar, status, opening and saving files |
| `static/` | `index.html`, `themes.css` and `style.css`, copied to `dist/` |
