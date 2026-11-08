namespace DatasheetAnalyzer.Blazor.Services;

public class CompareStateService : ICompareStateService
{
    private readonly List<ComparisonFileResult> _results = new();

    public IReadOnlyList<ComparisonFileResult> Results => _results;

    public bool HasResults => _results.Count == 2;

    public string StatusText { get; set; } = "Bereit";

    public event Action? OnStateChanged;

    public void SetResults(ComparisonFileResult left, ComparisonFileResult right)
    {
        _results.Clear();
        _results.Add(left);
        _results.Add(right);
        NotifyStateChanged();
    }

    public void Reset()
    {
        _results.Clear();
        StatusText = "Bereit";
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}
