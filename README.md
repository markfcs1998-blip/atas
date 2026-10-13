# NQ Continuation (Confirmed) for ATAS

Continuation entries on a **one-minute, full-size NQ chart**, with automatic trend context from completed five-minute intervals. This replaces the pivot-reversal indicator; the earlier version remains in Git history.

**Default reference plan: one NQ contract, $250 desired profit, maximum $150 planned loss.** Entry arrows, a structural stop and target guides support manually managed trades. The indicator places no orders and does not track your actual account position. Targets are intended exits, not guaranteed income.

## Dollar targets and risk

Full NQ is $20 per index point and 0.25 points per tick ($5). [CME contract specifications](https://www.cmegroup.com/markets/equities/nasdaq/e-mini-nasdaq-100.contractSpecs.html).

| Desired profit before costs | Points | Ticks |
|---|---:|---:|
| $200 | 10 | 40 |
| $250 (default) | 12.5 | 50 |
| $300 | 15 | 60 |

Set **Desired profit USD** to your chosen exit, within $200–$300. Gold $200/$300 lines are alternative full exits; a single contract cannot be split into three partial exits. Reference tracking uses the selected target.

Set your actual round-trip fees in **Round-trip fees/costs USD**. Its default is zero because your costs are unknown. Targets round outward to cover desired dollars plus configured costs, so the calculated net amount can exceed the desired amount by less than one $5 tick. Entry and stop slippage allowances each default to one tick.

Planned risk equals entry-to-stop distance × $20, plus stop slippage and configured fees. Entry price already includes its slippage allowance. With zero fees and one stop-slippage tick, the structural stop may be at most 7.25 points from the planned entry: $145 distance + $5 stop slippage = $150. **Actual gaps, fills and costs can exceed this planned amount.** Minimum selected reward/planned-risk ratio is 1.5; selecting a $200 target can reject plans that pass with $250.

A symbol/tick guard accepts full NQ identifiers such as `NQZ6`, `/NQ`, and `NQ 12-26` at tick size 0.25. MNQ, ES and Gold cannot use these full-NQ dollar presets.

## Continuation setup

1. Build complete five-minute intervals from consecutive closed one-minute bars aligned to minute 0–4. Default higher EMAs are 9/21; 21 complete intervals require about 105 minutes after a reset. Partial groups supply no trend update.
2. Long bias requires higher fast EMA > slow EMA, rising higher slow EMA, latest completed higher close > slow EMA, and one-minute fast > slow by at least 0.1 prior ATR. Shorts mirror.
3. Require a preceding directional impulse within eight candles, closing at least 0.5 ATR beyond fast EMA. A later countertrend candle pulls back into the fast-EMA band while holding slow EMA on its close.
4. Within five subsequent candles, require a directional close one tick beyond the entire previous pullback range and fast EMA, near the candle's directional extreme. Confirmation range must be <=1.5 prior ATR. ATR is the previous 14 bars' mean true range.
5. Put the structural stop one tick outside the full pullback/confirmation extreme. At the successor's first tick, use its fixed open for an adverse tick-rounded entry and recompute risk. Withhold signals exceeding $150 planned risk or failing reward/risk, and across session/cadence boundaries.

The arrow is drawn on the entry candle, from the preceding closed confirmation and the entry candle's fixed open. Developing high/low/close never creates, removes or changes it. Corrected history and parameter changes recalculate results.

## Order flow and guides

Default **RequireOrderFlow** needs positive volume, available bid/ask data, delta sign aligned with the trade, and at least one evidence flag:

- Aligned delta >=10% of confirmation volume.
- Delta improvement >=15% of current volume against a prior candle with bid/ask data.
- Relative volume >=1.2 times the prior 20 bars' mean, with positive baseline volumes.
- Aggressive ask (long) or bid (short) volume in the directional extreme 20% of the candle: >=10% of candle volume and >=65% of that zone's bid/ask volume.

Missing flow withholds signals. **PriceOnly** explicitly bypasses the flow filter for OHLC imports. Evidence counts conditions; it is not a probability.

Arrows are green/orange; entry is gray, stop orange, selected target green, alternative targets gold, one-minute fast/slow EMAs blue/purple. Hidden numerical series expose evidence, planned risk and bid/ask availability.

One reference trade is tracked at a time. Guides end after a closed candle touches selected target/stop, or after 15 closed candles including entry. When both levels are touched in one candle, the reference assumes stop first. Stop gaps use the adverse open plus stop slippage. Sessions, missing minutes or invalid data end tracking unresolved. This reference state is separate from your manually placed orders.

## Build and install

This is a **source package**, not a compiled ATAS DLL. Core/replay tests run independently; wrapper tests use explicit ATAS doubles. Real installed-platform compilation and runtime verification still need matching vendor assemblies.

On Windows with the appropriate .NET SDK:

```powershell
dotnet build .\NqContinuation.csproj -c Release -p:AtasTargetFramework=net10.0-windows '-p:AtasPath=C:\Program Files (x86)\ATAS Platform'
```

For older .NET 8 ATAS, use `-p:AtasTargetFramework=net8.0-windows`. Replace `AtasPath` with your installed classic ATAS folder containing `ATAS.Indicators.dll` and its companions. This is a classic Windows/WPF build; ATAS X uses a different build configuration.

Import `bin\Release\net10.0-windows\NqContinuation.dll` (or the net8 equivalent) with **Add custom indicator** in ATAS. Alternatively, place just that DLL in `%APPDATA%\ATAS\Indicators` and restart. Add **NQ Continuation (Confirmed)** to a one-minute full NQ chart with sufficient history and bid/ask data. Entry alerts are off by default.

Resets use ATAS session flags and invalid/minute-cadence boundaries. Load enough one-minute history for a fresh completed trend warmup. Five-minute, range and tick charts do not satisfy this build's input contract.

## Tests and local replay

The dependency-free console projects target .NET 10:

```powershell
dotnet run --project .\continuation-tests\NqContinuation.Tests.csproj
dotnet run --project .\continuation-adapter-tests\NqContinuation.AdapterTests.csproj
dotnet run --project .\continuation-replay\tests\NqContinuation.ReplayTests.csproj
dotnet run --project .\continuation-replay\NqContinuation.Replay.csproj -- --replay .\data\Chart.csv --out .\private-reports\trades.csv
```

Keep your CSVs locally in `data/`; uploaded quotes and per-trade reports are excluded from the public source. See `continuation-replay/README.md` for assumptions and `reports/CONTINUATION_VERIFICATION.md` for actual checks.

The unchanged OHLC sample allowed one PriceOnly reference trade, which stopped for a modeled $130 loss under the full NQ assumption. It does not establish that the indicator meets your income goal. Real order-flow behavior still needs validation beyond the synthetic tests.

## API sources

- [ATAS indicator development, runtime matching and installation](https://docs.atas.net/en/md_DataFeedsCore_2Docs_2en_20010__BasicIndicator.html)
- [ATAS candle and footprint data](https://docs.atas.net/en/md_DataFeedsCore_2Docs_2en_20025__ReceivingProcessingData.html)
- [ATAS lifecycle](https://docs.atas.net/en/classATAS_1_1Indicators_1_1BaseIndicator.html)
- [Official arrow/alert example](https://github.com/AtasPlatform/Indicators/blob/Develop/Technical/DeltaTurnaround.cs)
