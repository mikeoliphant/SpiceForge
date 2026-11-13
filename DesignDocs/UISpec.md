# UI spec: LiveSPICE's editor and simulation app

This enumerates the features of LiveSPICE's WPF application (`Dependencies/LiveSPICE/LiveSPICE`, with the drawing
controls in `SchematicControls`), as the baseline for a web front end driven by a C# WebSocket server that does the
audio I/O and runs the simulation. It describes what the app does today, not what the new one should do; the last
section notes, for each area, where its state would naturally live once the UI and the engine are in separate
processes.

The VST plugin (`LiveSPICEVst`) has its own, smaller UI and isn't covered here.

## Application shell

### Main window

- One main window (1200 × 800 by default) with a menu bar, a toolbar tray, a docking area and a status bar.
- The docking area (AvalonDock) holds:
  - a **Components** panel and a **Properties** panel, stacked on the left (290 px wide);
  - the **schematic documents**, as tabs, filling the rest.
- Panels can be docked, floated, auto-hidden and closed. The layout is saved when a document is closed and restored at
  startup.
- Each document tab shows the schematic's file name without its extension (or `<Untitled>`), trimmed with an ellipsis
  past 100 px, with the full path as its tooltip. The title follows Save As.
- The status bar shows a status string (`Ready` by default).
- Schematic files (`.schx`) named on the command line are opened at startup.

### Menus

| Menu | Items |
|---|---|
| File | New, Open, Save, Save As, Save All, Close, Recent Files (submenu), Exit |
| Edit | Undo, Redo, Cut, Copy, Paste, Delete, Select All |
| Simulate | Simulate, Audio Configuration |
| View | Properties, Components (checkable: show or hide the panel) |
| About | About |

### Toolbar

- File and edit: New, Open, Save, Save All, Undo, Redo, Cut, Copy, Paste.
- View: Zoom In, Zoom Out, Zoom Fit.
- Simulation: Simulate.
- Buttons and menu items carry icons and are disabled when their command can't run (Close with no document, Paste
  with nothing on the clipboard, Undo with nothing to undo, Simulate with no document).

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+N, Ctrl+O, Ctrl+S | New, Open, Save |
| Ctrl+Shift+S | Save All |
| Ctrl+Z, Ctrl+Y | Undo, Redo |
| Ctrl+X, Ctrl+C, Ctrl+V | Cut, Copy, Paste |
| Delete | Delete the selection |
| Ctrl+A | Select All |
| F5 | Simulate (in the simulation window: rebuild the solution) |
| Alt+F4 | Exit |
| Ctrl+W | Wire tool |
| Ctrl+G | Place a ground |
| Ctrl+P | Place a rail (supply) |
| Ctrl+R | Place a resistor |
| Ctrl+F | Place a capacitor |
| Ctrl+L, Ctrl+H | Place an inductor |
| Ctrl+T | Place a label |
| Escape | Leave the current tool for the selection tool |
| Left, Right | Rotate the selection, or the symbol being placed, 90° counter-clockwise or clockwise |
| Up, Down | Flip the selection, or the symbol being placed |

Placement shortcuts are listed in each component's tooltip in the library. A `Rename` command (F2) is defined but
not used anywhere.

- Pressing Enter in any text box or editable combo box commits its value and leaves the field.

## Files

- **Format:** schematics are XML (`.schx`). The open and save dialogs also offer `*.xml` and all files.
- **New** opens an empty, untitled schematic in a new tab.
- **Open** allows several files at once. Opening a file that's already open switches to its tab and offers to
  revert it if it has unsaved changes. A file that fails to open shows an error and is removed from Recent Files.
- **Save** writes to the document's file, or falls back to Save As for an untitled one. **Save As** starts in the
  document's folder (or the user folder, below). **Save All** saves each document in turn, and stops at the first
  that fails or is cancelled.
- **Recent Files** keeps the 20 most recently opened or saved files, most recent first.
- **User folder:** `Documents/LiveSPICE` is created if missing and offered as a shortcut in the file dialogs. Its
  `Components` subfolder is searched for user libraries.
- **Unsaved changes:** a document tracks whether it has edits since its last save.
  - Closing a tab with unsaved changes asks Save / Don't Save / Cancel.
  - Exiting lists every document with unsaved changes, with checkboxes, and asks Yes (save the checked ones) / No /
    Cancel.
- **External changes:** when the window is activated, documents whose files changed on disk since they were last
  opened or saved are listed, with checkboxes, and the checked ones can be reloaded.

