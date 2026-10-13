using System;
using System.Collections.Generic;

namespace NqContinuation
{
    public enum TradeDirection { Long, Short }
    public enum DataMode { PriceOnly, RequireOrderFlow }
    [Flags]
    public enum FlowEvidence { None = 0, RelativeVolume = 1, AlignedDelta = 2, DeltaImprovement = 4, ExtremeAggression = 8 }

    public readonly record struct MarketBar(int Index, DateTime Time, decimal Open, decimal High, decimal Low, decimal Close,
        decimal Volume = 0, decimal Bid = 0, decimal Ask = 0, decimal Delta = 0, decimal MaxDelta = 0, decimal MinDelta = 0,
        decimal LowerBid = 0, decimal LowerAsk = 0, decimal UpperBid = 0, decimal UpperAsk = 0);
    public readonly record struct BarOpen(int Index, DateTime Time, decimal Open);

    public sealed record ContinuationOptions
    {
        public decimal TickSize { get; init; } = .25m;
        public decimal PointValue { get; init; } = 20m;
        public decimal ProfitTargetDollars { get; init; } = 250m;
        public decimal MaxLossDollars { get; init; } = 150m;
        public decimal RoundTripCostsDollars { get; init; } = 0m;
        public int EntrySlippageTicks { get; init; } = 1;
        public int StopSlippageTicks { get; init; } = 1;
        public decimal MinimumRewardRisk { get; init; } = 1.5m;
        public int FastPeriod { get; init; } = 9;
        public int SlowPeriod { get; init; } = 21;
        public int HigherFastPeriod { get; init; } = 9;
        public int HigherSlowPeriod { get; init; } = 21;
        public int AtrPeriod { get; init; } = 14;
        public int VolumePeriod { get; init; } = 20;
        public int MaxPullbackBars { get; init; } = 5;
        public int ImpulseLookbackBars { get; init; } = 8;
        public decimal ImpulseDistanceAtr { get; init; } = .5m;
        public decimal TouchBandAtr { get; init; } = .2m;
        public decimal MinTrendSeparationAtr { get; init; } = .1m;
        public decimal MaxConfirmationRangeAtr { get; init; } = 1.5m;
        public decimal MinCloseLocation { get; init; } = .65m;
        public int CooldownBars { get; init; } = 3;
        public int MaxTradeBars { get; init; } = 15;
        public DataMode DataMode { get; init; } = DataMode.RequireOrderFlow;
        public int MinimumEvidence { get; init; } = 1;
        public decimal DeltaFraction { get; init; } = .10m;
        public decimal RelativeVolume { get; init; } = 1.2m;
        public decimal DeltaImprovementFraction { get; init; } = .15m;
        public decimal ExtremeDominance { get; init; } = .65m;
        public decimal ExtremeVolumeFraction { get; init; } = .10m;
    }

    public sealed record ContinuationCandidate
    {
        public TradeDirection Direction { get; init; }
        public int SetupIndex { get; init; }
        public int ConfirmationIndex { get; init; }
        public DateTime ConfirmationTime { get; init; }
        public decimal ConfirmationClose { get; init; }
        public decimal StopPrice { get; init; }
        public decimal TriggerPrice { get; init; }
        public int EvidenceScore { get; init; }
        public FlowEvidence Evidence { get; init; }
    }

    public sealed class ContinuationEngine
    {
        private readonly ContinuationOptions _options;
        private readonly Queue<decimal> _trueRanges = new();
        private readonly Queue<decimal> _volumes = new();
        private readonly List<(int Index, TradeDirection Direction)> _impulses = new();
        private readonly HashSet<int> _seenIndices = new();
        private MarketBar? _previous;
        private int _count, _higherCount, _higherMinutes;
        private DateTime? _higherStart;
        private decimal? _previousHigherSlow;
        private Pullback? _pullback;
        private int? _lastCandidateIndex;
        private int? _consumedLongImpulse, _consumedShortImpulse;

        private sealed class Pullback
        {
            public TradeDirection Direction;
            public int Index;
            public decimal High, Low;
        }

        public ContinuationEngine(ContinuationOptions options)
        {
            OptionsValidation.Validate(options);
            _options = options;
        }

        public decimal? FastEma { get; private set; }
        public decimal? SlowEma { get; private set; }
        public decimal? HigherFastEma { get; private set; }
        public decimal? HigherSlowEma { get; private set; }

