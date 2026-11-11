# Lessons learned: dynamic pots

What the `dynamic-pots` branch taught us about making pots runtime parameters, without re-solving and without
paying for it in the inner loop.

## Requirements that shaped the design

- **No re-solve, ever.** Even a few milliseconds of symbolic or numeric re-solving when a pot moves is
  unacceptable for realtime use. Everything a pot affects has to be precomputed or updated in time proportional to
  the circuit's linear structure, between blocks.
- **Don't modify LiveSPICE.** Its `dynamic-parameter` branch showed that runtime pots can be cheap, but it diverges
  (on the SD-1, for example) and misbehaves at pot positions 0 and 1. Its low overhead was the target, not its
  formulation.

## Formulation

- **Model each pot leg as a current unknown with `v = R·P·i`.** P (the tapered wiper fraction) only multiplies a
  current, so it never ends up in a denominator: a leg at 0 Ω is an exact short, and positions 0 and 1 are as well
  behaved as any other. This is what fixes the end-stop problems of a conductance (`1/(R·P)`) formulation.
- **Split each leg around a nominal position.** With `v = R·P·i` alone, the leg's resistance never reaches E (the
  matrix of constant coefficients), which can make E singular. A singular E adds free unknowns and constraint rows
  to Newton, which makes it bigger and slower. Writing `R·P = R·P0 + R·(P − P0)` puts the nominal resistance in E,
  so E is nonsingular exactly when the baked circuit's is. The parameter becomes the offset `P − P0`, and the leg
  stays linear in its current.
- **Check that the extractor actually splits mixed coefficients.** The nominal split did nothing at first.
  `Extractor.Split` sent any coefficient that wasn't a pure constant (such as `R·(0.5 + δ)`) wholly to the
  nonlinear terms. Constant parts must go to E, and only the parameter-dependent parts become terms. Lesson:
  confirm a structural fix by measuring the structure (here, the free-unknown count), not just the timing.

## Removing the overhead

- **Pot legs are linear terms; take them out of Newton.** Their gradient depends only on the parameters, so the
  ports only they read can be eliminated exactly (`LinearPortReduction`). With `A = I − Z_LL·Jl` and
  `M = Jl·A⁻¹`, the effective coefficients are the circuit's own coefficients at the current pot positions. The
  per-step work is then the same as with the pots baked in.
- **Recompute only on change, once per block.** Comparing the parameter values at the start of each `Run` block
  is effectively free. Recomputing the reduction costs a small dense solve over the pot-leg ports and is still
  only done when a pot moves.
- **Read live coefficients only where they can change.** The sparsity patterns are learned by evaluating the
  reduction at a few random parameter settings and OR-ing the nonzeros. Only unknowns a linear term reaches
  ("affected" rows) read live arrays. Everything else keeps its inlined constants.
- **Load array constants into locals.** In compiled expression trees, an array constant is a closure lookup and a
  cast on every use. Load each live array, and each row of it, into a local once per step, outside the Newton
  loop.

## Finding structural cost

- **Separate the cost of live reads from the cost of structure.** Compiling the dynamic circuit with its live
  coefficients inlined as constants (`InlineLiveForTesting`) and running the baked circuit through the reduced
  path (`ForceReductionForTesting`) split the overhead into its parts. Without those two controls, the overhead
  looked like one number with no explanation.
- **Node counts don't predict time.** The dynamic and baked step functions had nearly identical expression trees
  and identical iteration counts, yet one was twice as slow. Benchmarking the step delegate alone on a fixed
  state, then deleting parts of its expression tree, recompiling and retiming, pinpointed the cause in a few runs.
- **Read the JIT's output when the arithmetic doesn't add up.** `DOTNET_JitDisasm=lambda_method*` with
  `DOTNET_JitStdOutFile` works for compiled expression trees. It showed the real problem:
  - Computing five pot-leg currents that nothing reads kept extra values live across the Newton loop.
  - x64 has 16 XMM registers, so the JIT spilled the running sum of each long linear combination through a single
    stack slot on every term.
  - That put a store-to-load forwarding delay on the critical path: about 100 ns per step on the TS-9, which was
    most of the gap.
- **Compute only what something reads.** Unknowns nobody reads still cost registers. The fix was not an
  arithmetic optimization: the reduced path stopped computing the eliminated ports and unknowns that only exist
  for LiveSPICE matching.