## Schematic editor

### Canvas

- Each document is a 1600 × 1600 canvas with its origin at the centre, on a 5-unit grid. Every pointer position
  snaps to the grid.
- **Zoom:** the mouse wheel zooms by 1.1× per notch about the pointer. Zoom In and Zoom Out step by 1.5×. Zoom is
  capped at 8×, and the lower limit is the largest power of two at which the whole canvas fills the view.
- **Zoom Fit** frames the selection if there is one, otherwise all the elements with a 40-unit margin, otherwise a
  200 × 200 area around the origin.
- **Pan:** scroll bars, or drag with the middle mouse button.
- **Auto-scroll:** dragging with the left button near the edge of the view scrolls it.
- Opening a document, or resizing the view, keeps the content framed.

### Elements

A schematic holds two kinds of element:

- **Symbols:** a component placed at a position, with a rotation (in 90° steps) and a flip.
- **Wires:** straight, horizontal or vertical segments between two grid points.

Drawing:

- **Symbols:** drawn from the component's own layout: lines, rectangles (optionally filled), ellipses, polylines,
  arcs and text in three sizes, each in one of the edge colours (black, gray, red, green, blue, yellow, cyan,
  magenta, orange, or wire blue). Text is drawn with a faint outline box. The text can be hidden (the editor hides it
  on the symbol being placed).
- **Terminals:** small squares, red when unconnected and wire-coloured when connected.
- **Wires:** dark blue lines, with square ends.
- **Selection** draws a dashed blue box around a symbol and turns a wire blue. **Highlighting** (hover, or the
  rubber band) draws a dashed gray box or turns a wire gray.

Tooltips:

- Over a terminal: `Terminal '<name>'`.
- Over a symbol: the component's name in bold, then each serialized property that differs from its default, as
  `name = value`.
- Over a wire: the name of the node it belongs to.

### Tools

The editor always has one active tool.

- **Selection tool (default).** Cross cursor.
  - **Click:** selects the element under the pointer, toggling it if it's already selected. Ctrl adds to the
    selection rather than replacing it.
  - **Drag on empty space:** draws a dashed blue rubber band. Releasing selects everything it touches (Ctrl adds).
  - **Drag on a selected element** (without Ctrl): switches to the move tool. The cursor shows the move arrows when
    over a selected element.
  - **Double-click:** selects every symbol of the same component type as the one clicked.
  - **Arrow keys:** rotate and flip the selection about its centre (see the shortcuts).
  - **Hover:** highlights the element under the pointer, or, while dragging the rubber band, everything inside it.
  - **Right-click:** context menu with Select All, Cut, Copy, Paste, Delete, Undo and Redo; the element under the
    pointer is selected first.
  - **Typing other keys** moves focus to the property grid, to edit the selection.
- **Move tool.** Drags the selection by whole grid steps. The whole drag is one undo step. Right-click cancels the
  move in progress.
- **Symbol tool (placing a component).** Entered by clicking a component in the library or with its shortcut. A
  gray ghost of the symbol follows the pointer, hidden while the pointer is outside the canvas.
  - The arrow keys rotate and flip the ghost.
  - A click places a copy, with the ghost's rotation and flip, and selects it.
  - Without Ctrl, the editor returns to the selection tool after one placement; with Ctrl held, placement repeats.
  - Clicking a component with no document open creates a new document for it.
- **Wire tool.** Pen cursor. Entered from the library's Conductor (wire) entry or Ctrl+W.
  - **Drawing:** press and drag draws a preview. The route is an L-shaped path, horizontal then vertical or
    vertical then horizontal, whichever follows the pointer's path more closely.
  - **Splicing on release:** the path is merged with existing wires. Overlapping collinear wires are joined into
    one, and the result is split wherever it passes a terminal or meets another wire, so connections form at those
    points. The whole wire is one undo step.
  - Without Ctrl, the editor returns to the selection tool afterwards.
- **Escape** in any tool returns to the selection tool.

### Editing operations

- Add, remove, move, rotate (about the selection's centre, snapped to the grid) and flip (about the selection's
  horizontal centre line), each undoable.
- **Cut, Copy and Paste** use the system clipboard, carrying the selected elements as schematic XML. Pasted elements
  keep their positions and are selected.
- **Delete** removes the selection.
- **Select All** selects every element.
- **Undo and Redo** cover every edit, property changes included. A multi-element change is one step.

### Connectivity

