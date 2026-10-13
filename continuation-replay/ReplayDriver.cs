namespace NqContinuation.Replay;

/// <summary>Injection seam for causal driver tests; production wraps the shared continuation engine.</summary>
public interface IReplayEngine
{
    ContinuationCandidate? Process(MarketBar bar);
    void Reset();
}

internal sealed class SharedReplayEngine(ContinuationOptions options) : IReplayEngine
{
    private readonly ContinuationEngine _engine = new(options);
    public ContinuationCandidate? Process(MarketBar bar) => _engine.Process(bar);
    public void Reset() => _engine.Reset();
}

public sealed class ReplayCounts
{
    public int Rows { get; internal set; }
    public int ClosedRows { get; internal set; }
    public int ExcludedFormingRows { get; internal set; }
    public int InvalidRows { get; internal set; }
    public int InvalidClosedRows { get; internal set; }
    public int ProcessedBars { get; internal set; }
    public int OneMinuteIntervals { get; internal set; }
    public int UsableSuccessorIntervals { get; internal set; }
    public int CadenceBreaks { get; internal set; }
    public int NonIncreasingTimestampPairs { get; internal set; }
    public int BoundaryResets { get; internal set; }
    public int CompleteFiveMinuteGroups { get; internal set; }
    public int DiscardedPartialFiveMinuteGroups { get; internal set; }
    public int HigherSlowGroupsRequired { get; internal set; }
    public int WarmupNominalMinutes { get; internal set; }
    public int Candidates { get; internal set; }
    public int SuppressedActiveCandidates { get; internal set; }
    public int UnavailableCandidates { get; internal set; }
    public int RejectedPlans { get; internal set; }
    public int Entries { get; internal set; }
    public int Targets { get; internal set; }
    public int Stops { get; internal set; }
    public int Ambiguous { get; internal set; }
    public int Timeouts { get; internal set; }
    public int Unresolved { get; internal set; }
    public decimal NetResolvedDollars { get; internal set; }
    public decimal MaximumPlannedRiskDollars { get; internal set; }
}

public sealed record ReplayIssue(int LineNumber, string Reason);

public sealed class ReplayTrade(TradePlan plan)
{
    public TradePlan Plan { get; } = plan;
    public TradeExit? Exit { get; internal set; }
    public DateTime? OutcomeKnownAt { get; internal set; }
    public string? OutcomeTimeBasis { get; internal set; }
    public string? UnresolvedReason { get; internal set; }
    public int? UnresolvedAtIndex { get; internal set; }
    public DateTime? UnresolvedAtTime { get; internal set; }
}

public sealed record ReplayResult(ReplayCounts Counts, IReadOnlyList<ReplayTrade> Trades, IReadOnlyList<ReplayIssue> Issues);

