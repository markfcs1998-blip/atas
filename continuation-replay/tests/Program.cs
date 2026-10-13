using System.Globalization;
using NqContinuation.Replay;
using NqContinuation;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    checks++;
}

var first = ReplayCsv.ParseLine("2026-12-10 00:00:00;100.00;101.25;99.75;101.00", 1);
Check(first.IsValidClosed, "headerless semicolon OHLC parses");
Check(first.Index == 0 && first.LineNumber == 1, "physical line index is stable");
Check(first.Time == new DateTime(2026, 12, 10), "literal future December date preserved");
Check(first.Time?.Kind == DateTimeKind.Unspecified, "timezone not inferred");
Check(first.Open == 100m && first.High == 101.25m && first.Low == 99.75m && first.Close == 101m, "OHLC field ordering");
var culture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    Check(ReplayCsv.ParseLine("2026-07-10 00:00:00;100.25;101.25;99.75;100.75", 2).IsValidClosed, "invariant decimal point");
    Check(!ReplayCsv.ParseLine("2026-07-10 00:00:00;100,25;101.25;99.75;100.75", 2).HasValidOpen, "decimal comma rejected");
}
finally { CultureInfo.CurrentCulture = culture; }
var invalid = ReplayCsv.ParseLine("2026-12-10 00:01:00;100;99;101;100", 2);
Check(!invalid.IsValidClosed && invalid.HasValidOpen && invalid.Error is not null, "invalid OHLC is flagged but fixed open remains distinct");
Check(!ReplayCsv.ParseLine("2026-12-10 00:01:01;100;101;99;100", 2).HasValidOpen, "nonzero seconds rejected");
Check(!ReplayCsv.ParseLine("2026-12-10 00:01:00;0;101;0;100", 2).HasValidOpen, "nonpositive open rejected");
Check(!ReplayCsv.ParseLine("Date;Open;High;Low;Close", 1).IsValidClosed, "header rejected");
var rows = ReplayCsv.ReadRows(new StringReader("2026-12-10 00:00:00;100;101;99;100\n\n2026-12-10 00:02:00;100;101;99;100\n"));
Check(rows.Count == 3 && rows[1].Index == 1 && !rows[1].IsValidClosed && rows[2].Index == 2, "blank row not removed or reindexed");
var options = new ContinuationOptions { DataMode = DataMode.PriceOnly };
CsvRow Row(int index, int minute, decimal open = 100, decimal high = 101, decimal low = 99, decimal close = 100)
    // Synthetic pipeline scenarios begin at index 1, allowing an earlier setup index 0.
    => ReplayCsv.ParseLine($"2026-12-10 00:{minute + 1:D2}:00;{open.ToString(CultureInfo.InvariantCulture)};{high.ToString(CultureInfo.InvariantCulture)};{low.ToString(CultureInfo.InvariantCulture)};{close.ToString(CultureInfo.InvariantCulture)}", index + 2);
ContinuationCandidate Candidate(int index, decimal stop = 95) => new()
{
    Direction = TradeDirection.Long, SetupIndex = index, ConfirmationIndex = index + 1,
    ConfirmationTime = new DateTime(2026, 12, 10, 0, index + 1, 0), ConfirmationClose = 100,
    StopPrice = stop, TriggerPrice = 100, Evidence = FlowEvidence.None, EvidenceScore = 0
};

var sequential = new CandidateEngine(index => index <= 2 ? Candidate(index) : null);
var replay = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1), Row(2, 2, 110, 113, 109, 112), Row(3, 3) }, options, sequential);
Check(replay.Counts.Entries == 2 && replay.Counts.SuppressedActiveCandidates == 1, "existing tracker resolves before current engine candidate and only one position is active");
Check(sequential.ProcessedIndices.SequenceEqual(new[] { 0, 1, 2 }), "engine updates during active trade and excludes final forming row");
Check(replay.Trades[0].Plan.EntryIndex == 2 && replay.Trades[0].Plan.EntryPrice == 100.25m, "entry uses next fixed open plus tick slippage");
Check(replay.Counts.Targets == 1 && replay.Counts.Unresolved == 1 && replay.Counts.NetResolvedDollars == 250m, "resolved PnL excludes unresolved reference trade");
Check(replay.Counts.MaximumPlannedRiskDollars == 110m, "risk includes conservative stop slippage");
Check(replay.Trades[0].Exit?.ExitTime == new DateTime(2026, 12, 10, 0, 3, 0) &&
    replay.Trades[0].OutcomeKnownAt == new DateTime(2026, 12, 10, 0, 4, 0) && replay.Trades[0].OutcomeTimeBasis == "SuccessorMinuteStart", "exit candle start and known-at close are distinct");