- **Rule things out quickly and explicitly.** Subnormal coefficients, subnormal state, recomputation per block,
  measurement order and hoisted loads were each checked and eliminated with a one-line measurement, rather than
  argued about.

Results with pots held still (µs per sample): TS-9 1.28 dynamic vs 1.06 baked (2.61 before), SD-1 3.60 vs 1.84
(5.19 before). On larger circuits (Big Muff, Rat), the inlined dynamic step is faster than baked, so what remains
there is the cost of the live reads.

## Residue in the live set

- **Count what the compiled step actually reads.** The SD-1 marked 283 live gains against 213 baked nonzeros, which
  looked like inflation. But 59 belonged to the pot-leg currents, which nothing reads, so the step never loads them.
  Counting array reads in the emitted expression tree (`--timing` prints it) is the reliable number.
- **Learning a sparsity pattern numerically picks up residue.** Folding the eliminated legs leaves entries at or below
  1e-17 of their row: a fifth of the Big Muff's live reads, and some on the TS-9, Blues Breaker and Rat. LiveSPICE
  pull request 282 hit the same thing with its parameter block. On the SD-1 there was none.
- **Most residue isn't a cancellation where you can see it.** The test from that PR, an entry tiny compared with the
  terms summed to make it, caught only 6 entries in total, even after including the eliminated ports' own gains.
  The rest arrives already as residue, from Z, G or A⁻¹.
- **Size relative to the row isn't safe everywhere.** In a high-gain circuit (the Rat), dropping even entries at 1e-16
  of their row from the Newton coefficients or the term gains changed the solution: +21 dB and 400 capped steps.
  Applied only to the gains that rebuild the unknowns from the step inputs, at 1e-16 (below the rounding error the
  row's sum already carries), it was safe on every circuit. Test a threshold on the most sensitive circuit, and per
  array.
- **Hold dropped entries at zero in every engine.** Once learning ends, the reduction zeroes every unmarked entry,
  so the interpreter and the compiled step still agree exactly.
- **A dropped term saved more than a load.** Removing about 8% of the Big Muff's reads cut 6 µs per sample (33.2 to
  26.8, against 25.5 baked), about 4.5 ns per term per step. None of the dropped values were subnormal. The serial
  running sums are the likely reason, but that isn't confirmed.

Results after dropping residue (µs per sample, pots still): Big Muff 26.8 dynamic vs 25.5 baked (33.2 before),
Blues Breaker 5.7 vs 5.3 (7.6), TS-9 1.27 vs 1.28, Rat 14.5 vs 13.2 (15.1), SD-1 unchanged at 3.70 vs 2.10.

## Register pressure in the reduced path

- **Compare paths on the same circuit.** Running the baked SD-1 through the reduced path cost 2.95 µs against 1.99 on
  its normal path, with no pots at all. `profile <circuit> --paths` compares the two emitted steps. Total work was
  about the same, but the reduced path did 176 more multiply-adds before the Newton loop and 167 fewer after it.
- **Compute only what the loop reads before the loop.** The reduced path computed the linear part of every needed
  unknown before Newton, so they all stayed live across the loop. Moving the ones only read afterwards to after the
  loop, as the normal path does, took dynamic SD-1 from 3.70 to 2.35 µs.
- **Don't load every input into a local.** The SD-1 has 15 history inputs, and x64 has 16 XMM registers. As locals
  they were all live through the linear parts, so the JIT spilled each running sum to one stack slot on every term:
  168 of the 239 stack stores in one step went to the same slot. Reading history values from their arrays where
  they're used costs nothing, because a multiply can take its operand from memory. The reduced path went from 2.95
  to 2.02 µs, and baked circuits got faster too (Rat 13.2 to 9.6, Big Muff 25.5 to 20.2).
- **A faster live build than inlined one is a clue.** After the first fix, the step with live arrays (2.35 µs) beat the
  same step with constants inlined (3.48 µs). Array loads happen where they're used, while the constants version
  kept every input in a register. Numbers that run backwards like that point at register allocation, not arithmetic.
- **The same spill pattern keeps coming back.** It's the third time a long linear combination has spilled its running
  sum through one stack slot. When a step is slower than its operation count suggests, count stores per stack slot
  in the JIT's output first.

