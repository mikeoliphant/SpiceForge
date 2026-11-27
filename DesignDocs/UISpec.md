# UI spec: SpiceForge's editor

This describes the editor as it is: the web front end (`SpiceForgeWebUI`), served by `SpiceForgeServer` and talking to
it over the WebSocket API (`WebSocketApi.md`). A change that alters what the UI does is written here, in the section
it belongs to, as part of making it (AGENTS.md). `LessonsLearned.md` keeps why things are as they are, what was tried,
and the evidence; this keeps what they are.

The editor began as a port of LiveSPICE's WPF application and still reads and writes its files. Three things are a
fixed contract with LiveSPICE, because the server builds circuits with LiveSPICE's own code: the file format, symbol
geometry, and connectivity. Everything else follows "does the operation make good sense"; the last section lists
where that departs from LiveSPICE.

The plugin (`SpiceForgePlugin`) has its own, smaller UI and isn't covered here.

## Application shell

### Layout

From top to bottom:

- **Toolbar** (below).
- **Tabs:** one per open schematic.
- **Banner:** shown when the active tab's file has changed on disk (see Files).
- **Workspace:**
  - the **Components** library on the left (236 px by default);
  - the **schematic** in the middle;
  - on the right (270 px by default), the **View** section above the **Properties** panel.
- **Simulation panel:** under the workspace, hidden until something is simulated.
- **Status bar:** the latest status message (errors in the error colour), the current tool ("Placing (Esc to
  stop)", "Wiring…", "Probing…", "Picking what … plays…"), the selection count ("3 selected"), and the schematic
  coordinates under the pointer.

The side panels' inner edges are handles. Dragging one sets that panel's width, from 160 to 600 px for the library,
or 200 to 600 px for the properties, and a double-click restores its default. The simulation panel's top edge works
the same way, for its height. A double-click there fits the panel to its controls, and the schematic always keeps
some room. This browser remembers each size.

The window's title is the active tab's name, with "• " in front when it has unsaved changes, then " - SpiceForge".

### Toolbar

| Button | Does |
|---|---|
| SpiceForge *v…* | About (the name, with the version muted) |
| New | A new untitled schematic, in a new tab |
| Open… | The open dialog (Ctrl+O) |
| Save | Save (Ctrl+S); Save As for an untitled schematic |
| Save As… | Save under a new name (Ctrl+Shift+S) |
| Export… | A menu: SVG image or PNG image |
| Undo, Redo | Their tooltips name the edit ("Undo Move") |
| Cut, Copy, Paste, Delete | On the selection |
| Simulate | Simulate the schematic shown (F5); highlighted while it's the one being simulated, when a click starts it again from scratch |
| Audio… | The audio dialog |
| Help | The shortcuts (F1) |

A button is disabled when it can't act: everything needing the server until it's connected; Save and Export with
no tab, Export with an empty drawing; Cut, Copy and Delete with nothing selected; Undo and Redo with nothing to undo
or redo; Audio… when the server has no audio support.

### View section

Above the properties:

- **Zoom:** −, the zoom as a percentage, +, and Fit.
- **Grid:** shows or hides the grid (see Canvas). This browser remembers the choice.
- **Theme:** System, Light, Dark, Dim, Paper or High contrast. System follows the operating system's light or dark
  setting as it changes. The choice is kept in this browser (applied before the page first paints, so a dark theme
  doesn't flash light) and in the server's settings, which win once connected, so it survives clearing the browser.
  Changing it in one window changes it in every other.
  - The drawing's colours come from the theme. Dark themes select in amber and High contrast in yellow, since a
    blue selection is hard to tell from light-blue wires on a dark canvas.

### Tabs

- **Name:** the file's name without its extension; "Untitled 1", "Untitled 2"… for unsaved schematics; a second
  tab on the same file is numbered ("Rat (2)"). A tab with unsaved changes ends in " •". Its tooltip is the file's
  path, or "Not saved yet".
- **Switching** keeps each tab's history, selection and view (zoom and position). A tab shown for the first time
  is zoomed to fit.
- **Closing:** the tab's ×, or a middle-click on it. A tab with unsaved changes asks first. Closing the tab being
  simulated stops the simulation.
- **At startup** the files open last time are opened again, the active one last, so it's shown. A `file`
  in the address (`?file=…`) is opened too, and shown. With nothing to open, an untitled tab is.

### Connecting

The server prints an address with a token (`?token=…`). The page keeps the token for reloads in this browser tab,
and takes it out of the address bar. Without one, the status bar says to open the address the server printed.
While connecting it says "Connecting…", then "Connected to SpiceForge v… (commit)". If the connection drops, the
status bar says why, the simulation panel closes, and New, Open and everything else that needs the server are
disabled. Editing what's open still works, where it doesn't need the server.

### Keyboard shortcuts

Shortcuts are ignored while typing in a field and while a dialog is open. On a Mac, ⌘ works wherever Ctrl does.

| Keys | Action |
|---|---|
| Ctrl+O | Open |
| Ctrl+S, Ctrl+Shift+S | Save, Save As |
| Ctrl+Z | Undo |
| Ctrl+Y, Ctrl+Shift+Z | Redo |
| Ctrl+X, Ctrl+C, Ctrl+V | Cut, Copy, Paste (under the pointer, if it's over the schematic) |
| Delete | Delete the selection |
| Ctrl+A | Select all |
| Ctrl+F | Find a part |
| Left, Right | Rotate the selection, or the part being placed, 90° counter-clockwise or clockwise |
| Up, Down | Flip it |
| Escape | Close the menu; else stop the pick or probe tool; else cancel the gesture in progress; else leave the place or wire tool; else clear the selection |
| W | The wire tool |
| G, P, R, C, L, T | Place a ground, rail, resistor, capacitor, inductor or label |
| Ctrl+G, P, R, L, H | The same, as in LiveSPICE (Ctrl+H is the inductor too) |
| + or =, − | Zoom in, out |
| F, 0 | Zoom to fit |
| Space (held) | The left button pans |
| F5 | Simulate the schematic shown (rather than reload the page) |
| Shift+F5 | Stop the simulation |
| F1, ? | Help |

Browsers keep Ctrl+W and Ctrl+T for themselves, so the tools also have the plain letters. Ctrl+F is Find; the
capacitor is C. Leaving the page with unsaved changes in any tab asks first.

### Dialogs

All modal; Escape closes them as Cancel or Close would.

- **Help:** the keyboard shortcuts and mouse gestures, in groups (Files, Editing, Placing parts, Mouse, View,
  Simulation, Help). The lists are written by hand in `help.ts`, so a change to a binding is mirrored there.
- **About:** the icon, name and version, the commit (linked), copyright, the license (its text in place), the
  source repository, and each third-party component the server ships, with its license text shown in place and a
  link to its notices. **Copy version info** copies the version, commit and browser, for a bug report.
- **Confirmations** (discarding changes, overwriting, deleting) are the browser's own.
- **Choices** (an already-open file) are a small dialog of buttons.

## Files

Files live on the server's machine, so the dialogs browse its file system. Schematics are LiveSPICE's `.schx`
(`.xml` is listed too).

### The file dialog

One dialog, in three modes: Open schematic, Save schematic as, and Choose a folder.

- **Navigation:** Up; a path box, where Enter goes to the folder (or opens a schematic typed there, when opening);
  the folder's contents, folders first.
  - Opening: a click on a schematic opens it.
  - Saving: a click puts the file's name in the name box (the file is marked in the list), and a double-click saves
    over it.
  - Choosing a folder: only folders are listed; **Use this folder** chooses the one shown.
- **Places:** the LiveSPICE folder (`Documents/LiveSPICE`) and the examples the server has.
- **Recent:** the files most recently opened or saved. When saving, picking one goes to its folder with its name.
- **Where it starts:** the folder of the active tab's file, or of the last active file; else the LiveSPICE folder.
  If that can't be listed (a new user has no LiveSPICE folder yet), each place in turn.
- **Saving:** the name box starts with the file's name, or the tab's name plus `.schx`. A name without an extension
  gets `.schx`. A name with a slash is taken as a path.
- **New folder** (saving and choosing a folder): a name box at the top of the list. Enter makes the folder and goes
  into it; Escape, or leaving the box, drops it.
- **Delete** (saving): deletes the schematic the name box names, after asking; it can't be undone. A file open in a
  tab can't be deleted. Only schematics are deleted, never folders.
- Enter in any of its boxes is handled there and goes no further, so it can't also press a button behind the
  dialog.

### Opening, saving and changes on disk

- **Open** reuses the active tab if it's untitled and untouched. A file already open shows its tab; the Open button
  then also asks whether to open another copy of it, in a tab of its own, or to reload the open one (warning that
  reloading discards unsaved changes). Paths are compared with either slash and ignoring case.
- **After opening,** the status bar gives the number of symbols and wires, with any problems loading it.
- **Save** writes the tab's file. If the file changed on disk since it was read or written, it asks before
  overwriting. **Save As** asks before replacing an existing file, and refuses a file open in another tab.
- **Changed on disk:** the server watches the open files. When one changes, its tabs show a banner: Reload (which,
  with unsaved changes, says it discards them) or Keep mine (saving then asks before overwriting). Saving one of two
  tabs on the same file shows the banner on the other.

### Export

Export… saves the drawing as an SVG image, or as a PNG at twice its size. It's drawn afresh, without the selection,
hover marks or the grid, in the theme's colours and on its canvas colour, with a 20-unit margin. The file is named
after the tab.

## Schematic editor

### Canvas

- **Grid:** every position snaps to a 5-unit grid, rounding halves to even, as LiveSPICE's .NET rounding does. The
  grid can be shown as dots on the grid points. Dots closer than 8 screen pixels are thinned to every second point
  (and again), so those shown are always snap points. The dots are a background, never part of an export.
- **Zoom:** the mouse wheel zooms by 1.1× per notch about the pointer; the buttons and keys by 1.5× about the
  centre; from 1/16× to 8×.
- **Fit** frames all the elements with a 40-unit margin, or a 200 × 200 area around the origin when there are none.
- **Pan:** drag with the middle button, or with the left while Space is held (for trackpads).
- **Focus:** a press on the schematic takes the focus from any field being edited, which commits the field's value.

### Drawing

- **Symbols** are drawn from each component's layout, as the server describes it: lines, rectangles, ellipses,
  polylines, arcs and upright text in three sizes, in the edge colours, with 1-unit round-capped strokes. Wires are
  drawn under symbols. A Label's two gray corner lines show only while it's selected.
- **Terminals** are small squares, red when unconnected. Wire ends are squares too.
- **Selection** draws a dashed outline around a symbol and turns a wire the selection colour. Hovering a symbol
  draws a dashed outline.
- **A component that couldn't be loaded** draws as a placeholder, with the reason in its tooltip.
- **Tooltips:**
  - a symbol: its type and name, each property the file stores (those that differ from their defaults) as
    `name = value`, and why it couldn't be loaded, if it couldn't;
  - a wire: its node, by the named wires on it ("GND") or else as "Node 12", and what the node connects
    ("Connects R1.A, C2.B…", up to eight, then "and 3 more").

### Selecting

The selection tool is the default.

- **Press on an element** (without Ctrl): it becomes the selection, if it wasn't in it, and a drag moves the
  selection. Releasing without moving selects just that element.
- **Press on empty space, or with Ctrl:** a rubber band. Releasing selects everything it touches (symbols by their
  bounds, wires by their segments); Ctrl adds to the selection.
- **Click** on empty space clears the selection; Ctrl+click toggles the element under the pointer.
- **Double-click** a symbol adds every symbol of its type to the selection.
- **Right-click:** the context menu. An unselected element under the pointer becomes the selection first. During a
  drag, the right button cancels it. In the place or wire tool, a right-click just returns to the selection tool.
- **Context menu:** Cut, Copy, Paste here, Delete; Rotate left, Rotate right, Flip; Select all, Undo, Redo (naming
  the edit), each with its keys, and disabled when it can't act.

### Moving

A drag moves the selection live, by whole grid steps. The whole move, with everything below, is one undo step.
Escape or the right button cancels it, putting everything back.

- **Attached wires follow.** A wire, not selected, with an end on a moved terminal or a moved wire's end, follows
  that end:
  - it stays straight if its ends still line up;
  - else it becomes an L that leaves the moved end in the wire's old direction, so it still meets the terminal as
    it did, and turns at the fixed end's line;
  - a wire with both ends moving moves whole;
  - a slanted wire just stretches.
- **Corners slide.** When a following wire's fixed end is a plain corner (exactly one other wire touches the point,
  ending there at a right angle, not moving or following itself, and no terminal is there), the corner moves across
  with the part, and the other wire lengthens or shortens to it. A wire moved to nothing is removed.
- **Tees follow.** A wire, not moving or following, with an end on the length of a moving or following wire (a T),
  keeps that end on it:
  - the end stays put if the wire still runs there;
  - else it slides along its own line to the nearest point where that line meets the wire, so it stretches or
    shortens straight;
  - else it goes to the wire's nearest point, with an L from there.

  Tees get corners like other followers. A wire between two wires that both move whole (a rung) moves whole. Only
  one level: a wire teed onto a tee doesn't follow.
- **Lone parts are towed.** A part, not selected, with a terminal at the far end of a moving wire (either end of a
  moved wire, the fixed end of a following one) moves too, if that wire is its only connection to any other part.
  Its other terminals may be unconnected, have dangling wires, or be wired back to the parts being moved; a wire on
  to any other part, even through a T, or a terminal sitting on another part's terminal, keeps it in place. (Moving
  the Boss SD-1's Level pot takes GND2 along; R10 and the output stay.) A towed part's own wires follow it, but the
  towing goes no further. Towed parts aren't selected. A wire that ends on a junction or a corner reaches no part.
- **Shift detaches.** Holding Shift, at the press or at any time during the drag, leaves the wires where they are,
  and tows nothing. Releasing it brings them back.
- **Shift heals.** A part sits in a wire when two of its terminals line up and each has a wire, not moving, ending
  there and going on along their line, away from the other. Moved out with Shift, such a part leaves the wire whole:
  a wire closes the gap where it was (shown while dragging), joined as the wire tool joins wires, so the run becomes
  one wire again. A part wired on one side only, or with its wires meeting it at a corner, leaves no gap to heal.
  Parts moved together that touch terminal to terminal heal nothing between them.
- **Dropped on a wire, a part is wired in.** After a move (with or without Shift), a placement or a drop from the
  library: for each pair of the part's terminals that line up, widest first, if wires along that line cover the
  whole span between them, the span is cut out of them, so the wire runs into one terminal and out of the other
  instead of shorting the part. If anything else connects inside the span (another wire's end, another part's
  terminal), it's left alone, so no branch is stranded. A part whose terminal lands on the middle of a wire,
  without a second terminal on it, isn't wired in. Pasting doesn't wire in.

On release, the rerouted wires are joined as the wire tool joins wires (merged where they overlap, split at
terminals and junctions).

### Extending a wire

A press within 10 screen pixels of a wire's free end (no other wire touches it, at its end or along it, and no
terminal is there) drags that end rather than the wire. The wire runs from its other end straight to the pointer if
they line up, else as an L going on in its old direction. On release it's joined as a drawn wire is, so dropping the
end on a terminal or a wire connects it, and the new pieces are selected. A release where it started, or on the
other end, changes nothing.