using (var tradeWriter = new StringWriter(CultureInfo.InvariantCulture))
{
    ReplayCommand.WriteTrades(tradeWriter, replay.Trades);
    Check(tradeWriter.ToString().Contains("exit_candle_time,outcome_known_at,outcome_time_basis"), "CSV timestamps disclose closed-outcome availability");
}

var finalHigh = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1, 100, 999, 1, 100) }, options, new CandidateEngine(index => Candidate(index)));
Check(finalHigh.Counts.Targets == 0 && finalHigh.Counts.Stops == 0 && finalHigh.Counts.Unresolved == 1, "forming high/low cannot close an entered plan");
var finalMalformed = ReplayDriver.Run(new[] { Row(0, 0), ReplayCsv.ParseLine("2026-12-10 00:02:00;100;bad;99;100", 3) }, options, new CandidateEngine(index => Candidate(index)));
Check(finalMalformed.Counts.Entries == 1 && finalMalformed.Counts.InvalidRows == 1, "malformed future HLC does not gate fixed-open entry");
var finalMissingHlc = ReplayDriver.Run(new[] { Row(0, 0), ReplayCsv.ParseLine("2026-12-10 00:02:00;100", 3) }, options, new CandidateEngine(index => Candidate(index)));
Check(finalMissingHlc.Counts.Entries == 1 && finalMissingHlc.Trades[0].Plan.EntryPrice == finalHigh.Trades[0].Plan.EntryPrice, "missing forming HLC never gates fixed time/open");
var finalExtraHlc = ReplayDriver.Run(new[] { Row(0, 0), ReplayCsv.ParseLine("2026-12-10 00:02:00;100;101;99;100;extra", 3) }, options, new CandidateEngine(index => Candidate(index)));
Check(finalExtraHlc.Counts.Entries == 1 && finalExtraHlc.Counts.InvalidRows == 1, "extra future column does not gate fixed time/open");
var closeMalformed = ReplayDriver.Run(new[] { Row(0, 0), ReplayCsv.ParseLine("2026-12-10 00:02:00;100;bad;99;100", 3), Row(2, 2) }, options, new CandidateEngine(index => index == 0 ? Candidate(index) : null));
Check(closeMalformed.Counts.Entries == 1 && closeMalformed.Counts.Unresolved == 1 && closeMalformed.Trades[0].UnresolvedReason == "InvalidClosedRow", "malformed row ends trade unresolved when it closes");

var gapEngine = new CandidateEngine(index => index == 0 ? Candidate(index) : null);
var gap = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1), Row(2, 3), Row(3, 4) }, options, gapEngine);
Check(gap.Counts.Unresolved == 1 && gap.Counts.Targets == 0 && gap.Counts.Stops == 0 && gap.Trades[0].UnresolvedReason == "CadenceBoundary", "gap closes tracker unresolved without invented PnL");
Check(gapEngine.Resets == 1, "gap resets same candidate engine");
var unavailable = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 2) }, options, new CandidateEngine(index => Candidate(index)));
Check(unavailable.Counts.Entries == 0 && unavailable.Counts.UnavailableCandidates == 1, "nonconsecutive successor rejected");
var risk = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1, 110, 111, 109, 110) }, options, new CandidateEngine(index => Candidate(index)));
Check(risk.Counts.Entries == 0 && risk.Counts.RejectedPlans == 1, "next-open gap recomputes risk and rejects oversized plan");

var ambiguous = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1, 100, 114, 94, 100), Row(2, 2) }, options, new CandidateEngine(index => index == 0 ? Candidate(index) : null));
Check(ambiguous.Counts.Stops == 1 && ambiguous.Counts.Ambiguous == 1 && ambiguous.Counts.Targets == 0 && ambiguous.Counts.NetResolvedDollars == -110m, "same-bar target and stop use conservative stop first");
var fees = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1, 100, 114, 99, 113), Row(2, 2) },
    options with { RoundTripCostsDollars = 20 }, new CandidateEngine(index => index == 0 ? Candidate(index) : null));
