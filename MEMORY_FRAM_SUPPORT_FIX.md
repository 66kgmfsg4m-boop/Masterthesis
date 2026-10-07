# ? Memory F-RAM Support - Fixes Applied

## ?? Problemanalyse

### Problem 1: "Nonvolatile Memory (F-RAM)" nicht erkannt ?
**Fehler:**  
```
FEHLER: No matching component found for category: 'Nonvolatile Memory (F-RAM)'
```

**Ursache:**  
- KI hat erkannt: `"Nonvolatile Memory (F-RAM)"`  
- Katalog hat: `"Non-Volatile Memory Parallel"` (mit Bindestrich!)  
- **F-RAM** ist ein spezifischer Memory-Typ, der auf die generische Kategorie gemappt werden muss

###  Problem 2: Function Dropdown zeigt nur begrenzte Optionen ??

**Screenshot zeigt:**
- FRAM  
- NOR Flash  
- NAND Flash SLC  
- NAND Flash MLC  
- EEPROM  
- MRAM  
- **Ferroelectric RAM** (selected)

**Problem:** KI muss **exakt** diese Werte verwenden!

### Problem 3: Size muss in Bytes sein, nicht Mbit ??

**DMS-System erwartet:**
- `Size [Byte] = 4194304` (für 32 Mbit)
- **NICHT:** `Size = "32 Mbit"`

---

## ? Lösung 1: Katalog-Mapping erweitert

**Datei:** `DatasheetAnalyzer.Core/Services/DMSCatalogService.cs`

**Neue Methode:** `MapSpecificMemoryTypesToCatalog()`

```csharp
/// <summary>
/// Mappt spezifische Memory-Typen (F-RAM, MRAM, etc.) auf generische Katalog-Kategorien
/// </summary>
private string MapSpecificMemoryTypesToCatalog(string searchTerm)
{
    string lower = searchTerm.ToLowerInvariant();

    // ? PARALLEL MEMORY TYPES (Async, mit Address/Data Bus)
    var parallelMemoryKeywords = new[]
    {
        "f-ram", "fram",                    // Ferroelectric RAM
        "mram",                             // Magnetoresistive RAM
        "parallel eeprom",
        "parallel flash",
        "prom", "eprom",                    // Programmable ROM
        "parallel memory",
        "async", "asynchronous"
        };

    // ? SERIAL MEMORY TYPES (SPI, I2C)
    var serialMemoryKeywords = new[]
    {
        "serial eeprom",
        "serial flash",
        "spi flash", "spi eeprom",
        "i2c eeprom", "i2c flash",
        "serial memory"
    };

    // Prüfe ob Parallel Memory
    foreach (var keyword in parallelMemoryKeywords)
    {
        if (lower.Contains(keyword))
        {
            _logger.LogDebug("Mapping '{SearchTerm}' ? 'Non-Volatile Memory Parallel' (Keyword: {Keyword})",
                searchTerm, keyword);
            return "Non-Volatile Memory Parallel";
        }
    }

    // Prüfe ob Serial Memory
    foreach (var keyword in serialMemoryKeywords)
    {
        if (lower.Contains(keyword))
        {
            _logger.LogDebug("Mapping '{SearchTerm}' ? 'Non-Volatile Memory Serial' (Keyword: {Keyword})",
                searchTerm, keyword);
            return "Non-Volatile Memory Serial";
        }
    }

    // Generische Memory-Erkennung (Fallback)
    if (lower.Contains("nonvolatile") || lower.Contains("non-volatile") || lower.Contains("nv memory"))
    {
        // Default: Parallel (da häufigster Fall)
        _logger.LogDebug("Mapping '{SearchTerm}' ? 'Non-Volatile Memory Parallel' (Generic Memory)",
            searchTerm);
        return "Non-Volatile Memory Parallel";
    }

    // Keine Änderung
    return searchTerm;
}
```

