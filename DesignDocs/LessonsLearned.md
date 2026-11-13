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
- **Don't modify LiveSPICE.** Its `dynamic-parameters` branch showed that runtime pots can be cheap, but it diverges
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
- **Know which failures are already there.** Compare against earlier runs before calling something a regression.
  Since the tight DC start and junction limiting, the only standing failure in `dynamic`, `livespice`, `jit` and
  `segment` is the Triode stage, which LiveSPICE itself can't solve. Before them the whole Unstable folder (Fuzz Face,
  Orange Squeezer, Triode stage) failed with pots baked in too, and the Rat's high-drive settings hit the iteration
  cap either way; the Rat still caps 24 steps in its sweep.
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
  `Circuits` and `Examples` agrees to −110 dB or better, most to −170 to −250 dB. Even the Rat agrees to −170 dB:
  once neither side has stopping error or the stale-values bug, its sensitivity no longer matters. The outliers are
  the Blues Breaker with its pots at 0 (−111 and −124 dB, against a −240 dB control) and the Rockerverb at all 0
  (−141 dB), so they are real if tiny differences, not sensitivity. (This read −147 dB or better before; the Blues
  Breaker was already at −111 dB then.)
- **Keep the reference's changes opt-in and verifiable.** The copy's tolerances and the fix default to stock, and the
  capture tool still confirms it is bit-identical to stock `Simulation` with those defaults. The project references
  only LiveSPICE, so the reference can't reach our code.
- **An independent reference finds robustness gaps, not just bugs.** On the Fuzz Face, our Newton hits the 100-iteration
  cap on hundreds of steps where LiveSPICE's caps on at most 10, with a clean noise control. Our own tight reference
  couldn't have shown that, since it runs the same Newton.

## The low-rank rebuild that wasn't

Every folded coefficient of an affected unknown is its static value plus that unknown's row of G·M (kl values, one
per eliminated port) times a static column. So rebuilding the unknown could read only those kl live values, adding
them times the eliminated ports' part from the inputs and their coupling to the solved terms, instead of reading every
folded gain: LiveSPICE pull request 282's "keep the pot-leg block factored".

- **It cut the reads and doubled the time.** On the Big Muff, live reads per step fell from 1,614 to 756, but the step
  went from 18.9 to 33.4 µs (baked 15.7), and as slow with every live value inlined (31.7): the cost was structural.
- **Folding cancels.** For affected unknowns, many static gains and G entries cancel exactly against the correction,
  and the reduction holds those at zero. The low-rank form computes the static entries and the correction separately,
  so it does both: 32% more expression nodes than baked (18,129 against 13,717), before, in and after the Newton
  loop, and long sums that spill. Recomputing the port part after the loop instead of holding it across changed
  nothing.
- **What's left is small anyway.** Before trying it, the live-coefficient overhead was 14 to 21% of the step on the
  large circuits (Big Muff 18.7 against 15.4 µs, Fender 5e3 23.9 against 20.8, Rat 7.65 against 6.66), and inlining
  every live value made the dynamic step as fast as baked (15.25 against 15.66), so a fix can win at most that.
- **Choosing per unknown finds almost nothing to choose.** Using the low-rank form only where its static entries plus
  the two through products are fewer than the folded coefficients it replaces picked 1 of 55 affected unknowns on the
  Big Muff, 1 of 46 on the Fender 5e3, and none on the Rat (37) or Blues Breaker (29). Every check passed, and the
  step didn't get faster (Big Muff 19.6 µs, about as before): the folded rows are already the sparse form. The live
  reads cost what they cost because they're reads, not because there are too many terms.

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

## The DC start

Measured with `livespice` (every circuit, four pot settings, 0.5 and 5.0 input, both sides at 1e-10 and a cap of 100)
and `dynamic`.

- **LiveSPICE's DC solve is loose, and the two sides stop in different places.** NSolve's default stops when the
  squared residual is under 1e-12·N, which on the Fuzz Face leaves junction voltages about 1e-5 V from the solution.
  Ours solves equations with the pots as parameters, LiveSPICE's has them baked in, so the iterates differ and each
  stops at a different point within that tolerance (Fuzz Face `_v7`: −4.62263 against −4.62246). That alone failed
  the Fuzz Face at schematic and all-0 settings (−93 and −82 dB, worst at sample 0, against a −242 dB control).
  Handing LiveSPICE's DC point to ours took both to −215 and −219 dB.
- **So the DC start is now solved to 1e-12** (`Extractor.SteadyStateTolerance`), falling back to the default for a
  partition that doesn't converge that tightly (two 45-unknown partitions do). The `livespice` reference does the
  same with LiveSPICE's own DC system, built only from LiveSPICE's API, so the check compares like with like.
  It's the same root, refined, not a different operating point.
- **The loose start costs real start-up error.** With only our side tight, the loose LiveSPICE start was −20 dB
  from it on the Phase 90 with its pots at 0, and −50 to −95 dB on most other circuits. Against our tightly
  converged sweep, the tight start took the Cry Baby from −105 to −169 dB and the Rockerverb from −84 to −147 dB,
  and the Fuzz Face sweep from 1,711 capped steps to 342. Every other circuit stayed within 2 dB, and `processor`,
  `switches`, `rejection`, `jit` and `segment` are unchanged (the last two still fail only on the Orange Squeezer,
  as before; junction limiting later fixed both, see SPICE's junction limiting).
- **Many of the Fuzz Face's capped steps came from the start, not from Newton.** Given LiveSPICE's DC point, ours
  capped 9 steps at schematic/5.0 against LiveSPICE's 10, where it had capped 250. That's not uniform: all-1/5.0
  went from 291 to 385.
- **The last Fuzz Face failure is a limit cycle in an ill-conditioned step.** At all 0.3, the first step of sample 1
  moves Q2's base-collector junction from −3.9 to −0.97 V (LiveSPICE does the same), and the next step's first
  Newton update overshoots to +1.9 V, past LinExpm1's knee at 50·Vt. There the junction's slope is e⁵⁰, about
  1e24 times the other entries, and our iterates repeat every 49 iterations without converging. LiveSPICE, solving
  the same system in different variables, escapes in 50. Both are at the mercy of rounding there.
- **Junction limiting fixes it there and breaks tube amps.** `JunctionLimit` 10 (or 4) after 3 iterations took
  the Fuzz Face to 0 problems (all 0.3 / 0.5 from +77 to −214 dB, 0 capped steps), and with the tight start a limit
  of 4 no longer breaks it at power-up. But it took the JCM2000 at 5.0 input from −155 to −44 and −25 dB, and the
  Rockerverb at all 1 / 5.0 from −191 to −29 dB. Limiting only after 10 iterations made the Fuzz Face far worse
  (tens of thousands of capped steps). See SPICE's junction limiting, below, for what fixed both.
- **The tube amps' capped steps in `livespice` are a roundoff floor, not divergence.** The JCM2000 at all 0
  capped 3,618 steps against LiveSPICE's 0, yet agrees to −170 dB. Newton converges quadratically (1e-4, 6e-10,
  3e-12) and then wanders randomly at about 1e-10 on a node at 0.038 V, just above that check's 1e-10·|v| + 1e-12
  test. That's about 1e4 ulps of the 60 V of terms the node is rebuilt from. At the shipping tolerance it can't
  show.

## SPICE's junction limiting

Two changes, first tried in the interpreter, now the default in both engines. Measured with `livespice` and with a throwaway harness: 0.5 s of a 440 Hz sine
at 0.1, 0.5 and 1.0 input, at 4× and at the default tolerance, with caps of 8, 32 and 100. It counted capped steps
and the error against our own run at 1e-10 and 100 iterations, with the limit off and on.