Check(fees.Counts.Targets == 1 && fees.Counts.NetResolvedDollars == 250m && fees.Counts.MaximumPlannedRiskDollars == 130m &&
    fees.Trades[0].Plan.TargetLower == 111.25m && fees.Trades[0].Plan.TargetPrimary == 113.75m && fees.Trades[0].Plan.TargetUpper == 116.25m,
    "round-trip costs enter planned risk and each desired net target exactly once");
var noContinuousSuccessor = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1, 100, 113, 99, 112), Row(2, 4) }, options,
    new CandidateEngine(index => index == 0 ? Candidate(index) : null));
Check(noContinuousSuccessor.Counts.Targets == 1 && noContinuousSuccessor.Trades[0].OutcomeKnownAt == new DateTime(2026, 12, 10, 0, 3, 0) &&
    noContinuousSuccessor.Trades[0].OutcomeTimeBasis == "NominalCandleCloseNoContinuousSuccessor", "missing successor labels nominal outcome close rather than inventing execution time");
var stopGap = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1), Row(2, 2, 90, 91, 89, 90), Row(3, 3) }, options, new CandidateEngine(index => index == 0 ? Candidate(index) : null));
Check(stopGap.Counts.Stops == 1 && stopGap.Trades[0].Exit?.ExitPrice == 89.75m && stopGap.Counts.NetResolvedDollars == -210m, "adverse stop gap can exceed planned cap");
var timeoutRows = Enumerable.Range(0, 17).Select(i => Row(i, i)).ToArray();
var timeout = ReplayDriver.Run(timeoutRows, options, new CandidateEngine(index => index == 0 ? Candidate(index) : null));
Check(timeout.Counts.Timeouts == 1 && timeout.Trades[0].Exit?.ExitIndex == 16 && timeout.Counts.NetResolvedDollars == -5m, "15 entry-and-following closed bars timeout at close");

var session = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1), Row(2, 2), Row(3, 3) }, options,
    new CandidateEngine(index => index == 0 ? Candidate(index) : null), new HashSet<int> { 3 });
Check(session.Counts.Unresolved == 1 && session.Trades[0].UnresolvedReason == "SessionBoundary", "explicit session metadata ends tracker unresolved");
var sessionNext = ReplayDriver.Run(new[] { Row(0, 0), Row(1, 1) }, options, new CandidateEngine(index => Candidate(index)), new HashSet<int> { 2 });
Check(sessionNext.Counts.Entries == 0 && sessionNext.Counts.UnavailableCandidates == 1, "session successor unavailable");
var midnightRows = ReplayCsv.ReadRows(new StringReader("2026-12-10 23:59:00;100;101;99;100\n2026-12-11 00:00:00;100;101;99;100\n2026-12-11 00:01:00;100;101;99;100\n"));
var midnightEngine = new CandidateEngine(_ => null);
var midnight = ReplayDriver.Run(midnightRows, options, midnightEngine);
Check(midnight.Counts.BoundaryResets == 0 && midnightEngine.Resets == 0, "normal date rollover has no inferred exchange-session reset");

var coverageRows = Enumerable.Range(0, 106).Select(i => ReplayCsv.ParseLine($"{new DateTime(2026, 12, 10).AddMinutes(i).ToString(ReplayCsv.TimestampFormat, CultureInfo.InvariantCulture)};100;101;99;100", i + 1)).ToArray();
var coverage = ReplayDriver.Run(coverageRows, options, new CandidateEngine(_ => null));
Check(coverage.Counts.CompleteFiveMinuteGroups == 21 && coverage.Counts.WarmupNominalMinutes == 105 && coverage.Counts.HigherSlowGroupsRequired == 21, "105 closed minutes contain required 21 completed higher groups");
var shortCoverage = ReplayDriver.Run(coverageRows.Take(105).ToArray(), options, new CandidateEngine(_ => null));
Check(shortCoverage.Counts.CompleteFiveMinuteGroups == 20, "forming fifth minute cannot complete higher interval");
var partialCoverage = ReplayDriver.Run(coverageRows.Skip(2).ToArray(), options, new CandidateEngine(_ => null));
Check(partialCoverage.Counts.CompleteFiveMinuteGroups == 20, "partial leading higher group discarded");
var fiveMinuteRows = Enumerable.Range(0, 22).Select(i => ReplayCsv.ParseLine($"{new DateTime(2026, 12, 10).AddMinutes(i * 5).ToString(ReplayCsv.TimestampFormat, CultureInfo.InvariantCulture)};100;101;99;100", i + 1)).ToArray();
var fiveMinute = ReplayDriver.Run(fiveMinuteRows, options, new CandidateEngine(_ => null));
Check(fiveMinute.Counts.OneMinuteIntervals == 0 && fiveMinute.Counts.CadenceBreaks == 21 && fiveMinute.Counts.CompleteFiveMinuteGroups == 0, "five-minute export never masquerades as one-minute closed groups");

