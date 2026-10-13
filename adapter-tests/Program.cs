using ATAS.Indicators;
using PivotReversal;

var tests = new (string Name, Action Run)[]
{
    ("forming confirmation stays invisible until the next bar", ClosedBarTiming),
    ("history and recalculation never send alerts", HistoricalAlerts),
    ("a session boundary cancels a pending setup", SessionReset),
    ("changing a signal setting requests recalculation", SettingChange),
    ("missing instrument tick size does not fabricate signals", MissingTick),
    ("new-session successor blocks late confirmation delivery", SuccessorSessionBoundary),
    ("gap successor blocks late confirmation delivery", SuccessorGapBoundary)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {test.Name}: {e.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} adapter unit tests passed (test doubles; no ATAS runtime).");
return failed == 0 ? 0 : 1;

static PivotReversalIndicator Fixture(int count)
{
    var indicator = new PivotReversalIndicator { UseAlerts = true };
    for (var i = 0; i < count; i++) indicator.TestCandles.Add(Candle(i));
    return indicator;
}

static IndicatorCandle Candle(int i)
{
    var c = new IndicatorCandle
    {
        Time = new DateTime(2026, 1, 1).AddMinutes(i), Open = 100, High = 103,
        Low = 99, Close = 101, Volume = 1000, Bid = 600, Ask = 400,
        Delta = -200, MinDelta = -600, MaxDelta = 100
    };
    if (i == 15) c.Low = 98;
    if (i == 16) { c.High = 102; c.Low = 95; }
    if (i == 17) c.Low = 97;
    if (i == 18) c.Low = 98;
    if (i == 19) { c.High = 102; c.Low = 94; }
    if (i == 20) { c.Open = 101; c.High = 104; c.Low = 96; c.Close = 103; }
    return c;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void ClosedBarTiming()
{
    var p = Fixture(21);
    p.TestLoadHistory();
    Check(p.DataSeries[0][20] == 0, "forming bar got a long entry");
    p.TestCalculate(20);
    Check(p.DataSeries[0][20] == 0 && p.TestAlerts.Count == 0, "intrabar tick produced signal");
    p.TestCandles.Add(Candle(21));
    p.TestCalculate(21);
    Check(p.DataSeries[0][20] > 0, "closed confirmation failed to plot");
    Check(p.TestAlerts.Count == 1, "expected exactly one live alert");
    Check(p.DataSeries[3][20] == 93.75m, "stop reference does not use actual tick size");
    p.TestCalculate(21);
    Check(p.TestAlerts.Count == 1, "duplicate live tick repeated alert");
    Check(p.DataSeries[0][21] == 0, "arrow plotted on forming successor");
}

static void HistoricalAlerts()
{
    var p = Fixture(22);
    p.TestLoadHistory();
    Check(p.DataSeries[0][20] > 0, "history has no confirmation arrow");
    Check(p.TestAlerts.Count == 0, "history raised an alert");
    var original = p.DataSeries[0][20];
    p.TestLoadHistory();
    Check(p.DataSeries[0][20] == original && p.TestAlerts.Count == 0, "recalculation changed the signal or alerted");
    p.TestCandles[20].Close = 101;
    p.TestLoadHistory();
    Check(p.DataSeries[0][20] == 0, "recalculation kept a stale arrow");
}

static void SessionReset()
{
    var p = Fixture(22);
    p.TestSessions.Add(20);
    p.TestLoadHistory();
    Check(p.DataSeries[0][20] == 0, "new session confirmed prior-session setup");
}

static void SettingChange()
{
    var p = Fixture(1);
    var before = p.TestRecalculateRequests;
    p.SwingLeft = 3;
    Check(p.TestRecalculateRequests == before + 1, "setting did not request history recalculation");
}

static void MissingTick()
{
    var p = Fixture(22);
    p.InstrumentInfo = null;
    p.TestLoadHistory();
    Check(p.DataSeries[0][20] == 0 && p.TestAlerts.Count == 0, "missing tick size emitted signal");
}

static void SuccessorSessionBoundary()
{
    var p = Fixture(21);
    p.TestLoadHistory();
    p.TestCandles.Add(Candle(21));
    p.TestSessions.Add(21);
    p.TestCalculate(21);
    Check(p.DataSeries[0][20] == 0 && p.TestAlerts.Count == 0, "new session delivered an old-session confirmation");
}

static void SuccessorGapBoundary()
{
    var p = Fixture(21);
    p.TestLoadHistory();
    var next = Candle(21);
    next.Time = next.Time.AddDays(1);
    p.TestCandles.Add(next);
    p.TestCalculate(21);
    Check(p.DataSeries[0][20] == 0 && p.TestAlerts.Count == 0, "gap delivered an old-session confirmation");
}
