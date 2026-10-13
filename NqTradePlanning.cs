using System;

namespace NqContinuation
{
    public sealed record TradePlan
    {
        public ContinuationCandidate Candidate { get; init; } = null!;
        public int EntryIndex { get; init; }
        public DateTime AvailableAt { get; init; }
        public decimal EntryPrice { get; init; }
        public decimal StopPrice { get; init; }
        public decimal TargetLower { get; init; }
        public decimal TargetPrimary { get; init; }
        public decimal TargetUpper { get; init; }
        public decimal PlannedRiskDollars { get; init; }
        public decimal TargetNetDollars { get; init; }
        public decimal RewardRisk { get; init; }
    }

    public static class TradePlanner
    {
        public static TradePlan? Create(ContinuationCandidate candidate, BarOpen successor, ContinuationOptions options)
        {
            OptionsValidation.Validate(options);
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            bool longSide = candidate.Direction == TradeDirection.Long;
            if ((candidate.Direction != TradeDirection.Long && candidate.Direction != TradeDirection.Short) ||
                candidate.SetupIndex < 0 || candidate.ConfirmationIndex <= candidate.SetupIndex || successor.Index < 0 ||
                !MarketValidation.MinuteStart(candidate.ConfirmationTime) || !MarketValidation.MinuteStart(successor.Time) ||
                !MarketValidation.IsSuccessor(candidate.ConfirmationIndex, candidate.ConfirmationTime, successor.Index, successor.Time) ||
                candidate.ConfirmationClose <= 0 || candidate.TriggerPrice <= 0 || candidate.StopPrice <= 0 || successor.Open <= 0 ||
                !TickMath.OnGrid(candidate.StopPrice, options.TickSize) ||
                (longSide ? candidate.ConfirmationClose <= candidate.StopPrice : candidate.ConfirmationClose >= candidate.StopPrice)) return null;
            decimal sign = longSide ? 1m : -1m;
            decimal entry = (longSide ? TickMath.Ceiling(successor.Open, options.TickSize) : TickMath.Floor(successor.Open, options.TickSize))
                + sign * options.EntrySlippageTicks * options.TickSize;
            if (entry <= 0 || (entry - candidate.StopPrice) * sign <= 0) return null;
            decimal risk = Math.Abs(entry - candidate.StopPrice) * options.PointValue +
                options.StopSlippageTicks * options.TickSize * options.PointValue + options.RoundTripCostsDollars;
            if (risk > options.MaxLossDollars || risk <= 0) return null;
            decimal target = Target(entry, sign, options.ProfitTargetDollars, options);
            decimal net = Math.Abs(target - entry) * options.PointValue - options.RoundTripCostsDollars;
            decimal ratio = net / risk;
            if (ratio < options.MinimumRewardRisk) return null;
            return new TradePlan
            {
                Candidate = candidate, EntryIndex = successor.Index, AvailableAt = successor.Time,
                EntryPrice = entry, StopPrice = candidate.StopPrice,
                TargetLower = Target(entry, sign, 200m, options), TargetPrimary = target,
                TargetUpper = Target(entry, sign, 300m, options), PlannedRiskDollars = risk, TargetNetDollars = net, RewardRisk = ratio
            };
        }

        private static decimal Target(decimal entry, decimal sign, decimal dollars, ContinuationOptions options) =>
            entry + sign * decimal.Ceiling((dollars + options.RoundTripCostsDollars) / (options.TickSize * options.PointValue)) * options.TickSize;
    }

    public enum ExitReason { Stop, Target, Timeout }
    public sealed record TradeExit
    {
        public ExitReason Reason { get; init; }
        public int ExitIndex { get; init; }
        public DateTime ExitTime { get; init; }
        public decimal ExitPrice { get; init; }
        public decimal NetDollars { get; init; }
        public bool Ambiguous { get; init; }
    }

    public sealed class TradeTracker
    {
        private readonly TradePlan _plan;
        private readonly ContinuationOptions _options;
        private MarketBar? _previous;
        private readonly System.Collections.Generic.HashSet<int> _seenIndices = new();
        private bool _terminal;
        private int _count;

        public TradeTracker(TradePlan plan, ContinuationOptions options)
        {
            OptionsValidation.Validate(options);
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (plan.Candidate == null || plan.EntryIndex < 0 || !MarketValidation.MinuteStart(plan.AvailableAt) ||
                plan.EntryPrice <= 0 || plan.StopPrice <= 0 || plan.TargetPrimary <= 0 ||
                !TickMath.OnGrid(plan.EntryPrice, options.TickSize) || !TickMath.OnGrid(plan.StopPrice, options.TickSize) ||
                !TickMath.OnGrid(plan.TargetPrimary, options.TickSize) ||
                (plan.Candidate.Direction != TradeDirection.Long && plan.Candidate.Direction != TradeDirection.Short) ||
                (plan.Candidate.Direction == TradeDirection.Long ? plan.StopPrice >= plan.EntryPrice || plan.TargetPrimary <= plan.EntryPrice
                    : plan.StopPrice <= plan.EntryPrice || plan.TargetPrimary >= plan.EntryPrice))
                throw new ArgumentException("Invalid reference trade plan.", nameof(plan));
            _plan = plan; _options = options;
        }

        /// <summary>Call once each bar is closed, including the entry candle. A gap or invalid bar ends the reference unresolved.</summary>
        public TradeExit? Process(MarketBar bar)
        {
            if (_terminal || _seenIndices.Contains(bar.Index)) return null;
            if (bar.Index < _plan.EntryIndex) return null;
            bool validCadence = _previous.HasValue ? MarketValidation.IsSuccessor(_previous.Value.Index, _previous.Value.Time, bar.Index, bar.Time)
                : bar.Index == _plan.EntryIndex && bar.Time == _plan.AvailableAt;
            if (!MarketValidation.IsValid(bar) || !validCadence) { _terminal = true; return null; }
            _seenIndices.Add(bar.Index); _previous = bar; _count++;
            bool longSide = _plan.Candidate.Direction == TradeDirection.Long;
            bool stop = longSide ? bar.Low <= _plan.StopPrice : bar.High >= _plan.StopPrice;
            bool target = longSide ? bar.High >= _plan.TargetPrimary : bar.Low <= _plan.TargetPrimary;
            ExitReason? reason = null;
            decimal price = 0;
            if (stop)
            {
                reason = ExitReason.Stop;
                price = longSide ? Math.Min(bar.Open, _plan.StopPrice) - _options.StopSlippageTicks * _options.TickSize
                    : Math.Max(bar.Open, _plan.StopPrice) + _options.StopSlippageTicks * _options.TickSize;
            }
            else if (target) { reason = ExitReason.Target; price = _plan.TargetPrimary; }
            else if (_count >= _options.MaxTradeBars) { reason = ExitReason.Timeout; price = bar.Close; }
            if (!reason.HasValue) return null;
            _terminal = true;
            decimal sign = longSide ? 1m : -1m;
            return new TradeExit
            {
                Reason = reason.Value, ExitIndex = bar.Index, ExitTime = bar.Time,
                ExitPrice = price, NetDollars = (price - _plan.EntryPrice) * sign * _options.PointValue - _options.RoundTripCostsDollars,
                Ambiguous = stop && target
            };
        }
    }
}