var temp = Path.Combine(Path.GetTempPath(), "nq-continuation-replay-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var input = Path.Combine(temp, "fixture.csv");
    var output = Path.Combine(temp, "trades.csv");
    File.WriteAllText(input, "2026-12-10 00:00:00;100;101;99;100\n2026-12-10 00:01:00;100;101;99;100\n");
    Check(ReplayCommand.Run(new[] { "--replay", input, "--out", output }, TextWriter.Null, TextWriter.Null) == 0, "valid CLI uses untouched NQ defaults");
    Check(File.Exists(output) && File.Exists(Path.ChangeExtension(output, ".summary.json")), "CLI writes trades and explicit-assumption summary");
    var summary = File.ReadAllText(Path.ChangeExtension(output, ".summary.json"));
    Check(summary.Contains("PriceOnly") && summary.Contains("250") && summary.Contains("150") && summary.Contains("105") && summary.Contains("assumption", StringComparison.OrdinalIgnoreCase), "summary discloses valuation, defaults and warmup");
    Check(ReplayCommand.Run(new[] { "--replay", input, "--out", output, "--costs", "12.50" }, TextWriter.Null, TextWriter.Null) == 0, "explicit nonnegative round-trip cost supported");
    Check(ReplayCommand.Run(new[] { "--replay", input, "--out", output, "--costs", "-1" }, TextWriter.Null, TextWriter.Null) == 2, "negative costs are usage error");
    Check(ReplayCommand.Run(new[] { "--replay", input, "--out", output, "--target", "300" }, TextWriter.Null, TextWriter.Null) == 2, "fixture tuning options rejected");
    Check(ReplayCommand.Run(new[] { "--replay", Path.Combine(temp, "missing.csv"), "--out", output }, TextWriter.Null, TextWriter.Null) == 1, "missing file is operational error");
    Check(ReplayCommand.Run(new[] { "--replay", "bad\0path", "--out", output }, TextWriter.Null, TextWriter.Null) == 2, "malformed path is usage error without unhandled exception");
    var summaryInput = Path.ChangeExtension(output, ".summary.json");
    File.WriteAllText(summaryInput, File.ReadAllText(input));
    var originalSummaryInput = File.ReadAllText(summaryInput);
    Check(ReplayCommand.Run(new[] { "--replay", summaryInput, "--out", output }, TextWriter.Null, TextWriter.Null) == 2 &&
        File.ReadAllText(summaryInput) == originalSummaryInput, "derived summary destination cannot overwrite source input");
    File.WriteAllText(input, "2026-12-10 00:00:00;100;101;99;100\n2026-12-10 00:05:00;100;101;99;100\n");
    Check(ReplayCommand.Run(new[] { "--replay", input, "--out", output }, TextWriter.Null, TextWriter.Null) == 3, "five-minute input has cadence rejection exit code");
    File.WriteAllText(input, "2026-12-10 00:00:00;100;101;99;100\n2026-12-10 00:01:00;0;101;0;100\n2026-12-10 00:05:00;100;101;99;100\n");
    Check(ReplayCommand.Run(new[] { "--replay", input, "--out", output }, TextWriter.Null, TextWriter.Null) == 3, "invalid open cannot provide sole usable one-minute interval");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine($"PASS: {checks} replay parser/pipeline/CLI behavioral checks.");

sealed class CandidateEngine(Func<int, ContinuationCandidate?> candidate) : IReplayEngine
{
    public List<int> ProcessedIndices { get; } = new();
    public int Resets { get; private set; }
    public ContinuationCandidate? Process(MarketBar bar) { ProcessedIndices.Add(bar.Index - 1); return candidate(bar.Index - 1); }
    public void Reset() => Resets++;
}
