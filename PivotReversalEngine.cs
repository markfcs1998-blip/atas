using System;
using System.Collections.Generic;

namespace PivotReversal;

public enum SignalDirection { Long, Short }
public enum OrderFlowMode { PriceOnly, RequireEvidence }
[Flags]
public enum EvidenceFlags { None = 0, RelativeVolume = 1, OpposingDelta = 2, DeltaRecovery = 4, ExtremeAggression = 8 }

public readonly record struct BarData(int Index, DateTime Time, decimal Open, decimal High, decimal Low,
    decimal Close, decimal Volume = 0, decimal Bid = 0, decimal Ask = 0, decimal Delta = 0,
    decimal MaxDelta = 0, decimal MinDelta = 0, decimal LowerBid = 0, decimal LowerAsk = 0,
    decimal UpperBid = 0, decimal UpperAsk = 0);

public sealed record EngineOptions
{
    public decimal TickSize { get; init; }
    public int SwingLeft { get; init; } = 2;
    public int SwingRight { get; init; } = 2;
    public int AtrPeriod { get; init; } = 14;
    public int VolumePeriod { get; init; } = 20;
    public int ConfirmationBars { get; init; } = 3;
    public int CooldownBars { get; init; } = 3;
    public int MaxGapMinutes { get; init; } = 20;
    public int MinimumEvidence { get; init; } = 1;
    public decimal MinWickFraction { get; init; } = .25m;
    public decimal CloseLocation { get; init; } = .60m;
    public decimal MinRangeAtr { get; init; } = .3m;
    public decimal MaxRangeAtr { get; init; } = 3m;
    public decimal RelativeVolume { get; init; } = 1.5m;
    public decimal DeltaFraction { get; init; } = .10m;
    public decimal DeltaRecoveryFraction { get; init; } = .20m;
    public decimal ExtremeVolumeFraction { get; init; } = .10m;
    public decimal ExtremeDominance { get; init; } = .65m;
    public OrderFlowMode OrderFlowMode { get; init; } = OrderFlowMode.RequireEvidence;
}

public sealed record ReversalSignal
{
    public SignalDirection Direction { get; init; }
    public int PivotIndex { get; init; }
    public int SetupIndex { get; init; }
    public int ConfirmationIndex { get; init; }
    public decimal ReferenceSwing { get; init; }
    public decimal StopPrice { get; init; }
    public decimal ConfirmationClose { get; init; }
    public int EvidenceScore { get; init; }
    public EvidenceFlags Evidence { get; init; }
}

public sealed class PivotReversalEngine
{
    private readonly EngineOptions _options;
    private readonly int _historyLimit;
    private readonly List<HistoryBar> _history = new();
    private Swing? _low, _high;
    private Setup? _long, _short;
    private int? _lastSignal;

    public PivotReversalEngine(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        _options = options;
        var needed = Math.Max((long)Math.Max(options.AtrPeriod, options.VolumePeriod) + 1,
            (long)options.SwingLeft + options.SwingRight + 1);
        if (needed > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "Combined history periods exceed the supported index range.");
        _historyLimit = (int)needed;
    }

    /// <summary>Consumes one closed candle. The caller publishes a returned signal on the next candle.</summary>
    public ReversalSignal? Process(BarData bar)
    {
        // The first closed snapshot wins; repeated platform callbacks cannot revise emitted history.
        if (_history.Count > 0 && bar.Index == _history[^1].Bar.Index) return null;
        if (!IsValid(bar)) { Reset(); return null; }
        if (_history.Count > 0)
        {
            var previous = _history[^1].Bar;
            if ((long)bar.Index != (long)previous.Index + 1 || bar.Time <= previous.Time ||
                bar.Time - previous.Time > TimeSpan.FromMinutes(_options.MaxGapMinutes))
                Reset();
        }
        try { return ProcessValid(bar); }
        catch (OverflowException) { Reset(); return null; }
    }

    public void Reset()
    {
        _history.Clear(); _low = null; _high = null; _long = null; _short = null; _lastSignal = null;
    }