### Placing parts

- **Clicking a component** in the library, or its shortcut, starts placing it: a gray ghost (without text, its
  terminals red) follows the pointer, hidden while the pointer is off the schematic. The arrow keys rotate and flip
  it. A click places a copy, selected, and wires it in if it lands on a wire. The editor then returns to the
  selection tool, unless Ctrl is held. With no schematic open, the status bar says to open or create one.
- **Dragging a component** from the library onto the schematic places it where it's dropped, as a click would.
- **Names:** a part whose name is already used gets the letters of its name plus the first free number (a resistor
  added beside R1 to R9 is R10).

### The wire tool

The library's Wire entry or W. A drag draws a preview: straight if its ends line up, else an L whose first leg goes
in the direction the drag started in. On release the wire is joined to what it meets: wires it overlaps along its
line are merged with it into one, and the result is split wherever a terminal or another wire's end lies on it, and
where its own end lies on a crossing wire. So a wire connects wherever it meets a terminal or ends on another wire,
never merely by crossing one. The whole wire is one undo step. The editor then returns to the selection tool, unless
Ctrl is held.

### Editing operations

- **Rotate** (Left counter-clockwise, Right clockwise) and **flip** (Up or Down, over the horizontal line through
  the centre) turn the selection about the middle of its bounds, snapped to the grid. Flipping a part negates its
  rotation as well as toggling its flip, so a part turned 90° stays on its wires.