        /// <summary>Consumes a final closed one-minute snapshot. The caller excludes forming bars and resets on sessions.</summary>
        public ContinuationCandidate? Process(MarketBar bar)
        {
            // A changed duplicate cannot revise a previously accepted closed candle.
            if (_seenIndices.Contains(bar.Index)) return null;
            if (!MarketValidation.IsValid(bar)) { Reset(); return null; }
            if (_previous is MarketBar prior && !MarketValidation.IsSuccessor(prior.Index, prior.Time, bar.Index, bar.Time))
                Reset();

            var previous = _previous;
            decimal? atr = _trueRanges.Count == _options.AtrPeriod ? Mean(_trueRanges) : null;
            FastEma = Ema(FastEma, bar.Close, _options.FastPeriod);
            SlowEma = Ema(SlowEma, bar.Close, _options.SlowPeriod);
            _count++;
            UpdateHigher(bar);

            ContinuationCandidate? candidate = null;
            var bias = Bias(atr);
            if (_pullback != null)
            {
                var setup = _pullback;
                if (bar.Index - setup.Index > _options.MaxPullbackBars || bias != setup.Direction ||
                    (setup.Direction == TradeDirection.Long ? bar.Close < SlowEma : bar.Close > SlowEma))
                    _pullback = null;
                else
                {
                    bool longSide = setup.Direction == TradeDirection.Long;
                    decimal trigger = longSide ? TickMath.Ceiling(setup.High, _options.TickSize) + _options.TickSize
                        : TickMath.Floor(setup.Low, _options.TickSize) - _options.TickSize;
                    decimal range = bar.High - bar.Low;
                    bool priceBreak = longSide ? bar.Close > bar.Open && bar.Close >= trigger &&
                        bar.Close >= FastEma + _options.TickSize && (bar.Close - bar.Low) >= range * _options.MinCloseLocation
                        : bar.Close < bar.Open && bar.Close <= trigger && bar.Close <= FastEma - _options.TickSize &&
                        (bar.High - bar.Close) >= range * _options.MinCloseLocation;
                    var evidence = EvaluateFlow(bar, previous, setup.Direction);
                    int score = EvidenceCount(evidence);
                    bool flowPass = _options.DataMode == DataMode.PriceOnly ||
                        (bar.Volume > 0 && bar.Bid + bar.Ask > 0 && (longSide ? bar.Delta > 0 : bar.Delta < 0) && score >= _options.MinimumEvidence);
                    if (priceBreak && range > 0 && range <= atr * _options.MaxConfirmationRangeAtr && flowPass &&
                        (!_lastCandidateIndex.HasValue || bar.Index - _lastCandidateIndex.Value > _options.CooldownBars))
                    {
                        decimal low = Math.Min(setup.Low, bar.Low), high = Math.Max(setup.High, bar.High);
                        candidate = new ContinuationCandidate
                        {
                            Direction = setup.Direction, SetupIndex = setup.Index, ConfirmationIndex = bar.Index,
                            ConfirmationTime = bar.Time, ConfirmationClose = bar.Close, TriggerPrice = trigger,
                            StopPrice = longSide ? TickMath.Floor(low, _options.TickSize) - _options.TickSize
                                : TickMath.Ceiling(high, _options.TickSize) + _options.TickSize,
                            Evidence = evidence, EvidenceScore = score
                        };
                        _lastCandidateIndex = bar.Index;
                        _pullback = null;
                    }
                    else
                    {
                        setup.High = Math.Max(setup.High, bar.High);
                        setup.Low = Math.Min(setup.Low, bar.Low);
                    }
                }
            }

            if (_pullback == null && candidate == null && bias.HasValue && previous.HasValue && atr > 0)
            {
                bool longSide = bias.Value == TradeDirection.Long;
                bool pullback = longSide ? bar.Close < bar.Open && bar.Close < previous.Value.Close &&
                    bar.Low <= FastEma + _options.TouchBandAtr * atr && bar.Close >= SlowEma
                    : bar.Close > bar.Open && bar.Close > previous.Value.Close &&
                    bar.High >= FastEma - _options.TouchBandAtr * atr && bar.Close <= SlowEma;
                int? consumed = longSide ? _consumedLongImpulse : _consumedShortImpulse;
                int? impulse = null;
                for (int i = _impulses.Count - 1; i >= 0; i--)
                {
                    var item = _impulses[i];
                    if (item.Direction == bias && bar.Index - item.Index <= _options.ImpulseLookbackBars &&
                        (!consumed.HasValue || item.Index > consumed.Value)) { impulse = item.Index; break; }
                }
                if (pullback && impulse.HasValue)
                {
                    _pullback = new Pullback { Direction = bias.Value, Index = bar.Index, High = bar.High, Low = bar.Low };
                    if (longSide) _consumedLongImpulse = impulse; else _consumedShortImpulse = impulse;
                }
            }

            // Impulses use their own EMA and prior ATR snapshot, never a later setup's indicators.
            if (atr > 0)
            {
                if (bar.Close > bar.Open && bar.Close - FastEma >= atr * _options.ImpulseDistanceAtr)
                    _impulses.Add((bar.Index, TradeDirection.Long));
                else if (bar.Close < bar.Open && FastEma - bar.Close >= atr * _options.ImpulseDistanceAtr)
                    _impulses.Add((bar.Index, TradeDirection.Short));
            }
            _impulses.RemoveAll(item => bar.Index - item.Index >= _options.ImpulseLookbackBars);
            decimal trueRange = bar.High - bar.Low;
            if (previous.HasValue) trueRange = Math.Max(trueRange,
                Math.Max(Math.Abs(bar.High - previous.Value.Close), Math.Abs(bar.Low - previous.Value.Close)));
            Enqueue(_trueRanges, trueRange, _options.AtrPeriod);
            Enqueue(_volumes, bar.Volume, _options.VolumePeriod);
            _previous = bar;
            _seenIndices.Add(bar.Index);
            return candidate;
        }

