# Lessons learned

What the work so far has taught us, roughly in order: the eight milestones in `PLAN.md`, the singular-E fix, the
switch to exact rebuild, and segment black boxes with lookup tables; then making pots and switches runtime parameters
without re-solving (the `dynamic-pots` work), automation, the host-facing `SchematicProcessor`, and a look at what
oversampling buys.

## Starting out

- **The plan was written from reading code, and parts of it were wrong.** Its core premise held: port-reduced Newton
  produces the same iterates as LiveSPICE's full Newton (to about 1e-14 relative). Its speed premise didn't (see
  Performance). Treat a plan built without compiling anything as a set of hypotheses for the milestones to test.
- **LiveSPICE can stay unmodified.** `Schematic.Build()` → `Circuit.Analyze()` exposes the flat MNA equations,
  unknowns and initial conditions publicly. Everything we replace starts after that, so the submodule stays pinned
  with no patches. Classes like `Potentiometer` aren't sealed, so components can be swapped after `Build()` when
  needed.
- **Verify the reference before trusting it.** Counting Newton iterations needed a copy of `Simulation` with one
  recording call. Every capture checks that the copy's output is bit-identical to the stock class, so the reference
  data is LiveSPICE's own output, not ours.
- **Know which circuits LiveSPICE itself can't run.** `Unstable/Triode stage` fails in LiveSPICE's own solve
  ("Failed to eliminate differentials"), and `Fuzz Face` and `Orange Squeezer` are unstable there too. Counting those
  as known failures from milestone 1 kept them from being chased as bugs later.

## Extraction

- **Infer ports from the equations rather than from device types.** Ports are a basis of the span of the gradients
  of q. That handled the triode's inline Child-Langmuir current, the diodes hidden inside the `OpAmp` macromodel and
  the JFET's `Min(Source.V, Drain.V)` with no device-specific code.
- **Sample across magnitudes to find the span.** Piecewise models only reveal all their branches when evaluated over
  a range of voltages. A branch that is rarely reached could still be missed, so the check uses wide sampling.
- **Validate the split at random points.** Two checks caught extraction errors before any simulation: the split form
  reproduces the original equations (worst 3.6e-16 relative), and q doesn't change when y moves without changing the
  ports.
- **An unknown with no equation is harmless if nothing reads it.** The Marshall Blues Breaker's extra unknown was an
  unused `Bypass` switch control. LiveSPICE leaves it at its previous value, so it can be dropped.

## Matching LiveSPICE

- **Matching depends on undocumented details.** LiveSPICE's `CodeGen` caches subexpressions, and `Simulation`
  never clears that cache after Newton. So the unknowns computed after the loop use values from before the last
  update. Reproducing that took most errors from about −55 dB to about −220 dB. The only way to find it was to read
  how the compiled code is generated.
- **Keep the iterates, not just the answer.** Solving coupled groups to convergence one after another (Gauss-Seidel)
  reaches the same fixed point by a different path, so capped samples differ. A joint Newton loop that solves each
  step block by block keeps the iterates exactly.
- **Use LiveSPICE's own linear algebra where it touches the result.** Switching to its SIMD elimination and clearing
  entries that exact arithmetic would make zero both mattered at the bit level. Clearing a 9e-15 roundoff entry in Z
  changed the Orange Squeezer's result, which shows how sensitive that circuit is.
- **Matching LiveSPICE isn't the same as being accurate.** A tightly converged reference (1e-10 tolerance, 100
  iterations) showed that LiveSPICE's post-Newton cache quirk is a real error. The quirk accounted for the Phase 90 at
  −14.3 dB from the converged solution against −172 dB for an exact rebuild, and for the Fender 5e3 at −16.5 dB against
  −51 dB. Our matched engine had been reproducing that error faithfully. LiveSPICE pull request 284 describes the same
  bug. Exact rebuild is now the default, and `matchLiveSpice: true` stays as a regression tool.
- **Rebuild unknowns from q, not from q(w).** In high-gain circuits G is large, so evaluating q at a converged but
  inexact w isn't accurate enough. Rebuilding with the nonlinear-term values Newton settled on is.

## Sensitivity and controls

- **Every comparison needs a noise control.** Rerunning stock `Simulation` with roundoff-sized input changes shows how
  far LiveSPICE moves from its own output. Runs where it moves past the threshold are classed as sensitive, not
  failed. Without that, chaotic circuits look like bugs.
