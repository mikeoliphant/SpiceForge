# WebSocket API

The protocol between a local web front end (TypeScript) and a C# server that does the audio I/O and runs the
simulation. It covers every feature in `UISpec.md`; a table at the end maps each feature to the client or to the
messages that serve it.

Version 1, draft.

## Principles

- **The client owns the document.** The schematic being edited, its undo history, selection, connectivity, wire
  joining, naming and all drawing live in the browser. Editing never waits on the server, and keeps working while the
  server rebuilds a simulation or restarts.
- **The server is an engine, a catalogue and a file system.** It does what needs LiveSPICE's C# code or the machine:
  - describing the component types and their symbols;
  - checking property values;
  - converting between `.schx` files and the document format;
  - building circuits, running simulations and driving the audio device;
  - keeping settings and the log.
  It holds no document state apart from the copy each simulation was started from.
- **Geometry is LiveSPICE's; artwork is the client's.** A `.schx` file stores where each symbol is, not where its
  terminals are. Connectivity therefore depends on every component's terminal positions matching LiveSPICE's
  exactly, so the server's catalogue is the authority on terminals. How a symbol looks is up to the client: it can
  draw the server's default artwork or its own.
- **A simulation runs on the document it was given.** The client sends the document to start a simulation, and sends
  it again to apply later edits.
- **Audio never waits on the socket.** Control changes apply from the next audio block. Streamed data is dropped, not
  queued, when the client falls behind.

## Transport

- **Endpoint:** one WebSocket at `ws://127.0.0.1:<port>/api?token=<token>`. The server binds to the loopback interface
  only.
- **Authentication:** any web page can try to open a WebSocket to localhost, and this API reads and writes files and
  drives the audio device, so every connection must present the token. The server generates it at startup and opens
  the front end's URL with it (or prints it). It also rejects an `Origin` other than its own front end's.
- **Frames:** text frames carry JSON messages (UTF-8). Binary frames carry streamed samples (see Binary frames).
- **Clients:** several may connect at once. Every client receives every event, but documents aren't shared between
  them: each client has its own.
- **Handshake:** the first message on a connection must be `hello`. Anything else gets `bad_request`, and the
  connection is closed.

## Messages

### Requests and responses

```json
→ { "op": "file.read", "id": 12, "args": { "path": "C:\\Users\\me\\Documents\\LiveSPICE\\Rat.schx" } }
← { "re": 12, "result": { "document": { "...": "..." }, "modified": "2026-11-10T21:14:03Z" } }
← { "re": 12, "error": { "code": "io_error", "message": "File not found.", "details": { "path": "..." } } }
```

- Requests on one connection are handled in order, and their responses come back in that order.
- **No id, no response.** A request without an `id` is fire-and-forget: it gets no response, and an error comes back
  as an `error` event. This is meant for high-rate updates such as dragging a knob.
- Omitted arguments take their documented defaults. Unknown arguments are ignored, so the protocol can grow.

### Events

```json
← { "event": "sim.state", "data": { "sim": "s2", "state": "running" } }
```

## Conventions

- **Coordinates:** schematic units, integers, x to the right and y down. A point is `[x, y]`. LiveSPICE's grid is 5
  units, but the server doesn't enforce it.
- **Symbol-local coordinates** (in layouts) are numbers, with y up; see Mapping symbols.
- **Ids:**
  - Elements: element ids belong to the client (any string, unique within a document). The server echoes them back
    when it refers to elements, and never stores them in files. Documents the server produces (`file.read`,
    `schx.fromXml`) come with provisional ids `e1`, `e2` and so on, which the client may keep or replace.
  - Simulations and probes: the server assigns ids such as `s2` and `p1`. A probe also has a small integer `stream`,
    which identifies its samples in binary frames.
- **Property values** are strings, exactly as LiveSPICE's type converters write them in invariant culture, and as
  `.schx` files store them: `"10 kΩ"`, `"100 nF"`, `"0.5"`, `"Logarithmic"`, `"true"`. The ohm in them is U+2126
  (OHM SIGN), as LiveSPICE writes it, not the Greek capital omega (U+03A9).
- **Enumerations** are strings. Edge colours are `wire`, `black`, `gray`, `red`, `green`, `blue`, `yellow`, `cyan`,
  `magenta` and `orange`. Log levels are `error`, `warning`, `info` and `verbose`.
- **Levels and gains:** levels are linear peaks, 1.0 being full scale. Gains are in dB.
- **Paths** are absolute, in the server's native form. **Times** are ISO 8601, in UTC.

## The document

The client's document format mirrors `.schx`, one-to-one:

```json
{
  "circuit": { "Name": "", "PartNumber": "", "Description": "" },
  "elements": [
    { "id": "e1", "kind": "symbol", "position": [-500, 150], "rotation": 0, "flip": false,
      "component": { "type": "Circuit.Input, Circuit", "properties": { "Name": "V1", "V0dBFS": "1 V" } } },
    { "id": "e2", "kind": "wire", "a": [-500, 170], "b": [-500, 200] }
  ]
}
```

- **`circuit`** holds the schematic's own properties: how it appears when used as a subcircuit.
- **`rotation`** counts 90° turns counter-clockwise. It's any integer, as files store it; it means the same modulo 4.
- **`component.type`** is the .NET type's full name and assembly name. Files store the full assembly-qualified name;
  the server maps between the two.