Results after these fixes (µs per sample, pots still): SD-1 about 2.35 dynamic vs 1.95 baked (3.70 vs 2.10 before),
Blues Breaker 3.9 vs 3.3, Rat 10.3 vs 9.6, Big Muff 24.2 vs 20.2.

## Validation

- **Measure where validation time goes before optimizing it.** The guess was symbolic work. In fact, simulation
  dominated: profiling timings, the fixed-setting runs through the interpreter, and the tightly converged
  reference sweep.
- **Keep profiling out of validation.** Best-of-3 timings were 38% of the Big Muff's validation time. They now run
  only with `--timing`.
- **Cache references that don't depend on the code under test.** The tightly converged sweep depends only on the
  circuit and the sweep, so it is cached in `.validate-cache/` (`--refresh` recomputes it). A change to a subcircuit
  file isn't part of the cache key.
- **Parallelize with processes, not threads.** Nothing guarantees LiveSPICE's computer algebra is thread-safe.
  One child process per circuit (`--jobs auto`) is simple and safe. The full dynamic suite went from over
  12 minutes to about a minute.
- **Have a quick subset for iteration.** Six circuits (`--quick`) cover the main structures: one diode pair, free
  unknowns, a large circuit, high gain, and linear only.
- **Know which failures are already there.** The Unstable folder (Fuzz Face, Orange Squeezer, Triode stage) fails
  with pots baked in too, and the Rat's high-drive settings hit the iteration cap either way. Compare against
  earlier runs before calling something a regression.
- **Know what each reference can and can't catch.** The tightly converged reference is our own engine with a far
  stricter stopping rule. It shares our extraction and discretization, so it only measures Newton stopping error:
  a bug in `Extractor` or in the BDF2 history would show up in it too. LiveSPICE's own `Simulation` is the only
  independent check of that front end. Sample-exact matching (`matchLiveSpice`) isn't needed for that, though; a
  tightly converged LiveSPICE at fixed pot settings does it (see below).
- **A regression oracle should test the path that ships.** Matched mode only runs with pots baked, on the normal
  Newton path. Once dynamic pots are the default, it no longer exercises production code.

- **Converge both sides, and an independent reference becomes precise.** `livespice` mode runs LiveSPICE's own
  solver (the instrumented copy, with the pull request 284 fix, 1e-10 tolerance and up to 100 iterations) against
  our dynamic engine at the same tolerance, at four pot settings including both end stops. Every circuit in
  `Circuits` and `Examples` agrees to −147 dB or better, most to −170 to −250 dB. Even the Rat agrees to −170 dB:
  once neither side has stopping error or the stale-values bug, its sensitivity no longer matters.
- **Keep the reference's changes opt-in and verifiable.** The copy's tolerances and the fix default to stock, and the
  capture tool still confirms it is bit-identical to stock `Simulation` with those defaults. The project references
  only LiveSPICE, so the reference can't reach our code.
- **An independent reference finds robustness gaps, not just bugs.** On the Fuzz Face, our Newton hits the 100-iteration
  cap on hundreds of steps where LiveSPICE's caps on at most 10, with a clean noise control. Our own tight reference
  couldn't have shown that, since it runs the same Newton.

## Making dynamic pots the default

- **Put loading in one place first.** Thirteen call sites each loaded, solved and extracted a schematic their own
  way, so "dynamic by default" had no single place to live. `CircuitLoader.Load` is now that place, with
  `dynamicPots: false` to bake pots in.
- **LiveSPICE's solve still needs a baked copy.** The engines use its solution for the stopping rule and DC start,
  and LiveSPICE can't solve with symbolic pots, so the loader builds the circuit twice. The stock engine always
  runs baked.
- **Default only where it works, and say why elsewhere.** Segment recording and black-box segments don't support
  dynamic pots, so `SpiceForgeSegments` bakes them explicitly, with a comment saying so. Making it dynamic would
  have broken it silently.
- **A dynamic default needs a way to set positions.** Without `--pot`, the renderer's dynamic pots sat at their
  schematic settings, costing 10–20% for nothing. Pot positions are applied at load, by name or group, so the
  dynamic, static and stock engines all see the same setting.
