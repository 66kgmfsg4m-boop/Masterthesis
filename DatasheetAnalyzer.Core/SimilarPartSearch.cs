using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Looks up similar parameter sets in the local DMS index. A hit is only a hint.
/// </summary>
public static class SimilarPartSearch
{
    public const double Tolerance = 0.25;

    public sealed record Parameter(string Name, string Text, double? Number, string? Unit);

    public sealed record Query(string? Function, IReadOnlyList<Parameter> Parameters);

    public sealed record Candidate(
        string MaterialNumber,
        string? PartNumber,
        string? Manufacturer,
        IReadOnlyDictionary<string, string?> Fields);

    public sealed record Match(Candidate Candidate, int Shared, int Compared, double Score);

    public sealed record Index(string Source, IReadOnlyList<Candidate> Records);

    public static Query QueryFrom(JObject json)
    {
        string? function = null;
        var parameters = new List<Parameter>();
        foreach (var row in ReadingContext.Rows(json))
        {
            if (row.Absent)
                continue;
            if (IsFunction(row.Designation))
            {
                function ??= row.Display;
                continue;
            }
            if (!TryNumber(row.Value, out double number))
                continue;
            parameters.Add(new Parameter(row.Designation, row.Display, number, NullIfEmpty(row.Unit)));
        }
        return new Query(function, parameters);
    }

    public static IReadOnlyList<Match> Rank(Query query, Index index)
    {
        var matches = new List<Match>();
        foreach (var candidate in index.Records)
        {
            if (!FunctionAgrees(query.Function, FunctionOf(candidate)))
                continue;

            int shared = 0;
            int compared = 0;
            foreach (var parameter in query.Parameters)
            {
                if (!TryCandidateNumber(candidate, parameter.Name, out double number, out string? unit))
                    continue;
                if (!UnitsAgree(parameter.Unit, unit))
                    continue;
                compared++;
                if (parameter.Number is double wanted && Close(wanted, number))
                    shared++;
            }

            if (shared == 0)
                continue;
            double score = compared == 0 ? 0 : (double)shared / compared;
            matches.Add(new Match(candidate, shared, compared, score));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Shared)
            .ThenBy(match => match.Candidate.MaterialNumber, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    public static Index LoadIndex()
    {
        foreach (string path in CandidatePaths())
        {
            if (!File.Exists(path))
                continue;
            return Parse(File.ReadAllText(path), path);
        }
        return Parse(SampleJson, "mitgelieferter Parameterindex");
    }

    public static Index Parse(string json, string source)
    {
        var root = JObject.Parse(json);
        var records = new List<Candidate>();
        foreach (var item in root["records"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (item["fields"] is JObject fieldObj)
            {
                foreach (var prop in fieldObj.Properties())
                    fields[prop.Name] = prop.Value?.Type == JTokenType.Null ? null : prop.Value?.ToString();
            }
            records.Add(new Candidate(
                item["materialNumber"]?.ToString() ?? "",
                item["partNumber"]?.ToString(),
                item["manufacturer"]?.ToString(),
                fields));
        }
        string? label = root["source"]?.ToString();
        if (string.IsNullOrWhiteSpace(label))
            label = source;
        return new Index(label, records);
    }

    public static string Keyword(Query query)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Function))
            parts.Add(query.Function.Trim());
        foreach (var parameter in query.Parameters.Take(4))
            parts.Add(parameter.Text);
        string keyword = string.Join(' ', parts);
        return keyword.Length <= 180 ? keyword : keyword.Substring(0, 180);
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? configured = Environment.GetEnvironmentVariable("DMS_PARAMETER_INDEX");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        yield return Path.Combine(AppContext.BaseDirectory, "dms-parameter-index.json");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            yield return Path.Combine(dir.FullName, "code", "data", "dms-parameter-index.json");
            yield return Path.Combine(dir.FullName, "data", "dms-parameter-index.json");
            dir = dir.Parent;
        }
    }

    private static bool FunctionAgrees(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return true;
        return left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
               right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FunctionOf(Candidate candidate)
    {
        foreach (var pair in candidate.Fields)
        {
            if (IsFunction(pair.Key) && !ReadingContext.IsAbsence(pair.Value))
                return pair.Value;
        }
        return null;
    }

    private static bool IsFunction(string name) =>
        name.Equals("Function", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Funktion", StringComparison.OrdinalIgnoreCase);

    private static bool TryCandidateNumber(Candidate candidate, string name, out double number, out string? unit)
    {
        number = 0;
        unit = null;
        if (!candidate.Fields.TryGetValue(name, out string? raw) || ReadingContext.IsAbsence(raw))
            return false;
        var split = ReadingContext.Split(raw);
        unit = split.Unit;
        return TryNumber(split.Value, out number);
    }

    private static bool TryNumber(string? text, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var match = Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
        if (!match.Success)
            return false;
        string token = match.Value.Replace(',', '.');
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool UnitsAgree(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return true;
        return string.Equals(NormUnit(left), NormUnit(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormUnit(string unit) =>
        unit.Replace("µ", "u", StringComparison.Ordinal).Replace("Ω", "Ohm", StringComparison.Ordinal).Trim();

    private static bool Close(double wanted, double found)
    {
        double scale = Math.Max(Math.Abs(wanted), 1e-9);
        return Math.Abs(wanted - found) / scale <= Tolerance;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    internal const string SampleJson = """
    {
      "source": "mitgelieferter Parameterindex",
      "records": [
        {
          "materialNumber": "1001001",
          "partNumber": "HMC-DEMO-17",
          "manufacturer": "Beispielhersteller",
          "fields": { "Function": "amplifier", "GainTypDb": "17 dB", "FminGHz": "24", "SupplyVoltage": "5 V" }
        },
        {
          "materialNumber": "1001002",
          "partNumber": "HMC-DEMO-19",
          "manufacturer": "Beispielhersteller",
          "fields": { "Function": "amplifier", "GainTypDb": "19 dB", "FminGHz": "24", "SupplyVoltage": "5 V" }
        },
        {
          "materialNumber": "1001003",
          "partNumber": "MIX-DEMO-7",
          "manufacturer": "Beispielhersteller",
          "fields": { "Function": "mixer", "GainTypDb": "7 dB", "FminGHz": "24" }
        }
      ]
    }
    """;
}
