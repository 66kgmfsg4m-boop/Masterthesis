namespace DatasheetAnalyzer.Blazor.Services;

public class AnalyzeStateService : IAnalyzeStateService
{
    private string _resultLog = string.Empty;
    private string? _currentJsonResult;
    private string? _currentFileName;
    private string? _pdfTextContent;

    public bool IsOrderConfirmationMode { get; set; } = false;
    
    public string ResultLog
    {
        get => _resultLog;
        set
        {
            _resultLog = value;
            Console.WriteLine($"[AnalyzeStateService] ResultLog gesetzt: {(_resultLog?.Length ?? 0)} Zeichen");
            Console.WriteLine($"[AnalyzeStateService] HasResults = {HasResults}");
            NotifyStateChanged();
        }
    }
    
    public string? CurrentJsonResult
    {
        get => _currentJsonResult;
        set
        {
            _currentJsonResult = value;
            Console.WriteLine($"[AnalyzeStateService] CurrentJsonResult gesetzt: {(_currentJsonResult?.Length ?? 0)} Zeichen");
            Console.WriteLine($"[AnalyzeStateService] HasResults = {HasResults}");
            NotifyStateChanged();
        }
    }
    
    public string? CurrentFileName
    {
        get => _currentFileName;
        set
        {
            _currentFileName = value;
            NotifyStateChanged();
        }
    }

    public string? PdfTextContent
    {
        get => _pdfTextContent;
        set
        {
            _pdfTextContent = value;
            NotifyStateChanged();
        }
    }

    public string StatusText { get; set; } = "Bereit";
    public int TotalPromptTokens { get; private set; }
    public int TotalCompletionTokens { get; private set; }
    public int ProcessedPdfCount { get; private set; }
    
    /// <summary>
    /// Gibt an, ob Ergebnisse vorhanden sind
    /// </summary>
    public bool HasResults => !string.IsNullOrEmpty(CurrentJsonResult) || !string.IsNullOrEmpty(ResultLog);
    
    /// <summary>
    /// Event das gefeuert wird wenn sich der State ändert
    /// </summary>
    public event Action? OnStateChanged;
    
    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke();
    }

    public void AddTokenUsage(int promptTokens, int completionTokens)
    {
        TotalPromptTokens += promptTokens;
        TotalCompletionTokens += completionTokens;
        ProcessedPdfCount++;
        NotifyStateChanged();
    }

    public void Reset()
    {
        ResultLog = string.Empty;
        CurrentJsonResult = null;
        CurrentFileName = null;
        PdfTextContent = null;
        StatusText = "Bereit";
        TotalPromptTokens = 0;
        TotalCompletionTokens = 0;
        ProcessedPdfCount = 0;
        NotifyStateChanged();
    }
}
