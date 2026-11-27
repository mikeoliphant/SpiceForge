# Credits

SpiceForge builds on other people's work, both as code it depends on and as ideas that shaped how it solves circuits.
SpiceForge itself is MIT licensed (see `LICENSE`); each dependency keeps its own license, given below.

## Dependencies

### Submodules

- [LiveSPICE](https://github.com/dsharlet/LiveSPICE) by Dillon Sharlet (MIT), used through the
  [mikeoliphant/LiveSPICE](https://github.com/mikeoliphant/LiveSPICE) fork in `Dependencies/LiveSPICE`, unmodified.
  Its schematic format, component models, `Circuit.Analyze()` and transient solution are the front end every engine
  here starts from; its `Simulation` is the reference the engines were validated against, and its `Tests` circuits
  are the test suite. The plugin's editor follows the layout of LiveSPICE's VST.
- [ComputerAlgebra](https://github.com/dsharlet/ComputerAlgebra) by Dillon Sharlet (MIT), inside LiveSPICE: the symbolic
  algebra (gradients, simplification, LINQ compilation) the extractor and the compiled engine are built on.
- [UILayout](https://github.com/mikeoliphant/UILayout) (Mozilla Public License 2.0), in
  `SpiceForgePlugin/Dependencies/UILayout`: the plugin's user interface and the image sheet processor that renders its
  textures. MPL 2.0 applies file by file, so UILayout's files stay under it while SpiceForge's own code is MIT.

### NuGet packages

- [AudioIOSharp](https://github.com/mikeoliphant/AudioIOSharp) (`AudioIOSharp` 0.1.0; MIT): real-time,
  allocation-free audio I/O, shared with AudioPlugSharp's standalone host. SpiceForgeServer's audio drivers (the
  internal clock, ASIO and JACK) and the engine that runs a simulation on them, falling back to the clock, started
  here and moved there; it has since added ALSA.
- [JackSharpCore](https://github.com/mikeoliphant/JackSharpCore) (`JackSharpCore` 1.1.1, through AudioIOSharp; MIT): a
  .NET wrapper of the JACK audio library, for the JACK support. It's a fork of
  [JackSharp](https://github.com/residuum/JackSharp) by Thomas Mayer, by way of
  [h3ll5ur7er/JackSharp](https://github.com/h3ll5ur7er/JackSharp)'s move to .NET Core, and finds the native JACK
  library with `NativeLibrary` on each platform.

- [AudioPlugSharp](https://github.com/mikeoliphant/AudioPlugSharp) (`AudioPlugSharp`, `AudioPlugSharpVst3`,
  `AudioPlugSharpHost`; MIT): the VST3 bridge, and the stand-alone ASIO host. The host talks to ASIO drivers through its
  own interop code and includes no Steinberg ASIO SDK code.
  - The VST3 bridge (`SpiceForgeVstBridge.vst3`) is built from Steinberg's
    [VST 3 SDK](https://github.com/steinbergmedia/vst3sdk), licensed under the Steinberg VST3 License or, alternatively,
    GPL version 3. SpiceForge's source is MIT; a distributed `.vst3` binary is subject to the Steinberg VST3 License's
    terms for the SDK parts. VST is a trademark of Steinberg Media Technologies GmbH.
- [MonoGame](https://github.com/MonoGame/MonoGame) (`MonoGame.Framework.WindowsDX`, `MonoGame.Framework.DesktopGL`,
  `MonoGame.Content.Builder.Task`; Microsoft Public License): rendering the plugin's editor, and building its content.
- [NAudio](https://github.com/naudio/NAudio) by Mark Heath (MIT): reading and writing WAV files in the renderer,
  validation tools and host. `NAudio.Asio` (3.3.0) is the ASIO support, through AudioIOSharp: its `AsioDevice` duplex mode
  runs one callback per buffer with each channel as a float span, converting to and from the driver's native format,
  and reports driver resets and resyncs. It calls ASIO drivers through its own COM vtable definitions
  (`AsioDriver.AsioDriverVTable`).
- [SVG.NET](https://github.com/svg-net/SVG) (`Svg`, through UILayout's image processor; Microsoft Public License):
  rendering the editor's SVG images.
- [Roslyn](https://github.com/dotnet/roslyn) (`Microsoft.CodeAnalysis.CSharp`; MIT), referenced by the plugin.
- UILayout's image processor also uses SharpFont (`SpaceWizards.SharpFont`; MIT) and StbImageSharp and
  StbImageWriteSharp, at build time only.

### Runtime

- [.NET](https://github.com/dotnet/runtime) and [ASP.NET Core](https://github.com/dotnet/aspnetcore) by the .NET
  Foundation and Contributors (MIT): the packages are self-contained, so they include the runtime. Their license
  texts are bundled (`Assets/Licenses/dotnet.txt`), and the About dialog links their third-party notices.

### Build tools

- [Inno Setup](https://jrsoftware.org/isinfo.php) by Jordan Russell and Martijn Laan (its own free license): builds the
  Windows installer from `SpiceForge.Package/SpiceForge.iss`, as LiveSPICE's installer is built.

### npm packages (build only)

The web front end, `SpiceForgeWebUI`, has no runtime dependencies. These build it, under
[Node.js](https://nodejs.org):

- [TypeScript](https://github.com/microsoft/TypeScript) (`typescript`; Apache License 2.0): the compiler, for type
  checking.
- [esbuild](https://github.com/evanw/esbuild) by Evan Wallace (`esbuild`; MIT): bundling the TypeScript into one
  JavaScript file.

### Assets

- [Inter](https://github.com/rsms/inter) by Rasmus Andersson (SIL Open Font License 1.1): the editor's font.
- The editor's file, edit and reload icons are from Google's
  [Material Symbols](https://github.com/google/material-design-icons) (Apache License 2.0).
- The switch lamps are styled after the PowerOn and PowerOff images of
  [Stompbox](https://github.com/mikeoliphant/StompboxUI).

## Ideas and insight

These didn't contribute code, but they contributed understanding that went directly into SpiceForge's solutions.
`DesignDocs/LessonsLearned.md` has the details.

- [jack2 pull request 889](https://github.com/jackaudio/jack2/pull/889), "JackClient: Lock before killing client
  notification thread", by DeviLaxmii, and its discussion: explained the stall in `jack_client_close` that JACK2 1.9.22
  on Windows showed when a client closed soon after activating, and why JackSharpCore keeps a client active 100 ms.

- [netlist_codegen](https://github.com/jatinchowdhury18/netlist_codegen) by Jatin Chowdhury: the original inspiration
  for the N-port, port-reduced approach. Reading its code generation led to extrapolating Newton's starting guess
  (20 to 45% faster on most real circuits), unrolling the small Newton solve into locals, and paying attention to
  register pressure; its junction limiting prompted the step-limiting experiments.
- Berkeley SPICE3's junction voltage limiting, `DEVpnjlim` in `devsup.c` (carried on in
  [ngspice](https://ngspice.sourceforge.io/)): limiting each p-n junction only once it is pushed past a critical
  voltage, vt·ln(vt/(√2·Is)), and then compressing the move logarithmically. `JunctionCritical` is the same rule in
  units of the junction's exponent. It fixed the Fuzz Face where scaling the whole Newton step did not.
- [LiveSPICE pull request 284](https://github.com/dsharlet/LiveSPICE/pull/284), "Evaluate the solutions after Newton's
  method with the updated values", by jopdorp: describes the stale cached values LiveSPICE uses after Newton, which
  we had reproduced exactly while matching it. That confirmed it was a real error, led to exact rebuild becoming the
  default, and its fix is part of the tightly converged LiveSPICE reference used for validation.
- [LiveSPICE pull request 282](https://github.com/dsharlet/LiveSPICE/pull/282), "Fix the Rockerverb divergence with
  dynamic parameters", by jopdorp: its handling of the parameter block informed how dynamic pots treat roundoff residue
  in the folded coefficients, and its idea of keeping the parameter-dependent block out of the reduction was tried
  here as a low-rank rebuild (it didn't pay off for us; see the lessons).
- LiveSPICE's [`dynamic-parameters`](https://github.com/dsharlet/LiveSPICE/tree/dynamic-parameters) branch: showed
  that runtime pots could be cheap, and its problems (divergence on the SD-1, misbehavior at positions 0 and 1) shaped
  SpiceForge's formulation of pot legs as `v = R·P·i`, which is exact at the end stops.
- SPICE's junction limiting (`pnjlim`), from Berkeley SPICE: the model for limiting Newton's steps on junction ports in
  units of their exponent.
