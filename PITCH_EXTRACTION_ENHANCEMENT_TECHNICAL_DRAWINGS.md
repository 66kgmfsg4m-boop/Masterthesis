# Pitch-Extraktion Verbesserung - Technische Zeichnungen

## Problem-Analyse

### Aktueller Zustand
Der Pitch (Abstand zwischen zwei Pins) wird oft **nicht erkannt**, obwohl er in den Datenblättern vorhanden ist.

**Hauptursache:**
- Der Pitch ist **meist nur in technischen Zeichnungen** sichtbar (wie im Screenshot: "0.50")
- Befindet sich typischerweise unter **"Package Information and Dimensions"**
- Wird als **Maßlinie mit Bemaßungspfeil** dargestellt
- **Keine Standard-Pitch-Werte** - jedes Bauteil kann unterschiedliche Werte haben

### Screenshot-Analyse
```
Package Information and Dimensions
???????????????????????????????
                        [Rot markiert: 0.50] ??? PITCH!
           ?????????????????????
           ?  QFN Package      ?
           ?  [Pin Layout]     ?
           ?????????????????????
              ? 0.50 (Pitch)
```

**Wichtige Komponenten für Pitch:**
- ? RF Amplifier
- ? Amplifier (alle Typen)
- ? Connectors
- ? ICs (QFN, QFP, SOIC, etc.)

---

## Technische Lösung

### 1. Vision API bereits implementiert ?
Die Infrastruktur ist bereits vorhanden:
- `PDFProcessor` extrahiert Bilder aus PDFs (JPEG)
- `DatasheetAnalysisService` nutzt Azure OpenAI Vision API
- Bilder werden an die API mit `detail="high"` gesendet

### 2. Problem: Prompt optimieren

#### Aktuelle Situation
Die Prompts weisen die KI an, Pitch zu extrahieren, aber:
- ? Keine spezifischen Anweisungen für **technische Zeichnungen**
- ? Keine Hinweise auf **Package Dimensions Sections**
- ? Keine Erklärung was **Maßlinien** sind

#### Lösung: Erweiterte Prompt-Strategie

```
?? SPEZIFISCHE ANWEISUNGEN FÜR PITCH-EXTRAKTION:

1. SUCHE IN TECHNISCHEN ZEICHNUNGEN:
   - Abschnitte: "Package Information", "Dimensions", "Mechanical Drawing"
   - Maßlinien mit Pfeilen zwischen Pins ? ? ?
   - Oft im Format: "0.50", "e = 0.5mm", "pitch: 1.27"

2. ERKENNUNGSREGELN:
   - Pitch = Abstand zwischen benachbarten Pin-Mitten
   - NUR Pin-to-Pin spacing (NICHT row-to-row!)
   - Typische Bereiche: 0.3mm bis 5mm
   - Meist in mm (manchmal inch: 0.1" = 2.54mm)

3. PACKAGE-SPEZIFISCHE HINWEISE:
   QFN:  Typisch 0.4mm oder 0.5mm (Dimension 'e')
   QFP:  Typisch 0.5mm, 0.65mm oder 0.8mm  
   SOIC: Typisch 0.65mm oder 1.27mm (Pin spacing, NOT row spacing!)
   DIP:  Typisch 2.54mm (0.1 inch)

4. VISION-ANALYSE:
   - Prüfe Zeichnungen mit Bemaßungen
   - Suche nach Maßpfeilen/Linien zwischen Pins
   - Beachte Legende und Dimensionstabellen

5. VALIDIERUNG:
   - Pitch MUSS zwischen 0.3 und 5.0 mm liegen
   - Bei mehreren Werten: Wähle Pin-to-Pin spacing (kleiner Wert)
   - Wenn unklar: "not found" statt falscher Wert
```

---

## Implementierung

### Schritt 1: Prompt-Erweiterung erstellen

#### Neue Datei: `pitch_extraction_guidelines.txt`
? Wird in Azure Blob Storage hochgeladen
? Wird in die Property-Definitions integriert

