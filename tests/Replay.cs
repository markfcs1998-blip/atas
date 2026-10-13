using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PivotReversal;

namespace PivotReversal.Tests;

/// <summary>Headerless OHLC replay. Physical row order and literal timestamps are preserved.</summary>
public static class Replay
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly TimeSpan DefaultMaximumGap = TimeSpan.FromMinutes(20);

    /// <returns>0 on completed replay, 2 on invalid usage, 1 on file/runtime failure.</returns>
    public static int Run(string[] args)
    {
        if (!TryParseArguments(args, out var command, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        try
        {
            var parsed = command!;
            List<CsvRow> rows;
            using (var reader = File.OpenText(parsed.InputFile)) rows = ReadRows(reader);
            var options = new EngineOptions { TickSize = parsed.TickSize, OrderFlowMode = OrderFlowMode.PriceOnly };
            var result = ReplayRows(rows, options);
            Directory.CreateDirectory(Path.GetDirectoryName(parsed.OutputFile)!);
            using (var writer = new StreamWriter(parsed.OutputFile)) WriteSignalsCsv(writer, result.Signals);

            var summaryPath = Path.ChangeExtension(parsed.OutputFile, "summary.json");
            var valid = rows.Where(row => row.Candle is not null).ToArray();
            var summary = new
            {
                SourceFile = parsed.InputFile,
                OutputFile = parsed.OutputFile,
                Mode = "PriceOnly",
                TickSize = parsed.TickSize,
                Parameters = options,
                Counts = result.Summary,
                FirstValidTimestamp = valid.FirstOrDefault()?.Candle?.Time.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                LastValidTimestamp = valid.LastOrDefault()?.Candle?.Time.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                FinalPhysicalRowValid = rows.Count > 0 && rows[^1].Candle is not null,
                Issues = result.Issues,
                Limitations = new[]
                {
                    "OHLC-only export: PriceOnly bypasses order-flow evidence; volume, bid/ask, delta, and footprint evidence are unavailable.",
                    "The supplied tick size is an explicit replay fixture; instrument identity and exchange timezone are not inferred.",
                    "Timestamps are preserved literally as yyyy-MM-dd HH:mm:ss with DateTimeKind.Unspecified; apparent date gaps are not repaired.",
                    "The final physical row is excluded from engine processing. A valid continuous successor is required for signal availability; unavailable confirmations are dropped.",
                    "Confirmation close and stop reference do not model an entry fill, execution cost, stop order, or profitability.",
                    "Invalid-row counts include malformed final rows; excluded-forming rows count the final physical row independently. Boundary resets follow the effective MaxGapMinutes parameter."
                }
            };
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, jsonOptions) + Environment.NewLine);
            var counts = result.Summary;
            Console.WriteLine($"Replay PriceOnly (explicit tick={parsed.TickSize.ToString(CultureInfo.InvariantCulture)}): " +
                $"rows={counts.Rows}, processed={counts.Processed}, invalid={counts.InvalidRows}, " +
                $"gap_resets={counts.GapResets}, excluded_forming={counts.ExcludedFormingRows}, " +
                $"confirmed={counts.ConfirmedSignals}, signals={counts.Signals}, dropped_unavailable={counts.DroppedUnavailableSignals}.");
            Console.WriteLine("CSV: " + parsed.OutputFile);
            Console.WriteLine("Summary: " + summaryPath);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("Replay failed: " + failure.Message);
            return 1;
        }
    }

    public static void RunParserTests()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Replay parser test failed: " + name);
            checks++;
        }

        var row = ParseLine("2026-12-10 00:00:00;31145.50;31157.25;31124.50;31127.00", 1);
        Check(row.Candle is not null && row.Error is null, "headerless semicolon row is accepted");
        Check(row.LineNumber == 1 && row.Index == 0, "physical line supplies stable zero-based index");
        Check(row.Candle!.Time == new DateTime(2026, 12, 10), "literal ISO date is preserved");
        Check(row.Candle.Time.Kind == DateTimeKind.Unspecified, "CSV timestamp has no inferred timezone");
        Check(row.Candle.Open == 31145.50m && row.Candle.High == 31157.25m &&
              row.Candle.Low == 31124.50m && row.Candle.Close == 31127m, "OHLC column order is preserved");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(ParseLine("2026-07-10 00:05:00;1.25;2.25;1.00;2.00", 2).Candle?.Time.Month == 7,
                "parsing ignores current culture and preserves July literal");
            Check(ParseLine("2026-07-10 00:05:00;1,25;2.25;1.00;2.00", 2).Candle is null,
                "locale decimal comma is rejected");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }

        foreach (var invalid in new[]
        {
            "", "time;open;high;low;close", "2026-12-10 00:00:00,1,2,0,1",
            "2026-12-10 00:00:00;1;2;0", "2026-12-10 00:00:00;1;2;0;1;3",
            "2026-13-10 00:00:00;1;2;0;1", "10/12/2026 00:00:00;1;2;0;1",
            "2026-12-10T00:00:00Z;1;2;0;1", "2026-12-10 00:00:00;NaN;2;0;1",
            "2026-12-10 00:00:00;1;Infinity;0;1", "2026-12-10 00:00:00;1;2;0;1e0",
            "2026-12-10 00:00:00;79228162514264337593543950336;2;0;1",
            "2026-12-10 00:00:00;3;2;0;1", "2026-12-10 00:00:00;1;2;0;3",
            "2026-12-10 00:00:00;1;0;2;1",
            "2026-09-10 22:50:00;31133.75;31139.75;31137.00;31127.75"
        })
        {
            var bad = ParseLine(invalid, 827);
            Check(bad.Candle is null && !string.IsNullOrWhiteSpace(bad.Error) && bad.LineNumber == 827,
                "invalid row is diagnosed with physical line: " + invalid);
        }

        Check(ParseLine("2026-12-10 00:00:00;-1;0;-2;-1", 1).Candle is not null,
            "valid negative prices are not rejected by instrument assumptions");
        Check(ParseLine("2026-12-10 00:00:00;1;1;1;1", 1).Candle is not null,
            "flat valid candle is accepted for engine handling");

        var input = ReadRows(new StringReader("2026-12-10 00:00:00;1;2;0;1\n\n2026-12-10 00:02:00;1;2;0;1\n"));
        Check(input.Count == 3 && input[1].Candle is null && input[2].Index == 2,
            "blank malformed physical row is retained as a reset boundary");
        Check(ReadRows(new StringReader("")).Count == 0, "empty file has zero rows");
        Check(ReadRows(new StringReader("2026-12-10 00:00:00;1;2;0;1\r\n")).Count == 1,
            "CRLF and terminal newline do not add a forming row");

        var before = ParseLine("2026-12-10 00:00:00;1;2;0;1", 1);
        Check(IsSuccessor(before, ParseLine("2026-12-10 00:01:00;1;2;0;1", 2), DefaultMaximumGap),
            "valid adjacent successor makes a prior confirmation available");
        Check(IsSuccessor(before, ParseLine("2026-12-10 00:20:00;1;2;0;1", 2), DefaultMaximumGap),
            "twenty-minute boundary is allowed by documented reset policy");
        Check(!IsSuccessor(before, ParseLine("2026-12-10 00:20:01;1;2;0;1", 2), DefaultMaximumGap),
            "gap over twenty minutes blocks signal delivery");
        Check(!IsSuccessor(before, ParseLine("2026-12-10 00:00:00;1;2;0;1", 2), DefaultMaximumGap),
            "duplicate timestamp blocks signal delivery");
        Check(!IsSuccessor(before, ParseLine("2026-12-09 23:59:00;1;2;0;1", 2), DefaultMaximumGap),
            "decreasing timestamp blocks signal delivery");
        Check(!IsSuccessor(before, ParseLine("2026-12-10 00:01:00;1;2;0;1", 3), DefaultMaximumGap),
            "nonconsecutive physical index blocks stale signal delivery");
        Check(!IsSuccessor(before, ParseLine("malformed", 2), DefaultMaximumGap),
            "malformed successor blocks signal delivery");
        Check(!IsSuccessor(ParseLine("malformed", 1), before, DefaultMaximumGap),
            "malformed predecessor blocks signal delivery");
        Check(IsSuccessor(before, ParseLine("2026-12-10 00:01:00;1;2;0;1", 2), DefaultMaximumGap),
            "forming final row may provide availability without engine processing");

        var options = new EngineOptions
        {
            TickSize = 1, SwingLeft = 1, SwingRight = 1, AtrPeriod = 2, VolumePeriod = 2,
            MinRangeAtr = .1m, MaxRangeAtr = 20, OrderFlowMode = OrderFlowMode.PriceOnly
        };
        var fixture = new[]
        {
            "2026-01-01 09:00:00;12;13;11;12", "2026-01-01 09:01:00;12;13;10;12",
            "2026-01-01 09:02:00;12;14;11;13", "2026-01-01 09:03:00;11;14;8;13",
            "2026-01-01 09:04:00;14;16;11;15", "2026-01-01 09:05:00;15;16;14;15"
        };
        ReplayResult ReplayFixture(IEnumerable<string> lines) => ReplayRows(
            ReadRows(new StringReader(string.Join('\n', lines))), options);

        var replay = ReplayFixture(fixture);
        Check(replay.Summary.Rows == 6 && replay.Summary.Processed == 5 && replay.Summary.ExcludedFormingRows == 1,
            "last physical candle is never processed");
        Check(replay.Signals.Count == 1 && replay.Summary.ConfirmedSignals == 1 && replay.Summary.DroppedUnavailableSignals == 0,
            "closed confirmation produces one available replay signal");
        Check(replay.Signals[0].PivotTime == new DateTime(2026, 1, 1, 9, 1, 0) &&
              replay.Signals[0].SetupTime == new DateTime(2026, 1, 1, 9, 3, 0) &&
              replay.Signals[0].ConfirmationTime == new DateTime(2026, 1, 1, 9, 4, 0) &&
              replay.Signals[0].AvailableAt == new DateTime(2026, 1, 1, 9, 5, 0),
            "signal report separates pivot/setup/confirmation from successor availability");
        Check(replay.Signals[0].Signal.ConfirmationClose == 15 && replay.Signals[0].Signal.StopPrice == 7,
            "replay reports engine confirmation close and reference stop");

        var withoutSuccessor = ReplayFixture(fixture.Take(5));
        Check(withoutSuccessor.Summary.Processed == 4 && withoutSuccessor.Signals.Count == 0,
            "confirmation in forming physical row cannot emit a signal");
        var badSuccessor = ReplayFixture(fixture.Take(5).Append("malformed"));
        Check(badSuccessor.Signals.Count == 0 && badSuccessor.Summary.ConfirmedSignals == 1 &&
              badSuccessor.Summary.DroppedUnavailableSignals == 1 && badSuccessor.Summary.InvalidRows == 1,
            "malformed successor drops pending signal instead of attaching stale availability");
        var gapSuccessor = ReplayFixture(fixture.Take(5).Append("2026-01-01 09:25:00;15;16;14;15"));
        Check(gapSuccessor.Signals.Count == 0 && gapSuccessor.Summary.GapResets == 1 &&
              gapSuccessor.Summary.DroppedUnavailableSignals == 1,
            "gap successor drops pending signal and records reset");
        var duplicateSuccessor = ReplayFixture(fixture.Take(5).Append("2026-01-01 09:04:00;15;16;14;15"));
        Check(duplicateSuccessor.Signals.Count == 0 && duplicateSuccessor.Summary.GapResets == 1 &&
              duplicateSuccessor.Summary.DroppedUnavailableSignals == 1,
            "duplicate successor timestamp drops pending signal");
        var invalidBetween = ReplayFixture(fixture.Take(4).Append("malformed").Concat(fixture.Skip(4)));
        Check(invalidBetween.Signals.Count == 0 && invalidBetween.Summary.InvalidRows == 1,
            "malformed candle between setup and confirmation resets the engine");
        var invalidOhlcBetween = ReplayFixture(fixture.Take(4).Append("2026-01-01 09:03:30;12;10;11;12").Concat(fixture.Skip(4)));
        Check(invalidOhlcBetween.Signals.Count == 0 && invalidOhlcBetween.Summary.InvalidRows == 1,
            "invalid OHLC between setup and confirmation resets the engine");
        var validAfterGap = ReplayFixture(fixture.Take(4).Concat(fixture.Skip(4).Select(line =>
            line.Replace("09:04:00", "09:25:00").Replace("09:05:00", "09:26:00"))));
        Check(validAfterGap.Signals.Count == 0 && validAfterGap.Summary.GapResets == 1 && validAfterGap.Summary.Processed == 5,
            "first valid closed candle after a gap is processed with fresh state");
        var oneRow = ReplayFixture(fixture.Take(1));
        Check(oneRow.Summary.Rows == 1 && oneRow.Summary.Processed == 0 && oneRow.Summary.ExcludedFormingRows == 1,
            "one-row input contains only a forming candle");
        var empty = ReplayFixture(Array.Empty<string>());
        Check(empty.Summary.Rows == 0 && empty.Summary.Processed == 0 && empty.Summary.ExcludedFormingRows == 0,
            "empty replay has no forming candle or signals");

        Check(TryParseArguments(["--replay", "fixture.csv", "--tick-size", ".25", "--out", "signals.csv"],
                out var command, out var argumentError) && command?.TickSize == .25m && argumentError is null,
            "CLI requires and parses an explicitly supplied invariant tick size");
        foreach (var invalidArguments in new[]
        {
            new[] { "--replay", "fixture.csv", "--out", "signals.csv" },
            new[] { "--replay", "fixture.csv", "--tick-size", "0", "--out", "signals.csv" },
            new[] { "--replay", "fixture.csv", "--tick-size", "-.25", "--out", "signals.csv" },
            new[] { "--replay", "fixture.csv", "--tick-size", "0,25", "--out", "signals.csv" },
            new[] { "--replay", "fixture.csv", "--tick-size", "NaN", "--out", "signals.csv" },
            new[] { "--replay", "fixture.csv", "--tick-size", ".25", "--out", "fixture.csv" },
            new[] { "--replay", "fixture.csv", "--tick-size", ".25", "--out", "" },
            new[] { "--replay", "fixture.csv", "--tick-size", ".25", "--unknown", "signals.csv" },
            new[] { "--replay", "fixture.csv", "--replay", "other.csv", "--out", "signals.csv" },
            new[] { "--replay", "fixture.csv", "--tick-size", ".25", "--out" }
        })
            Check(!TryParseArguments(invalidArguments, out _, out var error) && !string.IsNullOrWhiteSpace(error),
                "invalid CLI arguments produce an actionable usage error: " + string.Join(' ', invalidArguments));

        using var csvWriter = new StringWriter(CultureInfo.GetCultureInfo("fr-FR"));
        WriteSignalsCsv(csvWriter,
            [replay.Signals[0] with { Signal = replay.Signals[0].Signal with
                { ConfirmationClose = 15.25m, StopPrice = 7.25m, Evidence = EvidenceFlags.RelativeVolume | EvidenceFlags.OpposingDelta } }]);
        var csv = csvWriter.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(csv.Length == 2 && csv[0].Contains("confirmation_close") && csv[0].Contains("available_at") &&
              csv[1].Contains("2026-01-01 09:04:00,2026-01-01 09:05:00,15.25,10,7.25"),
            "CSV writer preserves causal timestamps and invariant numeric formatting");
        Check(csv[0].Split(',').Length == csv[1].Split(',').Length && csv[1].Contains("RelativeVolume|OpposingDelta"),
            "multiple evidence flags remain within a single CSV column");

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var directory = Path.Combine(Path.GetTempPath(), "PivotReversalReplayTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Console.SetOut(new StringWriter(CultureInfo.InvariantCulture));
            Console.SetError(new StringWriter(CultureInfo.InvariantCulture));
            Check(Run(["--replay", "fixture.csv", "--out", "signals.csv"]) == 2,
                "CLI returns two for missing explicit tick usage");
            Check(Run(["--replay", Path.Combine(directory, "missing.csv"), "--tick-size", ".25", "--out",
                    Path.Combine(directory, "missing.signals.csv")]) == 1,
                "CLI returns one for file IO failure");
            Directory.CreateDirectory(directory);
            var sourcePath = Path.Combine(directory, "fixture.csv");
            var outputPath = Path.Combine(directory, "fixture.signals.csv");
            var source = fixture[0] + "\nmalformed\n" + fixture[2] + "\n";
            File.WriteAllText(sourcePath, source);
            Check(Run(["--replay", sourcePath, "--tick-size", ".25", "--out", outputPath]) == 0,
                "CLI succeeds while diagnosing invalid rows and excluding forming row");
            Check(File.ReadAllText(sourcePath) == source && File.ReadAllLines(outputPath).Length == 1,
                "CLI preserves source and emits a standard CSV header when there are no signals");
            using var summaryJson = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(outputPath, "summary.json")));
            var root = summaryJson.RootElement;
            var counts = root.GetProperty("Counts");
            Check(root.GetProperty("Mode").GetString() == "PriceOnly" && root.GetProperty("TickSize").GetDecimal() == .25m &&
                  counts.GetProperty("Rows").GetInt32() == 3 && counts.GetProperty("Processed").GetInt32() == 1 &&
                  counts.GetProperty("InvalidRows").GetInt32() == 1 && counts.GetProperty("ExcludedFormingRows").GetInt32() == 1,
                "adjacent JSON declares explicit fixture tick, PriceOnly mode, and row counts");
            Check(root.GetProperty("Issues")[0].GetProperty("LineNumber").GetInt32() == 2 &&
                  root.GetProperty("Limitations").GetArrayLength() >= 3,
                "JSON includes physical-row diagnostics and replay coverage limitations");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        Console.WriteLine($"Replay parser: {checks} assertions passed.");
    }

    private sealed record Candle(DateTime Time, decimal Open, decimal High, decimal Low, decimal Close);
    private sealed record CsvRow(int LineNumber, Candle? Candle, string? Error)
    {
        public int Index => LineNumber - 1;
    }

    private sealed record ReplaySignal(ReversalSignal Signal, DateTime PivotTime, DateTime SetupTime,
        DateTime ConfirmationTime, DateTime AvailableAt);
    private sealed class ReplaySummary
    {
        public int Rows { get; set; }
        public int Processed { get; set; }
        public int InvalidRows { get; set; }
        public int GapResets { get; set; }
        public int ExcludedFormingRows { get; set; }
        public int ConfirmedSignals { get; set; }
        public int Signals { get; set; }
        public int DroppedUnavailableSignals { get; set; }
    }
    private sealed record ReplayIssue(int LineNumber, string Kind, string Detail);
    private sealed record ReplayResult(ReplaySummary Summary, List<ReplaySignal> Signals, List<ReplayIssue> Issues);
    private sealed record ReplayArguments(string InputFile, decimal TickSize, string OutputFile);

    private static bool TryParseArguments(string[] args, out ReplayArguments? command, out string? error)
    {
        command = null;
        error = "Use --replay FILE --tick-size POSITIVE_DECIMAL --out FILE.";
        if (args.Length != 6) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--replay" or "--tick-size" or "--out") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1]))
                return false;
        }
        if (!values.TryGetValue("--replay", out var input) || !values.TryGetValue("--out", out var output) ||
            !values.TryGetValue("--tick-size", out var tickText)) return false;
        if (!decimal.TryParse(tickText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var tickSize) || tickSize <= 0)
        {
            error = "--tick-size must be an explicitly supplied positive invariant decimal (for example .25).";
            return false;
        }
        try
        {
            input = Path.GetFullPath(input);
            output = Path.GetFullPath(output);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(input, output, comparison) ||
                string.Equals(input, Path.ChangeExtension(output, "summary.json"), comparison))
            {
                error = "Input CSV must differ from both output report paths.";
                return false;
            }
        }
        catch (ArgumentException pathError)
        {
            error = "Invalid input or output path: " + pathError.Message;
            return false;
        }
        command = new(input, tickSize, output);
        error = null;
        return true;
    }

    private static void WriteSignalsCsv(TextWriter writer, IReadOnlyList<ReplaySignal> signals)
    {
        writer.WriteLine("direction,pivot_index,pivot_time,setup_index,setup_time,confirmation_index,confirmation_time," +
            "available_at,confirmation_close,reference_swing,stop_reference,evidence_score,evidence_flags");
        foreach (var row in signals)
        {
            var signal = row.Signal;
            writer.WriteLine(string.Join(',', signal.Direction.ToString(),
                signal.PivotIndex.ToString(CultureInfo.InvariantCulture), row.PivotTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                signal.SetupIndex.ToString(CultureInfo.InvariantCulture), row.SetupTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                signal.ConfirmationIndex.ToString(CultureInfo.InvariantCulture), row.ConfirmationTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                row.AvailableAt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                signal.ConfirmationClose.ToString(CultureInfo.InvariantCulture), signal.ReferenceSwing.ToString(CultureInfo.InvariantCulture),
                signal.StopPrice.ToString(CultureInfo.InvariantCulture), signal.EvidenceScore.ToString(CultureInfo.InvariantCulture),
                signal.Evidence.ToString().Replace(", ", "|")));
        }
    }

    private static ReplayResult ReplayRows(IReadOnlyList<CsvRow> rows, EngineOptions options)
    {
        var summary = new ReplaySummary { Rows = rows.Count, ExcludedFormingRows = rows.Count == 0 ? 0 : 1 };
        var signals = new List<ReplaySignal>();
        var issues = new List<ReplayIssue>();
        var engine = new PivotReversalEngine(options);
        var maximumGap = TimeSpan.FromMinutes(options.MaxGapMinutes);
        CsvRow? preceding = null;
        ReversalSignal? pending = null;

        void DropPending()
        {
            if (pending is not null) summary.DroppedUnavailableSignals++;
            pending = null;
        }

        foreach (var row in rows)
        {
            if (row.Candle is not { } candle)
            {
                summary.InvalidRows++;
                issues.Add(new(row.LineNumber, "InvalidRow", row.Error!));
                DropPending();
                engine.Reset();
                preceding = null;
                continue;
            }

            if (preceding is not null && !IsSuccessor(preceding, row, maximumGap))
            {
                summary.GapResets++;
                issues.Add(new(row.LineNumber, "BoundaryReset",
                    $"Nonconsecutive index, nonincreasing time, or gap over {options.MaxGapMinutes} minutes: " +
                    $"{preceding.Candle!.Time.ToString(TimestampFormat, CultureInfo.InvariantCulture)} -> " +
                    candle.Time.ToString(TimestampFormat, CultureInfo.InvariantCulture)));
                DropPending();
                engine.Reset();
            }
            else if (pending is { } signal)
            {
                signals.Add(new(signal, rows[signal.PivotIndex].Candle!.Time,
                    rows[signal.SetupIndex].Candle!.Time, rows[signal.ConfirmationIndex].Candle!.Time, candle.Time));
                pending = null;
            }

            // The successor can be forming: its timestamp establishes availability, but its OHLC is never processed.
            if (row.Index < rows.Count - 1)
            {
                pending = engine.Process(new BarData(row.Index, candle.Time, candle.Open, candle.High, candle.Low, candle.Close));
                summary.Processed++;
                if (pending is not null) summary.ConfirmedSignals++;
            }
            preceding = row;
        }
        DropPending();
        summary.Signals = signals.Count;
        return new(summary, signals, issues);
    }

    private static CsvRow ParseLine(string line, int lineNumber)
    {
        var fields = line.Split(';');
        if (fields.Length != 5)
            return new(lineNumber, null, "Expected five semicolon-delimited fields (time;open;high;low;close).");
        if (!DateTime.TryParseExact(fields[0], TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return new(lineNumber, null, "Timestamp must match yyyy-MM-dd HH:mm:ss; no timezone is inferred.");
        var numbers = new decimal[4];
        for (var i = 0; i < numbers.Length; i++)
            if (!decimal.TryParse(fields[i + 1], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out numbers[i]))
                return new(lineNumber, null, $"Invalid invariant decimal in OHLC field {i + 1}.");
        var candle = new Candle(time, numbers[0], numbers[1], numbers[2], numbers[3]);
        if (candle.High < candle.Low || candle.Open < candle.Low || candle.Open > candle.High ||
            candle.Close < candle.Low || candle.Close > candle.High)
            return new(lineNumber, null, "Invalid OHLC: low <= open/close <= high is required.");
        return new(lineNumber, candle, null);
    }

    private static List<CsvRow> ReadRows(TextReader reader)
    {
        var result = new List<CsvRow>();
        while (reader.ReadLine() is { } line)
            result.Add(ParseLine(line, result.Count + 1));
        return result;
    }

    private static bool IsSuccessor(CsvRow before, CsvRow after, TimeSpan maximumGap) =>
        before.Candle is { } earlier && after.Candle is { } later &&
        after.Index == before.Index + 1 && later.Time > earlier.Time && later.Time - earlier.Time <= maximumGap;
}
