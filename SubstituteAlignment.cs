using System.Text;
using System.Text.RegularExpressions;

namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>
/// Joint reading required before two records can be treated as interchangeable:
/// function, at least one electrical limit, and pin assignment, on both sides.
/// String equality of the other keys stays a separate table.
/// This check does not approve a substitute and does not place a last-time buy.
/// </summary>
public static class SubstituteAlignment
{
    private static readonly string[] EmptyMarkers =
    {
        "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar",
        "mehrdeutig", "nicht relevant", "n/a", "-"
    };

    private static readonly string[] FunctionKeys = { "Function" };

    private static readonly Regex PinWord = new(
        @"^pin",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LimitKey = new(
        "gain|volt|current|fmin|fmax|freq|pdiss|ohm|dbm|supply|power",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record Report(
        bool FunctionOnBoth,
        bool LimitsOnBoth,
        bool PinsOnBoth)
    {
        public bool JointReadingPossible => FunctionOnBoth && LimitsOnBoth && PinsOnBoth;

        public string MissingGroups
        {
            get
            {
                var missing = new List<string>(3);
                if (!FunctionOnBoth) missing.Add("Funktion");
                if (!LimitsOnBoth) missing.Add("elektrische Grenzen");
                if (!PinsOnBoth) missing.Add("Pinbelegung");
                return missing.Count == 0 ? "" : string.Join(", ", missing);
            }
        }
    }

    public static Report Evaluate(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right)
    {
        return new Report(
            GroupOnBoth(left, right, IsFunction),
            GroupOnBoth(left, right, IsLimit),
            GroupOnBoth(left, right, IsPin));
    }

    private static bool GroupOnBoth(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right,
        Func<string, bool> inGroup)
    {
        return HasFilled(left, inGroup) && HasFilled(right, inGroup);
    }

    private static bool HasFilled(
        IReadOnlyDictionary<string, string?> record,
        Func<string, bool> inGroup)
    {
        foreach (var pair in record)
        {
            if (pair.Key.StartsWith("_", StringComparison.Ordinal))
                continue;
            if (!inGroup(pair.Key))
                continue;
            if (!IsEmpty(pair.Value))
                return true;
        }
        return false;
    }

    private static bool IsFunction(string key) =>
        FunctionKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static bool IsPin(string key)
    {
        foreach (string word in SplitKey(key))
        {
            if (PinWord.IsMatch(word))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> SplitKey(string key)
    {
        var word = new StringBuilder();
        foreach (char c in key)
        {
            if (!char.IsLetterOrDigit(c))
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }
                continue;
            }
            if (word.Length > 0 && char.IsUpper(c) && char.IsLower(word[word.Length - 1]))
            {
                yield return word.ToString();
                word.Clear();
            }
            word.Append(c);
        }
        if (word.Length > 0)
            yield return word.ToString();
    }

    private static bool IsLimit(string key) => LimitKey.IsMatch(key);

    public static bool IsEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        var trimmed = value.Trim();
        foreach (var marker in EmptyMarkers)
        {
            if (string.Equals(trimmed, marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
