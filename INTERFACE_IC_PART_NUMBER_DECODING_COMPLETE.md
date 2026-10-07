# Interface IC Support mit Part Number Decoding - Implementierungsübersicht

**Datum:** 2025-01-XX  
**Feature:** Interface IC Component Support mit 2-Stage Part Number Decoding  
**Status:** ? Implementiert (Code-Änderungen abgeschlossen, Azure Prompts bereit zum Upload)

---

## ?? Übersicht

Diese Implementierung fügt vollständige Unterstützung für **Interface IC** Komponenten hinzu, inklusive automatischer Part Number Dekodierung zur Extraktion von:
- **Funktion** (z.B. "Driver , Receiver , Transceiver")
- **Interface_internal** (z.B. "UART", "SPI", "I2C")
- **Interface_external** (z.B. "RS-232", "RS-485", "CAN")
- **NumberOfPin** (z.B. "16", "24")
- **PackageType** (z.B. "SOIC-16", "TSSOP-16", "QFN-24")
- **Pitch** (z.B. "0.5mm", "0.65mm", "1.27mm")

---

## ??? Architektur-Änderungen

### 1. **DatasheetAnalysisService.cs** (DatasheetAnalyzer.Core)

#### Generalisierung: Memory ? Generic Part Number Decoding

**Vorher:**
```csharp
bool hasPartNumberForMemory = IsMemoryComponent(component) && !string.IsNullOrWhiteSpace(partNumber);
JObject? decodedMemoryProperties = await DecodeMemoryPartNumberAsync(...);
```

**Nachher:**
```csharp
bool hasPartNumberForDecoding = RequiresPartNumberDecoding(component) && !string.IsNullOrWhiteSpace(partNumber);
JObject? decodedProperties = await DecodePartNumberAsync(...);
```

#### Neue Methoden:

1. **`RequiresPartNumberDecoding(CatalogComponent component)`**  
   Prüft ob Component Part Number Decoding benötigt (Memory + Interface IC)
   
   ```csharp
   var keywords = new[]
   {
       // Memory Components
       "Non-Volatile Memory Serial", "Non-Volatile Memory Parallel",
       "EEPROM Serial", "EEPROM Parallel", "Flash Serial", "Flash Parallel", "SRAM",
       // Interface ICs
       "Interface IC"
   };
   ```

2. **`GetPartNumberDecodingType(CatalogComponent component)`**  
   Bestimmt Komponententyp für Prompt-Auswahl:
   - Memory Components ? `"Memory"`
   - Interface IC ? `"InterfaceIC"`
   - Fallback ? `component.DisplayName.Replace(" ", "")`

3. **`DecodePartNumberAsync(PDFProcessor.PDFContent pdfContent, string partNumber, CatalogComponent component)`**  
   Generische 2-Stage Part Number Dekodierung:
   - **Stage 1:** Extrahiert Part Number Chart Struktur (`SeriesDatasheetInstructions_{type}.txt`)
   - **Stage 2:** Dekodiert spezifische Part Number (`PartNumberDecodingPrompt_{type}.txt`)

4. **`CombineDecodedPropertiesWithPdf(JObject resultJson, JObject decodedProperties, CatalogComponent component)`**  
   Intelligent Merge Strategy:
   - **Part Number Decoding bevorzugt:** `SizeByte`, `Width`, `PackageType`, `Interface_internal`, `Interface_external`
   - **PDF bevorzugt:** `Funktion` (Dropdown-Klassifikation!), `NumberOfPin`, `Pitch`, `tAA_Max`
   - **Fallback:** Part Number Decoding nur wenn PDF keinen Wert hat

---

### 2. **DMSCatalogService.cs** (DatasheetAnalyzer.Core)

#### Erweiterung: Series Datasheet Detection

**Methode:** `IsSeriesDatasheet(CatalogComponent component)`

**Vorher:**
```csharp
var seriesComponents = new[]
{
    "Resistor",
    "Non-Volatile Memory Serial",
    "Non-Volatile Memory Parallel",
    // ...
};
```

**Nachher:**
```csharp
var seriesComponents = new[]
{
    "Resistor",
    "Non-Volatile Memory Serial",
    "Non-Volatile Memory Parallel",
    // ...
    "Interface IC"  // ? NEU!
};
```

---

### 3. **IdentificationDialog.razor** (DatasheetAnalyzer.Blazor)

#### UI-Änderung: Series Datasheet Hinweis

**Vorher:**
```html
<li><strong>Memory Components:</strong> Size, Width, Package</li>
<li><strong>Resistors:</strong> Resistance, Tolerance, Package</li>
```