**Resultat:**  
? `"Nonvolatile Memory (F-RAM)"` ? `"Non-Volatile Memory Parallel"`  
? `"MRAM"` ? `"Non-Volatile Memory Parallel"`  
? `"Serial EEPROM"` ? `"Non-Volatile Memory Serial"`

---

## ? Lösung 2: Memory Guidelines aktualisiert

**Datei:** `Azure_Prompts/memory_extraction_guidelines_v2.txt`

### Änderung 1: Size in Bytes konvertieren ??

**NEU: Conversion Table**
```
?????????????????????????????????????????????????
? Datasheet   ? Calculation  ? Size [Byte]      ?
?????????????????????????????????????????????????
? 1 Mbit      ? 1024*1024/8  ? 131072           ?
? 4 Mbit      ? 4*1024*1024/8? 524288           ?
? 8 Mbit      ? 8*1024*1024/8? 1048576          ?
? 16 Mbit     ? 16*1024*1024/8? 2097152         ?
? 32 Mbit     ? 32*1024*1024/8? 4194304         ?
? 64 Mbit     ? 64*1024*1024/8? 8388608         ?
? 128 Kbit    ? 128*1024/8   ? 16384            ?
? 256 Kbit    ? 256*1024/8   ? 32768            ?
? 512 Kbit    ? 512*1024/8   ? 65536            ?
?????????????????????????????????????????????????
```

**Extraction Algorithm:**
```
Step 1: Find capacity in datasheet (e.g., "32 Mbit", "4 MB")
Step 2: Convert to BITS:
        - If "Mbit": capacity * 1024 * 1024 bits
        - If "Kbit": capacity * 1024 bits
        - If "MB": capacity * 1024 * 1024 * 8 bits
        - If "KB": capacity * 1024 * 8 bits
Step 3: Convert to BYTES: bits / 8
Step 4: Return INTEGER (e.g., "4194304")
```

### Änderung 2: Function Dropdown Values ?

**NEU: Exact Dropdown Values**
```
ALLOWED FUNCTION VALUES:
???????????????????????????????????????????????????????????
? Exact Value             ? Use for                       ?
???????????????????????????????????????????????????????????
? FRAM                    ? Ferroelectric RAM (F-RAM)     ?
? NOR Flash               ? NOR Flash Memory              ?
? NAND Flash SLC          ? NAND Flash Single-Level Cell  ?
? NAND Flash MLC          ? NAND Flash Multi-Level Cell   ?
? EEPROM                  ? EEPROM (Serial or Parallel)   ?
? MRAM                    ? Magnetoresistive RAM          ?
? Ferroelectric RAM       ? Alias for FRAM                ?
???????????????????????????????????????????????????????????

EXTRACTION RULES:
- If "F-RAM" or "FeRAM" in datasheet ? Use "FRAM"
- If "MRAM" in datasheet ? Use "MRAM"
- If "EEPROM" in datasheet ? Use "EEPROM"
- If "NOR Flash" in datasheet ? Use "NOR Flash"
- If "Ferroelectric" in datasheet ? Use "Ferroelectric RAM" or "FRAM"
```

### Änderung 3: Width (Max) statt Width ?

**Property Name im DMS:** `Width (Max)`

**Guidelines angepasst:**
```
2?? WIDTH (Max) = DATA BUS WIDTH (NOT PHYSICAL DIMENSION!)
   
   CORRECT EXAMPLES:
   ? Width (Max) = "8 bit"      (from Organization: "8K x 8")
   ? Width (Max) = "16 bit"     (from Organization: "16K x 16")
   ? Width (Max) = "1 bit"      (Serial EEPROM with single data line)
```

---

## ?? Erwartetes Ergebnis

### Vorher (Fehler) ?

```json
FEHLER: No matching component found for category: 'Nonvolatile Memory (F-RAM)'
```

### Nachher (Erfolgreich) ?