- **Cut, Copy and Paste** use LiveSPICE's clipboard format (schematic XML), through the system clipboard, so copies
  go between this editor and LiveSPICE. Where the page can't use the system clipboard, the last copy made here is
  pasted instead. A paste is centred under the pointer (Ctrl+V, or the context menu's Paste here), or lands 20 units
  down and right of the original (the toolbar's Paste). Pasted parts are renamed where their names clash, and
  selected.
- **Delete** removes the selection. **Select all** selects every element.
- **Undo and Redo** cover every edit, property changes included; each operation, however many elements it touches,
  is one step. They do nothing during a drag. Undoing back to the saved state clears the unsaved marker.

### Find

Ctrl+F opens a box over the schematic. Typing finds the parts whose name or part number contains the text, ignoring
case: exact names first, then names starting with it, then the rest, each in natural order (R2 before R10). The
first match is selected alone and brought into view (zoomed in to at least 100%). Enter goes to the next, Shift+Enter
to the previous, and the box shows "2 of 5", or "No match" (marked invalid). Escape or × closes it.

### Connectivity

Part of the contract: the client must agree with `circuit.check`, LiveSPICE's own rules.

- Two wires connect when an end of either lies on the other, at its end or along it. Wires that only cross don't.
- A terminal connects to the node of a wire through its point. Two terminals that touch with no wire there aren't
  connected.
- Named wires (grounds are named GND) with the same name are one node. A file that doesn't store a ground's name
  takes its type's default.
- A wire whose ends coincide connects nothing.

Terminal colour and wire tooltips show the connections; there's no separate netlist view.

## Component library panel

- **Categories** come from the server's catalogue: Common (expanded at first) with the wire, ground, rail,
  resistor, capacitor and so on; the generic components; and the libraries (diodes, transistors, tubes, op-amps,
  SPICE models and subcircuit schematics), with folders as nested categories. Each is an expander; the panel
  remembers which are open while it's shown.
- **Entries:** each component's 18-pixel symbol icon and its name. The names tell a library's parts apart: its
  diodes, op-amps, transistors and tubes share their generic component's symbol. The tooltip is the name and
  description. The compact view (the four-squares button) shows icons only, with the names in their tooltips; this
  browser remembers it.
- **Filter:** typing replaces the categories with one flat list of the components whose names contain the text,
  ignoring case, without duplicates. Escape clears it.
- **↻** reloads the component libraries, for every connected window.
- **…** opens the **component folders** dialog: the server's own library folders, listed, and the user's, which can
  be added (through the folder dialog) and removed. LiveSPICE component libraries (`.xml`) in them appear in the
  library, in every window, as soon as the list changes.
- **Clicking** a component starts placing it (the wire entry starts the wire tool); **dragging** one onto the
  schematic places it there.

## Properties panel

- **What it shows:** with symbols selected, their components' properties; with none, the schematic's own (its name,
  part number and description, which are what it shows as when used as a subcircuit), headed "Schematic". A single
  component is headed by its type and name.
- **Rows:** grouped by category, categories and names in alphabetical order. Enumerations and booleans are
  drop-downs; everything else is a text box. Read-only properties are disabled. Focusing a row shows its name and
  description below the grid.
- **Several components:** only the properties all of them have (same name and kind) are shown, edited together. A
  value they don't share is blank, marked "(different values)". A drop-down above the grid lists "(n objects)" and
  each component, so one can be edited alone.
- **Editing:** a text box commits on Enter or when it loses focus; Escape puts the value back. Each value is checked
  by the server, which also normalizes it ("4.7k" becomes "4.7 kΩ"). A refused value marks the field red, with the
  reason in its tooltip and the status bar. A name another component already has is refused, as is one name for
  several components. Each change is one undo step, across all the components it sets, and redraws the symbols at
  once.
- **Focus:** the grid isn't rebuilt under a field being edited. A press on the schematic ends the edit (committing
  it to the component it was showing), and the grid then follows the new selection.

## Simulation

One schematic is simulated at a time, in the panel under the workspace.

### Starting and stopping

- **Simulate** (F5) simulates the schematic shown, replacing anything already running; on the schematic being
  simulated it starts again from scratch. **Stop** (Shift+F5) closes the panel. Closing the tab stops it too.
- **The head row:** the schematic's name; its state (Building…, Rebuilding…, Running, Stopped by an error); Probe;
  **Restart**, which rebuilds from the DC operating point; and Stop. Below it, a message when it's stopped by an
  error ("The simulation diverged.", or the server's reason) or is running an older version (below).
- **Edits follow the simulation.** An edit to the simulated schematic is sent to the server 0.4 s after the last
  one. The old build keeps playing until the new one is ready. An edit that doesn't build is refused, and the panel
  says it's still simulating the last version that did.
- **Live positions:** while simulating, switches are drawn at their live positions and pots show their live wipers,
  without editing the document: it stays as saved, and there's nothing to undo.
- **After a reload,** a simulation still running on the server is shown again if its schematic is reopened, and is
  sent the schematic as it is now.

### The panel's controls

- **Input:** what the circuit's Input plays: the audio device's open inputs ("Audio in: …"), test signals (sines of
  220 Hz at 0.5 V, 440 Hz at 0.1 V, 1 kHz at 1 V and 82 Hz at 0.2 V; a 220 Hz square at 0.2 V; Silence), or Custom.
  Below it, the signal as an expression in volts of `t` in seconds; Enter applies it. A refused expression is marked
  invalid, with the reason in the status bar. A circuit with no Input has both disabled.
- **Audio:** where it plays (the device, rate, buffer size and round-trip latency, or the internal clock), with a
  warning style when a configured device couldn't be used, and **Configure…** for the audio dialog.
- **Out:** a row per open output channel: its name, what it plays ("Speakers", or "V(R1) + V(C2)"), a level meter,
  **Pick** and **Speakers**.
  - **Pick** starts the pick tool: a click on a two-terminal component plays the voltage across it on that channel,
    and the tool stops; Ctrl+click adds it to what the channel plays, and the tool carries on. Escape stops it.
  - **Speakers** sets the channel back to the Speaker components' output.
- **Meters:** In (the Input's level against its full scale) and Out (the Speakers'), green below 0.5, then warmer
  colours to red from 0.95. Beside them, the load ("23% of real time"), with the Newton steps that hit the iteration
  cap in the last second when there are any; it's marked when the load passes 80% or steps are capped. Its tooltip
  adds dropouts and settings it couldn't solve.
- **Controls:** a slider per pot, with its position as a percentage (double-click for its position in the
  schematic), and a button per switch showing "2 of 3" (click for the next position). "No pots or switches." when
  there are none.
- **Engine:** oversampling (1, 2, 4, 8 or 16×), the Newton iteration cap (8, 16, 32, 64 or 127) and resampling (sinc
  or linear). A change rebuilds the simulation.

### On the schematic

Only on the tab being simulated.

- **Knobs and switch buttons** sit beside each pot and switch, in the least crowded of eight spots around its
  symbol (counting other symbols, wires, text, probe flags and controls already placed). On a dense schematic the
  least bad spot is used.
  - A knob points from seven o'clock (0) to five o'clock (1). Dragging up or down turns it, 150 pixels end to end,
    and the drag carries on wherever the pointer goes. The wheel over it turns it 2% per notch instead of zooming. A
    double-click puts it back to its position in the schematic.
  - A switch button shows its position number; a click moves it to the next.
- **Probes:** with Probe on, a click on a wire probes it where clicked, and a click on a symbol probes its nearest
  terminal. Each probe is a flag in its colour: a dot on the node and a numbered pennant up and to the right. Its
  tooltip is the node's name, or, for one of LiveSPICE's anonymous nodes, what it's clipped to ("R1.A"). A flag on
  a terminal follows its symbol; one on a wire stays where it was clipped while that's still on the wire, else it
  moves to the wire's middle. Escape, or Probe again, leaves the tool.

### Scope

Beside the panel's controls: the probes' voltages, on the theme's canvas colour.

- **Display:** ten divisions across and eight down, centred on 0 V, with a scale readout top left ("50 mV/div
  1 ms/div", and "Frozen" when frozen). Before any probe it says to probe a wire or terminal.
- **Time:** Auto, or 0.1 ms to 20 ms per division. Auto shows about three periods of the first probe (between 2 and
  100 ms). The display is triggered on the first probe's rising crossing of its mean, so a steady waveform stands
  still.
- **Volts:** Auto, or 1 mV to 10 V per division. Auto fits the largest peak, in steps of 1, 2 and 5.
- **Freeze** holds the display still.
- **Legend:** a row per probe with its colour, name, peak-to-peak voltage and frequency (for a periodic signal), and
  × to remove it.
- About a quarter of a second of each probe is kept.

### What's remembered

For each schematic with a file, this browser remembers its probes (and where their flags were clipped), the scope's
scales, the Input's signal, what each output channel plays, and the pots' and switches' positions. They're put back
when it's simulated again, skipping whatever no longer applies (a pot renamed, a wire deleted). An untitled
schematic's aren't remembered.

## Audio configuration

The Audio dialog, from the toolbar's Audio… or the simulation panel's Configure…. The server applies the choice,
saves it, and moves a running simulation onto it.

- **Driver:** the kinds of audio device the server has (ASIO, ALSA, JACK), and None: the server's own clock, with no
  audio. Choosing a driver starts from what was last used with it.
- **Device**, with **Control panel…** for a driver that has one: the driver's own settings window, which opens on
  the server's machine (many ASIO drivers set their buffer size only there).
- **Inputs, Outputs:** the device's channels, as checkboxes. A device starts with its first input and its first two
  outputs. JACK's lists refresh as ports come and go.
- **Rate** and **Buffer:** the device's own, or one it offers; buffer sizes show their milliseconds.
- **Now:** a line saying what the audio is doing, warning when a configured device couldn't be used and the clock
  stands in. For JACK, each of SpiceForge's ports and what it's connected to now.
- **Test:** applies the settings, then plays a 440 Hz tone, or the inputs looped back, on every output, with a meter
  per channel and a scope of the inputs summed. The other controls are locked while it runs; Stop test ends it.
- **OK** applies and saves, and stays open with the reason if that fails. **Cancel** changes nothing.

## Where things are kept

| What | Where |
|---|---|
| The theme | This browser, and the server's client settings |
| Open files, the active file | This browser |
| Library view (compact), grid shown, panel widths, simulation panel height | This browser |
| A schematic's simulation setup | This browser, by file path |
| The token, the running simulation | This browser tab (session storage) |
| Recent files | The server |
| Component folders | The server |
| Audio configuration, per driver | The server |

## Departures from LiveSPICE

Where the editor behaves differently from LiveSPICE's WPF application, deliberately:

- **Moving** drags attached wires, slides corners, keeps tees, tows lone parts, heals with Shift, and wires in parts
  dropped on wires; LiveSPICE moves only the selection, leaving its wires behind.
- **A press on an unselected part** selects and drags it in one gesture.
- **A wire's free end** can be dragged to extend it.
- **Flipping** a part turned 90° keeps it on its wires (LiveSPICE mirrors it the wrong way, off them).
- **The wire tool's L** follows the direction the drag starts in, rather than a distance heuristic.
- **Paste** lands under the pointer, not on top of the original.
- **Shortcuts:** plain letters for the tools; Ctrl+F is Find, not the capacitor.
- **A library click with no schematic open** says so, rather than creating one.
- **Files** are on the server's machine, in tabs, with a banner (not a prompt) for changes on disk; there's no
  Save All or Exit.
- **One simulation at a time,** in a panel under the schematic it simulates, following its edits, rather than
  windows on copies. Probes are added without rebuilding.
- **Not here:** docking layouts, the simulation log panel, input and output gain controls, and LiveSPICE's
  spectrogram.
