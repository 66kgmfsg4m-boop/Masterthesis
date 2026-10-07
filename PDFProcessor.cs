using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using PDFtoImage;

namespace DatasheetAnalyzer.Core.Services;

public class PDFProcessor
{
    private const int MIN_TEXT_LENGTH = 100;
    private const int MAX_IMAGES_PER_PDF = 50;
    private const int MIN_IMAGE_WIDTH = 100;
    private const int MIN_IMAGE_HEIGHT = 100;
    // Maximalzahl gerenderter PDF-Seiten (Fallback, wenn PDF keine eingebetteten Bilder hat).
    // ACHTUNG: Muss mit dem Limit in DatasheetAnalysisService.CreateImageRequestBody konsistent sein,
    // sonst werden gerenderte Seiten verworfen, bevor sie die Vision-API erreichen.
    // Die Auswahl WELCHE 15 Seiten verwendet werden uebernimmt SelectRelevantPages
    // (Score-basiert mit Multi-Keyword-Bonus, Negativliste und End-of-Document-Bonus).
    private const int MAX_RENDERED_PAGES = 15;
    private static readonly Regex MEANINGFUL_TEXT_PATTERN = new Regex(@"[A-Za-zÄÖÜäöüß]{10,}");

    /// <summary>
    /// Stichworte, die typischerweise auf "interessanten" Seiten eines Datenblatts
    /// stehen (Ordering Information, Package Drawing, Pin Configuration, ...).
    /// Werden in <see cref="SelectRelevantPages"/> verwendet, um die Top-15-Seiten
    /// fuer die Vision-API zu priorisieren.
    /// Pro Treffer wird der Wert addiert; siehe Multi-Keyword-Bonus in
    /// <see cref="ScorePage"/> fuer den Boost bei thematischer Vielfalt.
    /// </summary>
    private static readonly (string Keyword, int Weight)[] RelevanceKeywords = new[]
    {
        // Series-Datasheet / Ordering / Variants
        ("part number decoder",       14),
        ("part number guide",         12),
        ("ordering information",      12),
        ("ordering guide",            11),
        ("part numbering",            11),
        ("ordering code",             10),
        ("order code",                 9),
        ("device number",              8),
        ("part number",                8),
        ("device naming",              8),
        ("variant",                    5),
        ("marking",                    7),
        ("date code",                  4),

        // Package / Mechanik / Pinout (fuer Pitch + NumberOfPin)
        ("package outline",           11),
        ("package dimensions",        11),
        ("package drawing",           11),
        ("outline drawing",           11),
        ("mechanical drawing",        10),
        ("ball assignment",            9),
        ("ball configuration",         9),
        ("pin configuration",          9),
        ("pin assignment",             8),
        ("pin description",            8),
        ("signal description",         7),
        ("pin function",               7),
        ("pinout",                     9),
        ("mechanical",                 6),
        ("pitch",                      9),
        ("dimensions",                 4),
        ("land pattern",               7),
        ("recommended footprint",      6),
        ("solder pad",                 5),

        // Cover-/Beschreibungs-Bereich
        ("features",                   3),
        ("description",                2),
        ("overview",                   2),
        ("general description",        4),
        ("product description",        4),

        // Elektrische Daten (oft Tabellen mit Min/Typ/Max)
        ("operating conditions",       4),
        ("electrical characteristics", 4),
        ("absolute maximum",           3),
        ("dc characteristics",         3),
        ("ac characteristics",         3),
        ("timing characteristics",     3),
    };

    /// <summary>
    /// Stichworte, die auf "Deko"-Seiten typisch sind und KEINE wertvollen
    /// Informationen fuer die Property-Extraktion liefern. Treffer ziehen Score ab.
    /// </summary>
    private static readonly (string Keyword, int Penalty)[] NegativeKeywords = new[]
    {
        ("table of contents",   12),
        ("contents",             4),
        ("revision history",    10),
        ("document history",     8),
        ("change history",       8),
        ("legal disclaimer",    10),
        ("disclaimer",           6),
        ("trademark",            5),
        ("copyright",            4),
        ("legal information",    8),
        ("important notice",     6),
        ("safety information",   3),
        ("contact information",  6),
        ("worldwide sales",      6),
        ("sales offices",        6),
    };