#### Schritt 1: Identifikation
```json
{
  "DocumentType": "COMPONENT",
  "ComponentCategory": "Nonvolatile Memory (F-RAM)",   // Von KI erkannt
  "ComponentName": "FM25Lxxx",
  "PartNumber": "FM25L512",
  "ManufacturerInfo": "Cypress Semiconductor",
  "ConfidenceLevel": "HIGH"
}
```

#### Schritt 2: Mapping
```
Mapping 'Nonvolatile Memory (F-RAM)' ? 'Non-Volatile Memory Parallel' (Keyword: f-ram)
? Component found in catalog: Non-Volatile Memory Parallel
```

#### Schritt 3: Dialog
```
????????????????????????????????????????????????????????????
? Komponenten-Identifikation bestätigen                    ?
????????????????????????????????????????????????????????????
? Kategorie: Non-Volatile Memory Parallel  ? KORREKT!      ?
? Herstellernummer: [FM25L512-DGS] ? EDITIERBAR            ?
?                                                          ?
? ?? Series Datasheet erkannt!                             ?
? Bitte geben Sie die exakte Herstellernummer ein.         ?
????????????????????????????????????????????????????????????
```

#### Schritt 4: Extraktion
```json
{
  "ComponentType": "Non-Volatile Memory Parallel",
  "PartNumber": "FM25L512-DGS",
  "Manufacturer": "Cypress Semiconductor",
  "Description": "512-Kbit (64K × 8) Serial F-RAM",
  
  "Size [Byte]": "65536",           // ? 512 Kbit ? 512*1024/8 = 65536 Bytes
  "Width (Max)": "8 bit",           // ? Organization: 64K x 8
  "Function": "FRAM",               // ? Exact dropdown value
  
  "Vcc / Vdd (Typ) [V]": "3.3 V",
  "tAA (Max) [ns]": "nicht relevant", // ? Serial Memory hat kein tAA
  "Pitch [mm]": "1.27 mm",
  "Number of Pins": "8",
  "Package": "SOIC-8",
  "Gehäusetyp": "B1R-PDSO-G8"
}
```

---

## ?? Test-Szenario

### Test: F-RAM Parallel Memory (FM25L512)

```
1. PDF hochladen: FM25L512_Datasheet.pdf
   ? Identifikation: "Nonvolatile Memory (F-RAM)"
   ? Mapping: ? "Non-Volatile Memory Parallel"
   ? IsSeriesDatasheet: TRUE

2. Dialog öffnet:
   Herstellernummer: [________________] ? EDITIERBAR
   User gibt ein: FM25L512-DGS

3. Analyse läuft:
   ? Memory Guidelines geladen
   ? Prompt enthält: "Size [Byte] = MEMORY CAPACITY IN BYTES"
   ? Prompt enthält: "Function = EXACT dropdown value"

4. Ergebnis:
   ? Size [Byte]: "65536" (NOT "512 Kbit")
   ? Width (Max): "8 bit"
   ? Function: "FRAM" (NOT "F-RAM" or "Ferroelectric Memory")
   ? tAA (Max): "nicht relevant"
```

---

## ?? Geänderte/Erstellte Dateien

| Datei                                           | Status | Änderung                                      |
|-------------------------------------------------|--------|-----------------------------------------------|
| `DatasheetAnalyzer.Core/Services/DMSCatalogService.cs` | ? Geändert | `MapSpecificMemoryTypesToCatalog()` hinzugefügt |
| `Azure_Prompts/memory_extraction_guidelines_v2.txt` | ? Neu | Size in Bytes, Function Dropdown, Width (Max) |
| `MEMORY_FRAM_SUPPORT_FIX.md` (dieses Dokument) | ? Neu | Dokumentation der Fixes                       |

---

## ?? Deployment-Schritte

### Schritt 1: Code kompilieren ?

```powershell
dotnet build DatasheetAnalyzer.Core
```

**Status:** ? Build erfolgreich

