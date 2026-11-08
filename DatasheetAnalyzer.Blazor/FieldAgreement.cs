namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>
/// Share of filled DMS fields that match the proposal. Empty DMS fields stay outside that share.
/// </summary>
public static class FieldAgreement
{
    public readonly record struct Score(int Matches, int ReferenceFields, int Unsupported)
    {
        public double? Ratio => ReferenceFields == 0 ? null : (double)Matches / ReferenceFields;
    }

    public static Score Evaluate(IEnumerable<(string? Generated, string? Reference)> pairs)
    {
        int matches = 0;
        int referenceFields = 0;
        int unsupported = 0;

        foreach (var (generated, reference) in pairs)
        {
            bool referenceEmpty = string.IsNullOrWhiteSpace(reference);
            bool generatedEmpty = string.IsNullOrWhiteSpace(generated);

            if (referenceEmpty && generatedEmpty)
                continue;

            if (referenceEmpty)
            {
                unsupported++;
                continue;
            }

            referenceFields++;
            if (!generatedEmpty && StringsAgree(generated, reference))
                matches++;
        }

        return new Score(matches, referenceFields, unsupported);
    }

    public static bool StringsAgree(string? generated, string? reference)
    {
        if (string.IsNullOrWhiteSpace(generated) || string.IsNullOrWhiteSpace(reference))
            return false;

        return string.Equals(Normalize(generated), Normalize(reference), StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\u00A0' or '\u202F' or '\u2007' or '\u2009' or '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
                builder.Append(' ');
            else
                builder.Append(ch);
        }

        return System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }
}
