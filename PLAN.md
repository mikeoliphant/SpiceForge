# LiveSPICE N-Port Segmented Solver: Implementation Plan

## Architecture

The plan copies LiveSPICE's numerics wherever they affect the result, and changes only how the equations are organized. The pipeline is `Schematic.Build()` → `Circuit.Analyze()` → our code, with LiveSPICE pinned and unmodified.

At each timestep, the discretized circuit is written as:

`E·y = s(past states, inputs) + P·q(w)`, with `w = Q·y`

- `q` holds the nonlinear device expressions, and `w` holds their arguments.
- One offline numeric factorization of `E` gives `w = voc + Z·q(w)`, where `voc` is linear in states and inputs.
- The states then update explicitly: `x' = A·x + B·u + C·q`.
- There's no global solve per sample. Each Newton iteration only touches a k×k system.

## Matching LiveSPICE

Output should stay as close to LiveSPICE as possible, so these behaviors are copied directly.

| Behavior | What we copy |
| --- | --- |
| Integration | BDF2 |
| Oversampling | 8x, linearly interpolated input, averaged output |
| Newton | Starts from the previous sample, no damping, same iteration cap |
| Stopping rule | `abs(dv) < abs(v)*1e-4 + 1e-6`, checked on ports and currents |
| Device models and Jacobians | Same `Component.Analyze` output, differentiated by the same `Gradient` |
| DC start | Same `NSolve` procedure |

Working through the algebra, port-reduced Newton should produce the same iterates as LiveSPICE's full Newton, because eliminating linear equations doesn't change Newton steps. The differences should come down to floating-point roundoff and, where the stopping checks differ, an occasional extra or missing iteration. We'll test this directly.

Block Gauss-Seidel reaches the same answer by a different path, so samples that hit the iteration cap would differ. The default is therefore a joint Newton per coupled group, with Gauss-Seidel opt-in only.

## Milestones

Each milestone is checked against LiveSPICE's own `Simulation`, starting with simple circuits and ending with every example.

1. **Scaffold and reference:** create the new repo with LiveSPICE pinned as a submodule. Confirm the netstandard projects build, then record reference output and Newton iteration counts from `Simulation`.
2. **Extraction:** derive `E`, `P`, `Q` and `q` from `Analysis`, splitting each equation into affine and non-affine parts. This also has to handle currents written inline, such as Child-Langmuir.
3. **Linear circuits:** add BDF2 and the state-space form, running in a plain interpreter. Test on the passive RC/RLC circuits and the 59 Bassman tone stack.
4. **Port reduction and Newton:** test on the diode clipper and rectifier circuits, including the iterate-matching check.
5. **Pro Co Rat:** run it through the interpreter with all pots at their schematic positions, then at several settings via re-solve.
6. **Dependency graph:** split `Z` and the state matrices into strongly connected components and schedule them. The result must match milestone 5 sample for sample.
7. **LINQ JIT:** compile the same schedule with `CodeGen`, then benchmark against `Simulation`. Matrices stay in runtime arrays so dynamic pots can come later.
8. **Coverage:** run every `.schx` in `Tests/`, and report results for the `Unstable` folder separately.

**Later:** dynamic pots, the C++ emitter from the same IR, other black-box solvers, Gauss-Seidel, and approximate cuts.

**Acceptance:** report error against `Simulation` per circuit and per sample, plus CPU per sample. Start with a threshold of −80 dB relative to output level, and tighten it once the actual roundoff floor is measured.

## What to expect and watch for

This plan comes from reading the code. None of it has been compiled or run yet, and milestone 1 is where that starts.

- **The Rat will probably be one coupled group:** the JFET model includes gate diodes, so strictly it couples back to the clippers, and its source node is loaded by the tone and volume network. Under exact matching, expect one Newton group of about 6–7 ports and one state-space block. The speedup should still be real, since Newton only touches that small system, but visible segmentation will mostly appear in circuits with ideal buffers or truly one-way paths. A coupling report will show how large each `Z` term is, so you can see where approximate cuts would be cheap.
- **JFET ports:** the model uses `Min(Source.V, Drain.V)`, which isn't a function of a single voltage difference. Port inference has to work from the raw node voltages and then remove linearly dependent ones.
- **Singular or dependent systems:** `Parallel capacitors` tests dependent derivatives, and `Simulation` itself guards against underdetermined systems. Open switches and `null` KCL entries need handling too.
- **Conditioning:** op-amp gain (300k here) and wide component ranges make `E` harder to factor, so we'll use scaling and pivoting, and log the condition number.
- **Hard samples:** LiveSPICE silently continues when it hits the iteration cap, so we'll compare results on those samples separately rather than let them mask real bugs.

## Sources

- [LiveSPICE](https://github.com/dsharlet/LiveSPICE)
- [ComputerAlgebra](https://github.com/dsharlet/ComputerAlgebra)
- [netlist_codegen](https://github.com/jatinchowdhury18/netlist_codegen)
