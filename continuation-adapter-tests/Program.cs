using ATAS.Indicators;
using NqContinuation;

var tests = new (string Name, Action Run)[]
{
    ("closed confirmation waits for successor and sends one alert", Availability),
    ("entry, stop and dollar targets agree with one NQ contract", DollarGuides),
    ("forming HLC changes do not alter a confirmed signal", FormingIndependence),
    ("processing entry candle retains its arrow and ends touched guides", ExitGuides),
    ("history and recalculation suppress alerts and clear stale signals", History),
    ("successor gap rejects late entry", Gap),
    ("successor session boundary rejects prior-session entry", Session),
    ("next open beyond risk cap rejects entry", RiskGap),
    ("MNQ cannot use full NQ dollar presets", MicroGuard),
    ("settings change requests recalculation", Settings),
    ("invalid zero-price closed candle drops active guides", InvalidPrice)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {test.Name}: {e.Message}"); }
}
Console.WriteLine($"RESULT: {tests.Length - failed}/{tests.Length} adapter tests passed (explicit doubles, no real ATAS runtime).");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static IndicatorCandle Candle(int i)
{
    var open = 1000m + i * .5m;
    var c = new IndicatorCandle
    {
        Time = new DateTime(2026, 1, 5, 9, 0, 0).AddMinutes(i),
        Open = open, High = open + .5m, Low = open - .25m, Close = open + .25m,
        Volume = 1000, Bid = 400, Ask = 600, Delta = 200
    };
    if (i == 125) { c.Open = 1062.25m; c.High = 1062.5m; c.Low = 1060.25m; c.Close = 1061.25m; c.Bid = 550; c.Ask = 450; c.Delta = -100; }
    if (i == 126) { c.Open = 1062.25m; c.High = 1063.25m; c.Low = 1062; c.Close = 1063; c.Volume = 1500; c.Bid = 400; c.Ask = 1100; c.Delta = 700; }
    return c;
}

static NqContinuationIndicator Fixture(int count)
{
    var p = new NqContinuationIndicator { UseAlerts = true };
    p.InstrumentInfo = new(.25m, "NQZ6");
    for (var i = 0; i < count; i++) p.TestCandles.Add(Candle(i));
    return p;
}

static NqContinuationIndicator Entered()
{
    var p = Fixture(127);
    p.TestLoadHistory();
    p.TestCandles.Add(Candle(127));
    p.TestCalculate(127);
    Check(p.DataSeries[0][127] > 0, "fixture did not produce a confirmed long");
    return p;
}

static void Availability()
{
    var p = Fixture(127);
    p.TestLoadHistory();
    Check(p.DataSeries[0][126] == 0 && p.TestAlerts.Count == 0, "forming confirmation emitted entry");
    p.TestCandles.Add(Candle(127));
    p.TestCalculate(127);
    Check(p.DataSeries[0][127] > 0 && p.TestAlerts.Count == 1, "successor failed to deliver exactly one entry");
    p.TestCalculate(127);
    Check(p.TestAlerts.Count == 1, "repeat tick repeated alert");
}

static void DollarGuides()
{
    var p = Entered();
    var entry = p.DataSeries[2][127];
    var stop = p.DataSeries[3][127];
    var target = p.DataSeries[4][127];
    Check(entry == 1063.75m && stop == 1060m, "entry/structural stop wrong");
    Check(target - entry == 12.5m, "$250 target wrong");
    Check(p.DataSeries[5][127] - entry == 10m && p.DataSeries[6][127] - entry == 15m, "$200/$300 alternatives wrong");
    Check(p.DataSeries[8][127] == (entry - stop) * 20 + 5 && p.DataSeries[8][127] <= 150, "risk did not include NQ stop slippage");
}

static void FormingIndependence()
{
    var p = Entered();
    var arrow = p.DataSeries[0][127];
    var entry = p.DataSeries[2][127];
    p.TestCandles[127].High = 1100;
    p.TestCandles[127].Low = 1050;
    p.TestCandles[127].Close = 1050;
    p.TestCalculate(127);
    Check(p.DataSeries[0][127] == arrow && p.DataSeries[2][127] == entry && p.TestAlerts.Count == 1,
        "forming HLC revised signal or guides");
}

static void ExitGuides()
{
    var p = Entered();
    var arrow = p.DataSeries[0][127];
    p.TestCandles[127].High = 1100;
    p.TestCandles[127].Low = 1050;
    p.TestCandles.Add(Candle(128));
    p.TestCalculate(128);
    Check(p.DataSeries[0][127] == arrow, "processing a closed entry candle erased its arrow");
    Check(p.DataSeries[2][128] == 0 && p.DataSeries[3][128] == 0, "guides survived reference stop/target resolution");
}

static void History()
{
    var p = Fixture(128);
    p.TestLoadHistory();
    Check(p.DataSeries[0][127] > 0 && p.TestAlerts.Count == 0, "history signal missing or history alerted");
    var value = p.DataSeries[0][127];
    p.TestLoadHistory();
    Check(p.DataSeries[0][127] == value && p.TestAlerts.Count == 0, "recalculation unstable or alerted");
    p.TestCandles[126].Close = 1062.25m;
    p.TestLoadHistory();
    Check(p.DataSeries[0][127] == 0 && p.DataSeries[2][127] == 0, "stale entry survived recalculation");
}

static void Gap()
{
    var p = Fixture(127); p.TestLoadHistory();
    var next = Candle(127); next.Time = next.Time.AddMinutes(10);
    p.TestCandles.Add(next); p.TestCalculate(127);
    Check(p.DataSeries[0][127] == 0 && p.TestAlerts.Count == 0, "gap delivered late signal");
}

static void Session()
{
    var p = Fixture(127); p.TestLoadHistory();
    p.TestCandles.Add(Candle(127)); p.TestSessions.Add(127); p.TestCalculate(127);
    Check(p.DataSeries[0][127] == 0 && p.TestAlerts.Count == 0, "new session delivered old setup");
}

static void RiskGap()
{
    var p = Fixture(127); p.TestLoadHistory();
    var next = Candle(127); next.Open = next.High = next.Low = next.Close = 1070;
    p.TestCandles.Add(next); p.TestCalculate(127);
    Check(p.DataSeries[0][127] == 0 && p.TestAlerts.Count == 0, "open gap breached chosen risk cap");
}

static void MicroGuard()
{
    var p = Fixture(128); p.InstrumentInfo = new(.25m, "MNQZ6"); p.TestLoadHistory();
    Check(p.DataSeries[0][127] == 0 && p.DataSeries[4][127] == 0, "micro used full NQ valuation");
}

static void Settings()
{
    var p = Fixture(1); var before = p.TestRecalculateRequests; p.MaxLossDollars = 100;
    Check(p.TestRecalculateRequests == before + 1, "risk setting did not recalculate");
}

static void InvalidPrice()
{
    var p = Entered();
    p.TestCandles[127].Open = p.TestCandles[127].High = p.TestCandles[127].Low = p.TestCandles[127].Close = 0;
    p.TestCandles.Add(Candle(128));
    p.TestCalculate(128);
    Check(p.DataSeries[2][128] == 0 && p.DataSeries[3][128] == 0 && p.DataSeries[4][128] == 0,
        "invalid closed candle kept an unresolved reference trade alive");
}
