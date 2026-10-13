# Pivot Reversal (Confirmed) for ATAS

A C# indicator for **classic Windows ATAS**, intended for reversal entries on NQ, ES, and Gold futures. Green arrows indicate confirmed long setups; orange/red arrows indicate confirmed short setups. It submits no orders.

Start with a **1-minute execution chart** and a separate **5-minute context chart**. Use the context chart to assess nearby session levels and market structure. The indicator calculates on the chart to which it is attached; the separate 5-minute chart is a discretionary context recommendation, not an automatic higher-timeframe filter. This configuration is a starting point, not an optimized timeframe for each instrument. Tick size comes directly from ATAS, so Gold does not inherit the 0.25 grid seen in the uploaded data.

## Signal logic

1. A swing low/high becomes usable after two candles on each side have closed. Equal-price plateaus do not qualify.
2. A long setup sweeps a previously confirmed swing low by at least one tick and closes at least one tick above that swing. A short setup mirrors this at a swing high.
3. The rejection wick occupies at least 25% of the candle range; the close lies in the upper 40% for longs or lower 40% for shorts. The range must be 0.3–3.0 times the prior 14 candles' mean true range.
4. Within the next three closed candles, a long must close at least one tick above the rejection candle's high with a bullish body. A short must close below its low with a bearish body. A return to the rejection extreme cancels the setup. A newer valid setup can replace a pending setup in the same direction.
5. The arrow is placed on the **confirmation candle**, once its successor begins. No entry arrow is placed on the earlier swing candle. Three full candles must intervene before another signal.

For example, a 10:05 one-minute confirmation candle becomes actionable on the first tick of the 10:06 candle. Its closing price is a signal reference, not a simulated executable fill. Signals remain stable for the same closed data and settings; platform history corrections and parameter changes recalculate them.

## Order-flow modes

The default is **RequireEvidence**, with **MinimumEvidence = 1**. The rejection candle needs positive volume, available bid/ask volume, and at least one of these flags:

| Evidence flag | Default requirement |
|---|---|
| Relative volume | At least 1.5 times the previous 20 candles' mean volume |
| Opposing delta | Bullish long setup with negative delta, or bearish short setup with positive delta; magnitude at least 10% of candle volume |
| Delta recovery | Final delta recovers from the running minimum/maximum by at least 20% of volume; extrema must be internally consistent |
| Extreme aggression | In the lowest/highest 20% of the candle, bid/ask volume is at least 10% of candle volume and the opposing aggression share is at least 65% |

Extreme aggression plus price rejection is an **absorption proxy**. It does not directly measure passive limit orders. The evidence count is a count of conditions, not a confidence percentage; some conditions may be correlated.

**PriceOnly** applies the same price and volatility rules without an order-flow requirement. Use this explicitly for OHLC-only imports. RequireEvidence withholds signals when bid/ask volume is missing; it does not switch modes automatically. Footprint data is optional for the extreme-aggression flag; other evidence can qualify without it.

The implementation uses candle OHLC, confirmed swings, volatility, volume, bid/ask totals, final delta, running delta extremes, and price-level bid/ask data. Separate DOM/tape event filters would need additional historical data and a separate validation path.

## Build and install

The package contains source, not a precompiled platform DLL. ATAS's official build method uses assemblies from your installed ATAS folder. The order-flow engine and wrapper unit tests were compiled separately; the real platform assembly and runtime were unavailable in the build environment.

1. Install the .NET SDK matching your platform's runtime. Current classic ATAS uses .NET 10; older builds may use .NET 8. Verify your installed build. This project targets classic ATAS; ATAS X uses a different color/API build configuration.
2. Locate `ATAS.Indicators.dll` in the ATAS installation directory.
3. Open PowerShell in this package's folder. For a .NET 10 build:

```powershell
dotnet build .\PivotReversal.csproj -c Release -p:AtasTargetFramework=net10.0-windows '-p:AtasPath=C:\Program Files (x86)\ATAS Platform'
```

For an older .NET 8 installation, change the framework:

```powershell
dotnet build .\PivotReversal.csproj -c Release -p:AtasTargetFramework=net8.0-windows '-p:AtasPath=C:\Program Files (x86)\ATAS Platform'
```

Replace `AtasPath` with your actual installation folder. The project references the installed vendor assemblies with copying disabled. The built file is `bin\Release\net10.0-windows\PivotReversal.dll` (or the corresponding net8.0-windows folder).

4. In ATAS, open the chart's Indicators dialog and choose **Add custom indicator** to import the built DLL. Alternatively, place just `PivotReversal.dll` in `%APPDATA%\ATAS\Indicators` and restart ATAS. Do not copy vendor DLLs into that folder.
5. Add **Pivot Reversal (Confirmed)** to a 1-minute chart with bid/ask history. Start with RequireEvidence and one required flag. Live alerts are off by default; enable **Live alerts** if wanted.

Session boundaries use ATAS's own session definition. The engine also resets after gaps longer than 20 minutes, invalid OHLC, nonconsecutive indices, or nonincreasing timestamps. After a reset it needs fresh volatility history and confirmed swings. For slower charts, set the maximum allowed gap above the chart's interval.

The chart exposes long and short arrows plus hidden numerical series: evidence count, invalidation reference price (one tick beyond the rejection extreme), and bid/ask availability (0 or 1). The invalidation reference is not a calculated position size or submitted stop order.

## Verify or replay without ATAS

The console projects have no third-party package dependencies and target .NET 10. They compile the same engine used by the indicator.

```powershell
dotnet run --project .\tests\PivotReversal.Tests.csproj
dotnet run --project .\adapter-tests\PivotReversal.AdapterTests.csproj
dotnet run --project .\tests\PivotReversal.Tests.csproj -- --replay .\data\Chart.csv --tick-size 0.25 --out .\signals.csv
```

The adapter tests use explicit test doubles, not an ATAS runtime. They verify callback timing, alerts, session resets, settings, and recalculation, but cannot prove compatibility with your installed vendor DLLs. The CSV CLI uses PriceOnly and treats the final row as forming. It preserves literal dates and reports malformed candles rather than repairing them. A signal's availability time comes from the following observed candle; gaps and invalid successor rows block delivery.

Keep your chart exports locally in `data/` to run the replay command above. This public source package includes the indicator, executable tests, and build instructions. Verification passed 39 engine/replay tests (including 69 parser/replay/CLI assertions) and 7 wrapper tests. The supplied OHLC fixtures verified price logic and data handling; they do not validate order-flow performance or profitability. Test logs are in `reports/`.

## API references

- [ATAS custom indicator development and runtime matching](https://docs.atas.net/en/md_DataFeedsCore_2Docs_2en_20010__BasicIndicator.html)
- [ATAS indicator lifecycle](https://docs.atas.net/en/classATAS_1_1Indicators_1_1BaseIndicator.html)
- [Candle and price-level data](https://docs.atas.net/en/md_DataFeedsCore_2Docs_2en_20025__ReceivingProcessingData.html)
- [Official arrow and alert example](https://github.com/AtasPlatform/Indicators/blob/Develop/Technical/DeltaTurnaround.cs)
- [Classic and ATAS X color aliases](https://github.com/AtasPlatform/Indicators/blob/Develop/Technical/GlobalUsings.cs)

API research checked against the official public repository at commit `c1915a661eff9dd4deb703bfb64fdc568835186c`, dated 2026-10-09.