        public void Reset()
        {
            FastEma = SlowEma = HigherFastEma = HigherSlowEma = _previousHigherSlow = null;
            _previous = null; _higherStart = null; _latestHigherClose = null; _pullback = null;
            _lastCandidateIndex = _consumedLongImpulse = _consumedShortImpulse = null;
            _count = _higherCount = _higherMinutes = 0;
            _trueRanges.Clear(); _volumes.Clear(); _impulses.Clear(); _seenIndices.Clear();
        }

        private TradeDirection? Bias(decimal? atr)
        {
            if (_count < _options.SlowPeriod || _higherCount < _options.HigherSlowPeriod || !atr.HasValue || atr <= 0 ||
                !_previousHigherSlow.HasValue || !_latestHigherClose.HasValue) return null;
            decimal separation = _options.MinTrendSeparationAtr * atr!.Value;
            if (HigherFastEma > HigherSlowEma && _latestHigherClose > HigherSlowEma && HigherSlowEma > _previousHigherSlow &&
                FastEma > SlowEma && FastEma - SlowEma >= separation) return TradeDirection.Long;
            if (HigherFastEma < HigherSlowEma && _latestHigherClose < HigherSlowEma && HigherSlowEma < _previousHigherSlow &&
                FastEma < SlowEma && SlowEma - FastEma >= separation) return TradeDirection.Short;
            return null;
        }

        private decimal? _latestHigherClose;
        private void UpdateHigher(MarketBar bar)
        {
            if (bar.Time.Minute % 5 == 0) { _higherStart = bar.Time; _higherMinutes = 1; }
            else if (_higherStart.HasValue && bar.Time == _higherStart.Value.AddMinutes(_higherMinutes)) _higherMinutes++;
            else { _higherStart = null; _higherMinutes = 0; }
            if (_higherMinutes != 5) return;
            _previousHigherSlow = HigherSlowEma;
            HigherFastEma = Ema(HigherFastEma, bar.Close, _options.HigherFastPeriod);
            HigherSlowEma = Ema(HigherSlowEma, bar.Close, _options.HigherSlowPeriod);
            _latestHigherClose = bar.Close;
            _higherCount++;
            _higherStart = null; _higherMinutes = 0;
        }

        private FlowEvidence EvaluateFlow(MarketBar bar, MarketBar? previous, TradeDirection direction)
        {
            if (bar.Volume <= 0 || bar.Bid + bar.Ask <= 0) return FlowEvidence.None;
            bool longSide = direction == TradeDirection.Long;
            decimal sign = longSide ? 1m : -1m;
            var result = FlowEvidence.None;
            if (bar.Delta * sign >= bar.Volume * _options.DeltaFraction) result |= FlowEvidence.AlignedDelta;
            if (previous.HasValue && previous.Value.Bid + previous.Value.Ask > 0 &&
                (bar.Delta - previous.Value.Delta) * sign >= bar.Volume * _options.DeltaImprovementFraction)
                result |= FlowEvidence.DeltaImprovement;
            if (_volumes.Count == _options.VolumePeriod)
            {
                bool valid = true; foreach (decimal volume in _volumes) if (volume <= 0) valid = false;
                if (valid && bar.Volume >= Mean(_volumes) * _options.RelativeVolume) result |= FlowEvidence.RelativeVolume;
            }
            decimal aggressive = longSide ? bar.UpperAsk : bar.LowerBid;
            decimal passive = longSide ? bar.UpperBid : bar.LowerAsk;
            if (aggressive >= bar.Volume * _options.ExtremeVolumeFraction && aggressive + passive > 0 &&
                aggressive / (aggressive + passive) >= _options.ExtremeDominance) result |= FlowEvidence.ExtremeAggression;
            return result;
        }

