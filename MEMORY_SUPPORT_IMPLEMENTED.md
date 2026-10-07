# ? NON-VOLATILE MEMORY UNTERSTÜTZUNG IMPLEMENTIERT

## ?? Was wurde implementiert?

### 1. **Memory-Guidelines erstellt** (`memory_extraction_guidelines.txt`)

#### Wichtigste Regeln:

**Serial vs. Parallel Unterscheidung:**
```
Serial Memory (SPI/I2C):
  - tAA: "nicht relevant" (kein Address Access!)
  - Interface: "SPI", "I2C"
  - Width: "1 bit" (serielle Datenübertragung)
  - Pins: 6-8
  - Package: SOIC-8, DFN-8

Parallel Memory:
  - tAA_Max: Aus MAX-Spalte (z.B. "55 ns")
  - Interface: "Parallel", "Asynchronous"
  - Width: "8 bit", "16 bit" (aus Organization)
  - Pins: 20-48
  - Package: DIP-28, TSOP-32
```

**Width (Data Bus Width) - WICHTIG:**
```
? CORRECT: Width = "8 bit" (aus Organization "8K x 8")
? CORRECT: Width_Max = "16 bit" (MAX-Spalte)
? WRONG: Width = "3.9 mm" (das ist Package-Breite!)
```

**tAA (Address Access Time):**
```
Property: tAA_Max
? Lese MAX-Spalte aus AC Characteristics
? WICHTIG: Nur für Parallel Memory!
? Für Serial Memory: "nicht relevant"
```

**Min/Typ/Max Logic (wie bei RF Amplifier):**
```
tAA_Max ? MAX-Spalte
tRC_Min ? MIN-Spalte
tWC_Typ ? TYP-Spalte
tAA (ohne Suffix) ? TYP-Spalte (oder MAX wenn TYP fehlt)
```

---

### 2. **Vision-Analysis für Memory aktiviert**

#### DatasheetAnalysisService.cs - RequiresVisionAnalysis()
```csharp
var visionRequiredComponents = new[]
{
    "RF Amplifier", "Amplifier", ...
    "Non-Volatile Memory Serial",    // ? NEU
    "Non-Volatile Memory Parallel",  // ? NEU
    "NV Memory", "Memory"
};
```

**Effekt:**
- Memory-ICs mit Bildern ? Vision API aktiviert
- Ermöglicht Extraktion von Pitch aus Package Dimensions
- Ermöglicht Timing-Tabellen-Erkennung

---

### 3. **Pitch-Extraktion für Memory-ICs**

#### DMSCatalogService.cs - RequiresPitchGuidelines()
```csharp
var pitchRelevantComponents = new[]
{
    "Amplifier", ...
    "Non-Volatile Memory Serial",     // ? NEU
    "Non-Volatile Memory Parallel",   // ? NEU
    "NV Memory", "Memory"
};
```