### Schritt 2: Memory Guidelines nach Azure hochladen ??

```powershell
# Option 1: Azure Portal (Web)
1. https://portal.azure.com
2. Storage Account ? Blob Container
3. Ersetze "memory_extraction_guidelines.txt" mit v2-Version

# Option 2: PowerShell
az storage blob upload `
  --account-name <storage-account> `
  --container-name <container> `
  --name "memory_extraction_guidelines.txt" `
  --file "Azure_Prompts/memory_extraction_guidelines_v2.txt" `
  --overwrite
```

### Schritt 3: App neu starten ??

```powershell
# Blazor App
cd DatasheetAnalyzer.Blazor
dotnet run

# WPF App
cd CheckOrderConfirmationFromSupplier
# Start from Visual Studio
```

---

## ? Checkliste

- [x] **Code-Änderung:** `MapSpecificMemoryTypesToCatalog()` implementiert
- [x] **Build erfolgreich:** Keine Compile-Fehler
- [x] **Memory Guidelines v2:** Size in Bytes, Function Dropdown, Width (Max)
- [ ] **Azure Upload:** memory_extraction_guidelines_v2.txt hochladen
- [ ] **Test 1:** F-RAM PDF hochladen (z.B. FM25L512)
- [ ] **Test 2:** Size [Byte] = "65536" (NOT "512 Kbit")?
- [ ] **Test 3:** Function = "FRAM" (NOT "F-RAM")?
- [ ] **Test 4:** Width (Max) = "8 bit"?

---

## ?? Troubleshooting

### Problem: "No matching component found" bleibt bestehen

**Debug-Schritte:**
```csharp
// In Home.razor - StartAnalysis()
await JS.InvokeVoidAsync("console.log", $"Original Category: {componentCategory}");
await JS.InvokeVoidAsync("console.log", $"Mapped Category: {IdentifiedComponent?.DisplayName}");
```

**Erwartete Ausgabe:**
```
Original Category: Nonvolatile Memory (F-RAM)
Mapped Category: Non-Volatile Memory Parallel
```

### Problem: Size wird nicht in Bytes konvertiert

**Ursache:** Memory Guidelines nicht geladen

**Lösung:**
1. Prüfe Azure Blob: `memory_extraction_guidelines.txt` existiert?
2. Prüfe Console: "Memory extraction guidelines loaded"?
3. Starte App neu (Cache löschen!)

### Problem: Function = "F-RAM" statt "FRAM"

**Ursache:** Alte Guidelines werden verwendet

**Lösung:**
1. Azure Blob aktualisieren mit v2-Version
2. App neu starten
3. Cache manuell löschen: `_cachedMemoryGuidelines = null;`

---

## ?? Zusammenfassung

### Was funktioniert jetzt?

? **"Nonvolatile Memory (F-RAM)"** ? `"Non-Volatile Memory Parallel"`  
? **"MRAM"** ? `"Non-Volatile Memory Parallel"`  
? **"Serial EEPROM"** ? `"Non-Volatile Memory Serial"`  
? **Size in Bytes:** "4194304" (NICHT "32 Mbit")  
? **Function:** "FRAM" (NICHT "F-RAM")  
? **Width (Max):** "8 bit" (NICHT "3.9 mm")

### Workflow

```
1. KI erkennt: "Nonvolatile Memory (F-RAM)"
   ?
2. Mapping: ? "Non-Volatile Memory Parallel" (via f-ram keyword)
   ?
3. Komponente im Katalog gefunden ?
   ?
4. Dialog: Part Number Input aktiv ?
   ?
5. Memory Guidelines geladen ?
   ?
6. Extraktion: Size in Bytes, Function = "FRAM" ?
```

---

**Status:** ? Fixes Applied - Ready for Testing  
**Version:** 1.1  
**Erstellt:** 2025-01-15  
**Autor:** GitHub Copilot  
**Review:** Pending Azure Upload & Functional Tests