- **Check equivalence on real renders.** Dynamic and baked renders agree to −140 dB or better (the resolution of
  32-bit float output) on five circuits, including the SD-1 with a pot at an end stop.

## What netlist_codegen does differently

`netlist_codegen` (the original inspiration for the N-port approach) makes every component value a runtime
parameter, recomputing all coefficients once per block from closed-form symbolic expressions. Its resistors are
conductances, so it has LiveSPICE's end-stop hazard, not our formulation. What's worth learning from it is code
generation:

- **It models register pressure.** Its CPU profiles include register counts, and it estimates peak live values to
  choose between per-block affine coefficients and per-sample expressions, and between Cramer's rule and an
  unrolled LU. Our two largest speedups this branch were register-pressure problems found by hand.
- **It inlines the Newton step solve.** The k×k pivoted LU is unrolled with constant subscripts in locals. Ours calls
  `SolveStep` on arrays in memory every iteration. Not yet tried.
- **It extrapolates Newton's starting guess and limits junction steps** (SPICE's pnjlim). We start from the
  previous sample, which was only needed to match LiveSPICE. Not yet tried.
- **Its segmentation is manual.** Automatic clusters are undirected union-find, coarser than our strongly
  connected components. Real stage splits come only from `* sub-circuit:` directives, which drop the loading
  across the cut and are exact only at ideal-source nodes. Its own tests cut only at ideal op-amp outputs.
- **Ideal sources make exact cuts.** Part of why LiveSPICE's circuits don't split is that its op-amp macromodel has a
  50 Ω output. With ideal outputs, our strongly connected components should split stages exactly (untested).

## One Newton path, and segments on it

- **A baked circuit is a reduced circuit with nothing to eliminate.** Every circuit now runs the reduced path. With no
  pot legs, its folded coefficients are the original ones, so its segments, recordings and lookup tables came out
  identical to the old path's.
- **Compile what doesn't vary as constants.** The reduction records which folded coefficients change between its
  learning settings, and the compiled step inlines the rest. Without that, a baked circuit read every Newton
  coefficient from an array and ran up to 10% slower. Both engines hold the invariant coefficients at their learned
  values, so they still agree bit for bit.
- **Tiered compilation can hide in a benchmark.** The TS-9 ran 25% slower through the reduced path with nearly
  identical machine code. With tiering off, both paths were equal: the compiled step's `SolveLinearStep` helper was
  still at tier 0, because on the reduced path the interpreter (run first) never calls it. Marking hot helpers
  `AggressiveOptimization` fixes it.
- **Don't share mutable arrays between stages.** The reduction kept the interpreter's `Z` itself, and clearing `Z`'s
  roundoff entries for the port groups then changed what the reduction folded whenever a pot moved after
  construction. It broke a dozen dynamic circuits, twice, before the reduction got its own copy. The tests had mostly
  held pots still, which is why it hadn't shown earlier.
- **A segment needs only the pots that change its solve.** Each pot is moved to either end, and a pot becomes a
  segment input only if the segment's Newton coefficients change (with the change recorded as its sensitivity). On the
  SD-1 that's Drive alone; the full-system segments took all three pots and five pot-leg ports.
- **A pot input needs its range in the samples.** Recording with Drive held still gives a 1-D table that can't vary
  Drive. `SpiceForgeSegments --sweep Drive` sweeps it during recording, and the SD-1's inputs then span exactly two
  dimensions (signal × Drive): a 2-D table, 2× faster than dynamic Newton and stock LiveSPICE compiled.
- **Give each table axis the resolution it needs.** At 256 × 256 the SD-1's table was −68 dB from a tightly converged
  render, limited by interpolation. The signal axis is sharp and the Drive axis smooth: 4096 × 64 points gave −109 dB,
  and 64 × 4096 gave −43 dB. Axes that are mostly a pot now get 64 points by default.
- **Failed grid points can be harmless.** Thousands of points in the table's corners don't converge, because the
  samples' bounding box includes signal and Drive combinations the circuit never reaches. No lookup landed there.

- **Remove a path only once nothing depends on it.** With segments on the reduced path and the tightly converged
  LiveSPICE reference in place, the normal Newton path, LiveSPICE-matched mode, its post-Newton evaluator and the
  sample-exact LiveSPICE checks (reference captures, coverage, pot settings, step traces) went in one change, with
  every remaining check at its old baseline. Each engine now has one Newton path plus black boxes.

