# SpiceForge

SpiceForge simulates guitar pedal and amplifier circuits in real time from their schematics. It includes a schematic editor
that runs in a browser or a window of its own, playing through ASIO or JACK, on Windows and Linux.

<img width="1384" height="861" alt="SpiceForge" src="https://github.com/user-attachments/assets/fcb27b05-9f4e-44e5-ac42-68b27703b674" />

## Based on LiveSPICE

SpiceForge is built on [LiveSPICE](https://github.com/dsharlet/LiveSPICE), which it includes,
unmodified, as a submodule. It uses LiveSPICE's schematic files (`.schx`) and component libraries, and LiveSPICE's own
analysis turns each schematic into its circuit equations. SpiceForge's own engine starts from those equations.
LiveSPICE's simulation is also the reference SpiceForge is validated against, and LiveSPICE's test circuits are its
test suite.

## What it adds

- **Live knobs and switches.** Pots and switches are fully dynamic under live simulation.
  This is done without sacrificing accuracy. It has a small performance cost over fixed pots/switches, but the
  new engine still runs about 30% faster than the original LiveSPICE circuit simulation. See the [circuit benchmarks](DesignDocs/CircuitBenchmarks.md) for the full results.
- **A new, cross-platform application.** It runs in a browser, served by a local server. It has simulation with meters, a scope and
  output selection, and audio through ASIO and JACK. Packages are built for Windows and Linux.
- **Circuits LiveSPICE can't run.** SPICE-style junction limiting and a tightly converged DC start run the Fuzz Face
  and Orange Squeezer, which are unstable in LiveSPICE.
- **Better resampling.** Sinc resampling around the oversampled circuit is an option, in place of LiveSPICE's linear
  interpolation and averaging.

`DesignDocs/LessonsLearned.md` records what was tried and measured. `CREDITS.md` lists the projects SpiceForge
builds on.

## Getting it

You can find binaries in the [Releases section](https://github.com/mikeoliphant/SpiceForge/releases/latest).

Binaries are provided for x64 Windows and Linux.

## Current notable limitations

- No macOS package is currently produced - it must be built locally.
- macOS support is untested. It *should* work with JACK audio. macOS Core Audio is not yet implemented.
- There is initial DAW plugin support, but it is still very bare bones and is limited to Windows VST. It is not currently packaged for releases.

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

MIT. See [LICENSE](LICENSE). Dependencies keep their own licenses, listed in [CREDITS.md](CREDITS.md).
A full set of applicable licenses is provided with the binaries distributed in the [Releases section](https://github.com/mikeoliphant/SpiceForge/releases). If you find any omissions, please let me know.