- **The tube amps were limited because of their softplus terms.** The triode's grid and cathode currents are
  `Ln1Exp(x) = ln(1 + eˣ)` of an argument linear in the ports, so `JunctionLimit`'s detector took them for junctions.
  A softplus grows only linearly, so Newton doesn't overshoot on it. `ExpArguments` now skips exponentials inside a
  logarithm, and no tube port is limited at all (JCM2000 and Rockerverb: 0 of 20). BJTs and diodes keep theirs: the
  Fuzz Face's 4 junction ports at 39.4 per volt (1/Vt), the Big Muff's 10 at 39.4 and 22.5 (a diode with n ≈ 1.75).
- **That fixed `livespice`, not the shipping settings.** With the tubes excluded, `JunctionLimit` 10 after 3
  iterations left `livespice` at 1 problem (the Triode stage) and changed no other circuit. A limit of 4 broke the
  JFET at 5.0 input (−230 → −31 dB). But at 4×, a cap of 32 and the default tolerance it sent the Fuzz Face
  non-finite at 0.5 and 1.0 input (78,403 and 86,965 capped steps, from 10 and 141), and at a cap of 8 at every level.
  The 0.5 V step limit did the same. At 8× it does help at a cap of 32 (256/305/92 → 0/0/161), which is what the
  earlier experiment measured: at 4× the steps are larger, a limited Newton runs out of iterations partway, and
  the next step starts badly.
- **Limit the way SPICE does: per junction, only past a critical voltage, logarithmically.** `JunctionCritical`
  is pnjlim in units of the exponent. For each port that alone sets an exponential's argument x = s·w + c, it
  compresses a move that takes x past the critical value by more than 2 to x_old + ln(1 + Δx), or to ln(x_new)
  from reverse bias. Ordinary moves are untouched, however large, and a limited iteration doesn't count as
  converged. At 4×, a critical value of 30 and limiting from the first iteration:
  - The Fuzz Face at a cap of 32: 257/10/141 capped steps to 0/0/0. At a cap of 8: about 93,000 to 580–660, finite
    at every level.
  - Across every circuit at a cap of 32: capped steps went from 543 to 29, and none got worse. The Common Emitter
    amplifier at 1.0 went from −65.6 to −189.0 dB, the Orange Squeezer from −45 to −206 dB, and the Rat at 1.0 from
    58 capped steps to 29. Every tube amp was bit-identical; the Wien bridge moved by 1 dB.
  - At a cap of 8: 194,638 capped steps to 5,793, mostly better (Orange Squeezer +10 → −67 dB, Common Emitter
    −27 → −42 dB). Two small losses: the Op-Amp Model at 1.0 (−52 → −39 dB) and the Rat at 0.5 (−26 → −23 dB).
  - `livespice`: 1 problem (the Triode stage). The Fuzz Face at all 0.3 / 0.5 went from +76.5 to −213.7 dB with 0
    capped steps, and nothing else changed status.
- **30 is the critical value, with little margin below it.** At 25 the limit acts in normal operation: the Op-Amp
  Model at a cap of 32 fell from −160 to −107 dB and the Rat at 0.5 from −158 to −102 dB. At 40 it caught less (149
  capped steps left at a cap of 32, against 29). SPICE's vcrit for a 1e-14 A junction is about 28 Vt. 30 is only
  about 5 Vt above where the Op-Amp Model's transistors normally run.
