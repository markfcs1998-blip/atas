using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NqContinuation.Replay;

public static class ReplayCommand
{
    public const string Usage = "Usage: --replay FILE --out FILE [--costs NONNEGATIVE_DOLLARS]";

    /// <returns>0 completed; 1 operational failure; 2 invalid usage; 3 no usable one-minute cadence.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h") { output.WriteLine(Usage); return 0; }
        if (!TryArguments(args, out var inputPath, out var outputPath, out var costs, out var message))
        {
            error.WriteLine(message);
            error.WriteLine(Usage);
            return 2;
        }
        try
        {
            using var reader = File.OpenText(inputPath!);
            var rows = ReplayCsv.ReadRows(reader);
            var options = new ContinuationOptions { DataMode = DataMode.PriceOnly, RoundTripCostsDollars = costs };
            var replay = ReplayDriver.Run(rows, options);
            var csvPath = Path.GetFullPath(outputPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            using (var writer = new StreamWriter(csvPath)) WriteTrades(writer, replay.Trades);
            var summaryPath = Path.ChangeExtension(csvPath, ".summary.json");
            var usable = replay.Counts.UsableSuccessorIntervals > 0 && replay.Counts.ProcessedBars > 0;
            var summary = new
            {
                InputFile = Path.GetFullPath(inputPath!), OutputFile = csvPath,
                Status = usable ? "CompletedUnderExplicitNqAssumptions" : "RejectedNoOneMinuteCadence",
                Parameters = options, replay.Counts,
                FirstLiteralTimestamp = rows.FirstOrDefault(row => row.Time is not null)?.Time?.ToString(ReplayCsv.TimestampFormat, CultureInfo.InvariantCulture),
                LastLiteralTimestamp = rows.LastOrDefault(row => row.Time is not null)?.Time?.ToString(ReplayCsv.TimestampFormat, CultureInfo.InvariantCulture),
                FinalPhysicalRowValidClosedOhlc = rows.LastOrDefault()?.IsValidClosed ?? false,
                Issues = replay.Issues,
                Assumptions = new[]
                {
                    "Instrument identity is unavailable in CSV. All dollar values assume one full-size NQ contract, point value $20, tick 0.25 ($5); this is an explicit fixture valuation assumption.",
                    "PriceOnly explicitly bypasses order-flow evidence unavailable in this headerless OHLC export. Strategy defaults remain unchanged; no fixture tuning is performed.",
                    "Primary desired net target $250, alternative desired targets $200/$300, maximum planned risk $150, minimum reward/risk 1.5; entry slippage one tick and stop slippage one tick; configured round-trip costs are charged once per resolved trade.",
                    "Entries use only the following physical row's fixed Index/Time/Open after a valid closed confirmation. Future High/Low/Close and future OHLC validity never gate the entry.",
                    "Only fully completed consecutive clock-aligned five-minute groups feed higher bias. Default 21-group higher slow warmup requires 105 closed minutes when starting on a complete group boundary.",
                    "The final physical row is forming: its High/Low/Close are excluded from the engine and exit evaluation. Unfinished plans are reported unresolved with no invented PnL.",
                    "Both target and stop touched in one closed candle are resolved stop first. Adverse stop gaps use open plus stop slippage and can exceed the planned loss cap. Targets fill at their target price; timeouts close after 15 closed entry-and-following candles.",
                    "exit_candle_time is the exit candle's start stamp, not an intrabar execution time. outcome_known_at is its following consecutive fixed-open minute stamp; when that successor is missing, the nominal candle close is marked explicitly by outcome_time_basis.",
                    "Invalid closed rows and any cadence other than exactly one minute reset state and end active references unresolved. Exchange session metadata is absent; dates do not imply session boundaries.",
                    "Literal yyyy-MM-dd dates and DateTimeKind.Unspecified are preserved, including future labels. No timezone, instrument symbol, contract rollover or timestamp correction is inferred.",
                    "NetResolvedDollars summarizes only resolved hypothetical reference trades under these assumptions. OHLC has no intrabar ordering, executable quotes or liquidity model; these are not real order fills or guaranteed income."
                }
            };
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, jsonOptions) + Environment.NewLine);
            var c = replay.Counts;
            output.WriteLine($"PriceOnly; explicit NQ one-contract valuation: rows={c.Rows}, closed={c.ProcessedBars}, invalid={c.InvalidRows}, cadence_breaks={c.CadenceBreaks}, higher_groups={c.CompleteFiveMinuteGroups}.");
            output.WriteLine($"candidates={c.Candidates}, rejected_plans={c.RejectedPlans}, entries={c.Entries}, targets={c.Targets}, stops={c.Stops}, ambiguous={c.Ambiguous}, timeouts={c.Timeouts}, unresolved={c.Unresolved}, net_resolved_assumed_NQ={c.NetResolvedDollars.ToString(CultureInfo.InvariantCulture)}, max_planned_risk={c.MaximumPlannedRiskDollars.ToString(CultureInfo.InvariantCulture)}.");
            output.WriteLine("Trades: " + csvPath);
            output.WriteLine("Summary: " + summaryPath);
            if (!usable)
            {
                error.WriteLine("Input rejected: no usable consecutive one-minute interval. A five-minute CSV is not valid input for this one-minute engine.");
                return 3;
            }
            return 0;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        {
            error.WriteLine("Replay failed: " + failure.Message);
            return 1;
        }
    }

    private static bool TryArguments(string[] args, out string? input, out string? destination, out decimal costs, out string? error)
    {
        input = null;
        destination = null;
        costs = 0;
        error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is not ("--replay" or "--out" or "--costs") || !seen.Add(key) || i + 1 == args.Length)
            {
                error = "Unknown, duplicate or missing argument: " + key;
                return false;
            }
            var value = args[++i];
            switch (key)
            {
                case "--replay": input = value; break;
                case "--out": destination = value; break;
                case "--costs":
                    if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out costs) || costs < 0)
                    {
                        error = "--costs must be a nonnegative invariant decimal amount.";
                        return false;
                    }
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(destination))
        {
            error = "Both --replay and --out are required.";
            return false;
        }
        try
        {
            var source = Path.GetFullPath(input);
            var trades = Path.GetFullPath(destination);
            var summary = Path.ChangeExtension(trades, ".summary.json");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(source, trades, comparison) || string.Equals(source, summary, comparison))
            {
                error = "Input must differ from both the trades and derived summary destination.";
                return false;
            }
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Invalid input or output path: " + failure.Message;
            return false;
        }
        return true;
    }

    public static void WriteTrades(TextWriter writer, IReadOnlyList<ReplayTrade> trades)
    {
        writer.WriteLine("Trade,Direction,SetupIndex,ConfirmationIndex,ConfirmationTime,EntryIndex,AvailableAt,PlannedEntry,PlannedStop,Target200,TargetPrimary,Target300,PlannedRiskDollars,TargetNetDollars,RewardRisk,ExitReason,ExitIndex,exit_candle_time,outcome_known_at,outcome_time_basis,ResolvedExitPrice,ResolvedNetDollars,Ambiguous,UnresolvedReason,UnresolvedAtIndex,UnresolvedAtTime");
        for (var i = 0; i < trades.Count; i++)
        {
            var trade = trades[i];
            var p = trade.Plan;
            var c = p.Candidate;
            var e = trade.Exit;
            string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
            string Time(DateTime? value) => value?.ToString(ReplayCsv.TimestampFormat, CultureInfo.InvariantCulture) ?? "";
            writer.WriteLine(string.Join(',', i + 1, c.Direction, c.SetupIndex, c.ConfirmationIndex, Time(c.ConfirmationTime),
                p.EntryIndex, Time(p.AvailableAt), Number(p.EntryPrice), Number(p.StopPrice), Number(p.TargetLower),
                Number(p.TargetPrimary), Number(p.TargetUpper), Number(p.PlannedRiskDollars), Number(p.TargetNetDollars), Number(p.RewardRisk),
                e?.Reason.ToString() ?? "", e?.ExitIndex.ToString(CultureInfo.InvariantCulture) ?? "", Time(e?.ExitTime), Time(trade.OutcomeKnownAt), trade.OutcomeTimeBasis ?? "", Number(e?.ExitPrice),
                Number(e?.NetDollars), e?.Ambiguous.ToString() ?? "", trade.UnresolvedReason ?? "",
                trade.UnresolvedAtIndex?.ToString(CultureInfo.InvariantCulture) ?? "", Time(trade.UnresolvedAtTime)));
        }
    }
}
