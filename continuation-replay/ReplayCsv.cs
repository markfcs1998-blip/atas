using System.Globalization;

namespace NqContinuation.Replay;

/// <summary>A physical export row. Fixed-open validity is separate from closed-OHLC validity.</summary>
public sealed record CsvRow(int LineNumber, int Index, DateTime? Time, decimal? Open,
    decimal? High, decimal? Low, decimal? Close, string? Error)
{
    public bool HasValidOpen => Time is not null && Time.Value.Second == 0 && Time.Value.Millisecond == 0 && Open > 0;
    public bool IsValidClosed => Error is null;
}

public static class ReplayCsv
{
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private const NumberStyles DecimalStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public static List<CsvRow> ReadRows(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rows = new List<CsvRow>();
        string? line;
        while ((line = reader.ReadLine()) is not null) rows.Add(ParseLine(line, rows.Count + 1));
        return rows;
    }

    public static CsvRow ParseLine(string line, int lineNumber)
    {
        if (lineNumber < 1) throw new ArgumentOutOfRangeException(nameof(lineNumber));
        var fields = line.Split(';');
        var timeValid = DateTime.TryParseExact(fields[0].Trim(), TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsedTime);
        DateTime? time = timeValid ? DateTime.SpecifyKind(parsedTime, DateTimeKind.Unspecified) : null;
        decimal? Number(int index) => fields.Length > index && decimal.TryParse(fields[index].Trim(), DecimalStyle,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        var open = Number(1);
        var high = Number(2);
        var low = Number(3);
        var close = Number(4);
        string? error = null;
        if (fields.Length != 5) error = "Expected five semicolon-separated fields.";
        else if (time is null) error = "Timestamp must use yyyy-MM-dd HH:mm:ss with no timezone.";
        else if (time.Value.Second != 0) error = "Minute start timestamp has nonzero seconds.";
        else if (open is null || high is null || low is null || close is null) error = "OHLC requires invariant decimal numbers.";
        else if (open <= 0 || high <= 0 || low <= 0 || close <= 0) error = "OHLC prices must be positive.";
        else if (high < low || high < Math.Max(open.Value, close.Value) || low > Math.Min(open.Value, close.Value))
            error = "OHLC high/low do not contain open and close.";
        return new CsvRow(lineNumber, lineNumber - 1, time, open, high, low, close, error);
    }
}
