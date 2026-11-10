# Lessons learned: the solver milestones

What the work up to the `dynamic-pots` branch taught us: the eight milestones in `PLAN.md`, the singular-E fix, the
switch to exact rebuild, and segment black boxes with lookup tables. Dynamic pots have their own notes in
`DynamicPotsLessonsLearned.md`.

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

## Open issues at the branch point

- The Rat at 0.5 input is −10 dB from stock, while stock under noise stays near −80 dB. The cause isn't established.
- The Orange Squeezer's start-up from DC can't be judged by the noise control.
- The renderer can't use lookup tables yet, and tables aren't saved to disk.
- Approximate cuts, Gauss-Seidel, other black-box solvers and the C++ emitter haven't been started.
