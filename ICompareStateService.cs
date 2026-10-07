using Newtonsoft.Json.Linq;

namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>
/// Pro-User-State fuer die Vergleichs-Seite (genau 2 PDFs).
/// </summary>
public interface ICompareStateService
{
    /// <summary>Ergebnisse der bisher analysierten PDFs (max. 2).</summary>
    IReadOnlyList<ComparisonFileResult> Results { get; }

    /// <summary>True wenn beide PDFs analysiert wurden und ein Vergleich vorliegt.</summary>
    bool HasResults { get; }

    string StatusText { get; set; }

    event Action? OnStateChanged;

    void SetResults(ComparisonFileResult left, ComparisonFileResult right);
    void Reset();
}

/// <summary>
/// Ergebnis einer einzelnen Datei innerhalb des Vergleichs.
/// </summary>
public class ComparisonFileResult
{
    public string FileName { get; set; } = string.Empty;
    public JObject Properties { get; set; } = new();
}