    private ReversalSignal? ProcessValid(BarData bar)
    {
        // Follow-through consumes only a setup from an earlier closed candle.
        var longSignal = Confirm(ref _long, bar);
        var shortSignal = Confirm(ref _short, bar);
        var signal = longSignal ?? shortSignal;
        if (signal is not null) _lastSignal = bar.Index;

        if (PriorAtr() is { } atr && atr > 0 && bar.High > bar.Low)
        {
            var range = bar.High - bar.Low;
            if (range >= atr * _options.MinRangeAtr && range <= atr * _options.MaxRangeAtr)
            {
                if (_low is { } low && bar.Low <= low.Price - _options.TickSize &&
                    bar.Close >= low.Price + _options.TickSize &&
                    Math.Min(bar.Open, bar.Close) - bar.Low >= range * _options.MinWickFraction &&
                    bar.Close - bar.Low >= range * _options.CloseLocation)
                    Arm(ref _long, SignalDirection.Long, low, bar);
                if (_high is { } high && bar.High >= high.Price + _options.TickSize &&
                    bar.Close <= high.Price - _options.TickSize &&
                    bar.High - Math.Max(bar.Open, bar.Close) >= range * _options.MinWickFraction &&
                    bar.High - bar.Close >= range * _options.CloseLocation)
                    Arm(ref _short, SignalDirection.Short, high, bar);
            }
        }

        // Confirming a pivot happens last. Its right-hand candle never sees it as an older pivot.
        var trueRange = bar.High - bar.Low;
        if (_history.Count > 0)
        {
            var previousClose = _history[^1].Bar.Close;
            trueRange = Math.Max(trueRange, Math.Max(Math.Abs(bar.High - previousClose), Math.Abs(bar.Low - previousClose)));
        }
        _history.Add(new HistoryBar(bar, trueRange));
        if (_history.Count > _historyLimit) _history.RemoveAt(0);
        UpdateSwings();
        return signal;
    }

    private ReversalSignal? Confirm(ref Setup? pending, BarData bar)
    {
        if (pending is not { } setup) return null;
        var longSide = setup.Direction == SignalDirection.Long;
        if ((long)bar.Index - setup.Bar.Index > _options.ConfirmationBars ||
            (longSide ? bar.Low <= setup.Bar.Low : bar.High >= setup.Bar.High))
        { pending = null; return null; }
        var confirmed = longSide
            ? bar.Close >= setup.Bar.High + _options.TickSize && bar.Close > bar.Open
            : bar.Close <= setup.Bar.Low - _options.TickSize && bar.Close < bar.Open;
        if (!confirmed) return null;
        pending = null;
        if (_lastSignal is { } last && (long)bar.Index - last <= _options.CooldownBars) return null;
        return new ReversalSignal
        {
            Direction = setup.Direction, PivotIndex = setup.Swing.Index, SetupIndex = setup.Bar.Index,
            ConfirmationIndex = bar.Index, ReferenceSwing = setup.Swing.Price,
            StopPrice = longSide ? setup.Bar.Low - _options.TickSize : setup.Bar.High + _options.TickSize,
            ConfirmationClose = bar.Close, Evidence = setup.Evidence, EvidenceScore = Score(setup.Evidence)
        };
    }

    private void Arm(ref Setup? pending, SignalDirection direction, Swing swing, BarData bar)
    {
        var evidence = Evidence(bar, direction);
        if (_options.OrderFlowMode == OrderFlowMode.RequireEvidence &&
            (bar.Volume <= 0 || bar.Bid + bar.Ask <= 0 || Score(evidence) < _options.MinimumEvidence)) return;
        pending = new Setup(direction, swing, bar, evidence);
    }

    private EvidenceFlags Evidence(BarData bar, SignalDirection direction)
    {
        if (bar.Volume <= 0 || bar.Bid + bar.Ask <= 0) return EvidenceFlags.None;
        var flags = EvidenceFlags.None;
        if (_history.Count >= _options.VolumePeriod)
        {
            decimal sum = 0;
            for (var i = _history.Count - _options.VolumePeriod; i < _history.Count; i++) sum += _history[i].Bar.Volume;
            var mean = sum / _options.VolumePeriod;
            if (mean > 0 && bar.Volume >= mean * _options.RelativeVolume) flags |= EvidenceFlags.RelativeVolume;
        }
        var longSide = direction == SignalDirection.Long;
        if (longSide ? bar.Close > bar.Open && -bar.Delta >= bar.Volume * _options.DeltaFraction
                     : bar.Close < bar.Open && bar.Delta >= bar.Volume * _options.DeltaFraction)
            flags |= EvidenceFlags.OpposingDelta;

        // An absent running extreme defaults to zero. Require internally possible extrema so a
        // positive final delta with a missing MaxDelta cannot invent long recovery (and vice versa).
        var runningExtremaAvailable = bar.MinDelta <= Math.Min(0, bar.Delta) &&
            bar.MaxDelta >= Math.Max(0, bar.Delta);
        if (runningExtremaAvailable && (longSide ? bar.Delta - bar.MinDelta : bar.MaxDelta - bar.Delta)
            >= bar.Volume * _options.DeltaRecoveryFraction)
            flags |= EvidenceFlags.DeltaRecovery;

        var opposing = longSide ? bar.LowerBid : bar.UpperAsk;
        var other = longSide ? bar.LowerAsk : bar.UpperBid;
        var zone = opposing + other;
        if (zone >= bar.Volume * _options.ExtremeVolumeFraction && zone > 0 &&
            opposing / zone >= _options.ExtremeDominance)
            flags |= EvidenceFlags.ExtremeAggression;
        return flags;
    }