- **`component.properties`** holds only the properties a file would store, those that differ from their defaults.
  The client fills in the rest from the type's descriptors.
- **`component.body`** (optional) is an opaque string, which the client keeps and sends back unchanged. It carries the
  embedded contents of a subcircuit component.
- **`component.unresolved`** (set by the server) means the type couldn't be loaded, a plugin type that's missing, say.
  It carries a `message`. The client draws a placeholder from the layout the server provides, and must keep the
  element, which the server writes back to the file as it was read. An unresolved component's `type` is the full
  assembly-qualified name from the file, so it's written back exactly.
- **`kind: "raw"`** is any element that's neither a symbol nor a wire, kept as its `xml` (a string) and written back
  unchanged. LiveSPICE's own files have none; the kind exists so nothing a file holds is lost.

### Connectivity

The client works out the nodes itself, for drawing and for its own use. The rules are LiveSPICE's
(`Circuit.Schematic`):

- **Wires form nodes.** Two wires are connected when an end of either lies on the other: at its end, or anywhere
  along it (a T). Wires that only cross aren't connected. Connection is transitive, and each connected set of wires
  is one node.
- **A terminal connects to a node through a wire.** A symbol terminal connects to the node of any wire that passes
  through its mapped point, at an end or along it. **Two terminals that touch, with no wire there, are not
  connected.** A terminal with no wire through it is unconnected; it's drawn red, and building warns about it.
- **Named wires join nodes by name.** Every node touched by a Named Wire takes that wire's `WireName`, so all the
  Named Wires (and Grounds, whose name is `GND`) with the same name are one node.
- **Point tests are exact, in integers:**
  - `x` is on the segment from `a` to `b` when it's inside their bounding box and on their line:
    `(b.x − a.x)·(x.y − a.y) == (x.x − a.x)·(b.y − a.y)`.

`circuit.check` reports the server's node assignment, for testing the client's against it.

### Editing

**The contract and the editor are separate.** The client must keep compatibility with the schematic file format,
but its editing behaviour is its own, chosen for making good sense rather than to match LiveSPICE's desktop editor.
The contract is:

- **The file format:** documents go to and from `.schx` losslessly (the server does the conversion).
- **Symbol geometry:** each terminal lies where `catalog.layout` and the mapping above put it.
- **Connectivity:** the client's nodes are the server's (`circuit.check`, which runs LiveSPICE's own
  `Circuit.Schematic`).

`SpiceForge.Validate server` checks the contract. It runs `SpiceForgeWebUI`'s conformance test, which compares the
client's connectivity with `circuit.check`'s on every test schematic, as loaded and after random rotations, flips,
moves, wires and deletions.

**The editor's own rules**, as `SpiceForgeWebUI` implements them:

- **Rotating** a set of elements by `dt` quarter turns about the point `c`. For every symbol position and wire end,
  with `cos` and `sin` of dt·90° (each 0 or ±1) and `d = p − c`:

  ```
  p' = round( [ d.x·cos + d.y·sin,  −d.x·sin + d.y·cos ] + c )
  ```

  A symbol's `rotation` also increases by `dt`.
  - **The centre `c`** is the midpoint of the elements' bounds, in integers (truncating), snapped to the grid.
  - **Rounding** here and in snapping is to the nearest integer, halves to even.
- **Flipping** over the line y = `c.y`: every symbol position and wire end gets `y += round(2·(c.y − y))`. Each symbol's
  `rotation` negates and its `flip` toggles. A symbol maps by R(r)·S(s), so its mirror image is
  F·R(r)·S(s) = R(−r)·S(−s). LiveSPICE only toggles `flip`. That's the same at even rotations, but at odd ones it
  mirrors the part the wrong way, off its wires.
- **Wire joining** keeps LiveSPICE's `SchematicEditor.AddWire`, because it keeps the drawing tidy. The path is added
  segment by segment. For each segment from A to B:
  1. Find the existing wires collinear with it and overlapping it, and replace them and the new segment with their
     union, from a to b.
  2. Split the union at every point between a and b where an element has a terminal: a symbol terminal, or the end
     of a wire that isn't collinear with it. Also split at A or B where either lies on a crossing wire that the other
     doesn't.
  3. Add one wire per piece.

  The whole path is one undo step. The splitting is tidying, not meaning: a wire ending anywhere on another connects
  either way.
- **Routing a drag** into an L-shaped path: the first leg follows the direction the drag started in.
- **Names:** when a component is added whose `Name` is already used, it's renamed with the letters of its name plus
  the first free number (`R1` becomes `R4`), as LiveSPICE's component collection would rename it anyway. Building
  fails on duplicate names, so the client also refuses a rename that would clash.

## Symbols

### Layout

A component's symbol, in its own coordinates, from LiveSPICE's `Component.LayoutSymbol()`:

```json
{
  "bounds": { "min": [-10, -20], "max": [10, 20] },
  "terminals":  [ { "name": "A", "at": [0, 20] }, { "name": "B", "at": [0, -20] } ],
  "lines":      [ { "edge": "black", "a": [0, 20], "b": [0, 12] } ],
  "rectangles": [ { "edge": "black", "a": [-4, -12], "b": [4, 12], "fill": false } ],
  "ellipses":   [ { "edge": "black", "a": [-8, -8], "b": [8, 8], "fill": true } ],
  "curves":     [ { "edge": "black", "points": [[0, 0], [2, 3], [4, 0]] } ],
  "arcs":       [ { "edge": "black", "center": [0, 0], "radius": 6, "start": 0.0, "end": 3.1416,
                    "direction": "clockwise" } ],
  "texts":      [ { "text": "R1", "at": [6, 0], "h": "near", "v": "center", "size": "normal" } ]
}
```

