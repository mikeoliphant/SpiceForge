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

## Open issues

- Live coefficient reads remain a cost: the Big Muff's inlined step is 5 µs faster than its live one. Keeping the
  solved pot-leg block factored, as LiveSPICE pull request 282 does, might reduce the live set further.
- The SD-1's reduced path costs about 0.9 µs more than its baked path, even with pots baked in.
- The Phase 90's sweep has many capped steps that aren't explained yet.
