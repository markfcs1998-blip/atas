# NQ Continuation — design

User asks to replace reversal entries with a continuation indicator, aiming for $200–$300 per trade on one full-size NQ contract. User explicitly selected $150 maximum planned loss. Existing preference: confirmed signals only. Deliver the updated source through the existing GitHub repository; platform compilation still needs the user's real ATAS libraries.

## Scope and trade amounts

Classic Windows ATAS on a one-minute full-size NQ chart. Automatic five-minute aggregation supplies trend context from fully completed intervals. NQ point value is $20, minimum tick 0.25 ($5), one contract. Primary desired profit is configurable within $200–$300, default $250 (12.5 points before costs). Display $200/$250/$300 alternative exits, not partial exits for a single contract. MaxLossDollars defaults to 150. Round-trip costs default to 0 (unknown); entry slippage 1 tick and stop slippage 1 tick are conservative planning allowances. Planned risk includes stop slippage and configured costs; gaps can exceed it. No order placement or guaranteed per-trade income.

## Causal trend/pullback setup

Each valid closed one-minute bar updates 9/21 EMAs. Both are seeded from first close and require at least SlowPeriod bars. ATR is the arithmetic mean true range of the previous 14 bars, excluding the current bar. Aggregate complete clock-aligned five-minute groups containing minute 0,1,2,3,4 with no gaps; discard partial groups. Higher EMAs 9/21 require at least 21 complete groups. The just-completed five-minute interval is usable at its final one-minute candle close.

Long bias requires higher fast EMA > higher slow EMA, latest higher close > higher slow EMA, higher slow EMA rising against its previous completed value, and one-minute fast > slow by at least 0.1 prior ATR. Shorts mirror it. Within the preceding 8 bars, require a directional impulse candle closing at least 0.5 prior ATR away from its fast EMA in the trend direction. It cannot be the pullback candle itself.

Arm a long pullback when a bearish candle declines from the prior close, touches fast EMA +0.2 prior ATR or lower, and closes at/above the slow EMA while long bias remains. Shorts mirror. Consume the preceding impulse when arming. Track the highest high and lowest low of all pullback/confirmation candles. Expire after 5 subsequent candles; cancel when trend bias is lost or the candle closes beyond the slow EMA against the trade.

Confirmation requires a directional close at least one tick through the entire previous pullback range, at least one tick back beyond fast EMA, in the upper/lower 35% of its range, and range <=1.5 prior ATR. Include the confirmation candle's extreme in the structural stop. Stop is one tick below the tick-floor of the pullback low for longs; mirror with tick-ceiling for shorts. No candidate on the setup candle; three full bars intervene between emitted candidates. New plans are suppressed while a prior reference trade remains active.

## Order flow

Default RequireOrderFlow needs current Volume>0, Bid+Ask>0, delta sign aligned with trade direction, and at least one flag (configurable 1–4): directional delta >=10% volume; delta improvement from a previous bar with bid/ask data >=15% current volume; volume >=1.2 prior 20-bar mean (all baseline volumes positive); upper/lower extreme 20% zone aggression >=10% candle volume and >=65% ask/bid dominance in trend direction. PriceOnly bypasses this requirement explicitly for OHLC exports. No automatic missing-data fallback.

## Signal availability and reference plan

Candidate derives solely from closed candles. At the following candle's start, use only successor Index/Time/Open. Require consecutive index and exactly one minute after confirmation; reject session boundaries and malformed successor open/time. Never consult successor H/L/C or its full-row OHLC validity for plan availability; a malformed successor resets tracking only when that candle later closes. Entry is successor open rounded in the adverse direction to the tick grid plus one entry-slippage tick. Recompute stop distance and planned dollars here. Reject entry on the wrong side of the stop, risk above $150, or primary net reward/risk below 1.5. Targets are tick-rounded outward to cover desired net dollars plus configured costs. Plot the arrow on this entry candle, and fixed entry/stop/target guides. This is a locked signal based on the preceding closed candle, not a forming-candle condition.

Reference trade ends when a later closed candle (including the now-closed entry candle) touches primary target or stop. Both touched in one candle: stop first. Stop gaps use adverse open beyond stop plus stop slippage; target fills use target price. After 15 closed entry-and-following candles, mark a reference timeout exit at close. Session boundaries/gaps/invalid bars end tracking as unresolved, not synthetic profits. Guides do not submit or manage real orders.

## Data and reset policy

