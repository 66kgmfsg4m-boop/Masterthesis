using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Ranks whole pages of one uploaded datasheet and keeps five of them.
/// A page is the passage, so a table is not split from its header.
/// The rank is BM25 (k1 = 1.2, b = 0.75). Those constants are the usual
/// settings of the ranker and are not tuned on datasheets.
/// A dense embedding rank is not executed here: no embedding endpoint is linked.
/// </summary>
public static class PassageRetrieval
{
    public const int PassageCount = 5;
    private const double K1 = 1.2;
    private const double B = 0.75;
    private static readonly Regex TokenPattern = new(@"[A-Za-z0-9]+(?:[./-][A-Za-z0-9]+)*", RegexOptions.Compiled);

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "are", "was", "were",
        "ein", "eine", "und", "der", "die", "das", "den", "dem", "des", "von",
        "page", "document", "content", "value", "values", "return", "json"
    };

    public static string MarkPages(IReadOnlyDictionary<int, string>? pages)
    {
        if (pages == null || pages.Count == 0)
            return "";

        var builder = new StringBuilder();
        foreach (var page in pages.OrderBy(pair => pair.Key))
        {
            if (string.IsNullOrWhiteSpace(page.Value))
                continue;
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append("[[page:").Append(page.Key.ToString(CultureInfo.InvariantCulture)).AppendLine("]]");
            builder.AppendLine(page.Value.Trim());
        }
        return builder.ToString();
    }

    public static Dictionary<int, string> ParseMarkedPages(string? marked)
    {
        var pages = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(marked) || marked.IndexOf("[[page:", StringComparison.Ordinal) < 0)
            return pages;

        var matches = Regex.Matches(marked, @"\[\[page:(\d+)\]\]");
        for (int i = 0; i < matches.Count; i++)
        {
            int page = int.Parse(matches[i].Groups[1].Value, CultureInfo.InvariantCulture);
            int start = matches[i].Index + matches[i].Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : marked.Length;
            string body = marked.Substring(start, Math.Max(0, end - start)).Trim();
            if (body.Length > 0)
                pages[page] = body;
        }
        return pages;
    }

    /// <summary>
    /// Five pages, highest BM25 first. Fewer than five non-empty pages returns all of them in page order.
    /// </summary>
    public static string Select(IReadOnlyDictionary<int, string>? pages, string? query)
    {
        if (pages == null || pages.Count == 0)
            return "";

        var usable = pages
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key)
            .ToList();
        if (usable.Count == 0)
            return "";
        if (usable.Count <= PassageCount)
            return Format(usable);

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
            return Format(usable.Take(PassageCount));

        var documents = usable.Select(pair => (pair.Key, Tokens: Tokenize(pair.Value))).ToList();
        double averageLength = documents.Average(doc => doc.Tokens.Count);
        if (averageLength <= 0)
            return Format(usable.Take(PassageCount));

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (string term in document.Tokens.Distinct(StringComparer.Ordinal))
            {
                documentFrequency.TryGetValue(term, out int count);
                documentFrequency[term] = count + 1;
            }
        }

        int collectionSize = documents.Count;
        var ranked = new List<(int Page, double Score, string Text)>();
        for (int i = 0; i < documents.Count; i++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string token in documents[i].Tokens)
            {
                counts.TryGetValue(token, out int seen);
                counts[token] = seen + 1;
            }

            double score = 0;
            int length = documents[i].Tokens.Count;
            foreach (string term in queryTerms.Distinct(StringComparer.Ordinal))
            {
                if (!counts.TryGetValue(term, out int tf))
                    continue;
                documentFrequency.TryGetValue(term, out int df);
                double idf = Math.Log(1.0 + (collectionSize - df + 0.5) / (df + 0.5));
                double denominator = tf + K1 * (1.0 - B + B * length / averageLength);
                score += idf * (tf * (K1 + 1.0)) / denominator;
            }
            ranked.Add((usable[i].Key, score, usable[i].Value));
        }

        var chosen = ranked
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Page)
            .Take(PassageCount)
            .ToList();

        if (chosen.All(row => row.Score <= 0))
            return Format(usable.Take(PassageCount));

        return Format(chosen.Select(row => new KeyValuePair<int, string>(row.Page, row.Text)));
    }

    public static string SelectMarked(string? markedText, string? query)
    {
        var pages = ParseMarkedPages(markedText);
        if (pages.Count == 0)
            return "";
        return Select(pages, query);
    }

    private static string Format(IEnumerable<KeyValuePair<int, string>> pages)
    {
        var builder = new StringBuilder();
        foreach (var page in pages)
        {
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append("[Page ").Append(page.Key.ToString(CultureInfo.InvariantCulture)).AppendLine("]");
            builder.AppendLine(page.Value.Trim());
        }
        return builder.ToString().Trim();
    }

    private static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;
        foreach (Match match in TokenPattern.Matches(text))
        {
            string token = match.Value.ToLowerInvariant();
            if (token.Length < 2 || Stopwords.Contains(token))
                continue;
            tokens.Add(token);
        }
        return tokens;
    }
}