- **Make the control strong enough.** Scaling the whole input by 1 part in 1e15 was too gentle. Random per-sample
  noise is the honest control. Neither works when the input is essentially zero, such as the Orange Squeezer failing
  on its first step from DC.
- **Trace step by step before guessing.** On the Rat at 0.5 input, our error tracked stock-plus-noise at a constant
  ~70× from step 4,600 to 28,000, and then broke away when one step took 6 Newton iterations instead of 5. That
  pinned the mechanism (sensitivity plus a different stopping decision), even though the full −10 dB gap stayed
  unexplained.
- **Exact arithmetic isn't automatically better.** Exact rational coefficients improved early Rat steps by 10×, but
  made later drift 3× worse and hot runs worse overall, so that change was reverted. The same idea later helped the
  singular-E circuits a lot. Measure each case.

## Singular E

- **Fix the formulation, not the symptom.** A singular E was first shifted by a small diode linearization. That left
  a condition number of about 3.7e12, and on long recordings the SD-1's node between two reverse-biased series diodes
  ran away to ±9e26 V. Exact coefficients cut the bad windows from 58 to 14 but didn't remove them. A fallback to the
  best iterate on capped steps made things worse: after it fired once, Newton diverged on nearly every later step.
- **Split E exactly instead.** Unknowns E can't determine join the Newton unknowns. The rows E can't satisfy become
  constraints on the nonlinear terms alone, such as "the two diode currents are equal". Those cancel exactly, so the
  floating node can't run away. The SD-1 went to 2 of 1,900 bad windows, both between two non-converged results, and
  the iterates still match LiveSPICE in exact arithmetic.
- **Short tests don't find this.** The SD-1 passed the 4,800-sample reference at −157 dB while failing on a 190 s
  recording. Rendering real audio through both engines (`SpiceForgeRenderer`, `validate wav` and `wavtrace`) is a
  separate check.

## Performance

- **Compare against what LiveSPICE actually solves.** The Rat's 18 Newton unknowns looked like a big target for 7
  ports, but LiveSPICE's symbolic pre-solve already reduces its real solve to 8. Under exact matching, port reduction
  gains little: the same iterations, the same post-Newton work, a nearly identical system size. The first JIT ran at
  0.61× stock (median).
- **Skipping unread unknowns helped most.** LiveSPICE computes every current even when one output is used. Computing
  only what outputs and state need, inlining coefficients and keeping Newton values in locals took the median to
  0.75×, with large tube amps up to 1.32× faster than stock.
- **Benchmark the way LiveSPICE does.** Requesting every node hid the biggest saving. Benchmarks use one output.
- **Small and linear circuits are hard to beat.** Tiny circuits cost under 0.2 µs per sample either way, and call
  overhead dominates. LiveSPICE substitutes numbers into simplified expressions for linear circuits, which beats table
  lookups.
- **Inlining conflicts with runtime parameters.** Inlined coefficients were a big win, against the plan's "keep
  matrices in runtime arrays". Dynamic pots had to bring live reads back for the coefficients they affect.

## Segments and black boxes

- **Exact segmentation finds very little.** Under exact coupling, a stage's output loads its input, so almost every
  real circuit is one coupled group. Only three trivial circuits split, and the singular-E constraints later merged
  those too. Real segmentation needs approximate cuts, which haven't been started.
- **The Newton solve is a memoryless map.** Each step's solve is a fixed function from a few inputs (open-circuit
  port values, constraint constants, upstream ports) to the ports, free unknowns and nonlinear-term values. All state
  stays outside. That makes it a natural target for a lookup table or a network, with no cuts needed.
- **Verify the contract by replay.** Plugging the recorded outputs back in as a black box and reproducing the
  circuit's own output exactly proves the recorded inputs and outputs are everything a box needs.
- **Effective dimension is often tiny.** The SD-1's 4 inputs are exactly one-dimensional (two constant, two exact
  negatives), and the Rat's 7 span about 2. Measure it before choosing a model.
- **Build tables from the circuit's equations, not recorded data.** Each grid point is solved to tight tolerance,
  stepping from solved neighbours. The SD-1 table beat normal Newton against the converged render (6 bad windows of
  1,900 against 71), because Newton stops early on capped steps. Raising the resolution 16× changed nothing, so
  interpolation wasn't the limit.
