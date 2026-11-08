namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>
/// Fields the person has confirmed in this session. Not written to DMS.
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