**Pitch-Werte für Memory:**
- Serial SOIC-8: 1.27mm oder 0.65mm
- Parallel DIP-28: 2.54mm (0.1")
- Parallel TSOP-32: 0.5mm oder 0.8mm

---

### 4. **Property Definitions erweitert**

Neue Definitionen für Memory-Parameter:
```
tAA_Max=Maximum Address Access Time (MAX column, nur Parallel!)
tRC_Min=Minimum Read Cycle Time (MIN column)
Capacity=Memory Capacity (64 Kbit, 1 Mbit)
Organization=Memory Organization (8K x 8, 128K x 16)
Width=Data Bus Width in bits (NOT physical dimension!)
Width_Max=Maximum Data Bus Width (MAX column)
Interface=Interface Type (SPI, I2C, Parallel)
```

---

### 5. **Memory-Guidelines Integration**

#### DMSCatalogService.cs - GenerateDynamicPrompt()
```csharp
// Pitch-Guidelines (wie bisher)
if (RequiresPitchGuidelines(component, properties)) {
    promptBuilder.AppendLine(LoadPitchGuidelinesIfNeeded());
}

// ? NEU: Memory-Guidelines
if (RequiresMemoryGuidelines(component, properties)) {
    promptBuilder.AppendLine(LoadMemoryGuidelinesIfNeeded());
}
```

**Automatische Erkennung:**
- Component enthält "Memory" ? Memory-Guidelines geladen
- Properties enthalten tAA, Capacity, Organization ? Memory-Guidelines geladen

---

## ?? Welche Komponenten sind jetzt unterstützt?

| Komponente | Vision API | Pitch | Spezial-Guidelines | Status |
|------------|------------|-------|-------------------|--------|
| **RF Amplifier** | ? | ? | Pitch | ? Implementiert |
| **OP Amplifier** | ? | ? | Pitch | ? Implementiert |
| **Connector** | ? | ? | Pitch | ? Implementiert |
| **Non-Volatile Memory Serial** | ? | ? | Memory | ? **NEU!** |
| **Non-Volatile Memory Parallel** | ? | ? | Memory | ? **NEU!** |
| **Resistor** | ? | ? | - | ? Implementiert |
| **Capacitor** | ? | ? | - | ? Implementiert |

---

## ?? Wie funktioniert die Extraktion für Memory?

### Beispiel: Non-Volatile Memory Parallel (DIP-28, 256 Kbit)

#### PDF enthält:
```
Features:
  - 256 Kbit (32K x 8)
  - Parallel Asynchronous Interface

AC Characteristics:
  Symbol | Min | Typ | Max | Unit
  tAA    | -   | -   | 55  | ns   Address access time
  tRC    | 55  | -   | -   | ns   Read cycle time

Package: DIP-28, Pitch = 2.54mm (0.1")
```

#### Prompt wird generiert mit:
1. **Property-Liste:** (aus get_classes.csv)
   ```
   tAA_Max, tRC_Min, Capacity, Organization, Width, 
   Interface, Pitch, PackageType, NumberOfPin
   ```

2. **Property Definitions:** (erklärt was tAA_Max bedeutet)
   ```
   tAA_Max=Maximum Address Access Time (MAX column, nur Parallel!)
   Width=Data Bus Width in bits (NOT physical dimension!)
   ```

3. **Memory-Guidelines:** (spezifische Regeln)
   ```
   - tAA nur für Parallel Memory (Serial: "nicht relevant")
   - Width aus Organization extrahieren (8K x 8 ? "8 bit")
   - tAA_Max ? Lese MAX-Spalte
   ```

4. **Pitch-Guidelines:** (für Package Dimensions)
   ```
   - DIP-28: Pitch typisch 2.54mm
   - Suche nach Dimension 'e' in mechanical drawing
   ```

#### Erwartetes Ergebnis:
```json
{
  "ComponentType": "Non-Volatile Memory Parallel",
  "PartNumber": "AT28C256-15PU",
  "Manufacturer": "Atmel",
  "tAA_Max": "55 ns",
  "tRC_Min": "55 ns",
  "Capacity": "256 Kbit",
  "Organization": "32K x 8",
  "Width": "8 bit",
  "Interface": "Parallel",
  "Pitch": "2.54 mm",
  "PackageType": "DIP-28",
  "NumberOfPin": "28"
}
```

---

### Beispiel: Non-Volatile Memory Serial (SOIC-8, 1 Mbit SPI)

#### PDF enthält:
```
Features:
  - 1 Mbit (128K x 8)
  - SPI Serial Interface

Package: SOIC-8, Pitch = 1.27mm
```

#### Erwartetes Ergebnis:
```json
{
  "ComponentType": "Non-Volatile Memory Serial",
  "PartNumber": "AT25M01",
  "Manufacturer": "Atmel",
  "tAA": "nicht relevant",  ? ? Serial hat keinen Address Access!
  "Capacity": "1 Mbit",
  "Organization": "128K x 8",
  "Width": "1 bit",  ? ? Serial = 1 bit
  "Interface": "SPI",
  "Pitch": "1.27 mm",
  "PackageType": "SOIC-8",
  "NumberOfPin": "8"
}
```

---

## ?? Test-Anleitung

### Schritt 1: App neu starten
```sh
cd DatasheetAnalyzer.Blazor
dotnet build
dotnet run
```

### Schritt 2: Memory PDF hochladen
```
Browser: https://localhost:5001
? Upload: Memory Datasheet (Serial oder Parallel)
```

### Schritt 3: Console-Output prüfen

#### Erwarteter Output:
```
[PDFProcessor] PDF-Analyse: Seiten=16, Bilder=12 (gerendert)
[DatasheetAnalysisService] Component Non-Volatile Memory Parallel requires vision analysis
[DatasheetAnalysisService] ?? HYBRID-MODUS aktiviert
[DMSCatalogService] Memory extraction guidelines loaded from Azure (16034 characters)
[DMSCatalogService] Pitch extraction guidelines loaded from Azure (13245 characters)
[DMSCatalogService] ? Memory guidelines integrated into prompt
[DMSCatalogService] ? Pitch guidelines integrated into prompt

[DatasheetAnalysisService] ?? Sending 12 images to Vision API

Result:
{
  "tAA_Max": "55 ns",  ? ? Aus MAX-Spalte
  "Width": "8 bit",    ? ? Data Bus Width (NICHT mm!)
  "Capacity": "256 Kbit",
  "Organization": "32K x 8",
  "Interface": "Parallel",
  "Pitch": "2.54 mm"
}
```

---

## ?? Was wurde implementiert?

| Feature | Status | Details |
|---------|--------|---------|
| **Memory-Guidelines** | ? | 16 KB detaillierte Anweisungen |
| **Azure Upload** | ? | memory_extraction_guidelines.txt hochgeladen |
| **Vision API** | ? | Memory-ICs nutzen Vision API |
| **Pitch-Extraktion** | ? | Auch für Memory-ICs (SOIC, DIP, TSOP) |
| **tAA für Parallel** | ? | Aus MAX-Spalte, nur für Parallel! |
| **tAA für Serial** | ? | "nicht relevant" |
| **Width Disambiguation** | ? | Data Bus Width (8 bit), NICHT mm! |
| **Min/Typ/Max Logic** | ? | Property-Suffix bestimmt Spalte |
| **Embedded Fallback** | ? | Funktioniert auch ohne Azure |
| **Property Definitions** | ? | tAA_Max, Width, Capacity, etc. |

---

## ?? Zusammenfassung

**Komponenten hinzugefügt:**
- ? Non-Volatile Memory Serial (SPI/I2C)
- ? Non-Volatile Memory Parallel (Address/Data Bus)

**Spezielle Regeln:**
- ? tAA nur für Parallel (Serial: "nicht relevant")
- ? Width = Data Bus Width in bits (NICHT physische Breite!)
- ? tAA_Max ? MAX-Spalte, tRC_Min ? MIN-Spalte
- ? Capacity/Organization: Exakt wie im Datenblatt
- ? Interface: Spezifisch (SPI/I2C/Parallel)
- ? Pitch: Aus Package Dimensions (wie RF Amplifier)

**Status:** ?? **FERTIG! Ready to Test!**

**Teste mit:**
- Non-Volatile Memory Serial PDF (z.B. AT25M01, EEPROM SPI)
- Non-Volatile Memory Parallel PDF (z.B. AT28C256, SRAM)

Beide sollten jetzt korrekt analysiert werden mit Memory-spezifischen Guidelines! ??