**Nachher:**
```html
<li><strong>Memory Components:</strong> Size, Width, Package</li>
<li><strong>Interface ICs:</strong> Funktion, Interface, Package</li>
<li><strong>Resistors:</strong> Resistance, Tolerance, Package</li>
```

---

## ?? Neue Azure Prompt-Dateien

### 1. **SeriesDatasheetInstructions_InterfaceIC.txt**

**Zweck:** Extrahiert Part Number Chart Struktur aus Interface IC Datasheets  
**Location:** `Azure_Prompts/SeriesDatasheetInstructions_InterfaceIC.txt`

**Key Features:**
- Erkennung von typischen Part Number Segmenten:
  1. Base Model (z.B. `MAX3232`)
  2. Interface Type (z.B. `E` = Transceiver)
  3. Number of Channels (z.B. `2` = 2 Kanäle)
  4. Voltage/Speed (z.B. `C` = 3.3V)
  5. Package Type (z.B. `UE` = TSSOP-16)
  6. Temperature Range (z.B. `I` = Industrial)
  7. Tape & Reel Suffix (z.B. `+T`)

- Mapping zu "Funktion" Dropdown-Optionen:
  - "Interface Controller"
  - "Driver , Receiver , Transceiver"
  - "Switch , Mux , Demultiplexer"
  - "Signal Buffer , Repeater , Splitter"
  - "Serializer , Deserializer"
  - "Coder , Decoder ( CODEC )"
  - "I/O Expander"
  - "Bridge"

- Interface Detection:
  - **Internal:** UART, SPI, I2C, USB, PCIe
  - **External:** RS-232, RS-485, CAN, LIN, Ethernet

**Output Format:**
```json
{
  "ChartFound": true,
  "ChartDescription": "Part number breakdown for MAX3232 RS-232 transceiver family",
  "ChartLocation": "Page 14, Ordering Information",
  "PartNumberFormat": "MAX3232ECUE+T",
  "Segments": [
    {
      "Position": 1,
      "Name": "Base Model",
      "Example": "MAX3232",
      "PossibleValues": []
    },
    {
      "Position": 2,
      "Name": "Interface Type",
      "Example": "E",
      "PossibleValues": [
        {"Code": "E", "Meaning": "RS-232 Transceiver", "Funktion": "Driver , Receiver , Transceiver"}
      ]
    }
  ],
  "InterfaceMappings": {
    "Internal": "UART",
    "External": "RS-232"
  }
}
```

---

### 2. **PartNumberDecodingPrompt_InterfaceIC.txt**

**Zweck:** Dekodiert spezifische Part Number basierend auf extrahierter Chart-Struktur  
**Location:** `Azure_Prompts/PartNumberDecodingPrompt_InterfaceIC.txt`

**Platzhalter:**
- `{PART_NUMBER}` - Zu dekodierende Herstellernummer (z.B. `MAX3232ECUE+T`)
- `{COMPONENT_CATEGORY}` - Component-Typ (z.B. `Interface IC`)
- `{PART_NUMBER_STRUCTURE}` - Extrahierte Chart-Struktur aus Stage 1

**Key Features:**
- **Funktion Klassifikations-Algorithmus:**
  ```
  IF "Controller" or "Manager" ? "Interface Controller"
  ELSE IF "Transceiver" or "Driver+Receiver" ? "Driver , Receiver , Transceiver"
  ELSE IF "Multiplexer" or "Switch" ? "Switch , Mux , Demultiplexer"
  ELSE IF "Buffer" or "Repeater" ? "Signal Buffer , Repeater , Splitter"
  ELSE IF "SerDes" or "Serializer" ? "Serializer , Deserializer"
  ELSE IF "CODEC" or "Encoder" ? "Coder , Decoder ( CODEC )"
  ELSE IF "Expander" or "Extender" ? "I/O Expander"
  ELSE IF "Bridge" or "Translator" ? "Bridge"
  ELSE ? "nicht vorhanden"
  ```