```txt
SPECIAL INSTRUCTIONS FOR PITCH EXTRACTION (Technical Drawings):
????????????????????????????????????????????????????

CRITICAL: Pitch is often ONLY visible in mechanical drawings/package diagrams!

WHERE TO FIND PITCH:
1. Section titles: "Package Information", "Dimensions", "Mechanical Outline"
2. Package drawings showing pin layout (top view / side view)
3. Dimension lines with arrows between pins: ? e ? or ? e
4. Dimension tables listing "e" or "pitch" or "pin spacing"

WHAT IS PITCH:
- Pin-to-Pin spacing (center-to-center distance between adjacent pins)
- Symbol often: 'e' (in package drawings)
- NOT row-to-row spacing (that's a different dimension!)
- Typical format: "0.50", "e = 0.5 mm", "pitch: 1.27mm"

VALIDATION RULES:
? Value MUST be between 0.3mm and 5.0mm
? If multiple values exist, choose the SMALLER one (pin-to-pin, not row-to-row)
? Common values: 0.4, 0.5, 0.65, 0.8, 1.0, 1.27, 2.0, 2.54 mm
? If value is in inches, convert to mm (e.g., 0.1" ? 2.54mm)
? If unclear or not found ? "not found" (NEVER guess!)

PACKAGE-SPECIFIC HINTS:
Package | Typical Pitch | Note
--------|--------------|-----
QFN     | 0.4 or 0.5   | Look for dimension 'e' in bottom view
QFP     | 0.5 to 0.8   | Pin-to-pin on same side
SOIC    | 0.65 or 1.27 | NOT the 7.5mm body width!
SOP     | 0.65 or 0.8  | Pin spacing on one side
DIP     | 2.54 (0.1")  | Through-hole spacing

VISION ANALYSIS TIPS:
1. Focus on mechanical drawings (usually page 5-10 of datasheet)
2. Look for dimensioned package outlines
3. Check dimension tables near drawings
4. Maßlinien (dimension lines) often have arrows: ?? or ?
5. Pay attention to legend/notes explaining symbols

IF PITCH IS NOT CLEARLY VISIBLE:
? Return "not found" (this is better than a wrong value!)
? Do NOT extrapolate from package size
? Do NOT guess based on pin count
```

### Schritt 2: DMSCatalogService erweitern

Die Pitch-Guidelines müssen in die dynamischen Prompts integriert werden:

```csharp
// In GenerateDynamicPrompt() Method:

// Spezielle Behandlung für Pitch bei Amplifiern und anderen Komponenten
if (component.DisplayName.Contains("Amplifier") || 
    component.DisplayName.Contains("Connector") ||
    component.DisplayName.Contains("IC"))
{
    promptBuilder.AppendLine("\n" + await LoadPitchExtractionGuidelines());
}

private async Task<string> LoadPitchExtractionGuidelines()
{
    try
    {
        return await _remoteConfigService.LoadBlobContent("pitch_extraction_guidelines.txt");
    }
    catch
    {
        return ""; // Fallback: keine Extra-Guidelines
    }
}
```

### Schritt 3: Property-Validation erweitern

Die bestehende `PropertyValidationService` hat bereits Pitch-Validierung, aber wir können sie verbessern:

```csharp
private PropertyRule CreatePitchRule()
{
    return new PropertyRule
    {
        PropertyName = "Pitch",
        RequiredUnit = "mm",
        MinValue = 0.3,
        MaxValue = 5.0,
        CommonValues = new[] { 0.4, 0.5, 0.65, 0.8, 1.0, 1.27, 2.0, 2.54 },
        ValidationMessage = "Pitch should be between 0.3mm and 5mm. Check technical drawings!",
        ComponentTypeHints = new Dictionary<string, string>
        {
            {"QFN", "Typical 0.4mm or 0.5mm (pin-to-pin = dimension 'e' in package drawing)"},
            {"QFP", "Typical 0.5mm, 0.65mm or 0.8mm (pin-to-pin = 'e')"},
            {"SO", "Typical 0.65mm (pin-to-pin) or 1.27mm. NOT row-to-row (7.5mm)!"},
            {"SOIC", "Typical 0.65mm or 1.27mm (pin-to-pin = 'e', NOT body width!)"},
            {"DIP", "Typical 2.54mm (0.1 inch standard through-hole)"},
            {"RF Amplifier", "Check 'Package Information and Dimensions' section for dimension 'e'"},
            {"OP Amplifier", "Usually SOIC/SO package ? 0.65mm or 1.27mm"}
        },
        ExtractionHint = "? IMPORTANT: Pitch is usually ONLY in technical drawings (Package Dimensions section). Look for dimension lines between pins!"
    };
}
```

