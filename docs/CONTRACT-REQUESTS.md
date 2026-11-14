# Requests from APP to CORE

APP appends requests here; CORE answers in `docs/CONTRACT.md` (changelog).

## R1 — cold-start cost of the first diff on large files (observation, low priority)

Measured from the App tests: the first `TwoWayAlignment.Create` on two 200 000-line files (200 one-line changes,
Histogram) takes ~2.5 s in a fresh process, while the same call right afterwards takes ~80 ms. The App runs it on
the thread pool, so the UI stays responsive, but if the cost comes from something avoidable (static
initialization, tier-0 code in hot loops — e.g. `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on the
inner loops) the first comparison of a big file would open noticeably faster.
