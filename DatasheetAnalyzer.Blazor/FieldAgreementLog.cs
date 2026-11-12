using System.Text.Json;

namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>One comparison of a proposal with the DMS master. Stays on this machine.</summary>
public sealed class FieldAgreementRecord
{
    public DateTime RecordedAtUtc { get; init; }
    public string MaterialNumber { get; init; } = "";
    public string? ObjectId { get; init; }
    public string? Category { get; init; }
    public string? PartNumber { get; init; }
    public int Matches { get; init; }
    public int ReferenceFields { get; init; }
    public int Unsupported { get; init; }
    public double? Ratio { get; init; }
    public List<FieldAgreementField> Fields { get; init; } = new();
}

public sealed class FieldAgreementField
{
    public string Name { get; init; } = "";
    public string? Ai { get; init; }
    public string? Dms { get; init; }
    public string Outcome { get; init; } = "";
}

/// <summary>Latest run and the mean across runs for one material number.</summary>
public sealed class PartAgreementSummary
{
    public string MaterialNumber { get; init; } = "";
    public string? Category { get; init; }
    public string? PartNumber { get; init; }
    public int Runs { get; init; }
    public int LatestMatches { get; init; }
    public int LatestReferenceFields { get; init; }
    public double? LatestRatio { get; init; }
    public double? MeanRatio { get; init; }
    public DateTime LatestRecordedAtUtc { get; init; }
}

/// <summary>Appends each comparison and rewrites a per-part summary beside it.</summary>
public sealed class FieldAgreementLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions SummaryOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly object _gate = new();

    public FieldAgreementLog(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory is required.", nameof(directory));
        _directory = directory;
    }

    public string HistoryPath => Path.Combine(_directory, "field-agreement.jsonl");

    public string SummaryPath => Path.Combine(_directory, "field-agreement-by-part.json");

    public static FieldAgreementLog CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DatasheetAnalyzer");
        return new FieldAgreementLog(directory);
    }

    public static string Outcome(string? generated, string? reference)
    {
        bool referenceEmpty = string.IsNullOrWhiteSpace(reference);
        bool generatedEmpty = string.IsNullOrWhiteSpace(generated);
        if (referenceEmpty && generatedEmpty)
            return "empty";
        if (referenceEmpty)
            return "unsupported";
        if (generatedEmpty)
            return "missing";
        return FieldAgreement.StringsAgree(generated, reference) ? "match" : "different";
    }

    public void Append(FieldAgreementRecord record)
    {
        Directory.CreateDirectory(_directory);
        var line = JsonSerializer.Serialize(record, JsonOptions);
        lock (_gate)
        {
            File.AppendAllText(HistoryPath, line + Environment.NewLine);
            var summary = SummarizeByPart(ReadAllUnlocked());
            File.WriteAllText(SummaryPath, JsonSerializer.Serialize(summary, SummaryOptions));
        }
    }

    public IReadOnlyList<FieldAgreementRecord> ReadAll()
    {
        lock (_gate)
            return ReadAllUnlocked();
    }

    public IReadOnlyList<PartAgreementSummary> SummarizeByPart()
    {
        lock (_gate)
            return SummarizeByPart(ReadAllUnlocked());
    }

    private List<FieldAgreementRecord> ReadAllUnlocked()
    {
        var records = new List<FieldAgreementRecord>();
        if (!File.Exists(HistoryPath))
            return records;

        foreach (var line in File.ReadLines(HistoryPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var record = JsonSerializer.Deserialize<FieldAgreementRecord>(line, JsonOptions);
                if (record != null && !string.IsNullOrWhiteSpace(record.MaterialNumber))
                    records.Add(record);
            }
            catch (JsonException)
            {
                // A torn line from an interrupted write is skipped.
            }
        }

        return records;
    }

    public static IReadOnlyList<PartAgreementSummary> SummarizeByPart(IReadOnlyList<FieldAgreementRecord> records)
    {
        return records
            .GroupBy(r => r.MaterialNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(r => r.RecordedAtUtc).ToList();
                var latest = ordered[^1];
                var ratios = ordered.Where(r => r.Ratio.HasValue).Select(r => r.Ratio!.Value).ToList();
                return new PartAgreementSummary
                {
                    MaterialNumber = latest.MaterialNumber.Trim(),
                    Category = latest.Category,
                    PartNumber = latest.PartNumber,
                    Runs = ordered.Count,
                    LatestMatches = latest.Matches,
                    LatestReferenceFields = latest.ReferenceFields,
                    LatestRatio = latest.Ratio,
                    MeanRatio = ratios.Count == 0 ? null : ratios.Average(),
                    LatestRecordedAtUtc = latest.RecordedAtUtc
                };
            })
            .OrderBy(s => s.MaterialNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
