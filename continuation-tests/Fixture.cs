using NqContinuation;

public static class Fixture
{
    public static readonly DateTime Start = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Unspecified);
    public static List<MarketBar> Continuation(TradeDirection direction = TradeDirection.Long, bool flow = true)
    {
        var bars = new List<MarketBar>();
        for (int i = 0; i < 110; i++)
        {
            decimal close = 18000m + i * .5m;
            bars.Add(new(i, Start.AddMinutes(i), close - .25m, close + .75m, close - 1.25m, close,
                flow ? 1000m : 0, flow ? 400m : 0, flow ? 600m : 0, flow ? 200m : 0));
        }
        decimal prior = bars[^1].Close;
        bars.Add(new(110, Start.AddMinutes(110), prior, prior + .25m, prior - 2.5m, prior - .75m,
            flow ? 1000m : 0, flow ? 600m : 0, flow ? 400m : 0, flow ? -200m : 0));
        bars.Add(new(111, Start.AddMinutes(111), prior - .75m, prior + 1m, prior - .75m, prior + .75m,
            flow ? 1000m : 0, flow ? 350m : 0, flow ? 650m : 0, flow ? 300m : 0));
        if (direction == TradeDirection.Short)
            return bars.Select(b => b with { Open = 36000m - b.Open, High = 36000m - b.Low, Low = 36000m - b.High,
                Close = 36000m - b.Close, Bid = b.Ask, Ask = b.Bid, Delta = -b.Delta,
                LowerBid = b.UpperAsk, LowerAsk = b.UpperBid, UpperBid = b.LowerAsk, UpperAsk = b.LowerBid }).ToList();
        return bars;
    }

    public static ContinuationCandidate Candidate(TradeDirection direction = TradeDirection.Long, decimal stop = 17995m) => new()
    {
        Direction = direction, SetupIndex = 8, ConfirmationIndex = 9, ConfirmationTime = Start.AddMinutes(9),
        ConfirmationClose = 18000m, StopPrice = direction == TradeDirection.Long ? stop : 36000m - stop,
        TriggerPrice = 18000m
    };
    public static BarOpen Successor(decimal open = 18000m) => new(10, Start.AddMinutes(10), open);
    public static MarketBar TradeBar(TradePlan plan, int offset = 0, decimal? open = null, decimal? high = null,
        decimal? low = null, decimal? close = null) => new(plan.EntryIndex + offset, plan.AvailableAt.AddMinutes(offset),
            open ?? plan.EntryPrice, high ?? plan.EntryPrice + .5m, low ?? plan.EntryPrice - .5m, close ?? plan.EntryPrice);
    public static ContinuationCandidate? Run(IEnumerable<MarketBar> bars, ContinuationOptions? options = null)
    {
        var engine = new ContinuationEngine(options ?? new());
        ContinuationCandidate? result = null;
        foreach (var bar in bars) result = engine.Process(bar) ?? result;
        return result;
    }
}
