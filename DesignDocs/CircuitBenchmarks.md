# Circuit benchmarks

How fast SpiceForge's engine runs the LiveSPICE example circuits, against LiveSPICE's own simulation. Both sides are
engines only, with no application layer (no audio I/O, UI, probes or control smoothing), and run at LiveSPICE's
default settings:

- 48 kHz, 8× oversampling, Newton's iteration cap of 8, linear resampling (LiveSPICE's interpolation in, averaging
  out).
- One output, the speaker node, as LiveSPICE's own benchmark does.
- The input is 82 Hz with harmonics at 0.5 V peak. Knobs are held still at their schematic positions.
- Time is microseconds per output sample, the best of 5 passes of 0.5 s of audio, after a first untimed pass.
- Both engines run in the same .NET 10 process.

The three columns of times are:

- **Stock:** LiveSPICE's `Simulation`, unmodified, from the pinned submodule (LiveSPICE `b2a4bb0`, its current
  master). Pots and switches are built in, as in the LiveSPICE app, which re-solves the circuit when a knob moves.
- **Fixed:** SpiceForge's compiled engine with pots and switches built in, like stock.
- **Live:** SpiceForge's compiled engine with pots and switches as runtime parameters, as the SpiceForge editor and
  plugin run it. Moving a control never re-solves the circuit.

"Fixed vs stock" and "Live vs stock" are speedups: stock's time over ours, so above 1× is faster than LiveSPICE.
"Live cost" is how much longer live takes than fixed.

Measured at SpiceForge commit `3909840`, on an AMD Ryzen 7 5700X running Windows 10. To regenerate:

```
dotnet run -c Release --project SpiceForge.Validate -- bench
```

## Results

| Circuit | Pots | Switches | Stock µs | Fixed µs | Live µs | Fixed vs stock | Live vs stock | Live cost |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Examples/59 Bassman Preamp+Tone Stack | 4 | 1 | 7.30 | 3.35 | 3.62 | 2.18× | 2.02× | +8% |
| Examples/Big Muff Pi | 3 | 0 | 10.2 | 14.6 | 17.5 | 0.70× | 0.58× | +20% |
| Examples/Boss Super Overdrive SD-1 | 3 | 0 | 1.33 | 0.90 | 0.99 | 1.47× | 1.34× | +10% |
| Examples/Bridge Rectifier | 0 | 0 | 1.17 | 1.91 | 1.91 | 0.61× | 0.61× | 0% |
| Examples/Common Cathode Triode Amplifier | 0 | 0 | 2.53 | 1.47 | 1.47 | 1.73× | 1.72× | 0% |
| Examples/Common Emitter Transistor Amplifier | 0 | 0 | 0.73 | 0.58 | 0.58 | 1.26× | 1.25× | 0% |
| Examples/Dunlop Cry Baby GCB-95 | 1 | 0 | 5.89 | 4.76 | 4.31 | 1.24× | 1.37× | -9% |
| Examples/Fender 5e3 | 3 | 0 | 27.5 | 18.4 | 21.0 | 1.50× | 1.31× | +14% |
| Examples/Ibanez Tube Screamer TS-9 | 3 | 0 | 0.68 | 0.55 | 0.54 | 1.25× | 1.25× | 0% |
| Examples/MXR Distortion + | 2 | 0 | 2.21 | 1.33 | 1.65 | 1.66× | 1.34× | +24% |
| Examples/MXR Phase 90 | 2 | 0 | 26.5 | 20.5 | 24.2 | 1.29× | 1.09× | +18% |
| Examples/Marshall Blues Breaker | 3 | 1 | 1.99 | 1.51 | 2.36 | 1.32× | 0.84× | +56% |
| Examples/Marshall JCM2000 DSL Preamp | 5 | 1 | 21.0 | 11.8 | 12.6 | 1.78× | 1.66× | +7% |
| Examples/Marshall JCM800 2203 Preamp | 5 | 0 | 19.0 | 12.2 | 13.7 | 1.56× | 1.39× | +13% |
| Examples/Marshall JCM800 2203 preamp modded | 6 | 2 | 21.2 | 10.9 | 13.4 | 1.94× | 1.59× | +23% |
| Examples/Op-Amp Model | 0 | 0 | 1.06 | 0.82 | 0.82 | 1.28× | 1.29× | 0% |
| Examples/Orange Rockerverb 50 Preamp | 6 | 0 | 25.6 | 14.2 | 15.7 | 1.81× | 1.63× | +11% |
| Examples/Passive 1stOrder Lowpass RC | 0 | 0 | 0.02 | 0.13 | 0.13 | 0.13× | 0.13× | 0% |
| Examples/Pro Co Rat | 3 | 0 | 7.47 | 5.98 | 6.82 | 1.25× | 1.10× | +14% |
| Examples/Wien Bridge Oscillator | 1 | 0 | 0.94 | 0.83 | 0.92 | 1.13× | 1.03× | +10% |
| Unstable/Fuzz Face | 2 | 0 | 1.97 | 1.10 | 1.18 | 1.79× | 1.67× | +7% |
| Unstable/Orange Squeezer Compressor | 2 | 0 | 7.19 | 5.80 | 6.06 | 1.24× | 1.19× | +5% |
| Unstable/Triode stage | | | | | | | | LiveSPICE can't solve it |

Median over the 22 circuits both can run: fixed 1.30× stock, live 1.30× stock. That both medians land on 1.30× is a
coincidence of which circuits sit in the middle. Live costs a median of 9% over fixed, from −9% to +56%.

## Notes

- **Live is faster than stock on 18 of the 22 circuits.** The exceptions are the Big Muff (0.58×), the Blues Breaker
  (0.84×), the bridge rectifier (0.61×) and the passive RC filter (0.13×). With controls fixed, only the Big Muff,
  bridge rectifier and RC filter are slower than stock.
- **The smallest circuits measure per-call overhead, not solving.** The passive RC filter takes 0.02 µs a sample in
  stock and 0.13 µs in ours.
- **The Blues Breaker is the outlier for live cost.** It runs 56% slower live than fixed. Circuits with only pots
  cost −9 to +24%, and the other circuits with switches (the Bassman, JCM2000 and modded JCM800) 7 to 23%. Its
  one switch is the likely cause, but that hasn't been isolated.
- **The Cry Baby runs faster live than fixed.** It did so in every run; the reason hasn't been looked into.
- **The "unstable" circuits ran in stock at this input.** The Fuzz Face and Orange Squeezer fail in LiveSPICE at
  other pot settings and signal levels (see `LessonsLearned.md`). At these settings stock stayed finite, so both are
  timed. The Triode stage fails in LiveSPICE's own solve, which both engines start from.
- **Times repeat to within a few percent.** Over three full runs, most circuits agreed to about 2%; the largest
  spread was 6%, on the Blues Breaker's live time (2.36 to 2.50 µs).
- **Speed only.** The engines don't produce identical output: ours starts from a more tightly converged DC point and
  uses junction limiting, so they part ways where Newton hits the iteration cap. Accuracy against LiveSPICE is what
  the `livespice` and `jit` checks measure.