    private readonly ILogger<PDFProcessor> _logger;

    public PDFProcessor(ILogger<PDFProcessor> logger) => _logger = logger;

    public sealed class PDFContent
    {
        public bool IsTextBased { get; }
        public string TextContent { get; }
        public List<string> Base64Images { get; }
        public int PageCount { get; }
        public PDFContent(bool textBased, string textContent, List<string> base64Images, int pageCount)
        {
            IsTextBased = textBased;
            TextContent = textContent ?? "";
            Base64Images = base64Images ?? new List<string>();
            PageCount = pageCount;
        }
    }

    /// <summary>Verarbeitet eine PDF-Datei aus einem Stream (z. B. Browser-Upload) - Async Version.</summary>
    public async Task<PDFContent> ProcessPdfAsync(Stream stream)
    {
        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");
        try
        {
            using (var fs = File.Create(tempPath))
                await stream.CopyToAsync(fs);
            return ProcessPDF(tempPath);
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { /* ignore */ }
        }
    }

    /// <summary>Synchrone Version für Abwärtskompatibilität mit WPF.</summary>
    public PDFContent ProcessPdf(Stream stream)
    {
        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");
        try
        {
            using (var fs = File.Create(tempPath))
            {
                // Blazor/Web: Verwenden Sie ProcessPdfAsync stattdessen!
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    fs.Write(buffer, 0, read);
            }
            return ProcessPDF(tempPath);
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { /* ignore */ }
        }
    }

