# NQ continuation verification — 2026-10-13

The default goal is $250 desired profit with a $150 maximum planned loss for one full NQ contract. This report checks implementation and shows the supplied fixture's actual result; it does not establish a profitable trading edge.

## Executed source checks

- Core: **52/52 tests passed**; dollar/tick/cost math, exact risk boundaries, net target rounding, impulse/pullback confirmation, complete five-minute aggregation/warmup, prior-only baselines, symmetric shorts, expiry, evidence flags, missing data, resets, duplicates, prefix stability, stop gaps, ambiguity and timeouts.
- ATAS wrapper: **11/11 tests passed**, using explicit platform doubles. Confirmation waits for successor open, one alert per entry, fixed dollar guides, forming HLC independence, guide resolution without erasing arrows, history/recalc suppression, stale-value clearing, successor session/gap boundaries, risk-open rejection, MNQ guard and invalid-price resets.
- Replay: **51 parser/pipeline/CLI checks passed**. Literal dates, invariant OHLC, final forming exclusion, fixed successor-open causality, one reference trade, unresolved boundaries, conservative outcomes, output/source collision prevention and invalid cadence handling.
- These three test projects built with zero warnings and errors under .NET 10. Logs are alongside this file.
- A fresh read-only reviewer found a wrapper/core validation mismatch that could retain guides after an invalid zero-price candle. A regression failed at 10/11, then passed at 11/11 after the wrapper adopted shared validation. No remaining concrete core/wrapper defects were reported. Replay received separate review and regression coverage.

## Untuned OHLC replay

The one-minute file was replayed unchanged, explicitly assuming one full-size NQ contract, $20/point, 0.25 tick, PriceOnly, $250 primary desired profit, $150 planned risk cap, zero configured fees, one entry-slip tick, one stop-slip tick and minimum reward/risk 1.5.

| Measure | Result |
|---|---:|
| Physical rows | 1,264 |
| Closed rows processed | 1,263 |
| Forming rows excluded | 1 |
| Invalid closed rows / cadence breaks | 0 / 0 |
| Complete five-minute groups | 252 |
| Discarded partial groups | 1 |
| Candidates | 18 |
| Rejected entry plans | 17 |
| Accepted reference trades | 1 |
| Targets / stops / timeouts / unresolved | 0 / 1 / 0 / 0 |
| Ambiguous stop-and-target candles | 0 |
| Maximum planned risk | $130 |
| Resolved modeled net result, before unspecified fees | **−$130** |

This result supplies no evidence that the indicator achieves $200–$300 per trade. The $150 plan constraint withholds most setups in this fixture. Parameters were not optimized to make the sample profitable.

The five-minute file is invalid input to this one-minute engine and was correctly rejected by the replay CLI with exit code 3. It contains one malformed OHLC row. The indicator derives its own completed five-minute context from consecutive one-minute bars.

## Limits

The CSVs lack instrument, timezone, volume, delta, footprint and exchange-session metadata. NQ valuation is an assumption; default real order-flow mode cannot be empirically verified from these exports. Literal timestamps, including the future label 2026-12-10 relative to this report date, are preserved without interpreting provenance or correcting dates. Source data and per-trade quotes remain local and are excluded from this public repository.

The replay uses fixed next-open reference entries with configured slippage. Stop/target ambiguity is resolved stop first; adverse price gaps can exceed planned risk. Fee defaults are zero and must be replaced with actual costs before interpreting net dollar targets. Outcome candle start and outcome-availability time are reported separately, not as exact intrabar fills.

The real ATAS project cannot complete compilation without installed `ATAS.Indicators.dll` and companion vendor libraries. No real-platform compile/load/live-feed result is claimed. The executable wrapper checks use explicit test doubles, never deployable fake ATAS DLLs. This release is source code; build/import instructions are in README.md.
