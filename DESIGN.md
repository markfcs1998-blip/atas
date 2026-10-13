# Pivot Reversal for ATAS

Build request: a custom ATAS indicator for pivot-based reversal entries. User confirmed closed-candle signals only and trades NQ, ES, and Gold. User delegates timeframe selection. Use 1-minute execution charts and a separate 5-minute context chart as a starting configuration; no claim this is optimal or profitable.

## Signal and timing

Confirm swing highs/lows using two candles to the left and two to the right. Require strict extremes against all four neighbors; equal-price plateaus do not qualify. Only previously confirmed swings can be swept. A long setup trades at least one instrument tick below a confirmed low and closes at least one tick back above it; shorts mirror this. Require the rejection wick to be at least 25% of the candle range, a closing position in the top/bottom 40%, and range between 0.3 and 3.0 times the prior 14-bar arithmetic mean true range. Use ATAS instrument tick size, never infer it from the symbol or CSV grid.

Within the following three closed candles, a long confirmation must close at least one tick above the setup high with a bullish body; a short must close one tick below the setup low with a bearish body. A revisit to/beyond the setup extreme invalidates it. Plot the arrow on the confirmation candle, available at the first calculation of its successor. Suggested invalidation price is one tick outside the setup extreme; it is a reference, not an order. After a signal, require three full intervening candles before another signal. A newer valid setup may replace the pending setup in its direction. No backdated entry arrows, live-candle signals, or order execution.

## Order-flow evidence

Compute four independent, unweighted evidence flags on the rejection candle: volume >=1.5 times previous 20 candles' mean; opposing delta of at least 10% of candle volume on a directional candle; running-delta recovery from MinDelta/MaxDelta of at least 20% of volume; bid/ask aggression at the lower/upper 20% of candle range with at least 10% of volume and 65% dominance. The latter is an absorption proxy, not observation of passive limit orders. RequireEvidence (default) requires Volume>0 and Bid+Ask>0 and at least one flag. PriceOnly bypasses this filter and is the only replay mode possible from the supplied OHLC exports. No silent fallback in RequireEvidence mode. Missing footprints simply omit that flag.

## Boundaries

Keep the deterministic signal engine independent of ATAS. A thin classic Windows ATAS adapter maps candle data, draws long/short arrows, exposes settings, and emits one live alert per signal when enabled. Reset all state on recalculation; suppress historical alerts. Reset across ATAS new sessions, time gaps over 20 minutes, nonconsecutive bar indices, nonincreasing timestamps, or invalid OHLC. The forming final candle is never processed. Settings changes recalculate. Price aggregation preserves literal timestamps; no assumptions about date format beyond literal ISO parsing or exchange timezone.

## Delivery and verification

Ship C# source, a project supporting installed NET8/NET10 ATAS classic assemblies, a dependency-free engine test/replay console, build/install instructions, and replay outputs with actual signal-availability timestamps. Real platform compilation needs installed ATAS.Indicators.dll; do not ship a fake vendor DLL or label engine-only verification a platform test. Test strict pivot timing, no future access, sweep/confirmation symmetry, expiry/invalidation/cooldown, missing flow, all evidence branches, reset, and replay prefix stability. CSV reports exclude malformed candles and final forming candles and state coverage limitations. A fresh agent reviews the whole package.