    public PDFContent ProcessPDF(string pdfFilePath)
    {
        try
        {
            _logger.LogInformation("PDF-Verarbeitung mit iTextSharp: {Path}", pdfFilePath);
            
            using (var reader = new PdfReader(pdfFilePath))
            {
                int pageCount = reader.NumberOfPages;
                
                // Extract text PER PAGE (wird sowohl fuer Gesamttext als auch fuer
                // die smarte Seitenauswahl beim Rendering benoetigt)
                var pageTexts = ExtractTextPerPage(reader);
                string extractedText = string.Join("\n", pageTexts.OrderBy(kv => kv.Key).Select(kv => kv.Value)).Trim();
                bool isTextBased = IsTextBasedPDF(extractedText);
                
                // Extract images from all pages
                var base64Images = ExtractEmbeddedImages(reader);

                // ✅ NEU: Wenn keine eingebetteten Bilder, aber PDF hat technische Zeichnungen
                //         → Rendere die RELEVANTESTEN PDF-Seiten als Bilder (für Vision API).
                //         Smarte Auswahl: Cover (Seite 1) + Top-Seiten nach Keyword-Score
                //         (Ordering Information, Package, Pinout, Pitch, ...).
                //         Falls weniger als 15 relevante Seiten gefunden werden, wird mit
                //         den fruehesten verbleibenden Seiten aufgefuellt -> 15er-Budget
                //         wird IMMER ausgeschoepft (sofern PDF >= 15 Seiten hat).
                if (base64Images.Count == 0 && pageCount > 0)
                {
                    var selectedPages = SelectRelevantPages(pageTexts, pageCount, MAX_RENDERED_PAGES);
                    _logger.LogInformation("Keine eingebetteten Bilder - rendere {Count} ausgewaehlte Seiten ({Pages})...",
                        selectedPages.Count, string.Join(",", selectedPages));
                    base64Images = RenderPdfPagesToImages(pdfFilePath, pageCount, selectedPages);
                    _logger.LogInformation("✓ {Count} PDF-Seiten als Bilder gerendert", base64Images.Count);
                }

                _logger.LogInformation("PDF-Analyse: Seiten={Pages}, Zeichen={Chars}, Typ={Type}, Bilder={Images}",
                    pageCount, extractedText.Length, isTextBased ? "Text" : "Bild", base64Images.Count);

                if (isTextBased)
                    return new PDFContent(true, extractedText, base64Images, pageCount);
                
                if (base64Images.Count == 0)
                    _logger.LogWarning("Keine Bilder extrahiert – PDF könnte leer oder geschützt sein");
                
                return new PDFContent(false, extractedText, base64Images, pageCount);
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Verarbeiten der PDF");
            throw new IOException($"Fehler beim Verarbeiten der PDF: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extrahiert den Text JEDER Seite getrennt (Key = 1-basierte Seitennummer).
    /// Wird fuer das Keyword-Scoring der smarten Seitenauswahl benoetigt.
    /// </summary>
    private Dictionary<int, string> ExtractTextPerPage(PdfReader reader)
    {
        var result = new Dictionary<int, string>(reader.NumberOfPages);
        for (int page = 1; page <= reader.NumberOfPages; page++)
        {
            try
            {
                string pageText = PdfTextExtractor.GetTextFromPage(reader, page) ?? string.Empty;
                result[page] = pageText;
            }
            catch (System.Exception ex)
            {
                _logger.LogDebug(ex, "Text-Extraktion Seite {Page} fehlgeschlagen", page);
                result[page] = string.Empty;
            }
        }
        return result;
    }

    /// <summary>
    /// Whlt die <paramref name="limit"/> "interessantesten" Seiten aus.
    /// Strategie:
    ///   1. Seite 1 (Cover) ist IMMER dabei (Modellname, Hersteller, Family-Bezeichnung).
    ///   2. Restliche Seiten werden nach Gesamt-Score absteigend gewaehlt. Score = 
    ///        + Summe aller Keyword-Treffer (siehe <see cref="RelevanceKeywords"/>)
    ///        + Multi-Keyword-Bonus (verschiedene Themen auf einer Seite ist wertvoller
    ///          als 5x dasselbe Wort)
    ///        + End-of-Document-Bonus (Ordering/Package-Drawings stehen oft am Ende)
    ///        + Diagram-Bonus (Seiten mit wenig Text = vermutlich technische Zeichnung)
    ///        - Negativ-Keywords (Inhaltsverzeichnis, Revision History, Disclaimer, …)
    ///   3. Werden weniger als <paramref name="limit"/> Seiten mit positivem Score
    ///      gefunden, wird mit den fruehesten verbleibenden Seiten aufgefuellt
    ///      → 15er-Budget wird IMMER ausgeschoepft (sofern Doc >= 15 Seiten).
    ///   4. Rueckgabe ist nach Seitennummer aufsteigend sortiert (Kontext-Reihenfolge).
    /// </summary>
    private List<int> SelectRelevantPages(Dictionary<int, string> pageTexts, int pageCount, int limit)
    {
        if (pageCount <= limit)
            return Enumerable.Range(1, pageCount).ToList();

        // Score pro Seite ermitteln
        var scores = new Dictionary<int, int>(pageCount);
        foreach (var kv in pageTexts)
            scores[kv.Key] = ScorePage(kv.Key, kv.Value ?? string.Empty, pageCount);

        var selected = new HashSet<int> { 1 }; // Cover IMMER dabei

        // Phase 1: Seiten mit positivem Score nach Score-Hoehe (Gleichstand: fruehere Seite)
        foreach (var page in scores
                     .Where(kv => kv.Value > 0)
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key)
                     .Select(kv => kv.Key))
        {
            if (selected.Count >= limit) break;
            selected.Add(page);
        }

        // Phase 2: Auffuellen bis Budget voll (frueheste verbleibende Seiten)
        for (int p = 1; p <= pageCount && selected.Count < limit; p++)
            selected.Add(p);

        var picked = selected.OrderBy(p => p).Take(limit).ToList();
        _logger.LogInformation("Smarte Seitenauswahl: [{Pages}] (Top-Scores: {Scores})",
            string.Join(",", picked),
            string.Join(", ", picked
                .Select(p => $"S{p}={(scores.TryGetValue(p, out var s) ? s : 0)}")));
        return picked;
    }

    /// <summary>
    /// Berechnet den Relevanz-Score einer einzelnen Seite. Hoeher = wichtiger.
    /// </summary>
    /// <param name="pageNumber">1-basierte Seitennummer.</param>
    /// <param name="pageText">Kompletter Text dieser Seite (kann leer sein).</param>
    /// <param name="totalPages">Gesamtseitenzahl des Dokuments.</param>
    private static int ScorePage(int pageNumber, string pageText, int totalPages)
    {
        string lower = pageText.ToLowerInvariant();

        // 1) Negativ-Keywords zuerst pruefen (Deko-Seiten direkt aussortieren).
        int penalty = 0;
        foreach (var (kw, p) in NegativeKeywords)
            if (lower.Contains(kw)) penalty += p;

        // Reine Deko-Seite (z.B. nur "Table of Contents") -> stark abwerten.
        if (penalty >= 12 && pageText.Length < 800)
            return -50;

        // 2) Positive Keyword-Treffer + Multi-Keyword-Diversitaet.
        int rawScore = 0;
        int distinctTopics = 0;
        foreach (var (kw, w) in RelevanceKeywords)
        {
            if (lower.Contains(kw))
            {
                rawScore += w;
                distinctTopics++;
            }
        }
        // Multi-Keyword-Bonus: 3+ verschiedene Themen auf einer Seite -> +50%
        if (distinctTopics >= 3)
            rawScore = (int)(rawScore * 1.5);
        else if (distinctTopics >= 2)
            rawScore = (int)(rawScore * 1.2);

        rawScore -= penalty;

        // 3) Diagram-Bonus: Seiten mit sehr wenig Text (= vermutlich rein grafisch,
        //    z.B. Package-Outline oder Pinout-Zeichnung) bekommen einen Bonus,
        //    weil Vision dort die wertvollste Info liefert.
        if (pageText.Length < 400) rawScore += 6;
        else if (pageText.Length < 800) rawScore += 3;

        // 4) End-of-Document-Bonus: die letzten ~20% des Dokuments enthalten oft
        //    Ordering Tables und Mechanical Drawings.
        if (totalPages >= 5)
        {
            double position = (double)pageNumber / totalPages;
            if (position >= 0.80) rawScore += 4;
            else if (position >= 0.60) rawScore += 2;
        }

        return rawScore;
    }

    private string ExtractTextFromAllPages(PdfReader reader)
    {
        var textParts = new List<string>();
        
        for (int page = 1; page <= reader.NumberOfPages; page++)
        {
            try
            {
                string pageText = PdfTextExtractor.GetTextFromPage(reader, page);
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    textParts.Add(pageText);
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogDebug(ex, "Text-Extraktion Seite {Page} fehlgeschlagen", page);
            }
        }
        
        return string.Join("\n", textParts).Trim();
    }

    private static bool IsTextBasedPDF(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MIN_TEXT_LENGTH)
            return false;
        return MEANINGFUL_TEXT_PATTERN.IsMatch(text);
    }

    /// <summary>
    /// Extracts all embedded images from the PDF.
    /// Currently supports JPEG images (most common in technical documents).
    /// Encodes images as base64 data URLs for Azure OpenAI Vision API.
    /// Filters out small images (likely icons/logos).
    /// </summary>
    private List<string> ExtractEmbeddedImages(PdfReader reader)
    {
        var base64Images = new List<string>();
        int extractedCount = 0;
        int skippedCount = 0;

        try
        {
            for (int pageNum = 1; pageNum <= reader.NumberOfPages && extractedCount < MAX_IMAGES_PER_PDF; pageNum++)
            {
                PdfDictionary page = reader.GetPageN(pageNum);
                PdfDictionary? resources = page.GetAsDict(PdfName.RESOURCES);

                if (resources != null)
                {
                    PdfDictionary? xobjects = resources.GetAsDict(PdfName.XOBJECT);
                    
                    if (xobjects != null)
                    {
                        foreach (PdfName name in xobjects.Keys)
                        {
                            if (extractedCount >= MAX_IMAGES_PER_PDF)
                            {
                                _logger.LogInformation("Maximum von {Max} Bildern erreicht", MAX_IMAGES_PER_PDF);
                                break;
                            }

                            PdfObject obj = xobjects.Get(name);
                            
                            if (obj.IsIndirect())
                            {
                                PdfDictionary imageDict = (PdfDictionary)PdfReader.GetPdfObject(obj);
                                PdfName? subtype = (PdfName?)PdfReader.GetPdfObject(imageDict.Get(PdfName.SUBTYPE));

                                if (PdfName.IMAGE.Equals(subtype))
                                {
                                    try
                                    {
                                        string? base64Image = ExtractImageFromXObject(imageDict, pageNum);
                                        
                                        if (!string.IsNullOrEmpty(base64Image))
                                        {
                                            base64Images.Add(base64Image);
                                            extractedCount++;
                                            _logger.LogDebug("Bild {Count} extrahiert von Seite {Page}", extractedCount, pageNum);
                                        }
                                        else
                                        {
                                            skippedCount++;
                                        }
                                    }
                                    catch (System.Exception ex)
                                    {
                                        _logger.LogDebug(ex, "Fehler beim Extrahieren von Bild auf Seite {Page}", pageNum);
                                        skippedCount++;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (skippedCount > 0)
            {
                _logger.LogDebug("{Count} Bilder übersprungen (zu klein, nicht unterstützt, oder Fehler)", skippedCount);
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Bild-Extraktion fehlgeschlagen");
        }

        return base64Images;
    }

    /// <summary>
    /// Extracts an image from a PDF XObject and converts it to base64.
    /// Currently supports JPEG images (DCTDecode filter).
    /// Filters out small images based on dimensions.
    /// </summary>
    private string? ExtractImageFromXObject(PdfDictionary imageDict, int pageNum)
    {
        try
        {
            // Get image dimensions
            PdfNumber? width = imageDict.GetAsNumber(PdfName.WIDTH);
            PdfNumber? height = imageDict.GetAsNumber(PdfName.HEIGHT);

            if (width != null && height != null)
            {
                int imgWidth = width.IntValue;
                int imgHeight = height.IntValue;

                // Skip small images (likely icons/logos)
                if (imgWidth < MIN_IMAGE_WIDTH || imgHeight < MIN_IMAGE_HEIGHT)
                {
                    _logger.LogDebug("Überspringe kleines Bild ({Width}x{Height}) auf Seite {Page}", imgWidth, imgHeight, pageNum);
                    return null;
                }
            }

            // Get image filter (compression type)
            PdfObject? filterObj = PdfReader.GetPdfObject(imageDict.Get(PdfName.FILTER));
            string? filterName = GetFilterName(filterObj);

            // Extract based on filter type
            if (filterName == "/DCTDecode")
            {
                // JPEG images can be extracted directly (no conversion needed)
                return ExtractJpegImage(imageDict);
            }
            else
            {
                _logger.LogDebug("Nicht unterstütztes Bildformat: {Filter}", filterName ?? "unbekannt");
                return null;
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug(ex, "Bild-Extraktion fehlgeschlagen");
            return null;
        }
    }

    /// <summary>
    /// Gets the filter name from a PdfObject (can be single filter or array).
    /// </summary>
    private string? GetFilterName(PdfObject? filterObj)
    {
        if (filterObj == null)
            return null;

        if (filterObj is PdfName pdfName)
        {
            return pdfName.ToString();
        }
        else if (filterObj is PdfArray filterArray)
        {
            if (filterArray.Size > 0)
            {
                return ((PdfName)filterArray[0]).ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts JPEG image directly as base64 data URL.
    /// JPEG images are already compressed and can be used without conversion.
    /// Format: data:image/jpeg;base64,/9j/4AAQSkZJRg...
    /// </summary>
    private string? ExtractJpegImage(PdfDictionary imageDict)
    {
        try
        {
            // Get raw JPEG bytes
            byte[] imageBytes = PdfReader.GetStreamBytesRaw((PRStream)imageDict);
            
            // Convert to base64
            string base64 = Convert.ToBase64String(imageBytes);
            
            // Return as data URL (ready for Azure OpenAI Vision API)
            return $"data:image/jpeg;base64,{base64}";
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug(ex, "JPEG-Extraktion fehlgeschlagen");
            return null;
        }
    }

    /// <summary>
    /// Rendert die uebergebenen PDF-Seiten als JPEG-Bilder (Fallback wenn keine
    /// eingebetteten Bilder vorhanden sind). Nutzt die PDFtoImage-Library + SkiaSharp.
    /// </summary>
    /// <param name="pdfFilePath">Pfad zur PDF-Datei.</param>
    /// <param name="pageCount">Gesamtseitenzahl der PDF.</param>
    /// <param name="targetPages1Based">
    /// 1-basierte Seitennummern, die gerendert werden sollen. Wenn null, werden
    /// die ersten <see cref="MAX_RENDERED_PAGES"/> Seiten verwendet.
    /// </param>
    private List<string> RenderPdfPagesToImages(string pdfFilePath, int pageCount, IEnumerable<int>? targetPages1Based = null)
    {
        var renderedImages = new List<string>();

        try
        {
            // Liste der zu rendernden Seiten zusammenstellen (validieren + sortieren).
            var pages = (targetPages1Based ?? Enumerable.Range(1, Math.Min(pageCount, MAX_RENDERED_PAGES)))
                .Where(p => p >= 1 && p <= pageCount)
                .Distinct()
                .OrderBy(p => p)
                .Take(MAX_RENDERED_PAGES)
                .ToList();

            if (pages.Count == 0)
            {
                _logger.LogDebug("Keine zu rendernden Seiten ermittelt");
                return renderedImages;
            }

            _logger.LogDebug("Rendere {Count} PDF-Seiten als Bilder (DPI: 200, Seiten: {Pages})...",
                pages.Count, string.Join(",", pages));

            byte[] pdfBytes = File.ReadAllBytes(pdfFilePath);

            // Conversion-Optionen: DPI 200 für gute Qualität, JPEG für kleinere Größe
            var options = new RenderOptions
            {
                Dpi = 200,
                Width = 1600,
                Height = 2400,
                AntiAliasing = PdfAntiAliasing.All
            };

            foreach (var pageNumber in pages)
            {
                try
                {
                    // PDFtoImage nutzt 0-basierte Seitenindizes
                    using var image = Conversion.ToImage(pdfBytes, page: pageNumber - 1, options: options);
                    using var ms = new MemoryStream();

                    // Encode als JPEG (kleinere Dateigröße als PNG)
                    image.Encode(ms, SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
                    byte[] imageBytes = ms.ToArray();
                    string base64 = Convert.ToBase64String(imageBytes);
                    renderedImages.Add($"data:image/jpeg;base64,{base64}");

                    _logger.LogDebug("  Seite {Page} gerendert ({Size} KB)", pageNumber, imageBytes.Length / 1024);
                }
                catch (System.Exception ex)
                {
                    _logger.LogDebug(ex, "Fehler beim Rendern von Seite {Page}", pageNumber);
                }
            }

            _logger.LogInformation("✓ {Count} von {Total} ausgewaehlten Seiten erfolgreich gerendert",
                renderedImages.Count, pages.Count);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "PDF-Rendering fehlgeschlagen - keine Bilder verfügbar");
        }

        return renderedImages;
    }
}