- **Measure speed where it will really run.** The interpreter showed a modest gain. Compiled, the SD-1 table runs at
  0.59 µs per sample: 2.6× faster than compiled Newton and 2.3× faster than stock.
- **A black box can't be combined with LiveSPICE matching.** The post-Newton quirk needs LiveSPICE's Newton loop.

## Dynamic pots: requirements

The `dynamic-pots` work made pots runtime parameters, without re-solving and without paying for it in the inner loop.
The sections from here to Oversampling come from that work and what followed it.

- **No re-solve, ever.** Even a few milliseconds of symbolic or numeric re-solving when a pot moves is
  unacceptable for realtime use. Everything a pot affects has to be precomputed or updated in time proportional to
  the circuit's linear structure, between blocks.
- **Don't modify LiveSPICE.** Its `dynamic-parameter` branch showed that runtime pots can be cheap, but it diverges
  (on the SD-1, for example) and misbehaves at pot positions 0 and 1. Its low overhead was the target, not its
  formulation.

## Dynamic pots: formulation

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

## Limiting Newton's steps on the Fuzz Face

An experiment, in the interpreter only (`StepLimit`, `StepLimitAfter`, off by default): when an iteration would move
any port by more than a limit, scale the whole step down to it, the generic version of SPICE's junction limiting.
Counted over 0.5 s of a 440 Hz sine at 0.1, 0.5 and 1.0 input, at the default stopping tolerance:

- **The Fuzz Face's capped steps cascade at a low cap.** Unlimited, it capped 395, 24 and 8,713 steps at a cap of 8, 248
  to 492 at 32 and 6 to 13 at 100: a step that stops unconverged starts the next one badly.
- **Limiting every iteration needs iterations to spend.** At a cap of 100, a 0.1 or 0.3 V limit removed every capped
  step. At a cap of 8 it broke the circuit (non-finite output) at every level, because the first steps out of the DC
  solution move the junctions further than 8 limited iterations can go. A 1 V limit failed even at 100.
- **As a fallback it works at a cap of 32.** Limiting to 0.5 V only once a step has spent 3 iterations left ordinary
  steps alone and took the Fuzz Face to 0, 0 and 19 capped steps (from 248 to 492), and the Orange Squeezer from 1 to
  0, with the Rat, Big Muff and SD-1 unchanged at 0.
- **But one limit in volts doesn't suit every device.** The same fallback took the Fender 5e3 from 0 capped steps to
  475 to 3,999: tube ports legitimately move many volts in a step. That is why SPICE limits per junction, scaled to
  its thermal voltage, rather than every port alike. A version that limits only the ports feeding exponential
  junction terms would need to know which those are; the step limit stays an experiment until then.
