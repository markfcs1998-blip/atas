using NqContinuation;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void True(bool value, string message = "Expected true") { if (!value) throw new Exception(message); }
void Throws(Action run) { try { run(); } catch (ArgumentException) { return; } throw new Exception("Expected ArgumentException"); }
TradePlan Plan(TradeDirection direction = TradeDirection.Long, ContinuationOptions? options = null) => TradePlanner.Create(Fixture.Candidate(direction), Fixture.Successor(), options ?? new()) ?? throw new Exception("Expected accepted plan");

Test("long continuation requires warm trend, pullback, then closed full-range break", () => {
    var bars = Fixture.Continuation(); var engine = new ContinuationEngine(new());
    foreach (var bar in bars.Take(111)) Equal<ContinuationCandidate?>(null, engine.Process(bar));
    var c = engine.Process(bars[^1]); True(c != null, "Expected long continuation");
    Equal(TradeDirection.Long, c!.Direction); Equal(110, c.SetupIndex); Equal(111, c.ConfirmationIndex);
    Equal(18051.75m, c.StopPrice); True((c.Evidence & FlowEvidence.AlignedDelta) != 0);
});
Test("short continuation mirrors long price and flow rules", () => {
    var c = Fixture.Run(Fixture.Continuation(TradeDirection.Short)); True(c != null, "Expected short continuation");
    Equal(TradeDirection.Short, c!.Direction); Equal(17948.25m, c.StopPrice);
});
Test("price-only mode explicitly accepts missing order flow", () => True(Fixture.Run(Fixture.Continuation(flow:false), new() { DataMode = DataMode.PriceOnly }) != null));
Test("required flow rejects missing bid ask data", () => Equal<ContinuationCandidate?>(null, Fixture.Run(Fixture.Continuation(flow:false))));
Test("higher EMAs wait for a fully closed aligned five-minute group", () => {
    var engine = new ContinuationEngine(new()); var bars = Fixture.Continuation();
    foreach (var b in bars.Take(4)) engine.Process(b); Equal<decimal?>(null, engine.HigherFastEma);
    engine.Process(bars[4]); Equal<decimal?>(bars[4].Close, engine.HigherFastEma);
});
Test("minute duplicate preserves first closed snapshot", () => {
    var bars = Fixture.Continuation(); var engine = new ContinuationEngine(new()); engine.Process(bars[0]);
    var fast = engine.FastEma; Equal<ContinuationCandidate?>(null, engine.Process(bars[0] with { Close = bars[0].High })); Equal(fast, engine.FastEma);
});
Test("gap resets trend and blocks old setup", () => {
    var bars = Fixture.Continuation(); var engine = new ContinuationEngine(new()); foreach (var b in bars.Take(111)) engine.Process(b);
    Equal<ContinuationCandidate?>(null, engine.Process(bars[^1] with { Index = 112, Time = bars[^1].Time.AddMinutes(1) })); Equal<decimal?>(null, engine.HigherSlowEma);
});
Test("reset clears all exposed EMA state", () => {
    var engine = new ContinuationEngine(new()); foreach (var b in Fixture.Continuation()) engine.Process(b); engine.Reset();
    Equal<decimal?>(null, engine.FastEma); Equal<decimal?>(null, engine.SlowEma); Equal<decimal?>(null, engine.HigherFastEma); Equal<decimal?>(null, engine.HigherSlowEma);
});
Test("ATR warmup longer than higher warmup remains quiet", () => Equal<ContinuationCandidate?>(null,
    Fixture.Run(Fixture.Continuation(), new() { AtrPeriod = 200 })));