- **Now the default, in both engines, bit for bit.** `JunctionCritical` defaults to 30, and `CompiledCircuit` emits
  the same operations, unrolled over the junction ports it finds at build time. A circuit with no junction port (every
  tube amp) gets no extra code. `jit` agrees with the interpreter to −∞ dB with the same iteration counts on all 51
  circuits. All the references that run our own engine (the tight sweep, `livespice`'s side) now limit too.
- **It fixed most of the Unstable folder, not just the Fuzz Face.** Measured after the change, with the tight
  reference cache refreshed:
  - `dynamic`: 26 problems to 1 (the Triode stage, which LiveSPICE can't solve). The Fuzz Face's 15 mismatches
    between dynamic and baked pots are gone.
  - Sweeps against the tight reference: Fuzz Face +114 → −41 dB (342 → 88 capped steps), Orange Squeezer +105 →
    −37 dB (23 → 1), Rat −45 → −55 dB (90 → 24). Every other circuit's sweep was unchanged.
  - `segment`: from 2 failures (the Orange Squeezer) to none. `jit`: from 1 failure to none. The Orange Squeezer's
    compiled run had diverged against stock LiveSPICE (∞, 44.7 µs per sample); it now runs at 5.7 µs and is −36 dB
    from stock.
  - `livespice`: 1 problem (the Triode stage). `processor`, `switches` and `rejection`: still 0 problems.
- **It costs 0 to 5% on circuits with junctions, and nothing on the others.** Compiled µs per sample from single
  `jit` runs: Big Muff 14.33 → 14.66, Phase 90 20.17 → 20.76, Rat 5.75 → 6.02, the tube amps within noise. The
  Fuzz Face got faster (1.76 → 1.10), since it no longer spends its iterations walking back from overshoots. The
  oversampling study's table (see Oversampling) predates this and wasn't rerun.

## The editing server

`SpiceForgeServer` serves the editing side of DesignDocs/WebSocketApi.md, and `SpiceForge.Validate server` checks it.

- **Convert files attribute for attribute, not through LiveSPICE's model.** The JSON document is built straight from
  the XML: each component's attributes become its properties, and its child nodes (a subcircuit's contents) an opaque
  body. LiveSPICE is only asked whether each component would load. All 53 schematics in `Tests` come back
  `XNode.DeepEquals` to the original through the document and JSON text, including `Errors/Unserialized.schx`'s
  unresolved component. Going through `Schematic.Deserialize` and `Serialize` would drop what LiveSPICE can't load,
  and reorder and normalize attributes.
- **LiveSPICE renames duplicate names as components are added**, so `Schematic.Build`'s duplicate-name check can't
  fire on a loaded file. Its `ComponentCollection.Add` applies `Component.UniqueName`: two R1s load as R1 and R2.
  `circuit.check` therefore looks for duplicates in the document's own names.
- **A quantity parses as an expression.** `Quantity.Parse("banana")` succeeds, as a symbol, and the value only fails
  when the circuit is solved. `values.check` rejects any symbol but `t`, which a source's value may use
  (`Sin(2*Pi*440*t) V` checks).
- **LiveSPICE's ohm is U+2126 (OHM SIGN), not U+03A9.** A test written with the Greek omega failed against correct
  output.
- **Not every component type can be described.** `Circuit.Specialization` throws from `PartNumber`, and `Units`'s
  `==` throws when either side is null (test with `is not null`). LiveSPICE's library skips a type that fails;
  the catalogue does the same, and lists 131 entries.

## The web front end

`SpiceForgeWebUI`: plain TypeScript, no runtime dependencies, bundled by esbuild (the bundle is 22 KB), served by
`SpiceForgeServer` from the same origin as the API.

- **Drawing from the server's layouts with LiveSPICE's own rules is enough to match its look.** The Pro Co Rat (42
  symbols, 63 wires), the Fender 5e3 (61 and 105), the subcircuit tone stacks and `Errors/Unserialized` draw as
  LiveSPICE draws them. The rules are `Symbol.MapToGlobal`, the arc sweep reversing under mirroring, and
  `SymbolControl`'s upright text placement, with text measured by a canvas in the same font. All terminals connect
  where wires pass through them, and the unresolved component draws as a placeholder with its reason.
- **Headless screenshots with a virtual time budget don't run WebSockets.** `msedge --headless --screenshot
  --virtual-time-budget` left the page at "Connecting…". Driving Edge over its DevTools protocol, from a small Node
  script using Node's built-in WebSocket, works and also reports console errors.
- **npm now blocks install scripts by default.** npm 11.20 skipped esbuild's postinstall. The build works without it,
  since that script is only an optimization.
- **Selecting and moving follow LiveSPICE's selection tool, and its geometry.** Rubber bands use `Wire.Intersects` and
  `Element.Intersects`, and grid snapping rounds halves to even, as .NET's `Math.Round` does (JavaScript's rounds them
  up). A scripted session on a copy of the Pro Co Rat checked each rule:
  - a click selected one element, and a drag moved it live by whole grid steps;
  - undo and redo restored and re-applied it, with the dirty marker following;
  - a rubber band selected 12 elements, and Escape cleared them;
  - a double-click selected all 9 resistors, and Ctrl+click toggled one off;
  - a right-click in the middle of a drag cancelled it, leaving nothing to undo.
  The saved file differed from the original by one line: R9's position, moved by (95, 50).
- **Pointer capture retargets clicks.** Once the SVG captures the pointer on press, the browser aims the following
  `click` and `dblclick` at the SVG itself, so double-click found no element. Hit-testing by
  `document.elementFromPoint` works.
- **A second button pressed during a drag isn't a `pointerdown`.** Pointer events report it as a "chorded"
  `pointermove`, with the button in `buttons`. The right-click cancel listens for `mousedown` (which does fire) and
  checks `buttons` in `pointermove`.
- **Rotate and flip match LiveSPICE's own code, checked against it.** A throwaway program applied LiveSPICE's
  `Element.RotateAround` and `FlipOver` to whole schematics, with the centre computed as
  `SelectionTool.GetSelectionCenter` does (C#'s truncating division, then snapping). It compared the result with
  the files the web editor saved after the same keys. The geometry was identical on every element: the Pro Co Rat
  (105 elements) after Left, Up, and the Fender 5e3 (166) after Right, Right, Down, Left.
- **LiveSPICE's flip disconnects symbols turned 90°.** `Symbol.FlipOver` mirrors the position over a horizontal line
  but toggles the symbol's own flip, which mirrors its local y axis. For a symbol at an odd rotation that axis is
  horizontal on screen, so the symbol mirrors left-right while its position mirrors top-bottom. On the Pro Co Rat
  rotated once, a flip left 9 terminals unconnected, in LiveSPICE's own code as in the web editor. The web editor
  matches it, so files behave the same in both, and the README says how to avoid it. Fixing it (flipping x
  instead at odd rotations) would be a deliberate departure from LiveSPICE.
- **Wire joining matches LiveSPICE's `AddWire`, checked on randomized paths.** `AddWire` lives in the WPF editor, so a
  scratch program carried a near-verbatim copy of it (with `CoincidentWires` and `InRect`), running on LiveSPICE's
  own `Schematic`, `Wire` and `Symbol`. It and the web editor each drew the same 60 paths, from a fixed seed, on the
  Pro Co Rat:
  - straight runs along existing wires, which merge and split;
  - L-shapes between wire ends;
  - L-shapes to arbitrary points, for crossings and T junctions.
  Both ended with 173 wires and no unconnected terminals, and their geometry was identical on all 215 elements, in
  the same order.
- **Editing works end to end in the browser, checked by script on copies of the Pro Co Rat.**
  - Placing: a resistor placed from the library was named R10 (R1 to R9 exist), turned by the arrow keys, and
    selected.
  - Wiring: a wire drawn from its terminal joined it, and the red terminals went from 2 to 1.
  - Values: "4.7k" was stored as "4.7 kΩ"; "banana" and a clashing name were refused, with the reason shown.
  - Clipboard: copy and paste made R11 at the same place, and Delete removed it.
  - Undo: four undos brought the document back to its original 105 elements.
  - Multiple selection: R1 and R4 together showed their different resistances blank, and "22k" set both in one
    undo step.
- **The editor no longer copies LiveSPICE's editor; it keeps its file format.** Matching LiveSPICE's behaviour
  exactly had done its job: it gave a full editor quickly, and the checks against LiveSPICE's own code showed the
  geometry right. From there it mostly preserved rough edges, so the guideline became "does the operation make good
  sense", with a fixed contract: the file format, symbol geometry, and connectivity. First departures:
  - flipping a rotated part also negates its rotation (F·R(r)·S(s) = R(−r)·S(−s)). On the Pro Co Rat rotated and
    flipped, that leaves 0 unconnected terminals, against LiveSPICE's 9;
  - pressing on an unselected part selects and drags it in one gesture;
  - a paste lands under the pointer, not on top of the original;
  - a wire's first leg follows the direction the drag starts in, instead of a distance heuristic whose own comment
    says it's wrong.
- **The contract is checked against the server, not against LiveSPICE's editor.** `SpiceForgeWebUI`'s conformance
  test compares the client's nodes with `circuit.check` (LiveSPICE's `Circuit.Schematic`) on all 53 test schematics,
  as loaded and after 8 random edits each: 477 comparisons, none differing. The scratch copies of LiveSPICE's editor
  code used before are no longer needed. Getting there found three real discrepancies:
  - `circuit.check` listed a wire's own conductor terminals as node members;
  - it dropped subcircuit terminals, which belong to the Ports inside the subcircuit; it now reports each terminal by
    the symbol that exposes it;
  - the client counted a zero-length wire as connecting, where LiveSPICE loads one as a warning symbol.
  Also, older files don't store a ground's `WireName`, so a named wire's name falls back to its type's default
  ("GND").
- **The server announced it was listening before it had bound the port.** With another server already on 8370, a
  second one printed success and served nothing. It now announces itself once the port is bound, and reports a failed
  bind.
- **Theme the drawing with classes, not colours.** The renderer wrote LiveSPICE's colours into the SVG as `stroke` and
  `fill` attributes, which CSS variables can't reach. It now writes a class per edge colour (`s-red`, `f-red`) and per
  role (`wire-line`, `terminal unconnected`, the ghost group), and `themes.css` maps them to tokens. Six themes
  (System, Light, Dark, Dim, Paper, High contrast) are each one block of about 45 tokens; the Light block reproduces
  the old look exactly (compared by screenshot on the Pro Co Rat).
- **Dark themes need a selection colour that isn't blue.** Light blue wires on a dark canvas and a DodgerBlue
  selection were hard to tell apart; the dark themes select in amber, High contrast in yellow.
- **Muted text isn't a disabled colour.** High contrast's muted grey is nearly white, so disabled buttons looked live.
  Disabled controls have their own token.
- **Apply the stored theme before the first paint.** A small inline script in `index.html` sets `data-theme` from
  localStorage, so a dark theme doesn't flash white while the module loads. The server's client settings hold the
  same choice and win once connected, so it survives clearing the browser and follows to another one.
- **A hidden element leaves a CSS grid's rows.** The page is a grid of rows (toolbar, tabs, banner, workspace,
  status). The banner hides with `display: none`, which takes it out of the grid, so every later element moved up a
  row. The status bar got the stretchy row, the canvas almost none, and clicks on the canvas landed on the footer.
  Named grid areas fix each element's row whether the banner shows or not.
- **Compare paths with either slash.** A file named in the address with forward slashes, and reported by the server
  with backslashes, opened twice in tabs. The comparison now normalises slashes and ignores case, as Windows does.
- **The editor completes a file's life:** New, Save As (with a save mode of the file dialog), tabs that each keep
  their history, selection and view, reopened after a reload, a banner when an open file changes on disk, node names
  in wire tooltips, and a context menu. A scripted session checked each, on copies of the Pro Co Rat and a new
  schematic saved as NewOne.
- **Browsers keep some of LiveSPICE's shortcuts.** Ctrl+W and Ctrl+T can't be taken by a page, so the tools also
  have plain letters (W, G, P, R, C, L, T). LiveSPICE's other Ctrl shortcuts work too.
- **Moved parts drag their wires, rerouted with plain wires and joined once on release.** The editor used to
  move only the selection, so moving a part left its wires behind and broke its connections. Now unselected wires with an end on a moved terminal
  (or a moved wire's end) follow it: straight if the ends still line up, else an L that leaves the terminal in the
  wire's old direction and turns at the fixed end's line. While dragging, the stand-ins are plain wires swapped in and
  out with one `ChangeEdit` per pointer step, so a cancel or a Shift toggle just reverts it. On release the followers go
  through `addWire`, so they merge and split as drawn wires do, and the move, the removal and the joins are one
  `CompositeEdit`. A scratch script checked the routes (straight, L, both ends moving, slanted) and that undo and redo
  give back the document exactly. Shift is the override, at the press or during the drag: Ctrl+press is already the
  rubber band, so Ctrl could only have worked mid-drag. A terminal sitting on the middle of a wire (rather than at its end) is
  connected but isn't followed, so moving that part still disconnects it.
- **Slide a plain corner rather than adding a leg beside it.** With only the L route, a part whose wire turned a
  corner got a new leg collinear with the corner's other wire. Moving away from that wire's far end lengthened it
  properly. Moving toward it overlapped the wire, and `addWire` merges overlaps into their union, so the old wire kept
  its full length and a stub dangled past the new corner. Now, when a follower's fixed end is a plain corner (exactly
  one other wire touches the point, ending there at a right angle, neither moving nor following, and no symbol
  terminal there), both wires are rebuilt: the corner moves across with the part, and the other wire runs from it to
  its far end. The scratch script checked shortening (a 100-unit wire to 70), lengthening, shortening to nothing (the
  wire goes), a diagonal move, and that a T junction keeps the L route, each with undo and redo exact.
- **A wire's free end is a handle.** Pressing within 10 screen pixels of an end that connects to nothing (no other
  wire touches it, at its end or along it, and no terminal is there) drags that end, not the wire. The wire runs from
  its other end straight to the pointer if they line up, else as an L going on in its old direction, so pulling a
  stub sideways extends and turns it rather than swinging it slanted. Preview and commit work as for followers:
  stand-ins swapped by one `ChangeEdit`, then `addWire` on release, so dropping the end on a terminal or a wire joins
  it. Pixels rather than schematic units, so the handle is the same size at any zoom. Checked in the scratch script:
  free and connected ends (a terminal, another wire's end, a mere crossing), the routes, and an extension onto a
  terminal with undo and redo exact.
- **Stop Enter where it's handled, or it presses the button focus returns to.** Enter in Save As's name box saved and
  closed the dialog on keydown; closing a modal dialog returns focus to what opened it, the toolbar's Save As button,
  and the same keypress then activated that button. The dialog came straight back with the old name (the write hadn't
  finished, so the tab still had its old file). Every Enter handler in the file dialog now prevents the default.
- **Deleting is the server's, and narrow.** `fs.delete` deletes only `.schx` and `.xml` files, never folders, for good
  (no recycle bin: the server isn't Windows-only), and forgets them from the recent files. `fs.mkdir` makes one folder
  inside one that exists, so a mistyped path can't create a chain. `SpiceForge.Validate server` checks both, refusals
  included: 0 problems, with the web editor's conformance (53 schematics) still passing.
- **Show the grid as a CSS background, not SVG.** A dot pattern on the schematic `svg` element, sized and placed from
  the view on every pan and zoom, adds nothing to the drawing, so exports never include it. Dots closer than 8 pixels
  are thinned to every second grid point (and again), so those drawn are always snap points.

## Simulating without audio

`SpiceForgeServer` simulates through the API before it has an audio device: `sim.*` runs on a clock of its own, and
the web front end shows it on a scope.

- **A thread can stand in for the device.** The simulation thread processes 256-sample blocks whenever the wall
  clock says one is due (48 kHz), drives the Input from a compiled expression, and sends the output only to the
  meters. When real audio came, the clock became one audio driver among others (`ClockDriver`, see Audio I/O), and
  everything that had run inline on that thread moved to a monitor thread.
  The Pro Co Rat runs at 18 to 30% of real time at 4× sinc, from build to running in 0.6 to 0.75 s.
- **Probe every node, read at step rate.** Adding a probe mustn't rebuild. `SchematicProcessorOptions.Probes` makes
  every node voltage the engine solves for an output after the Speakers', and the processor runs the engine stage by
  stage: the Speakers' outputs go through the oversampler exactly as `CompiledCircuit.Run` does, while the probes
  read each sample's last step. Copying a few dozen unknowns per step costs little; resampling each through the sinc
  filter would have cost far more. The plugin path, without `Probes`, is untouched: `SpiceForge.Validate processor`
  still passes, allocation-free.
- **Checked end to end against the processor offline.** `SpiceForge.Validate server` starts the Rat through the API,
  probes the Speaker, and compares 72,000 streamed samples with `SchematicProcessor` run offline on the same file,
  input and settings: the worst difference is 7.5 nV, float32's rounding of a 0.15 V signal. That covers the
  document-to-circuit build, the input's scaling, and the probes.
- **Map probe targets through `circuit.check`'s nodes.** Dynamic pots and switches replace their components, and the
  replacements lose LiveSPICE's element tags, so a pot's terminal can't be traced in the simulated circuit. The nodes
  themselves survive, and LiveSPICE names them by construction order, so the same document gives the same names in
  both builds: a target resolves to a node name through `circuit.check`, and to an output by that name.
- **A builder, not a path.** `CircuitLoader.Load` and `SchematicProcessor` also take a function that builds the
  circuit (called twice: baked for LiveSPICE's solve, then dynamic), so the server simulates the client's document
  without writing a file.
- **Edits follow the simulation.** The client sends `sim.update` 0.4 s after the last edit to the simulated tab; the
  old build keeps running until the new one is ready. Deleting the Rat's clipping diodes took the output from 287 mV
  to 1.37 V peak to peak, and undo brought it back. An edit that doesn't build is refused, and the panel says it's
  still running the last version that did.

## Polishing the simulation and the editor

- **Count capped steps with one test per step.** `CompiledCircuit.CappedSteps` (mirrored in the interpreter) counts
  steps whose result has the cap flag, whether or not iterations are recorded, so `sim.stats` reports them without the
  per-step list a host can't afford. `SpiceForge.Validate processor` still allocates nothing. At 2× with a cap of 8 the
  Rat caps about 880 steps a second at the default 0.5 V input, and none at 4× with 32.
- **Pointer capture on an SVG group didn't survive the first move.** A knob drawn as a `<g>` on the schematic took
  capture on pointerdown and lost it at the next pointermove (headless Edge, CDP mouse events). It wasn't removed from
  the page, and nothing else took capture. The svg element's own capture, used by the editor, works. Following the drag
  with window listeners, without capture, works and keeps working when the pointer leaves the canvas.
- **Harness: a move with no buttons ends a drag.** CDP `mouseMoved` with `buttons: 0` after `mousePressed` releases
  pointer capture as if the button had been let go; drags in the test scripts must send `buttons: 1`.
- **Export by drawing afresh.** The image is rendered again, with no selection, into a hidden group inside the page's
  SVG so the theme's stylesheet applies, then each shape's computed style is written onto a copy and the classes,
  hit areas, outlines and tooltips are dropped. The result stands alone (no classes left), in the theme's colours.
- **User component folders live in the server's settings.** `settings.set` `componentFolders` checks that each folder
  exists, then the catalogue reloads with them and `catalog.changed` goes to every client. `SpiceForge.Validate
  server` adds a folder with one part, sees it arrive, removes the folder, and sees it go.

## Loose ends from the polish

- **Draw live positions with stand-in components, not edits.** A simulated switch's symbol depends on its `Position`
  property, and a pot's prints its `Wipe`. The renderer now takes a display-only component per symbol; the simulation
  panel supplies copies with the live values, fetches their layouts (throttled to 120 ms, wipers rounded to two
  places so a drag fetches a handful of layouts), and the editor draws them. On SP3T, clicking the switch moved the
  blade from the first throw to the second while the document kept `Position="0"`, stayed clean, and had nothing to
  undo; Stop drew it as saved again.
- **Place controls by cost, counting labels.** Text labels aren't in a symbol's layout bounds, so the first placement
  rule (right of a pot, or the first side clear of wires and symbols) put knobs over the Rat's "A100 kΩ" labels. Each
  candidate spot (four sides and four corners) now costs its overlaps with symbols, wires (a proper segment-box clip;
  `wireIntersects` only tests the box's corners), drawn text boxes, probe flags and controls already placed, and the
  cheapest wins. All three of the Rat's knobs land clear.
- **Reset the sliders' key with the sliders.** The panel rebuilds its sliders only when the set of controls changes,
  keyed on their names. Stopping cleared the sliders but not the key, so simulating the same circuit again showed none.
- **Remember per file in the browser; resume by id across a reload.** A schematic's probes (with where their flags
  were clipped), scope scales, input and control positions are saved in localStorage by file path, and put back
  after `sim.start`, skipping what no longer applies. The running simulation's id is kept in sessionStorage; after a
  reload, `sim.get` finds it still running on the server, the panel shows it again, and a `sim.update` brings it the
  schematic as reloaded. Checked on the Rat: two probes, 2 ms/div, Distortion 0.8 and a 440 Hz input survived both a
  reload and a stop and restart.
- **The longer engine checks still pass with the capped-step counters.** `SpiceForge.Validate dynamic` and
  `livespice`: every circuit ok but `Unstable/Triode stage`, which LiveSPICE can't solve either (see Starting out);
  the interpreter and compiled engine still agree exactly.

## Audio I/O

`SpiceForgeServer` plays simulations on ASIO devices, through NAudio, and keeps the clock of Simulating without audio
as its `None` driver and its fallback, and on JACK through JackSharpCore. Each sits behind the same interface: a
driver opens a stream for one client, prepares it while stopped, and calls it once per block. WASAPI may follow.

- **The device is the clock; everything else moves off its thread.** `Simulation.Process` runs once per device
  block and does only the audio work: the input, `SchematicProcessor.Process`, the outputs, the peaks, and the probe
  samples into a preallocated single-producer ring (`ScopeRing`). A monitor thread, at 60 Hz, drains the ring into
  scope frames, sends the levels and stats, and reports failures (divergence included), which used to happen inline
  on the clock thread with locks, LINQ and JSON. The refactor kept the offline agreement exact: the server check
  still compares 70,912 streamed samples with `SchematicProcessor` offline, worst difference 7.5 nV as before, and
  the scope streams without gaps.
- **NAudio 3's `AsioDevice` duplex mode is the right ASIO layer.** One callback per buffer gets each input as a
  float span and writes each output as one (`AsioProcessBuffers`), with NAudio converting to and from the driver's
  native format; it reports driver resets (`DriverResetRequest`, recovered by Stop, Reinitialize, Start), resyncs,
  and unexpected stops. The older `AsioOut` would have meant converting samples ourselves. The NAudio metapackage
  doesn't include ASIO for net9.0 and later: `NAudio.Asio` is a separate package. Every call into a driver goes
  through one STA thread (`StaWorker`), since ASIO drivers are COM objects made for one thread.
- **Drivers for absent hardware fail cleanly, and say why.** Of the eight ASIO drivers installed on the development
  machine, four couldn't open with nothing plugged in, each with a readable message ("No device is connected to the
  PC."), which `audio.deviceInfo` and the fallback pass on. Realtek's driver reports a current rate of 0 until it
  runs, so 0 is dropped from the rates offered and 48 kHz becomes its default.
- **Tiered compilation glitched ASIO buffers; the server runs without it.** On ASIO4ALL (onboard Realtek) at
  44.1 kHz, the Rat at 4× sinc with a probe streaming to the scope and the outputs silent (`SpiceForge.Validate asio`),
  with the runtime's defaults: at 64 samples (a 1.45 ms budget) each start overran 7 callbacks in its first 300 ms of
  playing, 1.6 to 5.2 ms each, and a rebuild overran 1, over two runs; at 128 samples, 4 at start and 1 more during
  the run. With `<TieredCompilation>false</TieredCompilation>`, both sizes overran only the first processed block
  (7.5 to 8.8 ms, the engine's first run), with no overruns in 20 s of running, in two runs each; rebuilds were clean
  in three of the four runs (see GC below for the fourth). Load fell from 0.187 to 0.180 at 64 samples, and from 0.196
  to 0.190 at 128. The overruns weren't GC: no collection ran between them. Without tiering the server took 35 ms
  longer to start listening (225 against 190 ms), and the whole server check no longer (8 against 9 s); those two
  were timed in the shell described next.
- **Check the environment before trusting a runtime measurement.** The first measurements ran in a shell that set
  `DOTNET_gcConcurrent=0`, `DOTNET_GCHeapCount=2` and `DOTNET_TC_CallCountThreshold=1000`, none of them set for the
  user. The raised call-count threshold put tier-up 6.9 s into every run (3 overruns, as late as that), and with
  background GC off, the gen2 collections during a rebuild's build blocked the audio thread for 4.8 to 5 ms. With the
  variables cleared, tier-up finishes in the first second and background GC is on; the conclusion about tiering
  stood, and the late overruns and long gen2 pauses were artefacts. `SpiceForge.Validate asio` should be run from a
  clean environment.
- **GC during a rebuild is the remaining risk at the smallest buffers.** Building in LiveSPICE's algebra allocates
  heavily, and its gen0 and gen1 collections pause the audio thread too. At 96 kHz with 64-sample buffers (a 0.67 ms
  budget, load 0.37 to 0.39) a rebuild overran 1 to 3 callbacks by 1.1 to 2.3 ms, in the shell with background GC
  forced on. At 44.1 kHz without tiering, in the clean environment, one rebuild of four overran a 128-sample callback
  by 3.2 ms, as a gen2 collection ran; the other three were clean. In steady state nothing
  collects: the audio path allocates nothing, and the monitor's allocations are small.
- **Don't dispose an ASIO device whose reset failed; abandon it.** Unplugging a USB interface during a simulation
  made its driver request a reset; `Reinitialize` then failed (`ASE_NoClock` while calling `getChannels`), the
  fallback logged it, and closing the failed stream disposed the device. The server died moments later: Windows
  recorded two crashes of `SpiceForgeServer.exe` with `0xc0000374` (heap corruption) in `ntdll`, and the editor lost
  its connection, leaving only Export enabled. The crash dumps couldn't be read, nor NAudio 3's `AsioDevice` source;
  NAudio 2's `DisposeBuffers` frees its callback block without clearing the pointer, so a dispose after a half-done
  reinitialise freeing it twice fits, but that's inferred, not confirmed. A device whose reset failed is now never
  called again: closing it keeps it (so callbacks a driver might still make stay alive) and frees the slot, leaking
  one dead driver instead. Checked by unplugging the interface again during a simulation: the server stayed up and
  the simulation carried on, on None. Plugging it back in and choosing it again in the audio dialog opened it
  normally, so the abandoned instance doesn't block the driver. That the dispose was the crash is now likely, though the double free is still
  inferred from NAudio 2's code.
- **A lost device resets to None, and the simulation carries on.** The fallback used to keep ASIO configured while
  running on the clock, so the editor still showed a device that was gone. Now the configuration becomes None at the
  failed device's rate, is saved, and `settings.changed` follows; the simulation panel and the audio dialog say the
  device failed and why, as a warning.
- **PipeWire's JACK can give a port no buffer; neither layer may assume one.** On Linux with PipeWire's JACK the
  server aborted (`SIGABRT`) as a simulation connected: a `NullReferenceException` in `JackSharp.Processor.Silence`,
  from `RunCycle`, from the process callback. `jack_port_get_buffer` had answered with no buffer, which JackSharp made
  into a span over address 0; SpiceForge's driver copied a whole cycle into that empty span, which threw, so JackSharp
  silenced the outputs in its catch, wrote through the null pointer, and that exception left an
  `[UnmanagedCallersOnly]` callback, which ends the process. Why PipeWire gives no buffer wasn't established: the
  ports are registered before activating, and JACK on Windows always gives one. JackSharpCore 1.1.1 gives an empty
  span for a port without a buffer and catches anything a cycle throws; SpiceForge's driver reads silence from an
  input without one and skips an output without one. `SpiceForge.Validate jack` (its own dummy JACK server, on
  Windows) and JackSharpCore's tests (17 passed) still pass. On the Linux box with PipeWire's JACK, the packaged
  server (single file, 0.0.2-dev.16) no longer crashes connecting; audio there still had trouble, thought to be the
  system's own, and wasn't pursued.
- **The JACK check also caught the device-loss change.** When the JACK server goes, the configuration now resets
  to None rather than falling back (see above), so `audio.state` has no `fallback`; `SpiceForge.Validate jack` waited
  for `fallback` and failed. It isn't in the regular server check, so it wasn't run when that changed; it now
  expects the reset, and passes.
- **The Input must follow a device change, not keep a channel the new device lacks.** Switching from JACK to ASIO
  while running moved the outputs (they're mapped by the device's channels) but left the Input on JACK's
  `system:capture_1`, which matched nothing on ASIO and so read silence, shown as "(not open)". `Prepare` now applies
  the start's rule again: a lost input becomes the new device's first, or the 220 Hz test sine without any (compiled
  on the engine, off the device's thread, taking over when ready); a source the server chose (marked `Automatic`)
  follows the device too, so the sine it fell back to becomes the first input of a later device; a client's own
  choice stays while the device has it. Checked in process on the Rat with a fake device switched between JACK-like,
  ASIO-like and input-less channel sets: all five cases. Confirmed on real devices: switching from JACK to ASIO while
  running moved the Input to ASIO's first input. The checks were built into a scratch folder
  (`-p:OutDir`, `SPICEFORGE_REPO`, `SPICEFORGE_SERVER`) because a running server held the build's executable.
- **The audio test needs a tone, and fades to start and stop.** Test only looped the inputs to the outputs, as
  LiveSPICE's does, so with nothing plugged in a working device played silence and looked broken. It now plays a
  440 Hz sine at −18 dBFS by default, with the loop as the other choice. Both pass through a raised-cosine gain,
  20 ms each way; stopping sets the fade going and waits until two whole blocks of silence have followed it (a device
  plays one block while the next is filled, so after one the fade's last block could still be cut off) before the
  device closes, with a half-second limit for a device that has stopped calling. Checked in process, block by block
  at 48 kHz: the tone starts at exactly 0, peaks at 0.1259, and its largest sample-to-sample step is 0.00725, the
  plain sine's own steepest, so the fades add nothing; the fade-out takes 959 samples and 577 silent ones follow
  before it reports done. The input loop, fed a steady 0.1, does the same. Not yet heard on a device.
- **The web front end configures it in one dialog.** Audio… (in the toolbar, and Configure… in the simulation
  panel) shows the driver, device, channels as checkboxes, rate and buffer size (with their milliseconds), the
  driver's control panel button, and the test with per-channel meters and a scope of the summed input. A driver's
  device info is only read when it's chosen, since loading an ASIO driver takes a moment. The simulation panel's
  Input offers the device's open inputs ("Audio in: …") ahead of the test signals, and a line says what it plays on,
  with the round-trip latency. Binary frames are routed by source, so the test's scope (source 0) and a
  simulation's don't mix. Checked in headless Chrome against ASIO4ALL with its outputs unticked: the dialog read the
  device, applied 128-sample buffers, the Rat played the mic input at 21% of real time, and the test was refused
  while it ran.
- **Output signals are extra engine outputs, resampled as the Speakers are.** LiveSPICE's pick tool plays the
  voltage across two-terminal components, summed, in volts. `SchematicProcessorOptions.Signals` takes each signal as
  a weighted sum of node voltages; their nodes become outputs after the Speakers' (and before the probes'), go
  through the same oversampler, and are combined per sample into `SignalSamples`. With no signals the engine's outputs
  are exactly as before, so the plugin's path is unchanged: `SpiceForge.Validate processor` passes on every test
  circuit, with the audio output bit-identical and a signal across the Speaker equal to its output (0 difference:
  the Speakers' cathodes are grounded), probes off and on. The server resolves an element to its Anode and Cathode
  nodes through `circuit.check`, as probes do, and each channel's signal is kept by name. Checked in process on the
  Rat with three output channels driven block by block: Out 2 (across the Speaker) equals Out 1 (the Speakers) times
  its 1 V full scale, exactly, and Out 3 (across the Input) peaks at the 0.5 V test sine.
- **Run the server in Release: a Debug build simulates four to five times slower.** The SD-1 read 33% of real time
  in the server against far less in the VST. `SchematicProcessor` alone, at 48 kHz, 4×, cap 32, sinc, probes on:
  6% of real time in Release and 25 to 27% in Debug; the Rat 20% against 42 to 44%. The compiled step is a dynamic
  method, always optimized, but the oversampler and the processor's loops run unoptimized in a Debug build, and
  `dotnet run` builds Debug unless told otherwise. Probes (on in the server, off in the VST) add about a third
  (0.66 to 0.88 µs a sample on the SD-1), and pacing blocks in real time rather than running flat out changes little
  (5.6 against 6.3%). The server now warns at startup when it's a Debug build, and the README runs it with
  `-c Release`.
- **JackSharpCore needed a rewrite before it could carry audio.** Its process path allocated every cycle (LINQ arrays,
  an object per port, each buffer copied into a new array and pinned to copy back), and patching lived on a separate
  `Controller`, so a program that both processes and patches needed two JACK clients. JackSharpCore 1.0
  (`Dependencies/JackSharpCore`, merged upstream) layers it as JACK does: `Client` has the server's state, the graph
  and patching; `Processor` adds ports and a process callback over spans of JACK's own buffers; `Controller` is a
  client without ports. Its tests measure 0 bytes allocated on JACK's thread over 200 cycles of audio and MIDI.
- **Test JACK on the dummy backend.** `jackd -d dummy` runs a real server on a timer, with no sound card: the
  library's tests and `SpiceForge.Validate jack` start their own (the check under a private server name, set for
  SpiceForgeServer through `JACK_DEFAULT_SERVER`), so neither touches a sound card or the user's server. A test run
  that crashes leaves its jackd behind, and later runs then use that server instead: every connection and latency
  failure after one crash came from such an orphan, not the code.
- **JACK2 1.9.22 on Windows has two bugs worth knowing.** Closing a client within milliseconds of activating it can
  deadlock in `jack_client_close` (the race jack2 pull request 889 addresses, unmerged): opening, activating and
  closing in a loop hung after 1 to 16 rounds, and a 100 ms wait before closing made 60 rounds clean, so JackSharpCore
  keeps a client active at least 100 ms. With a second client of the same process watching the graph, a close could
  still stall (once for about four minutes); SpiceForge's one long-lived client doesn't meet it. And
  `JackUseExactName` is ignored: a second client of the same name opens, so JackSharpCore checks the name it got.
- **A JACK client should stay in the graph.** Opening it per simulation, as an ASIO device is, dropped every
  connection made to SpiceForge's ports in a patchbay when a simulation stopped. The JACK stream is now resident: it
  opens when JACK is configured (at startup too), plays silence with nothing running, and simulations and the test
  swap in and out of it (`AudioStream.SetClient`), the new client prepared before the real-time thread sees it.
  `SpiceForge.Validate jack` checks it: the client is in the graph and connected before anything runs; a connection
  made by another client is reported in `audio.state` within a moment (graph events are coalesced over 100 ms); a
  simulation runs without the client ever leaving the graph, and stopping keeps it and the patchbay connection.
- **Report the simulation's latency to JACK.** JackSharpCore 1.1's `Processor.AddedLatency` installs JACK's latency
  callback, which passes upstream latency through the client plus what it adds. SpiceForge sets it to the running
  build's resampling delay, and again after each rebuild: on the dummy server SpiceForge's input reported a capture
  latency of 256 frames and its outputs 296, the Rat's 40 samples (4× sinc) added.
- **JACK works as a driver like any other.** `SpiceForge.Validate jack` runs the Rat at the server's 48 kHz and 256
  frames: SpiceForge's ports connected to `system:capture_1` and `system:playback_1` and `_2` (as another client sees
  the graph), load 0.20 of real time, no dropouts in 5 s, reported latencies of 256 frames in and 512 out, the client
  leaving the graph when the simulation stops, and the internal clock taking over, with JACK's reason, when the
  server is killed mid-simulation.
- **Close the device when nothing runs.** The device opens when a simulation or the test starts, and closes when it
  stops, so an idle server doesn't hold an ASIO driver another program (a DAW) wants. A configuration that doesn't
  check is refused before the running device is touched, unless a different ASIO driver has to be loaded to check
  it, in which case the old one is reopened.

## Packaging

`SpiceForge.Package` builds a self-contained server with the web editor for Windows (a zip, and an Inno Setup
installer) and Linux (a tarball), started in a window of its own.

- **A Chromium browser's app mode makes a good enough app window.** `--app=<url>` with a profile of its own
  (`--user-data-dir`, in the settings folder) gives a window with no tabs or address bar and its own taskbar entry,
  with nothing new to ship: Edge is always on Windows 10 and 11. Whether the window has closed comes from the editor's
  connection, not the browser's process (a browser can stay running in the background): the server stops 5 s after the
  last editor disconnects, and a reload reconnects well within that. A second launch reads the running server's port
  and token from `server-instance.json`, checks `/health`, and opens another window onto it.
- **Don't ship ReadyToRun code.** With tiered compilation off (see Audio I/O), precompiled code is used as it is,
  less optimized than the JIT's, on the audio path; so the packages are self-contained but JIT-compiled.
- **Properties on a publish's command line reach every project it builds.** `-p:OutputType=WinExe` (for a server
  without a console window) made the libraries executables too, and the publish failed; `-p:Version` would have
  versioned them all. The server's project takes properties of its own (`SpiceForgeNoConsole`, `SpiceForgeVersion`)
  and sets its own `OutputType` and `Version` from them.
- **Write the Linux tarball with its permissions.** Tar tools on Windows don't reliably record the execute bit, so the
  packaging tool writes the tarball itself (`System.Formats.Tar`), the server and `createdump` 755 and the rest 644.
- **Checked as packaged.** Each package's server, run from its folder with the window turned off, served its own
  editor and the 131-entry catalogue, listed its audio drivers (Windows: None, ASIO, JACK; Linux, under WSL's
  Ubuntu 22.04: None, JACK), logged to its file (Windows), and stopped itself when its client left. Both are about
  48 MB.
- **Ship the license texts, not just a credits list.** MIT asks for the copyright and permission notice to go with
  every copy, and the packages had only SpiceForge's own `LICENSE`, while shipping LiveSPICE, ComputerAlgebra,
  JackSharpCore, NAudio and the .NET runtime. The server's build now copies their texts into `licenses/` beside it
  (the submodules' own files; NAudio's and .NET's, which no file here held, checked in verbatim from upstream under
  `Assets/Licenses`), it serves them at `/licenses/`, and the editor's About dialog shows them. The .NET runtime's and
  ASP.NET Core's third-party notices (97 KB and 57 KB) are linked rather than bundled. `SpiceForge.Validate server`
  checks every listed file is served. Inter and the Material Symbols are the plugin's, which is packaged apart.
- **The SDK already records the commit.** The informational version carries `+<commit>` (Source Link, on by
  default), so the About dialog and the startup summary needed no build step for it. A development build was
  versioned 1.0.0, a release it never was; the server's project now versions it after the latest tag
  (`git describe --tags --abbrev=0`, so "0.0.1-dev"), in a target before `GetAssemblyVersion`, which derives the
  informational version from `Version`. A package's `SpiceForgeVersion` skips it (checked: 9.8.7+commit). The
  commit doesn't say whether the tree had uncommitted changes.
- **Publish the server as a single file, so the package's folder shows what a user needs.** A self-contained publish
  put 353 files at the package's root, 322 of them DLLs, around the six a user wants. With `PublishSingleFile` the
  runtime and every assembly are in the one executable; on Linux the runtime's native libraries are linked into it
  too, leaving nothing beside it. Symbols are embedded (`DebugType=embedded`), so traces keep line numbers. What was
  left was cleared too: reference documentation (`PublishReferencesDocumentationFiles=false`), an empty static web
  assets manifest (`StaticWebAssetsEnabled=false`), and on Windows IIS's `web.config`
  (`IsTransformWebConfigDisabled`) and its hosting module `aspnetcorev2_inprocess.dll`, which the packager deletes.
  The roots are now the server, `appsettings.json`, the README, LICENSE and CREDITS (and the icon on Linux), with
  `Components`, `Examples`, `licenses` and `wwwroot`; the Linux tarball holds 42 files instead of 389, and the
  packages are smaller (tarball 44.5 against 48.1 MB, zip 44.7 against 48.8, installer 33.2 against 35.2). Nothing
  reads an assembly's path, and `AppContext.BaseDirectory` is still the executable's folder. Checked by running the
  whole `SpiceForge.Validate server` against the Windows package's executable (`SPICEFORGE_SERVER`, new): 0 problems,
  simulation and the editor's conformance included, with the examples found beside it. The Linux executable can't
  run here, so it's checked only by its contents.
- **Packaged examples must be reachable from the editor, not just present.** The Linux tarball held all 20 of
  LiveSPICE's examples in `SpiceForge/Examples`, yet a user saw none: the file dialogs start in
  `Documents/LiveSPICE`, which only the Windows installer fills with the examples (and which a fresh account may not
  have at all). The server now reports the examples folder in `hello` (`folders.examples`: beside the server in a
  package, the submodule's from the repository), the dialogs list it under Places with the LiveSPICE folder, and they
  start there when the folder they'd start in can't be listed. Nothing is copied into the user's documents, but the
  server makes `Documents/LiveSPICE` at startup if it's missing, as LiveSPICE's `App.UserDocuments` does (just the
  folder, not `Components`), so Save As has it to go to. `SpiceForge.Validate server` now starts the server with a
  user folder that doesn't exist and checks it's made, and that the examples folder is named and lists them (20,
  from the repository).
- **A quiet build hides its errors.** `dotnet build -v q`, cut to its last lines, printed "0 Error(s)" for the
  project that built while the checks project failed to compile; the stale checks then ran and passed without the
  new one. Look for the check's own line, or build without trimming the output.
- **The git tag is the version's one source.** It used to come from three places that could disagree: the version
  typed into the Release workflow (passed separately to the build, the installer and the release), the packager's
  default when run by hand (`0.1.0`, a release that never existed), and the installer's (`0.0.0`); and the tag itself
  only appeared when the draft was published. Development builds were "0.0.1-dev", which semver sorts *before* 0.0.1.
  Now the server project's `SpiceForgeGitVersion` target reads `git describe --tags --long`: a tagged commit is that
  version, a later one the next patch with the commits since (`0.0.2-dev.9`), or after a pre-release tag, on from it
  (`0.3.0-beta.1.dev.3`); none is `0.0.0-dev`. The packager asks MSBuild for it (`-target:SpiceForgeGitVersion
  -getProperty:Version`) and has no `--version`; the installer has no default. The Release workflow tags the commit
  first, checks the build reads back the version asked for, and pushes the tag only with the packages built; a rerun
  of an unpublished version reuses or moves the tag. Checked in a scratch clone: 9 after v0.0.1 gave 0.0.2-dev.9, a
  tagged commit 0.0.2, 3 after v0.3.0-beta.1 gave 0.3.0-beta.1.dev.3, a tagged pre-release itself, and no tags
  0.0.0-dev. MinVer does the same from a package, but only the server is versioned, and ten lines of MSBuild do it.
- **The server's first line of output is no longer the listening line.** The About summary comes first, and
  `SpiceForge.Validate server` waited for "listening" on the first line only; it now reads lines until it.

## Open issues

- The Rat at 0.5 input was −10 dB from stock LiveSPICE, while stock under noise stayed near −80 dB, and the cause was
  never established. With both sides tightly converged it agrees to −170 dB (see Validation).
- The Orange Squeezer's start-up from DC can't be judged by the noise control.
- The renderer can't use lookup tables yet, and tables aren't saved to disk.
- Approximate cuts exist only as the `cut` experiment (see Stage segmentation). Gauss-Seidel, other black-box solvers
  and the C++ emitter haven't been started.
- Live coefficient reads remain a cost on large circuits: 14 to 21% of the step (the Big Muff's is about 3 µs slower
  than baked, 18.7 against 15.4). Keeping the pot-leg block factored doesn't help, for every affected unknown or chosen
  per unknown (see The low-rank rebuild that wasn't): the folded coefficients are already the sparsest form, and
  what's left is the cost of reading them.
- Stage cuts across loaded devices (BJT bases, conducting tube grids) need a load model that includes the downstream
  stage's input device; `cut` has only no load and a linear load.
- SPICE-style junction limiting (see SPICE's junction limiting) closed the Fuzz Face's robustness gap with
  LiveSPICE, but its critical value of 30 has a thin margin at the Op-Amp Model (25 already hurts it), and at a cap
  of 8 it costs that circuit at 1.0 input (−52 → −39 dB) and the Rat at 0.5 (−26 → −23 dB). A critical value
  per junction, from its saturation current as SPICE's vcrit is, would be the principled fix; the models don't
  expose Is to the extractor today. The Fuzz Face still caps 88 steps in the `dynamic` sweep.
- The Phase 90's sweep has capped steps (about 6,200) that aren't explained, and junction limiting doesn't touch them;
  in `livespice` it caps none at 100 iterations.
- The Blues Breaker with its pots at 0 is −111 dB from LiveSPICE at 5.0 input, with both sides tightly converged and a
  −246 dB control: a small, deterministic difference, not yet looked into. A pot at an end stop (a near-zero leg)
  is the likely place to start.
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
- Knobs and switch buttons go to the least crowded of eight spots beside their symbols; on a dense schematic every
  spot may overlap something, and the least bad one is used.
- A simulation's remembered setup is per file and per browser; an untitled schematic's isn't remembered.
- A JACK connection changed in a patchbay is shown, not adopted: the configured channels still say what SpiceForge
  connects when its client opens (at startup, or after a configuration change). Whether a patchbay change should
  become the configuration is open.
- JACK2 1.9.22 on Windows can stall closing a client opened and closed in quick succession while another client of
  the same process watches the graph (see Audio I/O, JACK); SpiceForge's one long-lived client doesn't do that.
- Output signals can be the Speakers or the voltage across picked two-terminal components, not an expression
  (`sim.setOutput` answers `unsupported` for one): that needs a way for a client to name voltages, and evaluation
  after the step for anything not linear in node voltages. Remembered output signals are put back a few seconds
  after a simulation starts, so a change made in those seconds can be overridden.
- A rebuild at the smallest buffers (96 kHz, 64 samples) can still glitch, from the build's own garbage collections
  (see Audio I/O). Options not yet tried: `GCSettings.LatencyMode = SustainedLowLatency` while a device runs, a
  `SchematicProcessor` method that sizes its buffers on the build thread so the first block allocates nothing, and
  building in a lower-allocation way.
- The first block a new simulation processes overruns its buffer (7.5 ms, the engine's first run). It comes straight
  after silence, so it isn't heard, but it's counted as a dropout.
- The plugin defaults to 4× with a cap of 32, which the study found nearly as accurate as 8× for about 40% less CPU.
  With sinc resampling, error falls about 12 dB per doubling of N, so a user choosing sinc may want 8× back.
- The web front end's tooltips sometimes don't appear, with no pattern found yet. They're all native (`title`
  attributes, and `<title>` elements on the schematic), so Chromium decides when they show. The code was checked
  for causes of its own: nothing rebuilds the DOM under the pointer while idle (the schematic redraws only on edits,
  selection and live control changes), and no overlay covers a titled element. Chromium's own rules are the likely
  cause: no tooltip after a click until the pointer leaves the element, none after a key press until the mouse moves,
  probably none while the app window is inactive (unconfirmed), and often none again once one has shown. One real
  defect: during a simulation the load label's tooltip text changes every second (`setStats`), restarting Chromium's
  delay each time. Two checks would narrow it down: hover the toolbar straight after switching from another program,
  then again after clicking into SpiceForge; and click a button, then rest on it. The proposed fix is a tooltip of
  the editor's own (one delegated hover handler showing the nearest `title`, following text that changes while it's
  shown, and themed), with the schematic's `<title>` elements moved to a data attribute.