- **Limit junctions in units of their exponent.** `JunctionLimit` finds every `Exp` whose argument is linear in the
  ports (a junction's V/(n·Vt), about 40 per volt) and limits each port's move by its largest slope there, so ports
  that drive no exponential, or only through the triode's nonlinear argument, aren't limited. As a fallback after 3
  iterations at a cap of 32 it left the Fender 5e3 at 0 capped steps, and a limit of 10 took the Fuzz Face to 0, 0 and
  161 (from 248 to 492); a limit of 4 broke it at power-up. Neither limit survives a cap of 8 on the Fuzz Face: the
  junctions' first moves out of the DC solution need more limited iterations than that.

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
- **Ramp a switch in log-resistance, not in its parameter.** A contact at δ = 0 is R0 = 10 MΩ, so a straight ramp in
  δ keeps it effectively open until the last moment: an instant switch, just later. `SchematicProcessor` (default
  5 ms) and the renderer (`--switch-ramp`, default 0) sweep each changing contact's resistance geometrically from 1 GΩ
  to 1 Ω, or back, and land exactly open or closed. The reduction already learns its sparsity at random parameter
  values in [−0.4, 0.4], so in-between contacts need nothing new.
- **Most switches don't pop, and a ramp only softens the ones that do.** With silence in, the Blues Breaker's Bypass,
  the JCM800's C1 and the Bassman's Bright make no transient at all. The JCM2000's OD1/OD2 shifts a bias: a 5 ms ramp
  lowers its largest step by 5 to 7 dB (−45 to −50 dBFS) and the click's energy by 2.5 to 6 dB, and the rest is the
  circuit re-biasing, which no ramp removes.
- **Switching barely troubles Newton at the iteration cap.** Against a cap of 64, a cap of 8 differs by −79 to −103 dB
  in the 50 ms around each flip at full-scale input, ramped or not, and not at all in steady state. The earlier report
  of a switched run at −22 dB came from a tighter stopping tolerance, not from steps hitting the cap.
- **Check names and values before rendering.** Every event is applied once to a scratch array before the render
  starts, so a misspelt pot or an out-of-range position fails at once with the list of what exists, rather than
  partway through a long file.

## A processor for hosts, without allocation

`SchematicProcessor` is the interface a host (the VST plugin) uses: it loads and compiles a schematic, lists its pots
and switches as controls, and processes blocks. The renderer now runs through it, with bit-identical output.

- **Controls are latched at the start of a block.** A UI thread sets a control's position; the audio thread reads the
  targets once per block, so every channel sees the same ones, and pot moves are smoothed in 32-sample steps. Writing
  straight into an engine's parameter array mid-block would race with the step reading it.
- **Starting positions belong to loading.** A preset's pot and switch positions are applied before the DC operating
  point is solved. Setting them on a running processor would start the circuit at the schematic's settings and then
  move it, with a transient.
- **A log that's harmless in a test leaks in a host.** The engines recorded one byte of Newton iteration count per
  oversampled step, about 380 KB a second at 48 kHz, which a plugin left running for hours can't afford. A host turns
  it off.
- **Compare the same arithmetic.** The processor first looked wrong on every switched circuit; the reference divided
  each speaker's voltage by V0dBFS where the processor multiplies by its reciprocal, a one-ulp difference.

Making pot and switch changes allocation-free:

- **Measure bytes, after a warm-up.** `GC.GetAllocatedBytesForCurrentThread` around blocks that move every pot and
  switch, after a few blocks to JIT everything and size the buffers, gives a number that must be exactly 0. Then
  measure the pieces separately (engine, coefficient update, processor) to find where it comes from.
- **The folded-coefficient update was the big one.** It allocated work matrices, a copy of the parameters, and a
  closure for every small lambda reading a port value. Work arrays are now allocated once, and the port values come
  from column vectors precomputed when the reduction is built.
- **Small things allocate too.** Converting a method to a delegate on every call (`Update(values, LinearJacobian)`)
  allocates each time; `foreach` over an `IReadOnlyList` boxes its enumerator, except over an empty array, which hid
  it on circuits without controls; and `Any(p => ...)` allocates a closure.
- **A lambda's closure is created where its captured variables live.** A buffer-growing lambda that captured a
  method's parameter made the compiler allocate the closure at the start of every call, though the branch using it
  almost never ran. Moving the allocation into its own method fixed the last 24 bytes per call.
- **The test can be the allocator.** The final 64 bytes a block were the check's own `foreach` over the controls.
- **Turn a bad setting away; don't fail the circuit.** A pot setting that made the reduction's small system singular
  threw on the audio thread, and a nearly singular one would have filled the coefficients with NaN. The update now
  checks the solve before writing anything and, if it's singular or gives non-finite values, keeps the previous
  coefficients and counts a rejection (it still throws while learning at construction, when there's nothing to keep).
  `validate rejection` makes every parameter NaN, then infinite, on each of the 20 circuits with eliminated ports,
  and checks the coefficients stay bit-identical and come back exactly when the parameters do.

## Oversampling

LiveSPICE's oversampling, which we copy: each input sample is linearly interpolated over N substeps, the circuit
steps at 1/(N·fs), and the N substep outputs are averaged into one output sample. To see what it buys, 1 kHz and 5 kHz
sines ran through four circuits at 48 kHz with N = 1 to 16. Each run was compared with the same circuit run at 64× the
rate with no oversampling, where sines can be generated exactly, measuring in-band harmonic error, the fundamental's
gain, non-harmonic in-band energy ("aliasing") and CPU time.

- **It's essential for some nonlinear circuits, but partly as a crutch for the iteration cap.** At the default cap of
  8, the Rat is wrong below N=8 (its 1 kHz fundamental 13 dB low at N=1, its 5 kHz one 19 dB low at N=2). The Blues
  Breaker diverges at N=1 on 5 kHz. With a cap of 64, the Rat at N=1 is right again (fundamental −0.015 dB), so that
  failure is Newton running out of iterations, not BDF2 being too coarse. N=4 with a cap of 64 beat the default (N=8,
  cap 8) on the Rat at 5 kHz, at less CPU: −29 dB error and −31 dB aliasing against −27.5 and −22, at 10.3 µs per
  sample against 13.0.
- **It does reduce aliasing, up to a point.** On the diode clipper at 5 kHz, aliasing goes from −35 dB at N=1 to −55 dB
  at N=4. On the Blues Breaker it goes from −19 dB at N=2 to −48 dB at N=4. Little changes after N=4 to 8, while CPU
  keeps growing about linearly.
- **On linear circuits it's pure cost.** The Bassman tone stack is more accurate without it: its 5 kHz gain error is
  +0.08 dB at N=1 and −0.47 dB at N=16.
- **The resampling, not N, sets the floor.** Linear interpolation has a sinc² response and the average a sinc, so
  together they roll off as sinc³(f/fs): measured 0.37 to 0.47 dB at 5 kHz, and about −8 dB at 20 kHz by the formula.
  The average also rejects harmonics that fold back near the top of the band by only 4 to 15 dB. Neither improves with
  N.
- **Better resampling helps more than more substeps.** With the same oversampled circuit at N=8, the outputs were
  resampled two other ways: a windowed-sinc lowpass on the way out, and also the input generated exactly at the high
  rate (what a good interpolator approaches).

  | 5 kHz, N=8: error / aliasing | Linear in, average out | Linear in, lowpass out | Exact in, lowpass out |
  | --- | --- | --- | --- |
  | Diode clipper | −27 / −55 dB | −32 / −68 dB | −133 / −124 dB |
  | Blues Breaker | −30 / −48 dB | −38 / −56 dB | −51 / −70 dB |

  At 1 kHz the Blues Breaker went from −54 dB error to −75 dB, and the Rat from −47 dB to −61 dB. The Rat at 5 kHz
  didn't improve, because its capped Newton steps dominate there. A decimating filter costs a few hundred multiply-adds
  per output sample, small next to the Rat's 9 µs per sample. It wouldn't match LiveSPICE sample for sample any more,
  so it would need to be an option.
- **Read "aliasing" with care.** The measure counts any non-harmonic error, including noise from capped Newton steps,
  and the tests used steady sines, two frequencies and one drive level per circuit. The reference uses the same device
  models, so it shows how close each setting gets to a converged simulation, not to real hardware.

Oversampling against the iteration cap, on all 19 examples (1 and 5 kHz at 0.3 input, linear resampling, against
the 64× reference):

- **Raising the cap is free.** At N=8 a cap of 32 matched a cap of 8 exactly on 17 circuits, at the same CPU, because
  the cap only costs anything on steps that would hit it. On the Rat it took 5 kHz error from −27.5 to −31.2 dB and
  aliasing from −22 to −39 dB; on the SD-1, 5 kHz aliasing from −47.1 to −48.7. The plugin now defaults to 32 (and to 4×, below);
  `SchematicProcessorOptions` and the renderer keep LiveSPICE's 8.
- **Half the substeps nearly match, once the cap is raised.** N=4 with a cap of 32 was within about 1 to 3 dB of the
  default (N=8, cap 8) on every circuit, and better on the Rat's 5 kHz aliasing (−28.6 against −22.4 dB), for 35 to
  45% less CPU (Fender 5e3 24.1 against 37.9 µs per sample, Big Muff 14.3 against 25.3, Rat 7.2 against 10.7). With
  linear resampling, the rolloff of the interpolation and average dominates the error at 5 kHz (−25 to −30 dB on most
  circuits) whatever N is.
- **But N=4 needs the higher cap, and N=2 is too few.** At N=4 with a cap of 8 the Rat breaks down (−2 dB at 5 kHz)
  and the SD-1's 5 kHz aliasing falls to −28 dB. At N=2 even a cap of 32 loses the Rat, and costs the TS-9, Blues
  Breaker and Rockerverb 5 to 15 dB at 5 kHz.
- **With sinc resampling the trade changes.** Its error falls about 12 dB per doubling of N (see below), so N buys
  accuracy there, where with linear resampling it mostly buys aliasing and Newton convergence.

Moving the oversampling out of the simulation loop:

- **The engines now step at one rate, and an `Oversampler` resamples around them.** `RunSteps` takes one timestep per
  input value; `Run` upsamples a base-rate block, runs the steps and downsamples each output. `LinearOversampler` is
  LiveSPICE's scheme, and another oversampler with the same factor can be swapped in, since the factor sets the
  timestep.
- **Copy the arithmetic, not the formula.** LiveSPICE accumulates the input (`input += delta` per substep, carried
  across samples) rather than computing each substep from the two samples, and multiplies the sum of the substeps by
  1/N. Doing the same made every circuit bit-identical: the compiled engine, the interpreter and the processor (with a
  pot and a switch moving), at 1×, 3× and 8×, iteration counts included.
- **Prove bit-identity with a frozen copy.** The baseline ran from a copy of the old build, so rebuilding while
  writing the new code couldn't change it, and the comparison was byte for byte rather than in dB.
- **The extra pass costs about nothing.** Writing and reading a step-rate buffer left real circuits unchanged within
  noise (Big Muff 18.7–18.8 µs per sample, Rat 7.6–7.7, Blues Breaker 3.0), and the smallest circuits at most about
  0.04 µs, within their run-to-run noise. The compiled engine keeps its step buffers between blocks, so it still
  allocates nothing.
- **A copy of the arithmetic drifts.** `CutCheck` interpolated its halves' input itself, restarting each ramp from the
  exact previous sample where the engine accumulates, so it differed from the uncut circuit's steps by roundoff
  though its comment said "exactly as the engine does". It now uses `LinearOversampler`, so it is exact.

A windowed-sinc oversampler (`SincOversampler`, `Resampling.Sinc` in `SchematicProcessorOptions`, `--resampling sinc`
in the renderer, a toggle in the plugin):

- **Design for a whole-sample delay.** Both filters are Kaiser-windowed sincs passing to 0.417·fs and stopping from
  0.583·fs at 100 dB, with a half-length that's a multiple of N, so each delays by a whole number of samples: 38 to 40
  in all (0.8 ms at 48 kHz), which the processor reports as `Latency`. The renderer drops that many samples from the
  start and runs silence through at the end, so its output still lines up with the input (checked by
  cross-correlation: lag 0, same length).
- **It's what the study predicted, and now N buys accuracy.** At N=8 the tone stack's 5 kHz error went from −26 dB to
  −64 dB, the clipper's from −27 to −116 dB, the Blues Breaker's from −30 to −51 dB at 5 kHz (aliasing −48 to −70)
  and from −54 to −75 dB at 1 kHz, and the Rat's 1 kHz error from −47 to −61 dB. With linear resampling the error
  leveled off past N=4 to 8; with sinc it keeps falling about 12 dB per doubling of N, BDF2's second-order error, so
  the resampling no longer hides the step size. The Rat at 5 kHz stays limited by capped Newton steps below N=16.
- **Make cheap things cheap.** The first version cost about 0.6 µs a sample more than linear at N=8. Most of it was
  filtering a speaker terminal tied to ground, which is zero at every step; skipping outputs that aren't unknowns
  (bit-identical for linear too) and vectorizing the two dot products brought it to 0.03 to 0.2 µs (tone stack 0.71
  against 0.54, Rat 7.78 against 7.70, Big Muff 18.9 against 18.5). It allocates nothing once warmed up.
- **A one-tap window divides by zero.** At N=1 the filter is a single tap, and the Kaiser window's k/half was 0/0;
  `Math.Max(0, NaN)` is NaN, so every N=1 run "diverged". N=1 is now a pass-through with no latency. The study caught
  it because it runs N=1 too.

The plugin's defaults (4×, a cap of 32, sinc) against LiveSPICE's (8×, a cap of 8, linear), on all 18 examples with an
input, at 1 and 5 kHz against the 64× reference:

| Circuit | 1 kHz error, LiveSPICE → plugin | 5 kHz error | 5 kHz aliasing | CPU µs per sample |
| --- | --- | --- | --- | --- |
| Pro Co Rat | −47 → −53 dB | −28 → −30 dB | −22 → −30 dB | 10.9 → 7.4 |
| Marshall Blues Breaker | −54 → −63 dB | −30 → −39 dB | −48 → −59 dB | 3.8 → 2.5 |
| Big Muff Pi | −30 → −46 dB | −25 → −41 dB | −36 → −71 dB | 25.2 → 14.6 |
| Fender 5e3 | −37 → −58 dB | −25 → −37 dB | −28 → −38 dB | 38.4 → 25.2 |
| Marshall JCM800 2203 Preamp | −32 → −61 dB | −25 → −44 dB | −23 → −27 dB | 21.2 → 12.1 |
| Ibanez Tube Screamer TS-9 | −50 → −56 dB | −29 → −41 dB | −47 → −59 dB | 0.76 → 0.56 |
| Boss Super Overdrive SD-1 | −48 → −52 dB | −31 → −38 dB | −47 → −50 dB | 1.37 → 1.11 |

- **The plugin's defaults are better everywhere.** Error and aliasing improved, or stayed at an already negligible
  level, on every circuit at both frequencies: 1 kHz error by 4 to 60 dB, 5 kHz error by 2 to 88 dB. The smallest
  gains were the Rat and the Rockerverb at 5 kHz (about 2 to 4 dB), where Newton and the tubes' own error dominate.
- **And cheaper on every circuit that's expensive.** 4× sinc cost 30 to 45% less than 8× linear on the large
  circuits; only the tiniest (a passive RC, 0.13 → 0.15 µs) cost slightly more.
- **8× sinc buys about 12 dB more**, as the 12 dB per doubling suggests (the Rat to −65 dB at 1 kHz, −46 dB at
  5 kHz), for 1.5 to 1.9× the CPU of 4× sinc: CPU, not accuracy, is the reason for the 4× default.
- **An earlier sinc run at a cap of 8 misled.** There the Rat at 4× looked broken (−2 dB at 5 kHz); at the plugin's
  cap of 32 it's better than LiveSPICE's default. Measure a default with all the settings that go with it.

## Open issues

- The Rat at 0.5 input was −10 dB from stock LiveSPICE, while stock under noise stayed near −80 dB, and the cause was
  never established. With both sides tightly converged it agrees to −170 dB (see Validation).
- The Orange Squeezer's start-up from DC can't be judged by the noise control.
- The renderer can't use lookup tables yet, and tables aren't saved to disk.
- Approximate cuts exist only as the `cut` experiment (see Stage segmentation). Gauss-Seidel, other black-box solvers
  and the C++ emitter haven't been started.
- Live coefficient reads remain a cost on large circuits: the Big Muff's dynamic step is about 3 µs slower than baked
  (18.5 against 15.7). Keeping the solved pot-leg block factored, as LiveSPICE pull request 282 does, might reduce the
  live set further.
- Stage cuts across loaded devices (BJT bases, conducting tube grids) need a load model that includes the downstream
  stage's input device; `cut` has only no load and a linear load.
- Our Newton is less robust than LiveSPICE's on the Fuzz Face (hundreds of capped steps against at most 10), even with
  the extrapolated guess and restart. Limiting junction ports in units of their exponent, as a fallback at a cap of 32,
  cuts its capped steps without touching the tube amps (see Limiting Newton's steps), but only in the interpreter, and
  it can't help at a cap of 8, where the power-up transient needs more limited iterations; starting from a better DC
  solution, or an unlimited first few steps, might. The Phase 90's sweep also has capped steps that aren't explained.
- Lookup tables over a pot need recordings that sweep it (`--sweep`), and are built in the table's bounding box, so
  thousands of corner points the circuit never reaches don't converge (harmlessly, so far).
- A switch that shifts a bias (the JCM2000's OD1/OD2) still pops with a 5 ms ramp, only 5 to 7 dB less; a longer ramp,
  or muting the output across the move, would be the next step if that matters.
- Automation lives in the renderer; a host drives SchematicProcessor's controls itself.
- The compiled step has no register-pressure estimate; spill traps are found by reading the JIT's output.
- A rejected control setting keeps the circuit at its last good coefficients, but nothing reports it to the host
  (`SchematicProcessor.RejectedSettings` counts them). It has never happened on the test circuits.
- Sinc resampling's 40 samples of latency can't be reported to a host: AudioPlugSharp has no latency call, so a DAW
  won't compensate for it. A minimum-phase or shorter filter would cut it, at some cost in phase or rejection.
- The plugin defaults to sinc resampling (the processor and renderer keep LiveSPICE's linear), so its 40 samples of
  latency, unreported to the host, are now the norm in a DAW.
- The plugin defaults to 4× with a cap of 32, which the study found nearly as accurate as 8× for about 40% less CPU.
  With sinc resampling, error falls about 12 dB per doubling of N, so a user choosing sinc may want 8× back.
