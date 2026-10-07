# Pitch-Extraktion Debugging - Warum wird Pitch nicht gefunden?

## Problem-Analyse für 3645.0210.00.pdf

### Screenshot-Analyse
```
"Pitch": "not found"  ? Sollte "0.50 mm" sein!
```

---

## Mögliche Ursachen

### 1. **PDF ist text-basiert** (WAHRSCHEINLICHSTE URSACHE!)
```
Wenn PDF text-basiert erkannt wird:
? Vision API wird NICHT verwendet!
? Nur Text-Extraktion
? Zeichnungen werden NICHT analysiert!
```

**Lösung:** PDFs sollten als **bild-basiert** erkannt werden, wenn sie technische Zeichnungen enthalten.

### 2. **Guidelines werden nicht geladen**
```
Falls Azure-Verbindung fehlschlägt:
? Embedded Fallback sollte verwendet werden
? Prüfe Console-Output!
```

### 3. **Zeichnung ist auf späterer Seite**
```
Aktuell werden nur erste 3 Seiten für Identifikation verwendet.
Bei Detail-Extraktion sollten ALLE Bilder verwendet werden!
```

### 4. **KI fokussiert nicht auf Package Dimensions**
```
Guidelines sind generisch.
Für RF Amplifier brauchen wir SPEZIFISCHE Anweisung!
```

---

## Diagnose-Checkliste

Prüfe Console-Output nach PDF-Upload:

### ? Checkpoint 1: PDF-Verarbeitung
```
Erwarteter Output:
[PDFProcessor] PDF-VERARBEITUNG MIT iTEXTSHARP
[PDFProcessor] PDF-Analyse:
  - Seiten: 16
  - Extrahierte Zeichen: 12345
  - Typ: Text-basiert ? ? PROBLEM!
  - Eingebettete Bilder: 8 ? ? Bilder vorhanden!
```

**Problem:** PDF wird als "Text-basiert" erkannt!

### ? Checkpoint 2: Guidelines-Loading
```
Erwarteter Output:
[DMSCatalogService] Loading pitch_extraction_guidelines.txt from Azure
[DMSCatalogService] Pitch extraction guidelines loaded from Azure (13245 characters)
```

### ? Checkpoint 3: Vision API Usage
```
Bei BILD-basierter PDF:
[DatasheetAnalysisService] Creating image request body
[DatasheetAnalysisService] Sending 8 images to Vision API with detail=high

Bei TEXT-basierter PDF:
[DatasheetAnalysisService] Creating text request body ? ? KEINE Bilder!
```

---

## Lösung: Erzwinge Bild-Analyse für technische Zeichnungen

### Problem
```csharp
// PDFProcessor.cs - Aktuelle Logik:
bool isTextBased = IsTextBasedPDF(extractedText);

if (isTextBased) {
    return new PDFContent(true, extractedText, embeddedImages, pageCount);
    // ? Bilder werden IGNORIERT, obwohl sie wichtige Infos enthalten!
}
```

### Lösung: Hybrid-Modus
```csharp
// Für Datenblätter: IMMER Bilder analysieren, wenn vorhanden!
if (embeddedImages.Count > 0) {
    return new PDFContent(false, extractedText, embeddedImages, pageCount);
    // ? BILD-basiert, auch wenn Text vorhanden!
}
```

---

## Implementierung: Hybrid PDF Processing

### Option A: Immer Bilder bevorzugen (wenn vorhanden)
```csharp
// In PDFProcessor.cs
public PDFContent ProcessPDF(string pdfFilePath, bool preferImages = false)
{
    ...
    bool isTextBased = IsTextBasedPDF(extractedText);
    List<string> embeddedImages = ExtractEmbeddedImages(reader);
    
    // ? NEU: Wenn Bilder vorhanden UND preferImages=true ? Bild-Modus
    if (preferImages && embeddedImages.Count > 0) {
        Console.WriteLine("HYBRID-MODUS: Bilder vorhanden - verwende Vision API");
        return new PDFContent(false, extractedText, embeddedImages, pageCount);
    }
    
    if (isTextBased) {
        return new PDFContent(true, extractedText, embeddedImages, pageCount);
    } else {
        return new PDFContent(false, extractedText, embeddedImages, pageCount);
    }
}
```

### Option B: Mehr Bilder für Detail-Extraktion
```csharp
// In DatasheetAnalysisService.cs - AnalyzeComponentWithCatalogAsync()

if (pdfContent.IsTextBased) {
    // Text-basiert, ABER: Wenn Bilder vorhanden, nutze sie zusätzlich!
    if (pdfContent.Base64Images.Count > 0) {
        Console.WriteLine($"HYBRID: Text + {pdfContent.Base64Images.Count} Bilder");
        return await AnalyzeImageContentAsync(pdfContent.Base64Images, dynamicPrompt);
        // ? Verwende Bilder für Detail-Extraktion!
    }
    return await AnalyzeTextContentAsync(pdfContent.TextContent, dynamicPrompt);
} else {
    return await AnalyzeImageContentAsync(pdfContent.Base64Images, dynamicPrompt);
}
```

### Option C: Alle Seiten als Bilder (nur für Pitch-wichtige Komponenten)
```csharp
// Wenn Komponente Pitch benötigt UND PDF hat viele Bilder:
if (RequiresPitchGuidelines(component) && pdfContent.Base64Images.Count >= 5) {
    // Verwende ALLE Bilder (nicht nur erste 3)
    return await AnalyzeImageContentAsync(pdfContent.Base64Images, dynamicPrompt);
}
```

---

## Schnelle Fix-Strategie

### Für DEIN spezifisches PDF (3645.0210.00.pdf):

Das PDF enthält **8 Bilder** (siehe Output), wird aber als **Text-basiert** erkannt.

**Quick Fix:**
```csharp
// Für RF Amplifier: Erzwinge Bild-Analyse wenn Bilder vorhanden

if (componentCategory.Contains("Amplifier") && pdfContent.Base64Images.Count > 0) {
    Console.WriteLine($"RF Amplifier mit {pdfContent.Base64Images.Count} Bildern - erzwinge Vision API");
    result = await AnalyzeImageContentAsync(pdfContent.Base64Images, dynamicPrompt);
}
```

---

## Test-Kommando

Nach Implementierung:
```sh
cd DatasheetAnalyzer.Blazor
dotnet build
dotnet run

# Im Browser:
https://localhost:5001
? Upload: 3645.0210.00.pdf
```

Erwarteter Output:
```
[PDFProcessor] Eingebettete Bilder: 8
[PDFProcessor] HYBRID-MODUS: RF Amplifier - verwende Vision API für Zeichnungen
[DatasheetAnalysisService] Sending 8 images to Vision API
[DatasheetAnalysisService] Pitch extraction guidelines active
...
"Pitch": "0.50 mm" ? ? ERKANNT!
```

---

## Zusammenfassung

**Warum "not found"?**
- ? PDF wird als text-basiert erkannt
- ? Vision API wird NICHT verwendet für Detail-Extraktion
- ? Zeichnungen werden ignoriert

**Lösung:**
1. ? Sprache geändert: "nicht gefunden" ?
2. ? Hybrid-Modus implementieren (Text + Bilder)
3. ? Für RF Amplifier: Immer Bilder analysieren

**Nächster Schritt:**
Soll ich den **Hybrid-Modus** implementieren?