- **Pitch Detection:**
  - SOIC: `1.27mm` (0.050")
  - TSSOP: `0.65mm` (0.026")
  - QFN: `0.5mm` (0.020")
  - PDIP: `2.54mm` (0.1")

- **Interface Beispiele:**
  - `MAX3232` (UART-to-RS232): Internal=UART, External=RS-232
  - `MCP2515` (SPI-to-CAN): Internal=SPI, External=CAN
  - `FT232` (USB-to-UART): Internal=USB, External=UART

**Output Format:**
```json
{
  "PartNumber": "MAX3232ECUE+T",
  "DecodingSuccess": true,
  "SegmentBreakdown": [
    {
      "Position": 1,
      "SegmentName": "Base Model",
      "ExtractedValue": "MAX3232",
      "Meaning": "RS-232 transceiver family"
    },
    {
      "Position": 2,
      "SegmentName": "Interface Type",
      "ExtractedValue": "E",
      "Meaning": "RS-232 Transceiver (2 Tx, 2 Rx)",
      "MappedFunktion": "Driver , Receiver , Transceiver"
    }
  ],
  "ExtractedProperties": {
    "Funktion": "Driver , Receiver , Transceiver",
    "Interface_internal": "UART",
    "Interface_external": "RS-232",
    "NumberOfPin": "16",
    "PackageType": "TSSOP-16",
    "Pitch": "0.65mm"
  }
}
```

---

## ?? Workflow: Interface IC Analyse

### Stage 1: Identification (wie bisher)
```
PDF ? Azure OpenAI (IdentificationPrompt.txt)
  ?
{
  "DocumentType": "COMPONENT",
  "ComponentCategory": "Interface IC",
  "PartNumber": "MAX3232ECUE+T",
  "Manufacturer": "Maxim Integrated"
}
```

### Stage 2a: Part Number Decoding (NEU für Interface IC!)
```
PDF + PartNumber ? Azure OpenAI (SeriesDatasheetInstructions_InterfaceIC.txt)
  ?
{
  "ChartFound": true,
  "Segments": [...]  // Part Number Chart Struktur
}
  ?
PDF + PartNumber + Chart ? Azure OpenAI (PartNumberDecodingPrompt_InterfaceIC.txt)
  ?
{
  "ExtractedProperties": {
    "Funktion": "Driver , Receiver , Transceiver",
    "Interface_internal": "UART",
    "Interface_external": "RS-232",
    "PackageType": "TSSOP-16",
    "Pitch": "0.65mm"
  }
}
```

### Stage 2b: PDF Property Extraction (parallel)
```
PDF ? Azure OpenAI (DynamicPrompt mit Interface IC Properties)
  ?
{
  "Funktion": "Driver , Receiver , Transceiver",  // ? Aus PDF-Klassifikation
  "Interface_internal": "UART",
  "Interface_external": "RS-232",
  "NumberOfPin": "16",
  "Pitch": "0.65mm",
  "Max. case temp. °C": "85",
  // ...
}
```

### Stage 3: Hybrid Merge (Intelligent Combination)
```
CombineDecodedPropertiesWithPdf(pdfResult, decodedResult)
  ?
FINAL RESULT:
{
  "Funktion": "Driver , Receiver , Transceiver",  // ? PDF bevorzugt (Dropdown!)
  "Interface_internal": "UART",                   // ? Part Number Decoding
  "Interface_external": "RS-232",                 // ? Part Number Decoding
  "NumberOfPin": "16",                            // ? PDF (genauer!)
  "PackageType": "TSSOP-16",                      // ? Part Number Decoding
  "Pitch": "0.65mm",                              // ? PDF oder Part Number Decoding
  "_DataSource": "Hybrid (PartNumberDecoding + PDF)"
}
```

---

## ? Vorteile der Hybrid-Strategie

### Memory Components (bestehend)
- **Part Number Decoding:** Präzise für `SizeByte`, `Width`, `PackageType`
- **PDF Extraction:** Präziser für `Funktion`, `NumberOfPin`, Timing-Parameter

### Interface ICs (neu)
- **Part Number Decoding:** Präzise für `Interface_internal`, `Interface_external`, `PackageType`
- **PDF Extraction:** Präziser für `Funktion` (aus Dropdown-Klassifikation!), `NumberOfPin`, `Pitch`

### Warum "Funktion" immer aus PDF?
- **Dropdown-Werte** sind sehr spezifisch (z.B. "Driver , Receiver , Transceiver")
- Part Number Chart könnte nur generisches "Transceiver" enthalten
- PDF-Text enthält präzisere Funktionsbeschreibung ? Besseres Klassifikations-Matching

---

## ?? Testing-Szenarien

### Test 1: MAX3232 (RS-232 Transceiver)
**Part Number:** `MAX3232ECUE+T`  
**Expected Output:**
```json
{
  "Funktion": "Driver , Receiver , Transceiver",
  "Interface_internal": "UART",
  "Interface_external": "RS-232",
  "NumberOfPin": "16",
  "PackageType": "TSSOP-16",
  "Pitch": "0.65mm"
}
```

### Test 2: MCP2515 (CAN Controller)
**Part Number:** `MCP2515-I/SO`  
**Expected Output:**
```json
{
  "Funktion": "Interface Controller",
  "Interface_internal": "SPI",
  "Interface_external": "CAN",
  "NumberOfPin": "18",
  "PackageType": "SOIC-18",
  "Pitch": "1.27mm"
}
```

### Test 3: FT232RL (USB-to-UART Bridge)
**Part Number:** `FT232RL-REEL`  
**Expected Output:**
```json
{
  "Funktion": "Bridge",
  "Interface_internal": "USB",
  "Interface_external": "UART",
  "NumberOfPin": "28",
  "PackageType": "SSOP-28",
  "Pitch": "0.65mm"
}
```

---

## ?? Nächste Schritte (Manuelle Aktionen erforderlich)

### 1. Azure Blob Storage Upload
**Dateien hochladen:**
```powershell
# Annahme: Azure Storage Account ist bereits konfiguriert
az storage blob upload --account-name <account> --container-name prompts \
  --file Azure_Prompts/SeriesDatasheetInstructions_InterfaceIC.txt \
  --name SeriesDatasheetInstructions_InterfaceIC.txt

az storage blob upload --account-name <account> --container-name prompts \
  --file Azure_Prompts/PartNumberDecodingPrompt_InterfaceIC.txt \
  --name PartNumberDecodingPrompt_InterfaceIC.txt
```

### 2. Katalog-CSV aktualisieren (falls notwendig)
**Prüfen:** Ist "Interface IC" bereits in `get_classes.csv` vorhanden?  
**Wenn NEIN:** Neue Zeilen hinzufügen für Interface IC mit Properties:
```
Funktion, Interface_internal, Interface_external, NumberOfPin, Pitch, 
Max. case temp. °C, Max. ambient temp. °C, Min. case temp. °C, Min. ambient temp. °C
```

### 3. Blazor App neu starten
```powershell
dotnet run --project DatasheetAnalyzer.Blazor
```

### 4. End-to-End Test mit echtem Interface IC Datasheet
**Test-Datasheet:** MAX3232, MCP2515, FT232, oder ähnlich  
**Erwartung:**
1. ? Identification erkennt "Interface IC"
2. ? Series Datasheet Warning erscheint im Dialog
3. ? Part Number Decoding extrahiert Interface-Parameter
4. ? Hybrid-Merge kombiniert Ergebnisse korrekt

---

## ?? Unterstützte Funktions-Typen (Dropdown-Optionen)

| Funktion | Beschreibung | Beispiel-ICs |
|----------|-------------|--------------|
| **Interface Controller** | Verwaltet komplettes Protokoll | MCP2515 (CAN), W5500 (Ethernet) |
| **Driver , Receiver , Transceiver** | Bidirektionale Signal-Konvertierung | MAX3232 (RS-232), MAX485 (RS-485) |
| **Switch , Mux , Demultiplexer** | Signal-Routing/Selektion | CD4051 (Analog Mux), 74HC4051 |
| **Signal Buffer , Repeater , Splitter** | Signal-Verstärkung/-Verteilung | 74HC244 (Buffer), LTC1480 (Repeater) |
| **Serializer , Deserializer** | Parallel ? Serial Konvertierung | SN65LVDS387 (SerDes), DS90C124 |
| **Coder , Decoder ( CODEC )** | Daten-Kodierung/-Dekodierung | ADV7611 (HDMI Decoder), ADV7343 (Video Encoder) |
| **I/O Expander** | Erweitert MCU I/O-Pins | MCP23017 (I2C Expander), PCF8574 |
| **Bridge** | Verbindet unterschiedliche Busse | FT232 (USB-to-UART), CH340 (USB-to-UART) |

---

## ?? Verwandte Dateien

### Code-Änderungen:
- ? `DatasheetAnalyzer.Core/Services/DatasheetAnalysisService.cs`
- ? `DatasheetAnalyzer.Core/Services/DMSCatalogService.cs`
- ? `DatasheetAnalyzer.Blazor/Components/IdentificationDialog.razor`

### Neue Prompt-Dateien:
- ? `Azure_Prompts/SeriesDatasheetInstructions_InterfaceIC.txt`
- ? `Azure_Prompts/PartNumberDecodingPrompt_InterfaceIC.txt`

### Bestehende Prompt-Dateien (Referenz):
- `Azure_Prompts/SeriesDatasheetInstructions_Memory.txt`
- `Azure_Prompts/PartNumberDecodingPrompt_Memory.txt`
- `Azure_Prompts/IdentificationPrompt.txt`

---

## ?? Zusammenfassung

? **Interface IC Support vollständig implementiert**  
? **Generisches Part Number Decoding Framework erstellt**  
? **Hybrid-Merge Strategie optimiert**  
? **Azure Prompts bereit zum Upload**  
? **UI aktualisiert mit Series Datasheet Hinweis**  

**Status:** Code-Änderungen abgeschlossen. Bereit für Azure Upload und Testing.

---

**Erstellt von:** GitHub Copilot  
**Review erforderlich:** Azure Prompts Upload + End-to-End Testing