    private decimal? PriorAtr()
    {
        if (_history.Count < _options.AtrPeriod) return null;
        decimal sum = 0;
        for (var i = _history.Count - _options.AtrPeriod; i < _history.Count; i++) sum += _history[i].TrueRange;
        return sum / _options.AtrPeriod;
    }

    private void UpdateSwings()
    {
        var needed = (long)_options.SwingLeft + _options.SwingRight + 1;
        if (_history.Count < needed) return;
        var at = _history.Count - 1 - _options.SwingRight;
        var candidate = _history[at].Bar;
        var isLow = true; var isHigh = true;
        for (var i = at - _options.SwingLeft; i <= at + _options.SwingRight; i++)
        {
            if (i == at) continue;
            if (candidate.Low >= _history[i].Bar.Low) isLow = false;
            if (candidate.High <= _history[i].Bar.High) isHigh = false;
        }
        if (isLow) _low = new Swing(candidate.Index, candidate.Low);
        if (isHigh) _high = new Swing(candidate.Index, candidate.High);
    }

    private static bool IsValid(BarData b) => b.Index >= 0 && b.Time != default && b.High >= b.Low &&
        b.High >= Math.Max(b.Open, b.Close) && b.Low <= Math.Min(b.Open, b.Close) &&
        b.Volume >= 0 && b.Bid >= 0 && b.Ask >= 0 && b.LowerBid >= 0 && b.LowerAsk >= 0 && b.UpperBid >= 0 && b.UpperAsk >= 0;

    private static int Score(EvidenceFlags flags)
    {
        var value = (int)flags; var count = 0;
        while (value != 0) { count += value & 1; value >>= 1; }
        return count;
    }

    private static void Validate(EngineOptions o)
    {
        static void Positive(decimal value, string name) { if (value <= 0) throw new ArgumentOutOfRangeException(name); }
        static void PositiveInt(int value, string name) { if (value < 1) throw new ArgumentOutOfRangeException(name); }
        static void Fraction(decimal value, string name, bool allowZero = false)
        { if (value > 1 || (allowZero ? value < 0 : value <= 0)) throw new ArgumentOutOfRangeException(name); }
        Positive(o.TickSize, nameof(o.TickSize)); PositiveInt(o.SwingLeft, nameof(o.SwingLeft)); PositiveInt(o.SwingRight, nameof(o.SwingRight));
        PositiveInt(o.AtrPeriod, nameof(o.AtrPeriod)); PositiveInt(o.VolumePeriod, nameof(o.VolumePeriod));
        PositiveInt(o.ConfirmationBars, nameof(o.ConfirmationBars)); PositiveInt(o.MaxGapMinutes, nameof(o.MaxGapMinutes));
        if (o.CooldownBars < 0) throw new ArgumentOutOfRangeException(nameof(o.CooldownBars));
        if (o.MinimumEvidence is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(o.MinimumEvidence));
        Fraction(o.MinWickFraction, nameof(o.MinWickFraction), true); Fraction(o.CloseLocation, nameof(o.CloseLocation));
        Positive(o.MinRangeAtr, nameof(o.MinRangeAtr));
        if (o.MaxRangeAtr < o.MinRangeAtr) throw new ArgumentOutOfRangeException(nameof(o.MaxRangeAtr));
        Positive(o.RelativeVolume, nameof(o.RelativeVolume)); Fraction(o.DeltaFraction, nameof(o.DeltaFraction));
        Fraction(o.DeltaRecoveryFraction, nameof(o.DeltaRecoveryFraction)); Fraction(o.ExtremeVolumeFraction, nameof(o.ExtremeVolumeFraction));
        if (o.ExtremeDominance < .5m || o.ExtremeDominance > 1) throw new ArgumentOutOfRangeException(nameof(o.ExtremeDominance));
        if (!Enum.IsDefined(o.OrderFlowMode)) throw new ArgumentOutOfRangeException(nameof(o.OrderFlowMode));
    }

    private readonly record struct HistoryBar(BarData Bar, decimal TrueRange);
    private readonly record struct Swing(int Index, decimal Price);
    private sealed record Setup(SignalDirection Direction, Swing Swing, BarData Bar, EvidenceFlags Evidence);
}
