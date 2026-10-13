using PivotReversal;

namespace PivotReversal.Tests;

internal static class Program
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Unspecified);
    private static int _passed, _failed;

    private static int Main(string[] args)
    {
        if (args.Length > 0)
            return Replay.Run(args);

        Test("long sweep confirms only on follow-through bar", LongSignal);
        Test("short behavior mirrors long prices and flow", ShortSignal);
        Test("confirmation body must point in its direction", ConfirmationBody);
        Test("one full tick is required on sweep and reclaim", SweepThresholds);
        Test("one full tick is required above setup for confirmation", ConfirmationThreshold);
        Test("pivot requires all right-hand candles before setup", PivotRightTiming);
        Test("swept unconfirmed candidate never becomes a reference swing", UnconfirmedPivot);
        Test("equal lows and equal highs are not confirmed pivots", EqualExtrema);
        Test("setup wick and closing position reject weak candles", RejectionShape);
        Test("range filter uses only previous true ranges", PriorAtr);
        Test("ATR warmup blocks setups until full history", AtrWarmup);
        Test("third following bar can confirm", ConfirmationDeadline);
        Test("fourth following bar cannot confirm expired setup", Expiry);
        Test("touching setup extreme invalidates pending setup", Invalidation);
        Test("one direction's signal still invalidates the opposite setup", OppositeInvalidation);
        Test("new valid setup replaces older in its direction", Replacement);
        Test("three full intervening candles enforce cooldown", Cooldown);
        Test("default order flow blocks missing bid and ask", MissingFlow);
        Test("default order flow blocks zero candle volume", MissingVolume);
        Test("present flow with no evidence is rejected", NoEvidence);
        Test("relative-volume evidence is independent", RelativeVolumeEvidence);
        Test("opposing-delta evidence is independent", OpposingDeltaEvidence);
        Test("running-delta recovery evidence is independent", RecoveryEvidence);
        Test("extreme aggression evidence is independent", ExtremeEvidence);
        Test("evidence threshold counts independent flags", EvidenceThreshold);
        Test("missing running extrema do not invent recovery", MissingRunningDelta);
        Test("prior volume mean excludes setup candle", PriorVolume);
        Test("reset clears pending setup and confirmed swings", ExplicitReset);
        Test("nonconsecutive indices isolate history", IndexGap);
        Test("large time gaps isolate history", TimeGap);
        Test("nonincreasing timestamps isolate history", TimeOrder);
        Test("invalid OHLC clears state and emits no signal", InvalidCandle);
        Test("negative volume clears state", InvalidVolume);
        Test("duplicate indices emit no duplicate or changed signal", Duplicate);
        Test("price-only mode needs no order-flow fields", PriceOnly);
        Test("all signal prefixes are stable under future append", PrefixInvariance);
        Test("rolling history supports long deterministic replay", LongHistory);
        Test("invalid options fail at construction", OptionValidation);
        Test("CSV parsing and replay boundary behavior", Replay.RunParserTests);
        Console.WriteLine($"RESULT: {_passed} passed; {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { _failed++; Console.WriteLine($"FAIL {name}: {error.Message}"); }
    }

    private static EngineOptions Options() => new()
    {
        TickSize = 1, SwingLeft = 1, SwingRight = 1, AtrPeriod = 2, VolumePeriod = 2,
        MinRangeAtr = .1m, MaxRangeAtr = 20, OrderFlowMode = OrderFlowMode.PriceOnly
    };

    private static BarData B(int i, decimal o, decimal h, decimal l, decimal c) =>
        new(i, Epoch.AddMinutes(i), o, h, l, c, 100, 50, 50);

    private static BarData[] Fixture() =>
    [B(0,12,13,11,12), B(1,12,13,10,12), B(2,12,14,11,13), B(3,11,14,8,13), B(4,14,16,11,15)];

    private static BarData Mirror(BarData b) => b with
    {
        Open = 100 - b.Open, High = 100 - b.Low, Low = 100 - b.High, Close = 100 - b.Close,
        Bid = b.Ask, Ask = b.Bid, Delta = -b.Delta, MaxDelta = -b.MinDelta, MinDelta = -b.MaxDelta,
        LowerBid = b.UpperAsk, LowerAsk = b.UpperBid, UpperBid = b.LowerAsk, UpperAsk = b.LowerBid
    };

    private static List<ReversalSignal> Signals(IEnumerable<BarData> bars, EngineOptions? options = null)
    {
        var e = new PivotReversalEngine(options ?? Options());
        var results = new List<ReversalSignal>();
        foreach (var b in bars) if (e.Process(b) is { } signal) results.Add(signal);
        return results;
    }

    private static ReversalSignal One(IEnumerable<BarData> bars, EngineOptions? options = null)
    {
        var s = Signals(bars, options);
        Equal(1, s.Count, "signal count");
        return s[0];
    }
    private static void None(IEnumerable<BarData> bars, EngineOptions? options = null) =>
        Equal(0, Signals(bars, options).Count, "unexpected signal");
    private static void Equal<T>(T expected, T actual, string label) where T : notnull
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{label}: expected {expected}, got {actual}"); }

    private static void LongSignal()
    {
        var data = Fixture();
        for (var count = 1; count < 5; count++) None(data.Take(count));
        var s = One(data);
        Equal(SignalDirection.Long, s.Direction, "direction"); Equal(1,s.PivotIndex,"pivot");
        Equal(3,s.SetupIndex,"setup"); Equal(4,s.ConfirmationIndex,"confirmation");
        Equal(10m,s.ReferenceSwing,"reference"); Equal(7m,s.StopPrice,"stop"); Equal(15m,s.ConfirmationClose,"close");
    }
    private static void ShortSignal()
    {
        var s = One(Fixture().Select(Mirror));
        Equal(SignalDirection.Short,s.Direction,"direction"); Equal(90m,s.ReferenceSwing,"reference"); Equal(93m,s.StopPrice,"stop");
        var flow = EvidenceFixture();
        var longSignal = One(flow, Options() with { OrderFlowMode=OrderFlowMode.RequireEvidence });
        var shortSignal = One(flow.Select(Mirror), Options() with { OrderFlowMode=OrderFlowMode.RequireEvidence });
        Equal(longSignal.Evidence, shortSignal.Evidence, "mirrored evidence"); Equal(4,shortSignal.EvidenceScore,"all flags");
    }
    private static void ConfirmationBody()
    { var b=Fixture(); b[4]=B(4,16,17,11,15); None(b); None(b.Select(Mirror)); }
    private static void SweepThresholds()
    {
        var b=Fixture(); b[3]=b[3] with { Low=9.5m }; None(b);
        b=Fixture(); b[3]=B(3,10,14,8,10.5m); None(b);
    }
    private static void ConfirmationThreshold()
    { var b=Fixture(); b[4]=b[4] with { Close=14.5m }; None(b); }
    private static void PivotRightTiming()
    {
        var b=Fixture(); None(b,Options() with {SwingRight=2});
        var extended=new[] {b[0],b[1],b[2],B(3,12,14,11,13), b[3] with {Index=4,Time=Epoch.AddMinutes(4)}, b[4] with {Index=5,Time=Epoch.AddMinutes(5)}};
        Equal(5,One(extended, Options() with {SwingRight=2}).ConfirmationIndex,"confirmation");
    }
    private static void UnconfirmedPivot()
    {
        // Descending lows offer no confirmed pivot. A later reclaim must not invent one.
        None(new[] {B(0,13,15,12,14),B(1,12,14,11,13),B(2,11,13,10,12),B(3,12,14,8,13),B(4,14,16,11,15)},
            Options() with { SwingRight=2 });
    }
    private static void EqualExtrema()
    { var b=Fixture(); b[2]=b[2] with {Low=10}; None(b); None(b.Select(Mirror)); }
    private static void RejectionShape()
    {
        var b=Fixture(); b[3]=B(3,8,14,8,13); None(b);
        b=Fixture(); b[3]=B(3,11,18,8,12); None(b);
    }
    private static void PriorAtr()
    {
        Equal(1,Signals(Fixture(),Options() with {MinRangeAtr=1.9m,MaxRangeAtr=2.1m}).Count,"prior ATR range");
        None(Fixture(),Options() with {MaxRangeAtr=1.9m});
        None(Fixture(),Options() with {MinRangeAtr=2.1m,MaxRangeAtr=3});
    }
    private static void AtrWarmup() => None(Fixture(),Options() with {AtrPeriod=5});
    private static BarData[] Delayed(int confirmingIndex)
    {
        var b=Fixture().Take(4).ToList();
        for(var i=4;i<confirmingIndex;i++) b.Add(B(i,12,14,11,13));
        b.Add(B(confirmingIndex,14,16,11,15)); return b.ToArray();
    }
    private static void ConfirmationDeadline() => Equal(6,One(Delayed(6)).ConfirmationIndex,"deadline");
    private static void Expiry() => None(Delayed(7));
    private static void Invalidation()
    { var b=Fixture().Take(4).Concat(new[]{B(4,9,13,8,9),B(5,14,16,11,15)}); None(b); None(b.Select(Mirror)); }
    private static void OppositeInvalidation()
    {
        var b = new[] {B(0,12,13,11,12),B(1,12,13,10,12),B(2,12,16,11,13),
            B(3,11,14,8,13),B(4,14,18,10,13),B(5,14,20,11,15),B(6,10,17,8,9)};
        var s=One(b,Options() with {CooldownBars=0});
        Equal(SignalDirection.Long,s.Direction,"only long signal"); Equal(5,s.ConfirmationIndex,"long confirmation");
    }
    private static void Replacement()
    {
        var b=Fixture().Take(4).Concat(new[]{B(4,11,15,9,14),B(5,14,16,11,15),B(6,15,17,11,16)});
        var s=One(b); Equal(4,s.SetupIndex,"replacement"); Equal(6,s.ConfirmationIndex,"new high threshold");
    }
    private static void Cooldown()
    {
        var b=Fixture().Concat(new[]{B(5,10,14,7,13),B(6,14,16,10,15),B(7,10,14,6,13),B(8,14,16,10,15)}).ToArray();
        var s=Signals(b); Equal(2,s.Count,"cooldown signals"); Equal(4,s[0].ConfirmationIndex,"first"); Equal(8,s[1].ConfirmationIndex,"after three full bars");
    }
    private static BarData[] EvidenceFixture()
    {
        var b=Fixture(); b[3]=b[3] with {Volume=200,Bid=120,Ask=80,Delta=-40,MinDelta=-80,MaxDelta=0,LowerBid=80,LowerAsk=20}; return b;
    }
    private static EngineOptions FlowOptions() => Options() with {OrderFlowMode=OrderFlowMode.RequireEvidence};
    private static void MissingFlow()
    { var b=EvidenceFixture(); b[3]=b[3] with {Bid=0,Ask=0}; None(b,FlowOptions()); }
    private static void MissingVolume()
    { var b=EvidenceFixture(); b[3]=b[3] with {Volume=0}; None(b,FlowOptions()); }
    private static void NoEvidence() => None(Fixture(),FlowOptions());
    private static void Flag(BarData setup, EvidenceFlags expected)
    { var b=Fixture(); b[3]=setup; var s=One(b,FlowOptions()); Equal(expected,s.Evidence,"flag"); Equal(1,s.EvidenceScore,"score"); }
    private static void RelativeVolumeEvidence() => Flag(Fixture()[3] with {Volume=150,Bid=75,Ask=75},EvidenceFlags.RelativeVolume);
    private static void OpposingDeltaEvidence() => Flag(Fixture()[3] with {Delta=-20,MinDelta=-20},EvidenceFlags.OpposingDelta);
    private static void RecoveryEvidence() => Flag(Fixture()[3] with {Delta=10,MaxDelta=10,MinDelta=-20},EvidenceFlags.DeltaRecovery);
    private static void ExtremeEvidence() => Flag(Fixture()[3] with {LowerBid=10},EvidenceFlags.ExtremeAggression);
    private static void EvidenceThreshold()
    {
        Equal(4,One(EvidenceFixture(),FlowOptions() with {MinimumEvidence=4}).EvidenceScore,"four flags");
        None(Fixture().Select(b=>b.Index==3?b with {Volume=150,Bid=75,Ask=75}:b),FlowOptions() with {MinimumEvidence=2});
    }
    private static void MissingRunningDelta()
    { var b=Fixture(); b[3]=b[3] with {Delta=30}; None(b,FlowOptions()); }
    private static void PriorVolume()
    { var b=Fixture(); b[3]=b[3] with {Volume=150,Bid=75,Ask=75}; Equal(EvidenceFlags.RelativeVolume,One(b,FlowOptions()).Evidence,"prior mean"); }
    private static void ExplicitReset()
    { var e=new PivotReversalEngine(Options()); foreach(var b in Fixture().Take(4)) e.Process(b); e.Reset(); Equal(true,e.Process(Fixture()[4]) is null,"reset"); }
    private static void IndexGap()
    { var b=Fixture(); b[4]=b[4] with {Index=6}; None(b); }
    private static void TimeGap()
    { var b=Fixture(); b[4]=b[4] with {Time=Epoch.AddMinutes(25)}; None(b); }
    private static void TimeOrder()
    { var b=Fixture(); b[4]=b[4] with {Time=b[3].Time}; None(b); }
    private static void InvalidCandle()
    { var b=Fixture().Take(4).Concat(new[]{B(4,12,10,11,12),B(5,14,16,11,15)}); None(b); }
    private static void InvalidVolume()
    { var b=Fixture().Take(4).Concat(new[]{B(4,12,14,11,12) with {Volume=-1},B(5,14,16,11,15)}); None(b); }
    private static void Duplicate()
    {
        var e=new PivotReversalEngine(Options()); foreach(var b in Fixture().Take(4)) e.Process(b);
        var confirm=Fixture()[4]; Equal(true,e.Process(confirm) is not null,"original signal");
        Equal(true,e.Process(confirm) is null,"duplicate"); Equal(true,e.Process(confirm with {Close=16}) is null,"same index update");
    }
    private static void PriceOnly() => Equal(1,Signals(Fixture().Select(b=>b with {Volume=0,Bid=0,Ask=0})).Count,"price-only");
    private static void PrefixInvariance()
    {
        var data=Fixture().Concat(new[]{B(5,10,14,7,13),B(6,14,16,10,15),B(7,10,14,6,13),B(8,14,16,10,15)}).ToArray();
        var all=Signals(data);
        for(var n=1;n<=data.Length;n++) Equal(string.Join(";",all.Where(s=>s.ConfirmationIndex<n)),string.Join(";",Signals(data.Take(n))),$"prefix {n}");
        Equal(2,all.Count,"prefix fixture contains signals");
    }
    private static void LongHistory()
    {
        var data=new List<BarData>();
        for(var block=0;block<1000;block++)
        foreach(var b in Fixture()) data.Add(b with {Index=block*5+b.Index,Time=Epoch.AddMinutes(block*5+b.Index)});
        var s=Signals(data); Equal(1000,s.Count,"long stream signals"); Equal(4999,s[^1].ConfirmationIndex,"last signal");
    }
    private static void OptionValidation()
    {
        var invalid=new[] {Options() with {TickSize=0},Options() with {TickSize=-1},Options() with {SwingLeft=0},Options() with {SwingRight=0},
            Options() with {AtrPeriod=0},Options() with {VolumePeriod=0},Options() with {ConfirmationBars=0},Options() with {CooldownBars=-1},
            Options() with {MaxGapMinutes=0},Options() with {MinimumEvidence=0},Options() with {MinimumEvidence=5},Options() with {MinWickFraction=-.1m},
            Options() with {CloseLocation=1.1m},Options() with {MinRangeAtr=0},Options() with {MaxRangeAtr=.05m},Options() with {RelativeVolume=0},
            Options() with {DeltaFraction=0},Options() with {DeltaRecoveryFraction=1.1m},Options() with {ExtremeVolumeFraction=0},
            Options() with {ExtremeDominance=.4m},Options() with {OrderFlowMode=(OrderFlowMode)42}};
        foreach(var o in invalid)
        { try { _=new PivotReversalEngine(o); } catch(ArgumentOutOfRangeException) { continue; } throw new Exception($"accepted invalid options {o}"); }
    }
}
