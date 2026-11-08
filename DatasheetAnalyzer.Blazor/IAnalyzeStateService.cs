namespace DatasheetAnalyzer.Blazor.Services;

/// <summary>
/// Pro-User-State für die Analyse-Seite: Modus, Ergebnis, Token-Statistik, Status.
/// </summary>
public interface IAnalyzeStateService
{
    bool IsOrderConfirmationMode { get; set; }
    string ResultLog { get; set; }
    string? CurrentJsonResult { get; set; }
    string? CurrentFileName { get; set; }
    /// <summary>
    /// Volltext des zuletzt analysierten PDFs. Wird vom Chatbot auf der Ergebnisseite
    /// als Kontext genutzt. Bewusst nur EIN PDF pro Analyse - keine Historie.
    /// </summary>
    string? PdfTextContent { get; set; }
    string StatusText { get; set; }
    int TotalPromptTokens { get; }
    int TotalCompletionTokens { get; }
    int ProcessedPdfCount { get; }
    
    /// <summary>
    /// Gibt an, ob Ergebnisse vorhanden sind (für Breadcrumb-Navigation)
    /// </summary>
    bool HasResults { get; }
    
    /// <summary>
    /// Event das gefeuert wird wenn sich der State ändert
    /// </summary>
    event Action? OnStateChanged;
    
    void AddTokenUsage(int promptTokens, int completionTokens);
    void Reset();
}
