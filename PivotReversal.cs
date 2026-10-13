using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using ATAS.Indicators;

namespace PivotReversal;

[DisplayName("Pivot Reversal (Confirmed)")]
[Category("Custom")]
public sealed class PivotReversalIndicator : Indicator
{
    private readonly ValueDataSeries _long = new("Long", "Confirmed long")
    {
        VisualType = VisualMode.UpArrow, Color = Colors.LimeGreen,
        Width = 3, ShowZeroValue = false, ShowCurrentValue = false
    };
    private readonly ValueDataSeries _short = new("Short", "Confirmed short")
    {
        VisualType = VisualMode.DownArrow, Color = Colors.OrangeRed,
        Width = 3, ShowZeroValue = false, ShowCurrentValue = false
    };
    private readonly ValueDataSeries _evidence = HiddenSeries("Evidence", "Order-flow evidence (0-4)");
    private readonly ValueDataSeries _stop = HiddenSeries("Stop", "Invalidation reference price");
    private readonly ValueDataSeries _flowAvailable = HiddenSeries("FlowAvailable", "Bid/ask data available (0/1)");
    private EngineOptions _options = new();
    private PivotReversalEngine? _engine;
    private decimal _tickSize;
    private int _lastProcessed = -1;
    private bool _historyComplete;
    private int _arrowOffsetTicks = 2;