## Stage segmentation

`idealopamp` swaps op-amp macromodels for ideal ones and compares the segments; `cut <schematic> <wav> <node>
[--supply nodes]` splits a schematic at a node into an upstream half (driving) and a downstream half (driven by an
ideal source following the cut node, on top of its DC bias), runs them chained at the oversampled rate, and measures
the error against the uncut circuit, with no load or a linear copy of the downstream input network as the load.

- **Ideal op-amps don't create splits here.** The pedals' op-amps are already ideal in most schematics (SD-1, TS-9),
  and their single clipping stage is one coupled group by physics. Making the Rat's macromodel ideal only removes its
  own rail diodes (7 ports to 5). The circuits with several nonlinear stages have no op-amps between them.
- **Cut where the downstream device draws no current.** At the Rat's JFET buffer gate, a cut with no load model is
  −150 dB from the uncut circuit, though our Newton treats both stages as one 7-port group (the JFET's gate-diode model
  couples them, negligibly). At the JCM800's cathode follower output, feeding a passive tone stack, a linear load gives
  −239 dB. A purely linear downstream is exact with the linear load.
- **The linear load model works when the downstream devices draw negligible current.** At the Rat's clipping node it
  copies the tone network up to the JFET gate: no load −39 dB, linear load −194 dB.
- **Loaded device stages don't cut.** In the Big Muff, every cut between BJT stages is worse than the signal level:
  base current is part of each stage's load, and a cut at Q2 also severs its collector-base feedback. In the JCM800,
  grid cuts are −14 to −25 dB, because grid current at these levels is part of the overdrive. A load model would need
  the downstream stage's input device itself (overlapping stages by one device), which isn't built.
- **Supply networks join every stage.** Tube stages share a B+ decoupling chain, so no node separates them until the
  supply network is copied into each half (`--supply`); that's a real but small approximation (no inter-stage sag).

## Newton's starting guess

- **Extrapolate the starting guess.** Starting Newton from 2·y[n-1] - y[n-2] instead of y[n-1], as netlist_codegen
  does, cut the iterations from about 2 per step to 1.1-1.3 on every nonlinear circuit, with the same accuracy
  against tightly converged references. Compiled, most real circuits got 20-45% faster (Fender 5e3 28.2 to 17.9 µs,
  now 1.5× stock; Phase 90 32.9 to 20.2; SD-1 1.39 to 0.95).
- **But give it a way back.** Extrapolating through a kink overshoots: the Rat at hot input went from a handful of
  capped steps to thousands, and its baked and dynamic engines diverged. Restarting once from the previous step when
  Newton hasn't converged after 3 iterations restored the Rat's capped-step counts exactly, while the steps that
  converge keep the saving. netlist_codegen limits junction voltages instead, which needs per-device knowledge.
- **It doesn't fix every robustness gap.** The Fuzz Face still caps where LiveSPICE's Newton doesn't, and it and the
  Orange Squeezer run slower with the restart firing.

## Inlining the Newton step solve

- **Unroll small solves into locals.** Newton systems of up to 8 unknowns are now solved by one small C# routine
  (Gaussian elimination with row swaps), which compiled steps emit unrolled on local variables instead of calling a
  solver on arrays; the interpreter calls the routine itself, so both still agree to the bit. The SD-1 went from 1.87
  to 1.43 µs baked, and the JIT benchmark's median speed against stock from 0.73× to 0.91×.
- **Not too large.** Unrolling up to 12 made the Big Muff's 10-port solve slower (15.7 to 18.0 µs baked).

## Dynamic switches

- **A switch contact fits the same scheme as a pot leg.** Each throw is a leg with its own current, obeying
  (1/2 - δ)·v = (1/2 + δ)·R0·i: closed (δ = -1/2) is v = 0, an exact short, and open (δ = +1/2) is i = 0, exactly
  open. Written as v - R0·i = 2δ·s with an auxiliary unknown s = v + R0·i, δ multiplies a single dedicated port, which
  the reduction always eliminates; otherwise a node a diode also reads would pull the switch into the Newton solve.
  Every combination of switch positions in the test circuits agrees with the baked circuit to -193 dB or better, and
  the circuits stay stable when switched every 0.1 s.