---

## Test-Plan

### Test-Szenarien

#### ? Szenario 1: RF Amplifier mit Pitch in Zeichnung
```
Datei: RF_Amplifier_QFN16.pdf
Erwartung: 
  - Pitch: 0.50 mm (aus Package Drawing)
  - Quelle: "Package Information and Dimensions" Section
```

#### ? Szenario 2: Amplifier ohne sichtbaren Pitch
```
Datei: Generic_Amplifier.pdf
Erwartung:
  - Pitch: "not found"
  - KEIN falscher Wert geraten!
```

#### ? Szenario 3: SOIC Package mit mehreren Dimensionen
```
Datei: IC_SOIC8.pdf
Pitch-Wert in Zeichnung: 1.27mm (pin-to-pin)
Body Width: 3.9mm (row-to-row) ? FALSCH!
Erwartung:
  - Pitch: 1.27 mm (kleinerer Wert = pin spacing)
```

### Validierungs-Checks

```csharp
// Nach Extraktion prüfen:
1. Ist Pitch im gültigen Bereich (0.3-5.0 mm)?
2. Falls mehrere Werte: Wurde der kleinere gewählt?
3. Bei "not found": Wurde wirklich keine Zeichnung gefunden?
4. Confidence Score: Ist er > 0.7 für Pitch-Wert?
```

---

## Erwartete Verbesserungen

### Vorher (Aktuell)
```json
{
  "Pitch": "not found",  ? 70% der Fälle, obwohl Wert vorhanden
  "ConfidenceLevel": "Low"
}
```

### Nachher (Verbessert)
```json
{
  "Pitch": "0.50 mm",  ? Aus technischer Zeichnung extrahiert
  "PitchSource": "Package Information and Dimensions, dimension 'e'",
  "ConfidenceLevel": "High"
}
```

### Metriken
- **Erkennungsrate Pitch:** 30% ? **85%+** (Ziel)
- **Falsch-Positive:** < 5% (durch Validierungsregeln)
- **Genauigkeit:** ± 0.05mm (akzeptabel bei hohem Detail-Level)

---

## Nächste Schritte

### 1. Prompt-Dateien aktualisieren ? (automatisch)
- Erstelle `pitch_extraction_guidelines.txt`
- Lade in Azure Blob Storage hoch

### 2. DMSCatalogService erweitern
- Füge Methode zum Laden der Guidelines hinzu
- Integriere in dynamische Prompts für relevante Komponenten

### 3. Testing durchführen
- Teste mit realen RF Amplifier PDFs
- Validiere Erkennungsrate
- Überprüfe False Positives

### 4. Dokumentation aktualisieren
- Füge Pitch-Extraktion Beispiele hinzu
- Erstelle Troubleshooting Guide

---

## Zusammenfassung

**Problem:**
Pitch wird nicht erkannt, weil er in technischen Zeichnungen versteckt ist.

**Lösung:**
- ? Vision API bereits vorhanden
- ? Prompt-Erweiterung mit spezifischen Zeichnungs-Anweisungen
- ? Validierungsregeln zur Qualitätssicherung
- ? Package-spezifische Hinweise

**Ergebnis:**
Pitch wird **verlässlich aus Zeichnungen extrahiert**, ohne zusätzliche Code-Änderungen an der Core-Logik.

---

**Erstellt:** 2025-01-XX  
**Status:** Implementation bereit  
**Review:** Pending
