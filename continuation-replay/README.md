This dependency-free .NET 10 CLI replays a headerless one-minute OHLC export through the same `ContinuationEngine`, `TradePlanner` and `TradeTracker` source as the indicator. It makes an explicit valuation assumption of **one full-size NQ contract**: $20 per point and 0.25-point ticks. A CSV supplies no instrument identity or exchange-session metadata.

From the repository root:

```sh
dotnet run --project continuation-replay/NqContinuation.Replay.csproj -- \
  --replay /path/to/one-minute.csv --out /path/to/private/trades.csv

dotnet run --project continuation-replay/tests/NqContinuation.ReplayTests.csproj
```

Optional `--costs 12.50` sets total round-trip costs in dollars. Costs default to zero because the export does not identify fees. All other strategy parameters stay at their documented defaults: `PriceOnly`, $250 primary desired net profit, $200/$300 alternative exits, $150 maximum planned loss, one entry-slippage tick, one stop-slippage tick, and minimum reward/risk 1.5. The CLI exposes no fixture-tuning options. These targets are three alternatives for one contract, with the primary target used for reference exits.

Each physical line has `yyyy-MM-dd HH:mm:ss;Open;High;Low;Close`, invariant decimal points and a literal unspecified-timezone timestamp. Dates are preserved. Invalid rows retain their physical indices and reset state when closed. Only exact one-minute cadence is valid; normal midnight rollover does not imply an exchange session. The five-minute export must not be passed to this one-minute engine.

The final physical row is forming and its high, low and close never reach the engine or exit tracker. A closed confirmation may use only the next row's fixed index, minute-start time and open. Missing, malformed or extra future HLC columns cannot gate that entry; they become a closed-data boundary later. Automatic higher-timeframe aggregation requires all five consecutive clock-aligned minute bars. The default 21 completed higher groups require 105 closed minutes from a group boundary; partial intervals and gaps extend warmup.

The CSV output contains planned entry, structural stop, three target prices, dollar risk and resolved reference outcomes. `exit_candle_time` is the candle's start stamp. `outcome_known_at` is its close/next minute stamp, and `outcome_time_basis` identifies nominal close estimates when the next minute is absent; these fields do not claim an exact intrabar fill time. The derived `.summary.json` records counts, parameters, issues and execution assumptions.

The existing reference trade is evaluated before the current closed bar advances trend state. Only one reference trade may be active. If one closed candle touches both stop and target, the stop wins. Adverse stop gaps use the open plus stop slippage and can exceed the planned cap. The timeout uses the close after 15 closed entry-and-following candles. Gaps, invalid closed data and explicit session boundaries end active references unresolved, with no synthetic PnL. Resolved net dollars use the stated NQ assumptions; unresolved references do not contribute.

Exit codes are `0` for a completed replay, `1` for a file/runtime failure, `2` for invalid CLI arguments, and `3` when no usable one-minute successor interval exists. Cadence-rejected input still produces diagnostic output. Input must differ from both output destinations. Store uploaded quotes and derived per-trade reports privately; do not commit them.

`ReplayDriver.Run` accepts the unique physical-line indices produced by `ReplayCsv.ReadRows`. Its `IReplayEngine` injection supports behavioral tests; the CLI always uses the shared production engine. Optional explicit session-boundary indices support tests and callers with actual session metadata; the CSV CLI infers none.