    public PivotReversalIndicator() : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = true;
        DataSeries[0] = _long;
        DataSeries.Add(_short);
        DataSeries.Add(_evidence);
        DataSeries.Add(_stop);
        DataSeries.Add(_flowAvailable);
    }

    [Display(Name = "Swing left candles", GroupName = "1. Price structure", Order = 10)]
    [Range(1, 20)]
    public int SwingLeft
    {
        get => _options.SwingLeft;
        set { if (value is >= 1 and <= 20) ChangeOptions(_options with { SwingLeft = value }); }
    }

    [Display(Name = "Swing right candles", GroupName = "1. Price structure", Order = 20)]
    [Range(1, 20)]
    public int SwingRight
    {
        get => _options.SwingRight;
        set { if (value is >= 1 and <= 20) ChangeOptions(_options with { SwingRight = value }); }
    }

    [Display(Name = "Confirmation window (candles)", GroupName = "1. Price structure", Order = 30)]
    [Range(1, 20)]
    public int ConfirmationBars
    {
        get => _options.ConfirmationBars;
        set { if (value is >= 1 and <= 20) ChangeOptions(_options with { ConfirmationBars = value }); }
    }

    [Display(Name = "Intervening candles after a signal", GroupName = "1. Price structure", Order = 40)]
    [Range(0, 100)]
    public int CooldownBars
    {
        get => _options.CooldownBars;
        set { if (value is >= 0 and <= 100) ChangeOptions(_options with { CooldownBars = value }); }
    }

    [Display(Name = "Minimum rejection wick fraction", GroupName = "1. Price structure", Order = 50)]
    [Range(0.0, 1.0)]
    public decimal MinWickFraction
    {
        get => _options.MinWickFraction;
        set { if (value is >= 0m and <= 1m) ChangeOptions(_options with { MinWickFraction = value }); }
    }

    [Display(Name = "Minimum close position (long, mirrored short)", GroupName = "1. Price structure", Order = 60)]
    [Range(0.5, 1.0)]
    public decimal CloseLocation
    {
        get => _options.CloseLocation;
        set { if (value is >= .5m and <= 1m) ChangeOptions(_options with { CloseLocation = value }); }
    }

    [Display(Name = "ATR candles (arithmetic mean)", GroupName = "2. Volatility", Order = 10)]
    [Range(1, 200)]
    public int AtrPeriod
    {
        get => _options.AtrPeriod;
        set { if (value is >= 1 and <= 200) ChangeOptions(_options with { AtrPeriod = value }); }
    }

    [Display(Name = "Minimum setup range / prior ATR", GroupName = "2. Volatility", Order = 20)]
    [Range(0.01, 20.0)]
    public decimal MinRangeAtr
    {
        get => _options.MinRangeAtr;
        set { if (value > 0 && value <= _options.MaxRangeAtr) ChangeOptions(_options with { MinRangeAtr = value }); }
    }

    [Display(Name = "Maximum setup range / prior ATR", GroupName = "2. Volatility", Order = 30)]
    [Range(0.01, 20.0)]
    public decimal MaxRangeAtr
    {
        get => _options.MaxRangeAtr;
        set { if (value >= _options.MinRangeAtr && value <= 20) ChangeOptions(_options with { MaxRangeAtr = value }); }
    }

    [Display(Name = "Data mode", GroupName = "3. Order flow", Order = 10)]
    public OrderFlowMode OrderFlowMode
    {
        get => _options.OrderFlowMode;
        set { if (Enum.IsDefined(value)) ChangeOptions(_options with { OrderFlowMode = value }); }
    }

    [Display(Name = "Minimum evidence flags (1-4)", GroupName = "3. Order flow", Order = 20)]
    [Range(1, 4)]
    public int MinimumEvidence
    {
        get => _options.MinimumEvidence;
        set { if (value is >= 1 and <= 4) ChangeOptions(_options with { MinimumEvidence = value }); }
    }

    [Display(Name = "Volume average candles", GroupName = "3. Order flow", Order = 30)]
    [Range(1, 200)]
    public int VolumePeriod
    {
        get => _options.VolumePeriod;
        set { if (value is >= 1 and <= 200) ChangeOptions(_options with { VolumePeriod = value }); }
    }

    [Display(Name = "Relative volume threshold", GroupName = "3. Order flow", Order = 40)]
    [Range(1.0, 20.0)]
    public decimal RelativeVolume
    {
        get => _options.RelativeVolume;
        set { if (value is >= 1m and <= 20m) ChangeOptions(_options with { RelativeVolume = value }); }
    }

    [Display(Name = "Opposing delta / volume threshold", GroupName = "3. Order flow", Order = 50)]
    [Range(0.01, 1.0)]
    public decimal DeltaFraction
    {
        get => _options.DeltaFraction;
        set { if (value is > 0m and <= 1m) ChangeOptions(_options with { DeltaFraction = value }); }
    }

    [Display(Name = "Running delta recovery / volume", GroupName = "3. Order flow", Order = 60)]
    [Range(0.01, 1.0)]
    public decimal DeltaRecoveryFraction
    {
        get => _options.DeltaRecoveryFraction;
        set { if (value is > 0m and <= 1m) ChangeOptions(_options with { DeltaRecoveryFraction = value }); }
    }

    [Display(Name = "Extreme-zone volume / candle volume", GroupName = "3. Order flow", Order = 70)]
    [Range(0.01, 1.0)]
    public decimal ExtremeVolumeFraction
    {
        get => _options.ExtremeVolumeFraction;
        set { if (value is > 0m and <= 1m) ChangeOptions(_options with { ExtremeVolumeFraction = value }); }
    }

    [Display(Name = "Extreme-zone bid/ask dominance", GroupName = "3. Order flow", Order = 80)]
    [Range(0.5, 1.0)]
    public decimal ExtremeDominance
    {
        get => _options.ExtremeDominance;
        set { if (value is >= .5m and <= 1m) ChangeOptions(_options with { ExtremeDominance = value }); }
    }

    [Display(Name = "Reset after gap longer than (minutes)", GroupName = "4. Sessions", Order = 10)]
    [Range(1, 1440)]
    public int MaxGapMinutes
    {
        get => _options.MaxGapMinutes;
        set { if (value is >= 1 and <= 1440) ChangeOptions(_options with { MaxGapMinutes = value }); }
    }

    [Display(Name = "Arrow offset (ticks)", GroupName = "5. Display and alerts", Order = 10)]
    [Range(0, 100)]
    public int ArrowOffsetTicks
    {
        get => _arrowOffsetTicks;
        set
        {
            if (value is < 0 or > 100 || value == _arrowOffsetTicks) return;
            _arrowOffsetTicks = value;
            RecalculateValues();
        }
    }

    [Display(Name = "Live alerts", GroupName = "5. Display and alerts", Order = 20)]
    public bool UseAlerts { get; set; }

    [Display(Name = "Alert sound", GroupName = "5. Display and alerts", Order = 30)]
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
        // Historical and live paths both process only the callback's predecessor.
        if (bar < 1 || bar >= CurrentBar || InstrumentInfo is null || InstrumentInfo.TickSize <= 0) return;
        var tickSize = InstrumentInfo.TickSize;
        var canAlert = _historyComplete && _engine is not null && _tickSize == tickSize
            && bar == CurrentBar - 1 && _lastProcessed == bar - 2;

        if (_engine is null || _tickSize != tickSize)
        {
            ResetCalculation();
            _tickSize = tickSize;
            _engine = new PivotReversalEngine(_options with { TickSize = tickSize });
        }

        var target = bar - 1;
        while (_lastProcessed < target)
        {
            var index = _lastProcessed + 1;
            if (IsNewSession(index)) _engine.Reset();
            var candle = GetCandle(index);
            var input = ToBar(index, candle);
            _long[index] = _short[index] = _evidence[index] = _stop[index] = 0;
            _flowAvailable[index] = input.Volume > 0 && input.Bid + input.Ask > 0 ? 1 : 0;
            // The successor's start time/session is already known at this callback.
            // A boundary cannot deliver a prior-session entry hours or days late.
            var successorTime = GetCandle(index + 1).Time;
            if (IsNewSession(index + 1) || successorTime <= candle.Time
                || successorTime - candle.Time > TimeSpan.FromMinutes(MaxGapMinutes))
            {
                _engine.Reset();
                _lastProcessed = index;
                continue;
            }
            var signal = _engine.Process(input);
            _lastProcessed = index;
            if (signal is null) continue;

            if (signal.Direction == SignalDirection.Long)
                _long[index] = candle.Low - ArrowOffsetTicks * tickSize;
            else
                _short[index] = candle.High + ArrowOffsetTicks * tickSize;
            _evidence[index] = signal.EvidenceScore;
            _stop[index] = signal.StopPrice;

            if (canAlert && index == target && UseAlerts)
            {
                var message = $"Pivot Reversal {signal.Direction}: closed-bar confirmation {candle.Time:yyyy-MM-dd HH:mm}; "
                    + $"invalidation {signal.StopPrice}; evidence {signal.EvidenceScore}/4 ({signal.Evidence}).";
                AddAlert(AlertSound, InstrumentInfo.Instrument, message,
                    signal.Direction == SignalDirection.Long ? Colors.LimeGreen : Colors.OrangeRed, Colors.Black);
            }
        }
    }

    private static BarData ToBar(int index, IndicatorCandle candle)
    {
        decimal lowerBid = 0, lowerAsk = 0, upperBid = 0, upperAsk = 0;
        var range = candle.High - candle.Low;
        if (range > 0)
        {
            var lowerBoundary = candle.Low + .20m * range;
            var upperBoundary = candle.High - .20m * range;
            var levels = candle.GetAllPriceLevels();
            if (levels is not null)
            {
                foreach (var level in levels)
                {
                    if (level is null || level.Price < candle.Low || level.Price > candle.High) continue;
                    if (level.Price <= lowerBoundary) { lowerBid += level.Bid; lowerAsk += level.Ask; }
                    if (level.Price >= upperBoundary) { upperBid += level.Bid; upperAsk += level.Ask; }
                }
            }
        }
        return new BarData(index, candle.Time, candle.Open, candle.High, candle.Low, candle.Close,
            candle.Volume, candle.Bid, candle.Ask, candle.Delta, candle.MaxDelta, candle.MinDelta,
            lowerBid, lowerAsk, upperBid, upperAsk);
    }

    private void ChangeOptions(EngineOptions options)
    {
        if (_options == options) return;
        _options = options;
        RecalculateValues();
    }

    private void ResetCalculation()
    {
        _engine = null;
        _tickSize = 0;
        _lastProcessed = -1;
        _long.Clear(); _short.Clear(); _evidence.Clear(); _stop.Clear(); _flowAvailable.Clear();
    }

    private static ValueDataSeries HiddenSeries(string id, string name) => new(id, name)
    {
        VisualType = VisualMode.Hide, ShowZeroValue = false, ShowCurrentValue = false
    };
}