        private static int EvidenceCount(FlowEvidence evidence)
        {
            int value = (int)evidence, count = 0; while (value > 0) { count += value & 1; value >>= 1; } return count;
        }
        private static decimal Ema(decimal? previous, decimal close, int period) => previous.HasValue
            ? previous.Value + (close - previous.Value) * 2m / (period + 1m) : close;
        private static decimal Mean(Queue<decimal> values) { decimal sum = 0; foreach (decimal value in values) sum += value; return sum / values.Count; }
        private static void Enqueue(Queue<decimal> values, decimal value, int maximum) { values.Enqueue(value); if (values.Count > maximum) values.Dequeue(); }
    }

    internal static class TickMath
    {
        public static decimal Floor(decimal value, decimal tick) => decimal.Floor(value / tick) * tick;
        public static decimal Ceiling(decimal value, decimal tick) => decimal.Ceiling(value / tick) * tick;
        public static bool OnGrid(decimal value, decimal tick) => value % tick == 0;
    }

    internal static class MarketValidation
    {
        public static bool MinuteStart(DateTime time) => time.Ticks % TimeSpan.TicksPerMinute == 0;
        public static bool IsSuccessor(int index, DateTime time, int nextIndex, DateTime nextTime) =>
            (long)nextIndex == (long)index + 1 && nextTime.Ticks - time.Ticks == TimeSpan.TicksPerMinute;
        public static bool IsValid(MarketBar bar) => bar.Index >= 0 && MinuteStart(bar.Time) && bar.Low > 0 &&
            bar.High >= bar.Low && bar.Open >= bar.Low && bar.Open <= bar.High && bar.Close >= bar.Low && bar.Close <= bar.High &&
            bar.Volume >= 0 && bar.Bid >= 0 && bar.Ask >= 0 && bar.LowerBid >= 0 && bar.LowerAsk >= 0 && bar.UpperBid >= 0 && bar.UpperAsk >= 0;
    }

    internal static class OptionsValidation
    {
        public static void Validate(ContinuationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.TickSize != .25m || options.PointValue != 20m || options.ProfitTargetDollars < 200m || options.ProfitTargetDollars > 300m ||
                options.MaxLossDollars <= 0 || options.RoundTripCostsDollars < 0 || options.EntrySlippageTicks < 0 || options.StopSlippageTicks < 0 ||
                options.MinimumRewardRisk <= 0 || options.FastPeriod < 1 || options.SlowPeriod <= options.FastPeriod || options.HigherFastPeriod < 1 ||
                options.HigherSlowPeriod <= options.HigherFastPeriod || options.AtrPeriod < 1 || options.VolumePeriod < 1 || options.MaxPullbackBars < 1 ||
                options.ImpulseLookbackBars < 1 || options.ImpulseDistanceAtr <= 0 || options.TouchBandAtr < 0 || options.MinTrendSeparationAtr < 0 ||
                options.MaxConfirmationRangeAtr <= 0 || options.MinCloseLocation < .5m || options.MinCloseLocation > 1m || options.CooldownBars < 0 ||
                options.MaxTradeBars < 1 || (options.DataMode != DataMode.PriceOnly && options.DataMode != DataMode.RequireOrderFlow) ||
                options.MinimumEvidence < 1 || options.MinimumEvidence > 4 || options.DeltaFraction <= 0 || options.DeltaFraction > 1 ||
                options.RelativeVolume <= 0 || options.DeltaImprovementFraction <= 0 || options.DeltaImprovementFraction > 2 ||
                options.ExtremeDominance < .5m || options.ExtremeDominance > 1 || options.ExtremeVolumeFraction <= 0 || options.ExtremeVolumeFraction > 1)
                throw new ArgumentException("Invalid NQ continuation settings.", nameof(options));
        }
    }
}