- **The nominal resistance matters for accuracy, not correctness.** E holds each contact at R0, and folding it to a
  short or an open puts roundoff on tiny nearby nodes. On the Blues Breaker, 10 kΩ left millivolt nodes at -41 dB from
  LiveSPICE; 10 MΩ passes everywhere.
- **Open exactly; tie nodes that only switches touch.** A finite open resistance leaks (10¹⁵ Ω put the Bassman's
  high-impedance nodes at -93 dB from 325 V of B+) or, made larger, makes nodes held by one device too stiff for
  Newton (10¹⁸ Ω: -67 dB). Opening exactly is fine except for a node only switch contacts touch, which would be
  undetermined: those get a 10¹⁵ Ω tie to ground, which leaves them at 0 V when floating, as LiveSPICE reports them.
  Opening those contacts to a finite resistance instead joined the switches' commons through the node.
- **A throw that connects to nothing carries no current.** Its contact is skipped, as LiveSPICE drops such a node.
- **Compare by node name, and only nodes that mean something.** A closed contact merges nodes in LiveSPICE's baked
  circuit, so node lists differ in length and order. Comparing by index made correct results look 100 dB wrong, twice.
  Nodes only switch contacts touch have no role in the circuit and aren't compared against LiveSPICE.

## Automation in the renderer

`--pot-at` and `--switch-at` move a pot or switch at a given time; `--pot-ramp` spreads pot moves over a few ms.

- **Parameters change between runs, so split the block at each event.** The engines read new parameter values when a
  `Run` starts, so the renderer cuts each block where events fall and runs the pieces, which puts every change on its
  exact sample in every channel. Each change recomputes the folded coefficients once, which is cheap.
- **A no-op event is the test that splitting is harmless.** Setting a pot or switch to the position it already has,
  in the middle of a block, has to leave the output bit-identical; a real change has to leave it identical up to the
  event and differ from exactly its sample on. Both held for pots and switches.
- **A ramp is a line of small steps.** A ramped move is a new pot position every 32 samples, from wherever the pot is
  to its target. A later move of the same pot starts from where the ramp has got to and drops the rest of it, so
  overlapping moves can't fight. A ramp of 0 is the instant case, bit for bit.
- **Measure what the change does, not the waveform's steps.** The largest sample-to-sample step barely moved with a
  ramp (0.401 against 0.393), because the SD-1's clipped wave has steeper edges of its own than any click. Its gain
  relative to the unchanged circuit, per millisecond, showed the difference plainly: a jump from 1.00 to 2.00 at once,
  against a steady rise over the 10 ms ramp.
- **Check names and values before rendering.** Every event is applied once to a scratch array before the render
  starts, so a misspelt pot or an out-of-range position fails at once with the list of what exists, rather than
  partway through a long file.

## Open issues

- Live coefficient reads remain a cost on large circuits: the Big Muff's dynamic step is about 3 µs slower than baked
  (18.5 against 15.7). Keeping the solved pot-leg block factored, as LiveSPICE pull request 282 does, might reduce the
  live set further.
- Stage cuts across loaded devices (BJT bases, conducting tube grids) need a load model that includes the downstream
  stage's input device; `cut` has only no load and a linear load.
- Our Newton is less robust than LiveSPICE's on the Fuzz Face (hundreds of capped steps against at most 10), even with
  the extrapolated guess and restart. Junction limiting, as netlist_codegen does, is a candidate fix. The Phase 90's
  sweep also has capped steps that aren't explained.
- Lookup tables over a pot need recordings that sweep it (`--sweep`), and are built in the table's bounding box, so
  thousands of corner points the circuit never reaches don't converge (harmlessly, so far).
- Switches move instantly, even with `--pot-ramp`, which only ramps pots. The jump causes a large transient, and on the
  Blues Breaker and JCM2000 Newton caps a few steps there (a switched run is -22 and -70 dB from a tightly converged
  one). Real switches pop too, but a short crossfade, or ramping the contact's δ, may be worth having.
- Automation lives in the renderer only; a host (the VST plugin, say) would drive ParameterValues the same way.
- The compiled step has no register-pressure estimate; spill traps are found by reading the JIT's output.
