// Unit-test doubles only. Not an ATAS assembly and never deployed to the platform.
namespace System.Windows.Media
{
    public readonly record struct Color(string Name);
    public static class Colors
    {
        public static Color LimeGreen => new("LimeGreen");
        public static Color OrangeRed => new("OrangeRed");
        public static Color White => new("White");
        public static Color Black => new("Black");
        public static Color Gold => new("Gold");
        public static Color Gray => new("Gray");
        public static Color DodgerBlue => new("DodgerBlue");
        public static Color MediumPurple => new("MediumPurple");
    }
}

namespace ATAS.Indicators
{
    public enum VisualMode { UpArrow, DownArrow, Hide, Hash, Line }
    public sealed class ValueDataSeries(string id, string name)
    {
        private readonly Dictionary<int, decimal> _values = [];
        public string Id { get; } = id;
        public string Name { get; } = name;
        public VisualMode VisualType { get; set; }
        public System.Windows.Media.Color Color { get; set; }
        public int Width { get; set; }
        public bool ShowZeroValue { get; set; }
        public bool ShowCurrentValue { get; set; }
        public bool IsHidden { get; set; }
        public bool ScaleIt { get; set; }
        public decimal this[int bar]
        {
            get => _values.GetValueOrDefault(bar);
            set => _values[bar] = value;
        }
        public void Clear() => _values.Clear();
    }
    public sealed record TestInstrumentInfo(decimal TickSize, string InstrumentName)
    {
        public string Instrument => InstrumentName;
    }
    public sealed class PriceVolumeInfo
    {
        public decimal Price { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
    }
    public sealed class IndicatorCandle
    {
        public DateTime Time { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Delta { get; set; }
        public decimal MaxDelta { get; set; }
        public decimal MinDelta { get; set; }
        public List<PriceVolumeInfo> Levels { get; } = [];
        public IEnumerable<PriceVolumeInfo> GetAllPriceLevels() => Levels;
    }
    public abstract class Indicator
    {
        protected Indicator(bool useCandles = false) { DataSeries.Add(new("Default", "Default")); }
        public List<ValueDataSeries> DataSeries { get; } = [];
        public int CurrentBar => TestCandles.Count;
        public TestInstrumentInfo? InstrumentInfo { get; set; } = new(.25m, "Fixture");
        public string Panel { get; set; } = "";
        public bool DenyToChangePanel { get; set; }
        public List<IndicatorCandle> TestCandles { get; } = [];
        public HashSet<int> TestSessions { get; } = [0];
        public List<string> TestAlerts { get; } = [];
        public int TestRecalculateRequests { get; private set; }
        protected IndicatorCandle GetCandle(int bar) => TestCandles[bar];
        protected bool IsNewSession(int bar) => TestSessions.Contains(bar);
        protected void RecalculateValues() => TestRecalculateRequests++;
        protected void AddAlert(string sound, string instrument, string message,
            System.Windows.Media.Color background, System.Windows.Media.Color foreground) => TestAlerts.Add(message);
        protected virtual void OnRecalculate() { }
        protected virtual void OnFinishRecalculate() { }
        protected abstract void OnCalculate(int bar, decimal value);
        public void TestCalculate(int bar) => OnCalculate(bar, TestCandles[bar].Close);
        public void TestLoadHistory()
        {
            OnRecalculate();
            for (var i = 0; i < CurrentBar; i++) TestCalculate(i);
            OnFinishRecalculate();
        }
    }
    public static class IndicatorDataProvider
    {
        public const string CandlesPanel = "Candles";
    }
}
