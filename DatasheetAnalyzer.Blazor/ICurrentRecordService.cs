namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>
/// Stub für Layout-NavLink (aktuell gewählter Datensatz). Ohne Katalogbaum-Logik.
/// </summary>
public interface ICurrentRecordService
{
    CurrentRecordInfo? CurrentRecord { get; set; }
}

public class CurrentRecordInfo
{
    public string? PartNumber { get; set; }
}

public class CurrentRecordService : ICurrentRecordService
{
    public CurrentRecordInfo? CurrentRecord { get; set; }
}
