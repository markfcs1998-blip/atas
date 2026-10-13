using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ATAS.Indicators;

namespace NqContinuation;

[DisplayName("NQ Continuation (Confirmed)")]
[Category("Custom")]
public sealed class NqContinuationIndicator : Indicator
{
    private readonly ValueDataSeries _long = Arrow("Long", "Confirmed long", VisualMode.UpArrow, Colors.LimeGreen);
    private readonly ValueDataSeries _short = Arrow("Short", "Confirmed short", VisualMode.DownArrow, Colors.OrangeRed);
    private readonly ValueDataSeries _entry = Guide("Entry", "Reference entry", Colors.Gray);
    private readonly ValueDataSeries _stop = Guide("Stop", "Structural stop", Colors.OrangeRed);
    private readonly ValueDataSeries _primary = Guide("Target", "Selected profit target", Colors.LimeGreen);
    private readonly ValueDataSeries _lower = Guide("Lower", "$200 alternative exit", Colors.Gold);
    private readonly ValueDataSeries _upper = Guide("Upper", "$300 alternative exit", Colors.Gold);
    private readonly ValueDataSeries _evidence = Hidden("Evidence", "Flow evidence count");
    private readonly ValueDataSeries _risk = Hidden("Risk", "Planned risk USD (one NQ)");
    private readonly ValueDataSeries _fast = Guide("FastEMA", "One-minute fast EMA", Colors.DodgerBlue);
    private readonly ValueDataSeries _slow = Guide("SlowEMA", "One-minute slow EMA", Colors.MediumPurple);
    private readonly ValueDataSeries _flow = Hidden("Flow", "Bid/ask data available (0/1)");
    private ContinuationOptions _options = new();
    private ContinuationEngine? _engine;
    private TradePlan? _active;
    private TradeTracker? _tracker;
    private DateTime? _lastClosedTime;
    private int _lastProcessed = -1;
    private bool _historyComplete;