First closed snapshot wins on duplicate index. Reset across nonconsecutive index, nonincreasing timestamp, any cadence other than exactly one minute, invalid OHLC/negative volumes, and ATAS session boundaries. Reject nonzero seconds in minute starts. Final forming candle is never used for engine calculations or exit evaluation; only its fixed open/time can make a prior confirmation actionable. Full NQ symbol guard excludes MNQ/ES/Gold from these dollar presets. CSV instrument identity is unavailable: any NQ valuation of uploaded OHLC is an explicit fixture assumption. Preserve literal dates, including the future December label; do not silently reinterpret.

## Interfaces (namespace NqContinuation)

- enum TradeDirection { Long, Short }; enum DataMode { PriceOnly, RequireOrderFlow }; [Flags] enum FlowEvidence { None=0, RelativeVolume=1, AlignedDelta=2, DeltaImprovement=4, ExtremeAggression=8 }.
- MarketBar readonly record struct: (int Index, DateTime Time, decimal Open, High, Low, Close, decimal Volume=0, Bid=0, Ask=0, Delta=0, MaxDelta=0, MinDelta=0, LowerBid=0, LowerAsk=0, UpperBid=0, UpperAsk=0).
- BarOpen readonly record struct: (int Index, DateTime Time, decimal Open).
- ContinuationOptions sealed record init props: TickSize=.25m; PointValue=20m; ProfitTargetDollars=250m; MaxLossDollars=150m; RoundTripCostsDollars=0m; EntrySlippageTicks=1; StopSlippageTicks=1; MinimumRewardRisk=1.5m; FastPeriod=9; SlowPeriod=21; HigherFastPeriod=9; HigherSlowPeriod=21; AtrPeriod=14; VolumePeriod=20; MaxPullbackBars=5; ImpulseLookbackBars=8; ImpulseDistanceAtr=.5m; TouchBandAtr=.2m; MinTrendSeparationAtr=.1m; MaxConfirmationRangeAtr=1.5m; MinCloseLocation=.65m; CooldownBars=3; MaxTradeBars=15; DataMode=RequireOrderFlow; MinimumEvidence=1; DeltaFraction=.10m; RelativeVolume=1.2m; DeltaImprovementFraction=.15m; ExtremeDominance=.65m; ExtremeVolumeFraction=.10m.
- ContinuationCandidate sealed record init props: TradeDirection Direction; int SetupIndex, ConfirmationIndex; DateTime ConfirmationTime; decimal ConfirmationClose, StopPrice, TriggerPrice; int EvidenceScore; FlowEvidence Evidence.
- ContinuationEngine(ContinuationOptions); ContinuationCandidate? Process(MarketBar); Reset(); decimal? FastEma, SlowEma, HigherFastEma, HigherSlowEma (getters). Constructor validates all options, including .25 tick and 20 point value for this NQ-specific build.
- TradePlan sealed record init props: ContinuationCandidate Candidate; int EntryIndex; DateTime AvailableAt; decimal EntryPrice, StopPrice, TargetLower, TargetPrimary, TargetUpper, PlannedRiskDollars, TargetNetDollars, RewardRisk.
- TradePlanner.Create(ContinuationCandidate, BarOpen, ContinuationOptions) static returns TradePlan?; validates options and successor/risk/target inputs. Lower/upper target desired dollars are fixed 200/300; primary comes from options.
- enum ExitReason { Stop, Target, Timeout }; TradeExit sealed record init props: ExitReason Reason; int ExitIndex; DateTime ExitTime; decimal ExitPrice, NetDollars; bool Ambiguous.
- TradeTracker(TradePlan, ContinuationOptions); TradeExit? Process(MarketBar). Track only consecutive valid one-minute bars at/after EntryIndex; duplicate exact indices emit no repeated exit; first terminal exit wins. Driver drops tracker unresolved at data/session boundaries.

## Verification

Behavioral tests must cover fully closed HTF aggregation, warmup and gaps; symmetric impulse-pullback-break triggers; range/flow filters; correct NQ dollars/tick rounding/costs; next-open gaps and risk cap; same-bar stop/target ambiguity and stop gaps; no live OHLC signal dependence; history/recalc alerts and guide clearing; non-NQ symbol guard; state resets and duplicate/prefix invariance. Replay one-minute CSV with unchanged defaults in PriceOnly and explicitly assumed NQ valuation; do not optimize to this short fixture. Report target/stop/timeout/unresolved counts and execution assumptions honestly, including unfavorable results. Five-minute CSV is not valid input to the one-minute engine. Ship source and a working GitHub ZIP link; disclose no real ATAS DLL compilation/runtime test.