- **`terminals` and `bounds` are binding.** Connectivity and hit-testing must use them.
- **The rest is the default artwork**, for a client that doesn't draw the type itself:
  - lines with edge `wire` are the leads;
  - angles are in radians;
  - text alignments are `near`, `center` or `far`, and sizes `small`, `normal` or `large`.
- **When it changes:** a layout is a function of the component's type and properties. Some layouts depend on property
  values: a label shows `Name`, a switch draws its contact at its `Position`, and a subcircuit's terminals come from
  its contents. The client caches layouts by type, properties and body, and asks for a new one (`catalog.layout`)
  when any of those change.

### Mapping symbols

A local point (x, y) maps to the schematic like this (LiveSPICE's `Symbol.MapToGlobal`), with rotation r, `cos` and
`sin` of r·90°, and position (px, py):

```
y1 = flip ? y : −y
X  = x·cos + y1·sin + px
Y  = y1·cos − x·sin + py
```

- **Terminals and bounds** map this way.
- **A symbol's schematic bounds** are the bounds of its four mapped corners.

**To match LiveSPICE's look with the default artwork:**
- **Shapes:**
  - Shapes map point by point.
  - An arc's sweep reverses when the mapping mirrors, which is when its determinant is negative.
  - Strokes are 1 unit wide, with round caps.
  - `fill` fills a shape with its edge colour.
  - Edge colours: `wire` is dark blue and `green` is lime; the rest are the colours they name.
- **Text** stays upright. Let L be the mapping without its translation, and (h, v) the alignments as fractions
  (`near` 0, `center` 0.5, `far` 1). For text of measured width w and height t, at `p = map(at)`:

  ```
  d1 = L·(−h, 1 − v)  scaled by (w, t)
  d2 = L·(−(1 − h), v) scaled by (w, t)
  box.left = min(p.x + d1.x, p.x − d2.x)
  box.top  = min(p.y + d1.y, p.y − d2.y)
  ```

  Normal text is 12 units, small 6 and large 18, in black, in a faint box (black at 1/8 opacity, 0.2 units).
- **Terminals** are 2-unit squares, red when unconnected and wire-coloured when connected.
- **Wires** are 1-unit dark blue lines, with 1.8-unit squares at their ends.
- **Selection:** a selected symbol gets a dashed DodgerBlue box, and a highlighted one a dashed gray box.
- **Colours** above are LiveSPICE's, and the file format's edge names (`red`, `wire`...) are what a layout carries.
  A client is free to map them to its own palette: SpiceForgeWebUI's themes do.

## Session and settings

| Op | Args | Result |
|---|---|---|
| `hello` | `protocol` (1), `client` (a name) | `protocol`, `server: {name, version}`, `capabilities`, `simulationDefaults`, `folders: {user, components}` |
| `settings.get` | | `recentFiles`, `audio`, `logVerbosity`, `simulationDefaults`, `client` |
| `settings.set` | any of `logVerbosity`, `simulationDefaults`, `client` | the settings |

- **`capabilities`** says what this server supports:
  - `simulation` and `audio`: whether those operations are implemented. While they're false, `sim.*` and `audio.*`
    answer `unsupported`;
  - `maxInputs`: how many Input components a simulation can drive (see Server notes);
  - `resampling`: the choices (`["linear", "sinc"]`);
  - `oversample` and `iterationCap`: their ranges;
  - `expressions`: whether signal expressions are supported.
- **`client`** is an opaque JSON value the server stores for the front end and returns as is: the dock layout, open
  document paths, and other per-user UI state. It should stay under 64 KB.
- **Persistence:** settings persist across server runs, and `settings.changed` is broadcast when they change.

## Catalogue

| Op | Args | Result |
|---|---|---|
| `catalog.get` | | Catalogue |
| `catalog.refresh` | | Catalogue |
| `catalog.layout` | `components: [{type, properties, body}]` | `layouts: [Layout]`, in the same order |

A catalogue:

```json
{
  "categories": [
    { "name": "Common", "expanded": true, "entries": ["l1", "l2"], "children": [] },
    { "name": "Transistors", "expanded": false, "entries": ["l40"], "children": [ ] }
  ],
  "entries": {
    "l1": { "name": "Resistor", "description": "...",
            "component": { "type": "Circuit.Resistor, Circuit", "properties": { } },
            "layout": { "...": "Layout" } }
  },
  "types": {
    "Circuit.Resistor, Circuit": { "name": "Resistor", "properties": [ "...PropertyDescriptor" ] }
  }
}
```

- **Sources:** the server loads it the way LiveSPICE does:
  - the Common category;
  - the component types in its loaded assemblies;
  - the XML, SPICE and schematic libraries in its components folder and in `<user folder>/Components`.
  Folders become nested categories, and load errors are logged.
- **`entries`:** placing an entry creates a copy of its `component`, and the entry's `layout` serves for the
  placement ghost.
- **`types`** gives the property descriptors for every type that can appear in a document: for the property grid,
  for filling in default values, and for tooltips, which list the properties that differ from their defaults.
- **`catalog.layout`** works for any component, including subcircuits and unresolved types. A component the server
  can't lay out gets a placeholder box with its terminals' last known positions, if it knows them, and a `message`.
- **`catalog.changed`** is broadcast after a refresh.

A property descriptor:

```json
{
  "name": "Sweep", "category": "Potentiometer", "description": "Taper of the pot.",
  "kind": "enum", "values": ["Linear", "Sigmoid", "AntiSigmoid", "Logarithmic", "AntiLogarithmic"],
  "default": "Linear", "units": null, "readOnly": false
}
```

- **`kind`:** one of `text`, `number`, `integer`, `boolean`, `enum` or `quantity`.
- **Quantities** carry `units` (`"Ω"`, `"F"`, `"V"`), and accept SI prefixes.
- **Which properties:** only browsable, serialized ones are listed.

### Checking values

| Op | Args | Result |
|---|---|---|
| `values.check` | `items: [{type, property, value}]` (`type` may be `"circuit"`) | `items: [{ok, value} or {ok: false, message}]` |

- The server parses each value with the property's type converter, sets it on a fresh component and reads it back.
  It returns the value as the component holds it (`"4.7k"` becomes `"4.7 kΩ"`), or the reason it doesn't parse.
- **Quantities are expressions.** LiveSPICE parses a quantity as an expression, so a word like `"banana"` parses (as a
  symbol) and only fails when the circuit is solved. `values.check` rejects any symbol other than `t`, the time, which
  a source's value may use (`"Sin(2*Pi*440*t) V"`).
- The client calls this when a property edit is committed, before putting the edit on its undo stack. It's a
  convenience: the client may also parse values itself, and the server checks everything again when it builds.

## Files

| Op | Args | Result |
|---|---|---|
| `fs.list` | `path`, `extensions` (e.g. `[".schx", ".xml"]`; default all) | `path`, `parent`, `entries: [{name, kind: "dir" \| "file", modified}]` |
| `fs.recent` | | `files: [path]` (up to 20, most recent first) |
| `file.read` | `path` | `document`, `modified`, `messages: [LogEntry]` |
| `file.write` | `path`, `document`, `overwrite` (true), `expectModified` | `modified` |
| `file.watch` | `paths` | |
| `schx.toXml` | `elements` | `xml` |
| `schx.fromXml` | `xml` | `elements` |

- **`fs.list`** exists because a web page can't open native file dialogs that return paths. The client builds its Open
  and Save As dialogs on it, starting from `folders.user`.
- **`file.read`** converts a `.schx` (or XML) file to a document. `messages` reports what LiveSPICE logged while
  loading, such as unresolved types.
- **`file.write`** converts a document to `.schx` and writes it safely (to a temporary file, then a replace), as
  LiveSPICE does.
  - With `overwrite: false` it fails with `exists`.
  - With `expectModified` (the `modified` time from the last read or write) it fails with `modified_on_disk` if the
    file has changed since, so the client can ask before overwriting someone else's edit.
- **Recent files:** reading or writing a file adds it to the recent files. A file that can't be read is removed from
  them.
- **`file.watch`** sets the files this connection wants watched: those it has open. A `file.changed` event (`path`,
  `modified`) arrives when one changes on disk, and the client offers to reload.
- **Clipboard:** `schx.toXml` and `schx.fromXml` convert elements to and from LiveSPICE's clipboard format, a
  `<Schematic>` with `<Element>` children. A client that wants copy and paste to work with the desktop app uses them;
  within the client, any format will do.

## Building

| Op | Args | Result |
|---|---|---|
| `circuit.check` | `document` | `errors: [Problem]`, `warnings: [Problem]`, `nodes: [Node]`, `controls: {pots, switches}` |

- **What it does:** builds the circuit as LiveSPICE does (`Schematic.Build`), without solving it.
- **Problems:** `{ "message": "...", "elements": ["e7"] }`. Duplicate names and unresolved types are errors;
  unconnected terminals and deprecated components are warnings.
- **Duplicate names** are found in the document's own names. LiveSPICE renames a clashing component as it's added to
  a circuit (R1 to R2), so a loaded schematic never holds duplicates, though a file can, and `Schematic.Build`'s own
  check never fires on one. The nodes and controls report the renamed components.
- **Nodes:** `{ "name": "_v12", "terminals": [{ "element": "e7", "terminal": "A" }], "wires": ["e2", "e3"] }`, the
  server's node assignment. A client test can compare it with its own connectivity.
- **`controls`** lists the pots and switches a simulation would have (see Controls). The client can show them before
  starting one.

## Audio

| Op | Args | Result |
|---|---|---|
| `audio.devices` | | `drivers: [{name, devices: [{name, inputs: [name], outputs: [name], sampleRates: [Hz], bufferSizes: [samples]}]}]` |
| `audio.getConfig` | | `driver`, `device`, `inputs: [name]`, `outputs: [name]`, `sampleRate`, `bufferSize` |
| `audio.setConfig` | the same fields | the configuration as applied |
| `audio.test` | `enabled` | |

- **`audio.setConfig`:**
  - Saves the choice to the settings.
  - Moves a running simulation to the new device, rebuilding first if the sample rate changed.
  - Channels are named, as LiveSPICE stores them.
  - An unsupported `sampleRate` or `bufferSize` fails with `invalid_value`, listing what the device supports.
- **`audio.test`** opens the device and loops the sum of its inputs to every output, as LiveSPICE's Test button does.
  While it runs, the summed signal streams as stream 0 and `audio.levels` events carry its level. It fails with
  `audio_busy` while a simulation is running.

Events:

- `audio.state`: `open`, `driver`, `device`, `sampleRate`, `bufferSize`, `error`. Sent when the device opens,
  closes, changes rate, or fails.
- `audio.levels`: `inputs: [peak]`, `outputs: [peak]`, at about 10 Hz, during a test.

## Simulation

At most one simulation runs on the audio device at a time.

**Without an audio device** (SpiceForgeServer so far: `capabilities.audio` is false), a simulation runs on its own
clock instead, in real time at 48 kHz, in blocks of 256 samples. The Input is driven by an expression (a 220 Hz sine
at half its full scale until the client sets one), and the output only drives the meters; `outputs` is empty, and
`sim.setOutput` and channel sources answer `unsupported`. If processing falls more than a quarter second behind, the
clock skips ahead and `sim.stats` counts a dropout. Everything else is as below.

### Lifecycle

| Op | Args | Result |
|---|---|---|
| `sim.start` | `document`, `title`, `settings` (default `simulationDefaults`), `replace` (false) | Simulation |
| `sim.update` | `sim`, `document` | |
| `sim.configure` | `sim`, `settings` | |
| `sim.restart` | `sim` | |
| `sim.get` | `sim` | Simulation |
| `sim.stop` | `sim` | |

- **`sim.start`:**
  - Builds the document and starts it on the configured audio device. With no channels configured, it runs silently,
    as LiveSPICE does.
  - The result comes back at once, in state `building`.
  - It fails with `audio_busy` if another simulation is running, unless `replace` is set, which stops that one first.
  - A document that doesn't build fails with `build_failed`, with the problems (as `circuit.check` gives them) in
    `details`.
- **`sim.update`** replaces the simulation's document with a newer one, after edits, and rebuilds. Controls keep their
  positions where a pot or switch of the same name remains, and probes re-attach to their targets where those remain
  (see Probes). This replaces LiveSPICE's rule that a simulation only ever sees the schematic as it was at Simulate;
  a client that wants that rule simply doesn't send updates.
- **`sim.configure`** changes the settings and rebuilds. **`sim.restart`** rebuilds as is, from the DC operating
  point.
- **Rebuilds** happen off the audio thread. While one runs, audio continues on the previous build, then switches over
  at a block boundary. Only the first build plays silence.

Settings:

```json
{ "oversample": 4, "iterationCap": 32, "resampling": "sinc", "potSmoothingMs": 5, "switchSmoothingMs": 5 }
```

These are SpiceForge's `SchematicProcessorOptions`. Note the plugin's defaults (4×, a cap of 32, sinc) against
LiveSPICE's (8×, 8, linear).

A simulation:

```json
{
  "sim": "s2", "title": "Rat", "state": "running", "settings": { "...": "..." },
  "sampleRate": 48000, "latency": 40,
  "pots":     [ { "name": "Distortion", "position": 0.5, "default": 0.5, "taper": "Logarithmic", "elements": ["e12"] } ],
  "switches": [ { "name": "Bright", "position": 0, "positions": 2, "default": 0, "elements": ["e30", "e31"] } ],
  "inputs":   [ { "element": "e1", "name": "V1", "source": { "channel": "In 1" } } ],
  "outputs":  [ { "channel": "Out 1", "signal": { "speakers": true } } ],
  "gains":    { "inputDb": 0, "outputDb": 0 },
  "probes":   [ "...Probe" ]
}
```

- **`latency`** is in samples: SpiceForge's resampling delay, which LiveSPICE doesn't have.
- **`pots` and `switches`** are named after the pot or switch, or after its group: a group is one control, as in
  `SchematicProcessor`. Each lists its elements by the client's ids, so the client can place a knob or button on each
  symbol. Moving any of them moves the group.

`sim.state` events carry `sim`, `state` and, depending on the state, `progress` (0 to 1, or null), `code` and
`message`:

| State | Meaning |
|---|---|
| `building` | Building the first solution; nothing plays yet |
| `running` | Running |
| `rebuilding` | Running on the previous build while a new one is made |
| `error` | Stopped by an error, with a `code` and `message`; `sim.restart` tries again |
| `stopped` | Stopped by `sim.stop`, by a replacing `sim.start`, or because its device closed |

- **Divergence:** the server silences the output, logs it, and sends `error` with code `diverged`. It rebuilds
  automatically if the simulation had run for more than a second, as LiveSPICE does.
- **Other errors:** any other run-time failure stops the simulation with `error`.

### Controls

| Op | Args | Result |
|---|---|---|
| `sim.set` | `sim`, `pots: {name: position}`, `switches: {name: position}` | |
| `sim.click` | `sim`, `switch` | `position` |
| `sim.setInput` | `sim`, `element`, `source` | |
| `sim.setGain` | `sim`, `inputDb`, `outputDb` | |
| `sim.setOutput` | `sim`, `channel`, `signal` | |

- **`sim.set`:**
  - Positions take effect, smoothed, from the next audio block. A pot runs from 0 to 1; a switch from 0 to
    `positions − 1`.
  - Send it without an `id` while dragging a knob; the server keeps only the latest value.
  - These are live settings of the simulation, not edits to the document. A client that wants a knob's position to
    become the pot's `Wipe` in the document makes that edit itself.
- **`sim.click`** advances a switch to its next position, wrapping round.
- **Input `source`** is one of:
  - `{ "channel": "In 1" }`: an audio input;
  - `{ "expression": "Sin(2*Pi*440*t)" }`: a signal in `t`, in volts;
  - `{ "silence": true }`.
  An expression that doesn't parse fails with `invalid_value`, and the input keeps its previous source.
- **Gains** run from −40 to +40 dB.
- **Output `signal`** is one of:
  - `{ "speakers": true }`: the sum of the Speaker outputs (the default);
  - `{ "voltageOf": ["e40", "e41"] }`: the sum of these two-terminal components' voltages, for the pick tool, where
    Ctrl+click adds to the list;
  - `{ "expression": "..." }`.
  Changing a signal rebuilds the simulation.
- **`sim.controls`** is broadcast after any change, with `pots`, `switches`, `inputs`, `outputs` and `gains`, so every
  client's controls follow.

### Probes and the scope

| Op | Args | Result |
|---|---|---|
| `sim.probe` | `sim`, `target`, `color` (default: the least-used colour) | Probe |
| `sim.removeProbes` | `sim`, `probes: [id]` | |
| `sim.scope` | `sim`, `enabled` | |

A probe:

```json
{ "id": "p1", "stream": 1, "target": { "element": "e7", "terminal": "A" }, "node": "_v12", "color": "red",
  "label": "V[_v12]" }
```

- **Targets:** a probe targets a node through something the client knows: `{ "element", "terminal" }` for a
  terminal, or `{ "wire": "e2" }` for a wire. The client finds the target under the pointer, from its own
  connectivity.
- **Moving:** a second probe on a node that already has one moves that probe instead, as LiveSPICE's probe tool does.
  The client keeps where it draws each probe's flag.
- **Failure:** a target that isn't connected to a node fails with `not_found`.
- **Colours** come from LiveSPICE's order: red, green, blue, yellow, cyan, magenta, orange.
- **Samples:** a probe reads its node's voltage at the last internal step of each sample, unfiltered by the output
  resampling (so it has none of `latency`'s delay). A node the circuit doesn't solve for, such as ground, reads 0 V.
- **`sim.probes`** is broadcast with the whole list after any change. After a `sim.update`, a probe whose target is
  gone is dropped.
- **`sim.scope`** turns the sample stream on or off for this connection (off by default). While it's on, the server
  sends binary frames at up to 60 per second, each with every probe's samples since the last frame, at the device
  sample rate. The client keeps the last 0.25 s per probe and does the display work: triggering on the fundamental,
  scaling, statistics, the cursor, and note names. LiveSPICE's `Util/Frequency.cs` estimator is the reference for
  that.

### Monitoring

| Event | Data | Rate |
|---|---|---|
| `sim.levels` | `sim`, `inputs: [peak]`, `outputs: [peak]` | about 10 Hz |
| `sim.stats` | `sim`, `load` (fraction of real time), `cappedSteps`, `rejectedSettings`, `dropouts` | 1 Hz |

- **Levels** are linear peaks after the gains, each decaying over about 0.25 s. The client colours its lamps
  directly: green below 0.5, yellow below 0.75, orange below 0.95, red above.
- **`cappedSteps`** counts Newton steps that hit the iteration cap since the last report; null where it isn't
  measured (SpiceForgeServer doesn't count them while running, for speed).
- **`rejectedSettings`** is SpiceForge's count of control settings it couldn't solve.

## Log

| Op | Args | Result |
|---|---|---|
| `log.history` | `limit` (200) | `entries: [LogEntry]` |
| `log.setVerbosity` | `level` | |

A `log` event carries one entry: `{ "time": "2026-11-11T14:03:22.512Z", "level": "warning", "source": "s2",
"message": "..." }`.

- **`source`** is a simulation id, `catalog`, `file`, `audio` or `server`.
- **Verbosity** is set per connection, and saved as the default for new ones.
- **History:** the server keeps the last 1,000 entries, for a client that connects late.

## Errors

| Code | Meaning |
|---|---|
| `bad_request` | Malformed message, missing argument, or no `hello` first |
| `unknown_op` | No such operation |
| `not_found` | No such simulation, probe, control, path, catalogue entry, element or node |
| `invalid_value` | A value doesn't parse or is out of range; `details` says which |
| `io_error` | A file couldn't be read or written |
| `exists` | `file.write` onto an existing file without `overwrite` |
| `modified_on_disk` | `file.write` with an `expectModified` the file no longer has |
| `bad_document` | A document doesn't follow the format (a missing field, a type that isn't a component) |
| `build_failed` | The circuit couldn't be built or solved; `details` has the problems, and the log has LiveSPICE's messages |
| `unsupported` | The circuit needs something this server can't do, such as more Inputs than `maxInputs` |
| `audio_unavailable` | The device can't be opened |
| `audio_busy` | The device is in use by a simulation or a test |
| `diverged` | The simulation diverged (as a `sim.state` code) |
| `internal` | A server bug; the message has the exception |

An `error` event (`{ "code", "message", "op" }`) reports a failure of a request sent without an id.

## Binary frames

Scope samples, little-endian, laid out so the client can read samples through `Float32Array` views without copying:

| Offset | Type | Field |
|---|---|---|
| 0 | u8 | Frame type: 1 (samples) |
| 1 | u8 | Format version: 1 |
| 2 | u16 | Reserved (0) |
| 4 | u32 | Source: the simulation's number (the digits of `s2`), or 0 for the audio test |
| 8 | u64 | Sample index of the first sample, counted from the start of the stream |
| 16 | f32 | Sample rate (Hz) |
| 20 | u16 | Number of signals |
| 22 | u16 | Reserved (0) |
| 24 | | For each signal: u32 `stream`, u32 sample count `n`, then `n` × f32 samples |

- **Stream numbers:** a probe's `stream`, or 0 for the audio test's summed input.
- **Gaps:** the sample index lets the client notice frames that were dropped, and draw a gap rather than splice.
- **Alignment:** every block starts on a 4-byte boundary.

## Example flows

**Open a schematic.** The client reads the file, then fetches layouts for the components its catalogue entries don't
cover (subcircuits, changed properties):

```json
→ { "op": "file.read", "id": 1, "args": { "path": "C:\\...\\Rat.schx" } }
← { "re": 1, "result": { "document": { "circuit": { ... }, "elements": [ ... ] }, "modified": "...", "messages": [] } }
→ { "op": "catalog.layout", "id": 2, "args": { "components": [ { "type": "Circuit.Potentiometer, Circuit",
      "properties": { "Name": "Distortion", "Resistance": "100 kΩ", "Wipe": "0.5" } } ] } }
← { "re": 2, "result": { "layouts": [ { ... } ] } }
→ { "op": "file.watch", "id": 3, "args": { "paths": ["C:\\...\\Rat.schx"] } }
```

The file format carries no element ids, so the client assigns them as it loads the document.

**Edit a value, then save.** The client checks the new value, applies it to its document and undo stack, fetches
the symbol's new layout if it caches by properties, and later writes the file:

```json
→ { "op": "values.check", "id": 4, "args": { "items": [ { "type": "Circuit.Resistor, Circuit",
      "property": "Resistance", "value": "4k7" } ] } }
← { "re": 4, "result": { "items": [ { "ok": true, "value": "4.7 kΩ" } ] } }
→ { "op": "file.write", "id": 5, "args": { "path": "C:\\...\\Rat.schx", "document": { ... },
      "expectModified": "2026-11-10T21:14:03Z" } }
← { "re": 5, "result": { "modified": "2026-11-11T14:10:51Z" } }
```

**Simulate, turn a knob, probe a node, and apply an edit.**

```json
→ { "op": "sim.start", "id": 6, "args": { "document": { ... }, "title": "Rat" } }
← { "re": 6, "result": { "sim": "s2", "state": "building", "pots": [ ... ], ... } }
← { "event": "sim.state", "data": { "sim": "s2", "state": "running" } }
→ { "op": "sim.set", "args": { "sim": "s2", "pots": { "Distortion": 0.71 } } }
→ { "op": "sim.probe", "id": 7, "args": { "sim": "s2", "target": { "element": "e7", "terminal": "A" } } }
← { "re": 7, "result": { "id": "p1", "stream": 1, "node": "_v12", "color": "red", ... } }
→ { "op": "sim.scope", "id": 8, "args": { "sim": "s2", "enabled": true } }
← (binary frames, about 60 per second)
→ { "op": "sim.update", "id": 9, "args": { "sim": "s2", "document": { ... } } }
← { "event": "sim.state", "data": { "sim": "s2", "state": "rebuilding" } }
← { "event": "sim.state", "data": { "sim": "s2", "state": "running" } }
```

## Feature coverage

Each area of `UISpec.md`, and what serves it:

| UISpec area | Served by |
|---|---|
| Main window, menus, toolbar, shortcuts, docking | Client; dock layout saved through `settings.set` `client` |
| New, Close, Exit, unsaved-changes prompts, Save All | Client |
| Open, Save, Save As | `fs.list` for the dialogs; `file.read`, `file.write` |
| Recent files | `fs.recent` |
| Files changed on disk | `file.watch`, `file.changed`; `expectModified` on write |
| Canvas, zoom, pan, auto-scroll, selection, rubber band | Client |
| Drawing symbols, terminals, wires | Client, from catalogue layouts (terminals binding, artwork optional) |
| Tooltips | Client, from the document and type descriptors |
| Move, rotate, flip, delete, undo, redo | Client, by its own editing rules (see Editing) |
| Placing components | Catalogue `entries` and their layouts |
| Wire tool, routing, joining | Client, by its own editing rules (see Editing) |
| Cut, Copy, Paste | Client; `schx.toXml` and `schx.fromXml` for the desktop app's clipboard format |
| Component library, categories, filter, refresh | `catalog.get`, `catalog.refresh`; filtering in the client |
| Properties panel, multi-selection, schematic properties | Type descriptors; `values.check`; edits in the client |
| Connectivity | Client, by the rules above; `circuit.check` to compare |
| Audio configuration and test | `audio.devices`, `audio.getConfig`, `audio.setConfig`, `audio.test`, `audio.levels`, stream 0 |
| Simulate | `sim.start` |
| Oversample, iterations | `sim.configure` |
| Rebuild (F5) | `sim.restart` |
| Knobs, groups | Simulation `pots` with `elements`; `sim.set`; `sim.controls` |
| Switch buttons | `switches`; `sim.click` |
| Input sources | `inputs`; `sim.setInput` |
| Input and output gain | `sim.setGain` |
| Output signals, pick tool | `outputs`; `sim.setOutput` with `voltageOf` |
| Signal-level lamps | `sim.levels` |
| Probes | `sim.probe`, `sim.removeProbes`, `sim.probes` |
| Scope | `sim.scope` and binary frames; display in the client |
| Log, verbosity, clear | `log` events, `log.history`, `log.setVerbosity`; clearing in the client |
| Progress while building | `sim.state` `building` with `progress` |
| Run-time errors, divergence, sample-rate changes | `sim.state` `error`, automatic rebuild, `audio.state` |
| Settings | `settings.get`, `settings.set` |

New beyond LiveSPICE:
- editing while a simulation runs (`sim.update`), with rebuilds that keep the audio playing;
- `circuit.check` before simulating;
- reporting `latency` and `sim.stats`;
- `expectModified` on save;
- running without an audio device, from expressions, to look at circuits on the scope.

## What the client must implement

The price of owning the document, in one list:

- **The document model:** loading from and saving to the JSON format, element ids, dirty tracking, and undo and
  redo.
- **Connectivity**, by LiveSPICE's rules: part of the contract.
- **Its own editing rules** (see Editing): rotate, flip, wire joining and names.
- **Symbols:** mapping and hit-testing, and caching layouts by type, properties and body.
- **Property editing**, from the type descriptors.
- **A conformance test** of the contract. `SpiceForgeWebUI/test/conformance.ts` compares the client's nodes with
  `circuit.check`'s on all of LiveSPICE's test schematics, as loaded and after random edits. It runs in
  `SpiceForge.Validate server`. Without it, files could open with silently broken connections.

## Server notes

**Status.** `SpiceForgeServer` implements the editing side:
- session and settings;
- the catalogue, layouts and value checks;
- files, watching and the clipboard format;
- `circuit.check`;
- the log.

Audio and simulation answer `unsupported`, and `hello` reports `simulation: false` and `audio: false`.

**Running it.** It listens on 127.0.0.1, port 8370 by default ('S' is 83 and 'F' is 70 in ASCII). The settings come
from the command line, environment variables prefixed `SPICEFORGE_`, or `appsettings.json`:

| Setting | Default |
|---|---|
| `--port` | 8370 |
| `--token` | A random token each run |
| `--allowedOrigins` | None, besides the server's own (comma-separated) |
| `--settings` | `%APPDATA%/SpiceForge/server-settings.json` |
| `--userFolder` | `Documents/LiveSPICE` |

It prints the URL to connect to, with the token. `SpiceForge.Validate server` checks it: every test schematic through
the JSON document and back, and every editing operation against a running server.

What the server needs beyond today's SpiceForge:

- **Building from a document, not a path.** `SchematicProcessor` and `CircuitLoader` load from a file path. The
  server converts each JSON document to a `Circuit.Schematic` (through its XML form, the same code that writes
  files), so the loader needs to accept a `Schematic` directly.
- **Several Inputs.** `SchematicProcessor` drives exactly one Input, from the audio samples.
  - Expression sources can be generated on the server and fed in as that input's samples.
  - Several Input components need `CompiledCircuit` to take more than one input symbol. Until it does, report
    `maxInputs: 1`, and fail `sim.start` with `unsupported` on a schematic with more.
- **Probes and output signals** are extra outputs of the compiled circuit. Outputs are fixed when it's compiled, so
  adding a probe recompiles from the already-extracted system. That takes tens of milliseconds, against seconds for
  a full build, and the swap happens at a block boundary.
- **Output expressions** that aren't linear in node voltages need evaluating after the step. `voltageOf` and
  `speakers` are always linear.
- **Element ids:** the server maps the client's element ids to LiveSPICE's components (by name) and wires (by
  position) when it builds, to report problems, nodes, controls and probes in the client's terms.
- **Lossless files.** A file read and then written without edits should come back unchanged, apart from formatting:
  unresolved components and subcircuit bodies included. A round-trip test over the `Tests` folder checks it.
- **Audio drivers:** the stand-alone host's ASIO support (`AudioPlugSharpHost`) covers Windows. Which other drivers
  to support decides what `audio.devices` lists.
- **Threading:** LiveSPICE's computer algebra isn't known to be thread-safe (see LessonsLearned.md, Validation).
  Builds, layouts and checks should therefore run one at a time, on a dedicated thread.

## Open questions

- Should the client do its own value parsing (quantities with SI prefixes), or always go through `values.check`?
  Doing it locally would make property editing work while the server is down.
- Should catalogue layouts be computed once per type and reused unless a property changes, or does the client simply
  ask for every distinct (type, properties) it sees? The second is simpler, and on a local socket probably fast
  enough.
- Is one simulation at a time right? LiveSPICE allowed several windows, each opening the device, which ASIO doesn't
  permit.
- Should the scope stream be decimated on the server for slow clients, rather than dropped?
