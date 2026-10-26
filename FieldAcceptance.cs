namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>
/// Per-field confirmation held for the lifetime of one analysis session.
/// The set records which keys the person has looked at.
/// It is not written to DMS, and clearing the analysis clears the set.
/// </summary>
public sealed class FieldAcceptance
{
    private readonly HashSet<string> _accepted = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _accepted.Count;

    public bool IsAccepted(string key) => _accepted.Contains(key);

    public void Set(string key, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (accepted)
            _accepted.Add(key);
        else
            _accepted.Remove(key);
    }

    public void Clear() => _accepted.Clear();
}