- Nodes are formed by wires.
  - Two wires connect when an end of one lies anywhere on the other, at its end or along it. Wires that only cross
    don't connect.
  - A symbol terminal connects to the node of any wire through its point. Two terminals that touch with no wire there
    aren't connected.
  - Named wires (and grounds) with the same name share a node.
- The editor shows connection state through terminal colour and wire tooltips; there is no separate netlist view.

## Component library panel

- **Common:** a category, expanded by default, holds Wire (Conductor), Ground, Rail, Resistor, Capacitor, Inductor,
  Voltage Source, Current Source, Named Wire and Label.
- **Generic:** holds every public, non-obsolete component type found in the loaded assemblies. Any DLL in the
  application folder is loaded at startup, so a plugin assembly can add components.
- **Built-in libraries** (the application's `Components` folder) add categories:
  - **XML component libraries:** models with part numbers: Diodes, Transistors (BJT and JFET), Tubes, Op-Amps.
  - **SPICE model libraries:** `.MODEL` statements; only a subset of models and parameters is supported.
  - **Schematics,** which become subcircuit components; their `Port` components are the terminals.
  - Folders become nested categories.
- **User libraries:** the same formats are also loaded from `Documents/LiveSPICE/Components`.
- **Display:** each category is an expander showing its components in a wrapping grid, each a 16 × 16 symbol icon
  beside its name (80 px wide, trimmed with an ellipsis). The names are what tell a library's parts apart: its
  diodes, op-amps, transistors and tubes share their generic component's symbol. Each component's tooltip is its
  description, plus its shortcut keys.
- **Filter:** a text box above the categories. A non-empty filter replaces the categories with one flat list of the
  components whose names contain the text, case-insensitively. A clear button resets it.
- **Refresh:** a button reloads every library, behind a modal progress dialog.
- **Clicking a component** starts the symbol tool with a copy of it (the wire tool for Conductor).

### Component types

Generic, in the Circuit project:

| Group | Types |
|---|---|
| Passive | Resistor, Capacitor, Inductor, Potentiometer, Variable Resistor, Transformer, Center-Tap Transformer |
| Sources and definitions | Voltage Source, Current Source, Voltage Definition, Current Definition, Rail, Ground |
| Wiring | Conductor (wire), Named Wire, Port (subcircuit terminal) |
| Switches | Switch (on/off, obsolete but still loaded from old files), SPDT, SP3T, SP4T, SP5T |
| Semiconductors | Diode, BJT, JFET |
| Op-amps | Ideal Op-Amp, Op-Amp |
| Tubes | Triode, Pentode, Tube Diode |
| Signal flow | Input, Speaker (output), Buffer, Delay Buffer |
| Decoration | Label (text and subtext) |

Components that fail to load from a file appear as `Error` or `Warning` placeholders, rather than failing the whole
file.

## Properties panel

- **What it shows:**
  - With a selection: the selected components' properties, using a Windows Forms property grid hosted in WPF, so
    properties are grouped by category, with descriptions, type converters and drop-downs for enumerations.
  - With nothing selected: the schematic's own circuit, whose name, part number and description are what it shows
    as when used as a subcircuit.
- **Multiple selection:** properties are edited together. A combo box above the grid lists `(n objects)` and each
  selected object, so one of them can be edited alone.
- **Undo:** every change is one undoable edit, across all the selected objects. The old values are captured when a
  property is selected.
- **Values:** with units are typed as quantities, such as `10 kΩ`, `100 nF` or `9 V`.
- **Typical properties:**
  - Every component: Name, Part Number, Description.
  - Resistor, Capacitor, Inductor: value.
  - Potentiometer and Variable Resistor: Resistance; Wipe (0 to 1); Sweep (Linear, Sigmoid, Anti-Sigmoid,
    Logarithmic, Anti-Logarithmic); Group (pots with the same group move together).
  - SPxT switches: Position and Group.
  - Input: V0dBFS (the voltage of a full-scale sample).
  - Speaker: V0dBFS, Impedance.
  - Rail: Voltage.
  - Named Wire: Wire Name.
  - Port: Number, Name.
  - Label: Text, Subtext.
  - Device models: their model parameters, such as the Koren parameters on tubes.
- **Live update:** changing a property updates the symbol's drawing at once (for example a value shown as text).

## Audio configuration

A modal tool window (400 × 500), opened from Simulate > Audio Configuration, or automatically by Simulate when no
input or output channels are configured yet.

- **Driver:** a drop-down of the available audio drivers.
- **Device:** a drop-down of that driver's devices.
- **Inputs, Outputs:** multi-select lists of the device's channels.
- **Test:** a toggle that opens the device and loops the sum of the selected inputs to every selected output,
  showing the summed signal on an embedded oscilloscope. While testing, the device and channel controls are
  disabled. It is enabled only with at least one channel selected.
- **OK** saves the choice (driver, device, input and output channel names) to the user settings. **Cancel**
  discards it.
- The saved choice is restored when the window opens again, falling back to the first driver and device.

## Live simulation window

Simulate (F5) opens a separate window per run, owned by the main window and titled `<schematic> - Live Simulation`.
Several can be open at once.

### What it simulates

- **A copy:** the window works on a copy of the schematic, taken when it opens. Later edits in the editor don't
  reach it, and nothing done in it changes the document.
- **Solving:** the circuit is built and solved for the device's sample rate times the oversampling factor, behind a
  modal progress dialog ("Building circuit solution..."). Build and solve messages go to the window's log.
- **Audio:** runs continuously on the selected device and channels from the moment the window opens. With no
  channels selected, a silent null stream drives the simulation instead.
- **Closing** the window stops the audio stream.

### Layout

- A menu bar, a toolbar, a docking area and a status bar.
- **Menus:** File > Exit; an empty Edit menu; View > Scope, Audio, Log (checkable).
- **Docking area:**
  - **Audio panel** (left, top): see below.
  - **Scope panel** (left, bottom).
  - **Schematic** (centre): the copy, read-only apart from probes. This panel can't be closed, hidden or floated.
  - **Log panel** (below the schematic, 200 px).
- The status bar shows `Ready` and has an unused slot meant for the processing rate.

### Toolbar

- **Oversample:** an editable drop-down (1, 2, 3, 4, 6, 8, 12 or 16, or any typed value of at least 1; default 8).
- **Iterations:** a spinner from 1 to 64 (default 8): the Newton iteration cap.
  - Changing either rebuilds the solution, after a 1 s debounce.
- **Simulate:** rebuilds the solution.
- **Zoom:** Zoom In, Zoom Out, Zoom Fit, and an editable zoom drop-down (0.5, 1.0, 2.0, 4.0).

### Controls on the schematic

Interactive overlays are placed on top of the components that have them. Each is semi-transparent (50%) and turns
nearly opaque (95%) on hover.

- **Potentiometers and variable resistors:** an 80 × 80 rotary knob centred on the symbol.
  - **Dragging:** click or drag sets the value from the pointer's angle, over a 300° sweep from 0 to 1. Changes
    re-solve the circuit in the background without interrupting audio; only the newest pending update is applied.
  - **Grouping:** moving a pot moves every other pot in its group to the same value.
- **Switches** (anything with positions): a button covering the symbol.
  - **Clicking:** advances it to the next position. It also clicks every switch in its group, then rebuilds the
    simulation, with messages to the log.
- **Inputs:** an editable 80 × 24 combo box on each Input symbol, whose own label is hidden.
  - **Source:** choose an audio input channel, or type an expression in `t` (for example a sine), parsed when a key
    is pressed. An expression that doesn't parse feeds 0.
  - **Default:** the first input channel, or `0 V` if there are none.

### Probes

- **Placing:** the schematic's tool is the probe tool. Clicking a node places a probe there, or moves the probe
  that's already on that node.
- **Colour:** each probe takes the least-used of red, green, blue, yellow, cyan, magenta and orange.
- **Label:** a probe draws as a small coloured flag, labelled with the node's voltage expression.
- **On the scope:** each probe becomes a trace in its colour, named after the node, and the new trace becomes the
  scope's selected signal.
- **Selecting:** clicking a probe selects it, and selects its trace on the scope.
- **Removing:** Delete removes the selected probes. Select All selects every probe.
- Adding or removing a probe changes the simulation's outputs without rebuilding it.

### Audio panel

- **Input gain:** a slider from −40 to +40 dB with a text box (shown as `+n dB`), applied to every input before the
  simulation.
- **Input channels:** one row per channel, with its name and a signal-level lamp.
- **Output gain:** the same, applied to every output after the simulation.
- **Output channels:** one row per channel, with its name, a signal-level lamp, and a **Signal** expression.
  - **Default signal:** the sum of the Speaker components' outputs. Editing the signal rebuilds the solution.
  - **Pick (🔍):** switches the schematic to a pick mode with a hand cursor. Clicking a two-terminal component sets
    the signal to its voltage; Ctrl+click adds the component's voltage to the current signal.
- **Signal-level lamps** are refreshed 10 times a second from a peak meter that decays over about 0.25 s:
  - none below 1e-3;
  - green below 0.5;
  - yellow below 0.75;
  - orange below 0.95;
  - red at 0.95 and above.

### Scope

An oscilloscope of the probed signals, drawn on a dark background with a gray grid.

**Toolbar:**
- Zoom In and Zoom Out, for the time scale.
- A drop-down of the signals, each in its trace colour, to choose the selected signal.
- A ♪/ƒ toggle, to show frequencies as note names.
- An A₄ reference for note names.

**Time axis:**
- Labelled in time above and in frequency (1/t) below.
- The mouse wheel zooms time by 2× per 4 notches.

**Vertical scale:**
- Labelled in volts.
- Follows the signals' peak and mean, smoothed over frames, in power-of-two steps of at least 5 mV.
- The mean is centred only when it's significant against the range.

**Triggering:** the display is stabilized on the selected signal (or the first one). Its fundamental frequency is
estimated from the last 8192 samples, and every trace is shifted by its phase, so a periodic signal stands still.

**Statistics** for the selected signal, top left: ƒ₀ (as a frequency or a note), peak, mean and RMS.

**Cursor:** hovering shows crosshairs with the time, the frequency (1/t) and the voltage at the pointer.

**Data:**
- Traces keep about a quarter of a second of samples.
- A trace that received no data in a block is cleared.
- The display redraws as data arrives.

A spectrogram mode exists in the code but isn't reachable from the UI.

### Log panel

- A scrolling text log that follows new messages when scrolled to the end.
- A **Verbosity** drop-down, saved in the user settings (default Info), and a **Clear** button.
- **Messages:** build and solve messages, warnings from loading the schematic, exceptions, and simulation errors.

### Errors at run time

- **Divergence:** if the simulation diverges, the outputs are silenced, the error is logged, and the solution is
  rebuilt automatically if it had run for more than a second.
- **Other errors:** any other error silences the output and stops the simulation until it's rebuilt (Simulate).
- **Sample rate:** if the device's sample rate changes, the solution is rebuilt for the new rate.

## Other windows

- **About:** a small window with the LiveSPICE logo, a link to livespice.org and OK. It shows no version.
- **Unhandled exception:** shows the exception text in a read-only box, with a link to the LiveSPICE issues page and
  Copy to Clipboard, Abort and Continue buttons. Continue carries on. Abort resets the user settings and lets the
  exception end the application.
- **Progress dialog:** modal, with a message, optional progress and an optional Cancel. It can't be closed while
  its task is running.
- **Error messages:** failures to open or save files, or to start a simulation, are shown in message boxes.

## Settings

User-scoped settings, saved on exit. Starting with the `clearsettings` argument resets them.

| Setting | Use |
|---|---|
| Recent files | Up to 20 paths |
| Main window layout | AvalonDock layout |
| Live simulation layout | Defined but not saved or restored |
| Audio driver, device, inputs, outputs | Names, from Audio Configuration |
| Log verbosity | From the log panel |

## Notes for the web port

Where each area's state would naturally live, once the UI is a local web page and the engine a C# server:

| Area | Client | Server |
|---|---|---|
| Canvas, zoom, pan, tools, selection, hover, tooltips | All of it | Nothing |
| Schematic document | The working copy being edited, and its undo stack | Load and save (`.schx`), recent files, change watching |
| Component library | Browsing, filtering, icons | Enumerating components, their symbol layouts and default properties, user libraries |
| Properties | The grid | Property metadata (categories, descriptions, types, enumerations, defaults, units) and validation |
| Connectivity | Drawing terminal state | Node assignment, if the client doesn't reproduce it |
| Audio configuration | The dialog | Drivers, devices, channels, test loopback, saved choice |
| Simulation | Overlays, panels and meters | Building, solving, running, rebuilding on change |
| Pots, switches, input sources | Knobs, buttons, combos | Parameter changes (with grouping), input routing, signal expressions |
| Probes and output signals | Placement, colours | Which nodes and expressions are computed, streamed back |
| Scope | Drawing, zoom, cursor, note names | Probe samples (or decimated blocks), and possibly the frequency estimate and statistics |
| Levels | Lamps | Peak levels, at about 10 Hz |
| Log | The panel and its filter | The messages |

Things the WPF app gets from the platform that a web UI will need to provide itself: a docking layout, a property
grid with type converters, the clipboard (schematic XML as text), file dialogs (or a server-side file browser), and
modal progress for long builds.
