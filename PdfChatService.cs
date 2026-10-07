using DatasheetAnalyzer.Core.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Interaktiver Chatbot fuer ein bereits analysiertes PDF.
/// Es gibt bewusst KEINE Historie und KEIN Export - jeder Aufruf ist eine
/// eigenstaendige Frage/Antwort-Runde zum aktuell geladenen PDF.
/// </summary>
public interface IPdfChatService
{
    /// <summary>
    /// Gibt an, ob der Chatbot einsatzbereit ist (Azure-Konfig vorhanden).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Beantwortet eine einzelne Nutzerfrage zum PDF-Kontext.
    /// </summary>
    /// <param name="pdfTextContent">Extrahierter PDF-Text (aus PDFProcessor). Kann leer sein.</param>
    /// <param name="analysisJson">Optional: JSON-Ergebnis der Analyse als zusaetzlicher Kontext.</param>
    /// <param name="question">Frage des Nutzers.</param>
    Task<string> AskAsync(string? pdfTextContent, string? analysisJson, string question);
}

public class PdfChatService : IPdfChatService
{
    private readonly AzureConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PdfChatService> _logger;

    // Begrenzung des PDF-Kontexts, damit wir bei sehr grossen Datenblaettern
    // nicht in Token-/Latenz-Probleme laufen. 60000 Zeichen ~= 12-15k Tokens.
    private const int MaxPdfContextChars = 60000;

    public PdfChatService(
        AzureConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger<PdfChatService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsAvailable => _config.IsConfigurationValid();

    public async Task<string> AskAsync(string? pdfTextContent, string? analysisJson, string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Frage darf nicht leer sein.", nameof(question));

        if (!IsAvailable)
            throw new InvalidOperationException(
                "Azure-Konfiguration ist nicht verfuegbar - Chatbot ist deaktiviert.");

        string pdfContext = TruncateForContext(pdfTextContent, MaxPdfContextChars);

        var systemMessage = new
        {
            role = "system",
            content =
                "Du bist ein technischer Assistent fuer EIN einzelnes Elektronik-Datenblatt (PDF). "
                + "Beantworte Fragen ausschliesslich auf Basis des unten bereitgestellten PDF-Inhalts "
                + "und der bereits extrahierten Analyse-Daten. "
                + "Wenn eine Information nicht im PDF/den Analyse-Daten steht, sage das offen "
                + "(z.B. 'Dazu enthaelt das Datenblatt keine Angabe'). "
                + "Erfinde keine Werte. Antworte kompakt, sachlich und in derselben Sprache wie die Frage."
        };

        var contextBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(analysisJson))
        {
            contextBuilder.AppendLine("### Bereits extrahierte Analyse-Daten (JSON)");
            contextBuilder.AppendLine(analysisJson);
            contextBuilder.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(pdfContext))
        {
            contextBuilder.AppendLine("### PDF-Inhalt (moeglicherweise gekuerzt)");
            contextBuilder.AppendLine(pdfContext);
        }

        string userContent = contextBuilder.Length > 0
            ? $"KONTEXT:\n{contextBuilder}\n\nFRAGE:\n{question}"
            : question;

        var userMessage = new { role = "user", content = userContent };

        var requestBody = new
        {
            messages = new object[] { systemMessage, userMessage },
            // WICHTIG bei Reasoning-Modellen (o1/o3/gpt-5-Serie):
            // 'max_completion_tokens' zaehlt Reasoning-Tokens + Output-Tokens
            // gemeinsam. 1200 ist zu knapp - dann bleibt fuer die eigentliche
            // Antwort nichts uebrig. 4000 gibt genug Puffer fuer typische
            // Datenblatt-Fragen.
            max_completion_tokens = 4000
            // 'temperature' bewusst NICHT setzen: neuere Azure-OpenAI-Modelle
            // (o1/o3/gpt-5-Serie) akzeptieren nur den Default-Wert 1.
        };

        string requestJson = JsonConvert.SerializeObject(requestBody);
        string url = $"{_config.GetEndpoint()}/openai/deployments/{_config.GetDeploymentName()}/chat/completions?api-version={_config.GetApiVersion()}";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", _config.GetApiKey());
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        try
        {
            var response = await client.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Azure OpenAI Chat-Fehler {Status}: {Body}",
                    response.StatusCode, responseBody);
                throw new HttpRequestException(
                    $"Azure API Error: {response.StatusCode} - {responseBody}");
            }

            var jsonResponse = JObject.Parse(responseBody);
            string answer = jsonResponse["choices"]?[0]?["message"]?["content"]?.Value<string>() ?? "";
            return answer.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim PDF-Chatbot-Aufruf");
            throw;
        }
    }

    /// <summary>
    /// Kuerzt den PDF-Kontext auf <paramref name="maxChars"/> Zeichen und behaelt
    /// Anfang UND Ende, weil Datenblaetter haeufig sowohl vorne (Features/Overview)
    /// als auch hinten (Ordering Info, Package Drawings) wichtige Angaben enthalten.
    /// </summary>
    private static string TruncateForContext(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxChars) return text;

        int half = maxChars / 2 - 100;
        if (half <= 0) return text.Substring(0, maxChars);

        return text.Substring(0, half)
             + "\n\n[... PDF-Inhalt gekuerzt ...]\n\n"
             + text.Substring(text.Length - half);
    }
}