Test("incomplete first five-minute group is discarded", () => {
    var engine = new ContinuationEngine(new()); var bars=Fixture.Continuation();
    foreach(var b in bars.Skip(1).Take(4)) engine.Process(b); Equal<decimal?>(null,engine.HigherSlowEma);
    foreach(var b in bars.Skip(5).Take(4)) engine.Process(b); Equal<decimal?>(null,engine.HigherSlowEma);
    engine.Process(bars[9]); Equal<decimal?>(bars[9].Close,engine.HigherSlowEma);
});
Test("higher trend warmup needs twenty-one complete groups", () => {
    var bars=Fixture.Continuation(); var shortened=bars.Take(100).ToList();
    shortened.Add(bars[110] with { Index=100, Time=Fixture.Start.AddMinutes(100) });
    shortened.Add(bars[111] with { Index=101, Time=Fixture.Start.AddMinutes(101) });
    Equal<ContinuationCandidate?>(null,Fixture.Run(shortened));
});
Test("invalid bar and nonminute timestamp reset state", () => {
    foreach(var invalid in new[] { Fixture.Continuation()[111] with { Low=19000m },
        Fixture.Continuation()[111] with { Volume=-1m }, Fixture.Continuation()[111] with { Time=Fixture.Start.AddSeconds(1) } }) {
        var engine=new ContinuationEngine(new()); foreach(var b in Fixture.Continuation().Take(111)) engine.Process(b);
        Equal<ContinuationCandidate?>(null,engine.Process(invalid)); Equal<decimal?>(null,engine.FastEma); Equal<decimal?>(null,engine.HigherSlowEma);
    }
});
Test("changed duplicate confirmation cannot repaint a prior candidate", () => {
    var bars=Fixture.Continuation(); var engine=new ContinuationEngine(new()); ContinuationCandidate? c=null;
    foreach(var b in bars) c=engine.Process(b)??c; True(c!=null); var fast=engine.FastEma;
    Equal<ContinuationCandidate?>(null,engine.Process(bars[^1] with { Low=0,Close=0,Volume=-1 })); Equal(fast,engine.FastEma);
});
Test("historical prefix and complete sequence emit identical confirmation", () => {
    var bars=Fixture.Continuation(); var expected=Fixture.Run(bars);
    foreach(int split in new[] { 1,14,104,110,111 }) {
        var engine=new ContinuationEngine(new()); ContinuationCandidate? result=null;
        foreach(var b in bars.Take(split)) result=engine.Process(b)??result;
        foreach(var b in bars.Skip(split)) result=engine.Process(b)??result; Equal(expected,result);
    }
});
Test("confirmation must close beyond entire pullback range", () => {
    var bars=Fixture.Continuation(); bars[^1]=bars[^1] with { Close=bars[^2].High }; Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("wide confirmation does not use its own range to inflate ATR", () => {
    var bars=Fixture.Continuation(); bars[^1]=bars[^1] with { High=bars[^1].Close+.25m,Low=bars[^1].Close-10m,Open=bars[^1].Close-1m };
    Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("confirmation must close in directional thirty-five percent zone", () => {
    var bars=Fixture.Continuation(); var b=bars[^1]; bars[^1]=b with { High=b.Close+1.3m,Low=b.Close-1m,Open=b.Close-.5m };
    Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("confirmation candle extreme is included in structural stop", () => {
    var bars=Fixture.Continuation(); var b=bars[^1]; bars[^1]=b with { Open=b.Close-.5m,High=b.Close,Low=bars[^2].Low-.1m };
    var c=Fixture.Run(bars,new() { MaxConfirmationRangeAtr=2m }); True(c!=null); Equal(18051.5m,c!.StopPrice);
});
Test("pullback remains valid through fifth subsequent candle only", () => {
    foreach(int delay in new[] { 5,6 }) {
        var bars=Fixture.Continuation(); var setup=bars[^2]; var confirmation=bars[^1]; bars.RemoveAt(bars.Count-1);
        for(int offset=1;offset<delay;offset++) bars.Add(setup with { Index=setup.Index+offset,Time=setup.Time.AddMinutes(offset),Open=setup.Close,Close=setup.Close });
        bars.Add(confirmation with { Index=setup.Index+delay,Time=setup.Time.AddMinutes(delay) });
        var c=Fixture.Run(bars); if(delay==5) True(c!=null,"Fifth candle should confirm"); else Equal<ContinuationCandidate?>(null,c);
    }
});
Test("flow requires aligned delta even when relative volume is strong", () => {
    var bars=Fixture.Continuation(); bars[^1]=bars[^1] with { Delta=-1m,Volume=10000m }; Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("flow evidence threshold counts actual independent flags", () => {
    var bars=Fixture.Continuation(); var c=Fixture.Run(bars,new() { MinimumEvidence=2 }); True(c!=null); Equal(2,c!.EvidenceScore);
    Equal<ContinuationCandidate?>(null,Fixture.Run(bars,new() { MinimumEvidence=3 }));
    bars[^1]=bars[^1] with { Volume=1300,UpperAsk=200,UpperBid=50 };
    c=Fixture.Run(bars,new() { MinimumEvidence=4 }); True(c!=null); Equal(4,c!.EvidenceScore);
});
Test("relative volume uses twenty prior positive volumes", () => {
    var bars=Fixture.Continuation(); bars[^1]=bars[^1] with { Volume=1200,Delta=1 }; bars[^2]=bars[^2] with { Delta=1 };
    var c=Fixture.Run(bars); True(c!=null); Equal(FlowEvidence.RelativeVolume,c!.Evidence);
    bars[100]=bars[100] with { Volume=0 }; Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("delta improvement requires actual prior bid ask data", () => {
    var bars=Fixture.Continuation(); bars[^1]=bars[^1] with { Delta=1 }; bars[^2]=bars[^2] with { Delta=-200 };
    var c=Fixture.Run(bars); True(c!=null); Equal(FlowEvidence.DeltaImprovement,c!.Evidence);
    bars[^2]=bars[^2] with { Bid=0,Ask=0 }; Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("extreme aggression uses directional volume and dominance", () => {
    var bars=Fixture.Continuation(); bars[^1]=bars[^1] with { Delta=1,UpperAsk=100,UpperBid=50 }; bars[^2]=bars[^2] with { Delta=1 };
    var c=Fixture.Run(bars); True(c!=null); Equal(FlowEvidence.ExtremeAggression,c!.Evidence);
    bars[^1]=bars[^1] with { UpperBid=100 }; Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("trend without qualifying preceding impulse cannot arm", () => Equal<ContinuationCandidate?>(null,
    Fixture.Run(Fixture.Continuation(),new() { ImpulseDistanceAtr=10m })));
Test("pullback must touch fast EMA band before confirming", () => {
    var bars=Fixture.Continuation(); bars[^2]=bars[^2] with { Low=bars[^2].Close }; Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("close through slow EMA cancels armed setup", () => {
    var bars=Fixture.Continuation(); var setup=bars[^2]; var confirmation=bars[^1]; bars.RemoveAt(bars.Count-1);
    bars.Add(setup with { Index=111,Time=Fixture.Start.AddMinutes(111),Open=setup.Close,Close=setup.Close-10m,Low=setup.Close-10.25m });
    bars.Add(confirmation with { Index=112,Time=Fixture.Start.AddMinutes(112) }); Equal<ContinuationCandidate?>(null,Fixture.Run(bars));
});
Test("three full bars intervene between confirmed candidates", () => {
    var bars=Fixture.Continuation(); var first=bars[^1]; decimal last=first.Close;
    var secondSetup=new MarketBar(112,Fixture.Start.AddMinutes(112),last,last+.25m,last-3.25m,last-1m,1000,600,400,-200);
    bars.Add(secondSetup);
    bars.Add(new(113,Fixture.Start.AddMinutes(113),last-1m,last+1m,last-1m,last+.75m,1000,350,650,300));
    bars.Add(new(114,Fixture.Start.AddMinutes(114),last+.75m,last+1m,last-.5m,last+.75m,1000,500,500,0));
    bars.Add(new(115,Fixture.Start.AddMinutes(115),last+.75m,last+1.75m,last+.5m,last+1.5m,1000,350,650,300));
    var engine=new ContinuationEngine(new()); var indices=new List<int>(); foreach(var b in bars) { var c=engine.Process(b); if(c!=null) indices.Add(c.ConfirmationIndex); }
    Equal("111,115",string.Join(",",indices));
});
Test("options reject valuation for micro NQ and nonquarter tick", () => { Throws(() => new ContinuationEngine(new() { PointValue = 2 })); Throws(() => new ContinuationEngine(new() { TickSize = .1m })); });
Test("options validate periods counts ranges and flow thresholds", () => {
    var invalid=new ContinuationOptions[] {
        new() { MaxLossDollars=0 },new() { EntrySlippageTicks=-1 },new() { StopSlippageTicks=-1 },new() { MinimumRewardRisk=0 },
        new() { FastPeriod=0 },new() { SlowPeriod=9 },new() { HigherFastPeriod=0 },new() { HigherSlowPeriod=9 },new() { AtrPeriod=0 },
        new() { VolumePeriod=0 },new() { MaxPullbackBars=0 },new() { ImpulseLookbackBars=0 },new() { ImpulseDistanceAtr=0 },
        new() { TouchBandAtr=-1 },new() { MinTrendSeparationAtr=-1 },new() { MaxConfirmationRangeAtr=0 },new() { MinCloseLocation=1.01m },
        new() { CooldownBars=-1 },new() { MaxTradeBars=0 },new() { DataMode=(DataMode)99 },new() { MinimumEvidence=5 },
        new() { DeltaFraction=0 },new() { RelativeVolume=0 },new() { DeltaImprovementFraction=0 },new() { ExtremeDominance=1.01m },
        new() { ExtremeVolumeFraction=1.01m }
    }; foreach(var options in invalid) Throws(()=>new ContinuationEngine(options));
});
Test("default next-open long plan has one adverse slippage tick", () => {
    var p = Plan(); Equal(18000.25m, p.EntryPrice); Equal(110m, p.PlannedRiskDollars); Equal(18010.25m, p.TargetLower);
    Equal(18012.75m, p.TargetPrimary); Equal(18015.25m, p.TargetUpper); Equal(250m, p.TargetNetDollars); Equal(250m / 110m, p.RewardRisk);
});
Test("short plan rounds and slips entry adversely", () => {
    var p = TradePlanner.Create(Fixture.Candidate(TradeDirection.Short), Fixture.Successor(18000.13m), new()); True(p != null);
    Equal(17999.75m, p!.EntryPrice); Equal(17987.25m, p.TargetPrimary); Equal(110m, p.PlannedRiskDollars);
});
Test("long entry rounds upward before adding configured slippage", () => Equal(18000.50m, TradePlanner.Create(Fixture.Candidate(), Fixture.Successor(18000.01m), new())!.EntryPrice));
Test("fees and stop slippage are included in net plan math", () => {
    var p = Plan(options:new() { RoundTripCostsDollars = 4m, StopSlippageTicks = 2 });
    Equal(119m, p.PlannedRiskDollars); Equal(18013m, p.TargetPrimary); Equal(251m, p.TargetNetDollars);
});
Test("gap exceeding max planned loss rejects plan", () => Equal<TradePlan?>(null, TradePlanner.Create(Fixture.Candidate(), Fixture.Successor(18003m), new())));
Test("entry at wrong side of structural stop rejects plan", () => Equal<TradePlan?>(null, TradePlanner.Create(Fixture.Candidate(), Fixture.Successor(17994m), new())));
Test("planner rejects missing or delayed successor", () => {
    Equal<TradePlan?>(null, TradePlanner.Create(Fixture.Candidate(), Fixture.Successor() with { Index = 11 }, new()));
    Equal<TradePlan?>(null, TradePlanner.Create(Fixture.Candidate(), Fixture.Successor() with { Time = Fixture.Start.AddMinutes(11) }, new()));
});
Test("planner rejects insufficient primary net reward risk", () => Equal<TradePlan?>(null, TradePlanner.Create(Fixture.Candidate(), Fixture.Successor(), new() { MinimumRewardRisk = 3m })));
Test("planner validates monetary limits", () => {
    Throws(() => TradePlanner.Create(Fixture.Candidate(), Fixture.Successor(), new() { ProfitTargetDollars = 301m }));
    Throws(() => TradePlanner.Create(Fixture.Candidate(), Fixture.Successor(), new() { RoundTripCostsDollars = -1m }));
});
Test("planned risk accepts exactly cap but rejects one extra tick", () => {
    var candidate=Fixture.Candidate(stop:17993m); var accepted=TradePlanner.Create(candidate,Fixture.Successor(),new()); True(accepted!=null); Equal(150m,accepted!.PlannedRiskDollars);
    Equal<TradePlan?>(null,TradePlanner.Create(candidate,Fixture.Successor(18000.25m),new()));
});
Test("primary net reward risk includes fees and rounded actual reward", () => {
    var c=Fixture.Candidate(stop:17993m); Equal<TradePlan?>(null,TradePlanner.Create(c,Fixture.Successor(),new() { ProfitTargetDollars=200m }));
    var p=Plan(options:new() { RoundTripCostsDollars=4m,MinimumRewardRisk=251m/114m }); Equal(251m/114m,p.RewardRisk);
});
Test("planner rejects off-grid structural stops", () => Equal<TradePlan?>(null,TradePlanner.Create(Fixture.Candidate(stop:17995.01m),Fixture.Successor(),new())));
Test("target is evaluated when the entry candle closes", () => {
    var p = Plan(); var exit = new TradeTracker(p,new()).Process(Fixture.TradeBar(p, high:p.TargetPrimary)); True(exit != null);
    Equal(ExitReason.Target, exit!.Reason); Equal(250m, exit.NetDollars); Equal(p.EntryIndex, exit.ExitIndex);
});
Test("both stop and primary target are conservative stop first", () => {
    var p = Plan(); var exit = new TradeTracker(p,new()).Process(Fixture.TradeBar(p, high:p.TargetPrimary, low:p.StopPrice)); True(exit != null);
    Equal(ExitReason.Stop, exit!.Reason); True(exit.Ambiguous); Equal(17994.75m, exit.ExitPrice); Equal(-110m, exit.NetDollars);
});
Test("stop gap uses adverse open plus stop slippage", () => {
    var p = Plan(); var exit = new TradeTracker(p,new()).Process(Fixture.TradeBar(p, open:17990m, high:17991m, low:17989m, close:17990m)); True(exit != null);
    Equal(17989.75m, exit!.ExitPrice); Equal(-210m, exit.NetDollars);
});
Test("short stop gap mirrors long gap", () => {
    var p = Plan(TradeDirection.Short); var exit = new TradeTracker(p,new()).Process(Fixture.TradeBar(p, open:18010m, high:18011m, low:18009m, close:18010m)); True(exit != null);
    Equal(18010.25m, exit!.ExitPrice); Equal(-210m, exit.NetDollars);
});
Test("timeout follows fifteen closed entry and later candles", () => {
    var p = Plan(); var tracker = new TradeTracker(p,new()); for (int i=0;i<14;i++) Equal<TradeExit?>(null, tracker.Process(Fixture.TradeBar(p,i)));
    var exit=tracker.Process(Fixture.TradeBar(p,14, close:p.EntryPrice+.25m)); True(exit != null); Equal(ExitReason.Timeout,exit!.Reason); Equal(5m,exit.NetDollars);
});
Test("tracker terminal result is emitted once", () => {
    var p=Plan(); var tracker=new TradeTracker(p,new()); var bar=Fixture.TradeBar(p,high:p.TargetPrimary); True(tracker.Process(bar)!=null);
    Equal<TradeExit?>(null,tracker.Process(bar)); Equal<TradeExit?>(null,tracker.Process(Fixture.TradeBar(p,1,low:p.StopPrice)));
});
Test("tracker invalid or gapped data remains unresolved permanently", () => {
    var p=Plan(); var tracker=new TradeTracker(p,new()); Equal<TradeExit?>(null,tracker.Process(Fixture.TradeBar(p,1)));
    Equal<TradeExit?>(null,tracker.Process(Fixture.TradeBar(p,2,high:p.TargetPrimary)));
});
Test("tracker duplicate does not count twice toward timeout", () => {
    var p=Plan(); var tracker=new TradeTracker(p,new()); var first=Fixture.TradeBar(p); Equal<TradeExit?>(null,tracker.Process(first));
    Equal<TradeExit?>(null,tracker.Process(first with { Low=p.StopPrice }));
    for(int i=1;i<14;i++) Equal<TradeExit?>(null,tracker.Process(Fixture.TradeBar(p,i)));
    Equal(ExitReason.Timeout,tracker.Process(Fixture.TradeBar(p,14))!.Reason);
});
Test("target ignores favorable open gap and fills fixed target", () => {
    var p=Plan(); var e=new TradeTracker(p,new()).Process(Fixture.TradeBar(p,open:p.TargetPrimary+2m,high:p.TargetPrimary+3m,low:p.TargetPrimary+1m,close:p.TargetPrimary+2m));
    True(e!=null); Equal(p.TargetPrimary,e!.ExitPrice); Equal(250m,e.NetDollars);
});
Test("tracker subtracts round trip fees once on target and timeout", () => {
    var options=new ContinuationOptions { RoundTripCostsDollars=4m,MaxTradeBars=1 }; var p=Plan(options:options);
    Equal(251m,new TradeTracker(p,options).Process(Fixture.TradeBar(p,high:p.TargetPrimary))!.NetDollars);
    Equal(-4m,new TradeTracker(p,options).Process(Fixture.TradeBar(p))!.NetDollars);
});

int failed=0; foreach(var test in tests) { try { test.Run(); Console.WriteLine($"PASS {test.Name}"); } catch(Exception e) { failed++; Console.WriteLine($"FAIL {test.Name}: {e.Message}"); } }
Console.WriteLine($"{tests.Count-failed}/{tests.Count} tests passed"); return failed==0 ? 0 : 1;
