# SpiceForge

SpiceForge simulates guitar pedal and amplifier circuits in real time from their schematics. It includes a schematic editor
that runs in a browser or a window of its own, playing through ASIO or JACK, on Windows and Linux.

## Based on LiveSPICE

SpiceForge is built on [LiveSPICE](https://github.com/dsharlet/LiveSPICE), which it includes,
unmodified, as a submodule. It uses LiveSPICE's schematic files (`.schx`) and component libraries, and LiveSPICE's own
analysis turns each schematic into its circuit equations. SpiceForge's own engine starts from those equations.
LiveSPICE's simulation is also the reference SpiceForge is validated against, and LiveSPICE's test circuits are its
test suite.

## What it adds

- **Live knobs and switches.** Pots and switches are runtime parameters: moving one never re-solves the circuit.
  The result matches the circuit with the setting built in, to −140 dB for pots and −193 dB for switches.
- **A smaller solve each sample.** The equations are split once, offline, so that Newton's method works only on the
  nonlinear devices' ports, and the step is compiled to machine code. It runs at about LiveSPICE's speed with pots
  built in (median 0.91× over the test circuits, the large tube amps faster), while keeping every control live.
- **Circuits LiveSPICE can't run.** SPICE-style junction limiting and a tightly converged DC start run the Fuzz Face
  and Orange Squeezer, which are unstable in LiveSPICE.
- **Better resampling.** Sinc resampling around the oversampled circuit is an option, in place of LiveSPICE's linear
  interpolation and averaging.
- **A new editor.** It runs in a browser, served by a local server. It has simulation with meters, a scope and
  output selection, and audio through ASIO and JACK. Packages are built for Windows and Linux.

`DesignDocs/LessonsLearned.md` records what was tried and measured. `CREDITS.md` lists the projects SpiceForge
builds on.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the editor,
[Node.js](https://nodejs.org) 22 or later. Clone with the submodules:

```
git clone --recursive https://github.com/mikeoliphant/SpiceForge.git
cd SpiceForge
```

**Everything** (the plugin needs Windows):

```
dotnet tool restore
dotnet build SpiceForge.slnx -c Release
```

**The editor:** build the web front end, then run the server, which opens the editor in a window of its own:

```
cd SpiceForgeWebUI && npm ci && npm run build && cd ..
dotnet run -c Release --project SpiceForgeServer -- --open app
```

Build in Release. A Debug build simulates four to five times slower.

**Packages** (a Windows installer and zip, a Linux tarball) go into `artifacts/`. The installer needs
[Inno Setup 6](https://jrsoftware.org/isinfo.php).

```
dotnet run -c Release --project SpiceForge.Package
```

**Checks:** `SpiceForge.Validate` holds them, for example `server` (the server and editor) and `processor`:

```
dotnet run -c Release --project SpiceForge.Validate -- server
```

## License

MIT; see `LICENSE`. Dependencies keep their own licenses, listed in `CREDITS.md`.