    public NqContinuationIndicator() : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = true;
        DataSeries[0] = _long;
        foreach (var series in new[] { _short, _entry, _stop, _primary, _lower, _upper, _evidence, _risk, _fast, _slow, _flow })
            DataSeries.Add(series);
    }

    [Display(Name = "Desired profit USD (one NQ)", GroupName = "1. Trade plan", Order = 10)]
    [Range(200, 300)]
    public decimal ProfitTargetDollars
    {
        get => _options.ProfitTargetDollars;
        set { if (value is >= 200m and <= 300m) Change(_options with { ProfitTargetDollars = value }); }
    }

    [Display(Name = "Maximum planned loss USD", GroupName = "1. Trade plan", Order = 20)]
    [Range(5, 150)]
    public decimal MaxLossDollars
    {
        get => _options.MaxLossDollars;
        set { if (value is >= 5m and <= 150m) Change(_options with { MaxLossDollars = value }); }
    }

    [Display(Name = "Round-trip fees/costs USD", GroupName = "1. Trade plan", Order = 30)]
    [Range(0, 100)]
    public decimal RoundTripCostsDollars
    {
        get => _options.RoundTripCostsDollars;
        set { if (value is >= 0m and <= 100m) Change(_options with { RoundTripCostsDollars = value }); }
    }

    [Display(Name = "Entry slippage allowance (ticks)", GroupName = "1. Trade plan", Order = 40)]
    [Range(0, 10)]
    public int EntrySlippageTicks
    {
        get => _options.EntrySlippageTicks;
        set { if (value is >= 0 and <= 10) Change(_options with { EntrySlippageTicks = value }); }
    }

    [Display(Name = "Stop slippage allowance (ticks)", GroupName = "1. Trade plan", Order = 50)]
    [Range(0, 10)]
    public int StopSlippageTicks
    {
        get => _options.StopSlippageTicks;
        set { if (value is >= 0 and <= 10) Change(_options with { StopSlippageTicks = value }); }
    }

    [Display(Name = "Minimum selected reward / planned risk", GroupName = "1. Trade plan", Order = 60)]
    [Range(0.5, 5.0)]
    public decimal MinimumRewardRisk
    {
        get => _options.MinimumRewardRisk;
        set { if (value is >= .5m and <= 5m) Change(_options with { MinimumRewardRisk = value }); }
    }

    [Display(Name = "Reference trade timeout (closed candles)", GroupName = "1. Trade plan", Order = 70)]
    [Range(1, 120)]
    public int MaxTradeBars
    {
        get => _options.MaxTradeBars;
        set { if (value is >= 1 and <= 120) Change(_options with { MaxTradeBars = value }); }
    }

    [Display(Name = "Fast EMA candles (one minute)", GroupName = "2. Trend", Order = 10)]
    [Range(1, 199)]
    public int FastPeriod
    {
        get => _options.FastPeriod;
        set { if (value >= 1 && value < _options.SlowPeriod) Change(_options with { FastPeriod = value }); }
    }

    [Display(Name = "Slow EMA candles (one minute)", GroupName = "2. Trend", Order = 20)]
    [Range(2, 200)]
    public int SlowPeriod
    {
        get => _options.SlowPeriod;
        set { if (value > _options.FastPeriod && value <= 200) Change(_options with { SlowPeriod = value }); }
    }

    [Display(Name = "Fast EMA candles (completed five minutes)", GroupName = "2. Trend", Order = 30)]
    [Range(1, 199)]
    public int HigherFastPeriod
    {
        get => _options.HigherFastPeriod;
        set { if (value >= 1 && value < _options.HigherSlowPeriod) Change(_options with { HigherFastPeriod = value }); }
    }

    [Display(Name = "Slow EMA candles (completed five minutes)", GroupName = "2. Trend", Order = 40)]
    [Range(2, 200)]
    public int HigherSlowPeriod
    {
        get => _options.HigherSlowPeriod;
        set { if (value > _options.HigherFastPeriod && value <= 200) Change(_options with { HigherSlowPeriod = value }); }
    }

    [Display(Name = "ATR candles (prior-only arithmetic mean)", GroupName = "3. Pullback", Order = 10)]
    [Range(1, 200)]
    public int AtrPeriod
    {
        get => _options.AtrPeriod;
        set { if (value is >= 1 and <= 200) Change(_options with { AtrPeriod = value }); }
    }

    [Display(Name = "Pullback expiry (candles)", GroupName = "3. Pullback", Order = 20)]
    [Range(1, 20)]
    public int MaxPullbackBars
    {
        get => _options.MaxPullbackBars;
        set { if (value is >= 1 and <= 20) Change(_options with { MaxPullbackBars = value }); }
    }

    [Display(Name = "Minimum preceding impulse / ATR", GroupName = "3. Pullback", Order = 30)]
    [Range(0.1, 5.0)]
    public decimal ImpulseDistanceAtr
    {
        get => _options.ImpulseDistanceAtr;
        set { if (value is >= .1m and <= 5m) Change(_options with { ImpulseDistanceAtr = value }); }
    }

    [Display(Name = "Touch band around fast EMA / ATR", GroupName = "3. Pullback", Order = 40)]
    [Range(0.0, 1.0)]
    public decimal TouchBandAtr
    {
        get => _options.TouchBandAtr;
        set { if (value is >= 0m and <= 1m) Change(_options with { TouchBandAtr = value }); }
    }

    [Display(Name = "Maximum confirmation range / ATR", GroupName = "3. Pullback", Order = 50)]
    [Range(0.1, 5.0)]
    public decimal MaxConfirmationRangeAtr
    {
        get => _options.MaxConfirmationRangeAtr;
        set { if (value is >= .1m and <= 5m) Change(_options with { MaxConfirmationRangeAtr = value }); }
    }

    [Display(Name = "Intervening candles between candidates", GroupName = "3. Pullback", Order = 60)]
    [Range(0, 100)]
    public int CooldownBars
    {
        get => _options.CooldownBars;
        set { if (value is >= 0 and <= 100) Change(_options with { CooldownBars = value }); }
    }

    [Display(Name = "Data mode", GroupName = "4. Order flow", Order = 10)]
    public DataMode DataMode
    {
        get => _options.DataMode;
        set { if (Enum.IsDefined(value)) Change(_options with { DataMode = value }); }
    }

    [Display(Name = "Minimum flow evidence flags", GroupName = "4. Order flow", Order = 20)]
    [Range(1, 4)]
    public int MinimumEvidence
    {
        get => _options.MinimumEvidence;
        set { if (value is >= 1 and <= 4) Change(_options with { MinimumEvidence = value }); }
    }

    [Display(Name = "Live entry alerts", GroupName = "5. Alerts", Order = 10)]
    public bool UseAlerts { get; set; }

    [Display(Name = "Alert sound", GroupName = "5. Alerts", Order = 20)]
    public string AlertSound { get; set; } = "alert1";

    protected override void OnRecalculate()
    {
        base.OnRecalculate();
        ResetCalculation();
        _historyComplete = false;
    }

    protected override void OnFinishRecalculate()
    {
        base.OnFinishRecalculate();
        _historyComplete = true;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var instrument = InstrumentInfo;
        if (instrument is null || instrument.TickSize != .25m || !IsFullNq(instrument.Instrument))
        {
            if (_engine is not null) ResetCalculation();
            return;
        }
        if (bar < 1 || bar >= CurrentBar) return;
        var canAlert = _historyComplete && _engine is not null && bar == CurrentBar - 1 && _lastProcessed == bar - 2;
        _engine ??= new ContinuationEngine(_options);
        var target = bar - 1;

        while (_lastProcessed < target)
        {
            var index = _lastProcessed + 1;
            var candle = GetCandle(index);
            var input = ToBar(index, candle);
            var valid = IsValid(input);
            if (IsNewSession(index) || !valid || (_lastClosedTime is { } previous && input.Time - previous != TimeSpan.FromMinutes(1)))
                ResetReference();

            if (!valid)
            {
                _lastProcessed = index;
                _lastClosedTime = null;
                ClearGuides(index + 1);
                continue;
            }

            if (_active is not null && _tracker is not null)
            {
                DrawGuides(index);
                if (_tracker.Process(input) is not null) { _active = null; _tracker = null; }
            }
            var candidate = _engine.Process(input);
            _fast[index] = _engine.FastEma ?? 0;
            _slow[index] = _engine.SlowEma ?? 0;
            _flow[index] = input.Volume > 0 && input.Bid + input.Ask > 0 ? 1 : 0;
            _lastClosedTime = input.Time;
            _lastProcessed = index;

            // Only the fixed opening information of the successor is read, never forming H/L/C.
            var successor = GetCandle(index + 1);
            if (IsNewSession(index + 1) || successor.Time - input.Time != TimeSpan.FromMinutes(1))
            {
                ResetReference();
                ClearGuides(index + 1);
                continue;
            }
            if (candidate is not null && _active is null)
            {
                var plan = TradePlanner.Create(candidate, new BarOpen(index + 1, successor.Time, successor.Open), _options);
                if (plan is not null)
                {
                    _active = plan;
                    _tracker = new TradeTracker(plan, _options);
                    var entryIndex = plan.EntryIndex;
                    if (candidate.Direction == TradeDirection.Long) _long[entryIndex] = plan.EntryPrice - 2 * .25m;
                    else _short[entryIndex] = plan.EntryPrice + 2 * .25m;
                    _evidence[entryIndex] = candidate.EvidenceScore;
                    _risk[entryIndex] = plan.PlannedRiskDollars;
                    if (canAlert && index == target && UseAlerts)
                    {
                        AddAlert(AlertSound, instrument.Instrument,
                            $"NQ continuation {candidate.Direction}: reference entry {plan.EntryPrice}, stop {plan.StopPrice}, "
                            + $"target {plan.TargetPrimary}, planned risk ${plan.PlannedRiskDollars:0.00}, "
                            + $"target ${plan.TargetNetDollars:0.00}; {candidate.EvidenceScore}/4 flow flags.",
                            candidate.Direction == TradeDirection.Long ? Colors.LimeGreen : Colors.OrangeRed, Colors.Black);
                    }
                }
            }
            if (_active is not null) DrawGuides(index + 1);
            else ClearGuides(index + 1);
        }
    }

    private void DrawGuides(int index)
    {
        if (_active is not { } plan) return;
        _entry[index] = plan.EntryPrice;
        _stop[index] = plan.StopPrice;
        _primary[index] = plan.TargetPrimary;
        _lower[index] = plan.TargetLower;
        _upper[index] = plan.TargetUpper;
    }

    private void ClearGuides(int index) => _entry[index] = _stop[index] = _primary[index] = _lower[index] = _upper[index] = 0;

    private void ResetReference()
    {
        _engine?.Reset();
        _active = null; _tracker = null; _lastClosedTime = null;
    }

    private void ResetCalculation()
    {
        _engine = null; _active = null; _tracker = null; _lastClosedTime = null; _lastProcessed = -1;
        foreach (var series in new[] { _long, _short, _entry, _stop, _primary, _lower, _upper, _evidence, _risk, _fast, _slow, _flow })
            series.Clear();
    }

    private void Change(ContinuationOptions options)
    {
        if (_options == options) return;
        _options = options;
        RecalculateValues();
    }

    private static bool IsFullNq(string instrument) => Regex.IsMatch(instrument ?? string.Empty,
        @"(^|[^A-Z])NQ(?=$|[^A-Z]|[FGHJKMNQUVXZ]\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsValid(MarketBar b) => b.Time != default && MarketValidation.IsValid(b);

    private static MarketBar ToBar(int index, IndicatorCandle candle)
    {
        decimal lowerBid = 0, lowerAsk = 0, upperBid = 0, upperAsk = 0;
        var range = candle.High - candle.Low;
        if (range > 0)
        {
            var levels = candle.GetAllPriceLevels();
            if (levels is not null)
                foreach (var level in levels)
                {
                    if (level is null || level.Price < candle.Low || level.Price > candle.High) continue;
                    if (level.Price <= candle.Low + .2m * range) { lowerBid += level.Bid; lowerAsk += level.Ask; }
                    if (level.Price >= candle.High - .2m * range) { upperBid += level.Bid; upperAsk += level.Ask; }
                }
        }
        return new MarketBar(index, candle.Time, candle.Open, candle.High, candle.Low, candle.Close,
            candle.Volume, candle.Bid, candle.Ask, candle.Delta, candle.MaxDelta, candle.MinDelta, lowerBid, lowerAsk, upperBid, upperAsk);
    }

    private static ValueDataSeries Arrow(string id, string name, VisualMode mode, Color color) => new(id, name)
    {
        VisualType = mode, Color = color, Width = 3, ShowZeroValue = false, ShowCurrentValue = false, ScaleIt = false
    };
    private static ValueDataSeries Guide(string id, string name, Color color) => new(id, name)
    {
        VisualType = VisualMode.Hash, Color = color, Width = 2, ShowZeroValue = false, ShowCurrentValue = false, ScaleIt = false
    };
    private static ValueDataSeries Hidden(string id, string name) => new(id, name)
    {
        VisualType = VisualMode.Hide, ShowZeroValue = false, ShowCurrentValue = false, ScaleIt = false
    };
}