public static class ReplayDriver
{
    /// <summary>
    /// All but the final physical row are closed. Successor availability reads only its fixed index/time/open.
    /// Session boundaries are optional explicit metadata; a CSV has none and calendar dates do not imply sessions.
    /// Rows must retain the unique physical-line indices supplied by ReplayCsv.ReadRows.
    /// </summary>
    public static ReplayResult Run(IReadOnlyList<CsvRow> rows, ContinuationOptions options,
        IReplayEngine? engine = null, IReadOnlySet<int>? sessionBoundaryIndices = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);
        engine ??= new SharedReplayEngine(options);
        var counts = new ReplayCounts
        {
            Rows = rows.Count, ClosedRows = Math.Max(0, rows.Count - 1), ExcludedFormingRows = rows.Count == 0 ? 0 : 1,
            InvalidRows = rows.Count(row => !row.IsValidClosed), HigherSlowGroupsRequired = options.HigherSlowPeriod,
            WarmupNominalMinutes = options.HigherSlowPeriod * 5
        };
        var issues = rows.Where(row => row.Error is not null).Select(row => new ReplayIssue(row.LineNumber, row.Error!)).ToList();
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i - 1].Time is not DateTime before || rows[i].Time is not DateTime after) continue;
            if (after - before == TimeSpan.FromMinutes(1))
            {
                counts.OneMinuteIntervals++;
                if (rows[i - 1].IsValidClosed && rows[i].HasValidOpen && rows[i].Index == rows[i - 1].Index + 1 &&
                    sessionBoundaryIndices?.Contains(rows[i].Index) != true)
                    counts.UsableSuccessorIntervals++;
            }
            else
            {
                counts.CadenceBreaks++;
                if (after <= before) counts.NonIncreasingTimestampPairs++;
                issues.Add(new ReplayIssue(rows[i].LineNumber, "Timestamp cadence is not exactly one minute; literal timestamps are preserved."));
            }
        }

        var trades = new List<ReplayTrade>();
        var coverage = new HigherCoverage();
        TradeTracker? tracker = null;
        ReplayTrade? active = null;
        void EndUnresolved(string reason, CsvRow row)
        {
            if (active is null) return;
            active.UnresolvedReason = reason;
            active.UnresolvedAtIndex = row.Index;
            active.UnresolvedAtTime = row.Time;
            counts.Unresolved++;
            tracker = null;
            active = null;
        }
        void Reset(string reason, CsvRow row)
        {
            EndUnresolved(reason, row);
            engine.Reset();
            coverage.Reset();
            counts.BoundaryResets++;
        }

        for (var i = 0; i < rows.Count - 1; i++)
        {
            var row = rows[i];
            if (!row.IsValidClosed)
            {
                counts.InvalidClosedRows++;
                Reset("InvalidClosedRow", row);
                continue;
            }
            if (sessionBoundaryIndices?.Contains(row.Index) == true) Reset("SessionBoundary", row);
            else if (i > 0 && rows[i - 1].IsValidClosed &&
                (row.Index != rows[i - 1].Index + 1 || row.Time - rows[i - 1].Time != TimeSpan.FromMinutes(1)))
                Reset("CadenceBoundary", row);

            var bar = new MarketBar(row.Index, row.Time!.Value, row.Open!.Value, row.High!.Value, row.Low!.Value, row.Close!.Value);
            counts.ProcessedBars++;
            coverage.Process(bar.Time);

            // Evaluate only the now-closed row, including an entered plan's entry row.
            if (tracker is not null)
            {
                var terminal = tracker.Process(bar);
                if (terminal is not null)
                {
                    active!.Exit = terminal;
                    var successor = rows[i + 1];
                    active.OutcomeKnownAt = bar.Time.AddMinutes(1);
                    active.OutcomeTimeBasis = successor.HasValidOpen && successor.Index == bar.Index + 1 &&
                        successor.Time == active.OutcomeKnownAt ? "SuccessorMinuteStart" : "NominalCandleCloseNoContinuousSuccessor";
                    switch (terminal.Reason)
                    {
                        case ExitReason.Target: counts.Targets++; break;
                        case ExitReason.Stop: counts.Stops++; break;
                        case ExitReason.Timeout: counts.Timeouts++; break;
                    }
                    if (terminal.Ambiguous) counts.Ambiguous++;
                    counts.NetResolvedDollars += terminal.NetDollars;
                    tracker = null;
                    active = null;
                }
            }

            // Trend state still advances while a previous reference trade is active.
            var candidate = engine.Process(bar);
            if (candidate is null) continue;
            counts.Candidates++;
            if (tracker is not null)
            {
                counts.SuppressedActiveCandidates++;
                continue;
            }
            var next = rows[i + 1];
            if (!next.HasValidOpen || next.Index != row.Index + 1 || next.Time - row.Time != TimeSpan.FromMinutes(1) ||
                sessionBoundaryIndices?.Contains(next.Index) == true)
            {
                counts.UnavailableCandidates++;
                continue;
            }
            var plan = TradePlanner.Create(candidate, new BarOpen(next.Index, next.Time!.Value, next.Open!.Value), options);
            if (plan is null)
            {
                counts.RejectedPlans++;
                continue;
            }
            active = new ReplayTrade(plan);
            trades.Add(active);
            tracker = new TradeTracker(plan, options);
            counts.Entries++;
            counts.MaximumPlannedRiskDollars = Math.Max(counts.MaximumPlannedRiskDollars, plan.PlannedRiskDollars);
        }
        if (rows.Count > 0) EndUnresolved("EndOfFileFinalFormingExcluded", rows[^1]);
        coverage.Reset();
        counts.CompleteFiveMinuteGroups = coverage.Complete;
        counts.DiscardedPartialFiveMinuteGroups = coverage.Discarded;
        return new ReplayResult(counts, trades, issues);
    }

    private sealed class HigherCoverage
    {
        private DateTime? _group;
        private int _seen;
        private bool _eligible;
        private bool _completed;
        public int Complete { get; private set; }
        public int Discarded { get; private set; }

        public void Process(DateTime time)
        {
            var minute = time.Minute % 5;
            var group = time.AddMinutes(-minute);
            if (_group != group)
            {
                Reset();
                _group = group;
                _eligible = minute == 0;
            }
            if (_eligible && minute != _seen) _eligible = false;
            _seen++;
            if (_eligible && _seen == 5)
            {
                _completed = true;
                Complete++;
            }
        }

        public void Reset()
        {
            if (_seen > 0 && !_completed) Discarded++;
            _group = null;
            _seen = 0;
            _eligible = false;
            _completed = false;
        }
    }
}
