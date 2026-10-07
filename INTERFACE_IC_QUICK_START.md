# Interface IC Analyse - Quick Start Guide

Schnellanleitung zur Verwendung der neuen Interface IC Part Number Decoding Funktionalität.

---

## ?? Voraussetzungen

- ? Code-Änderungen bereits implementiert
- ? Azure Prompts bereits erstellt (`SeriesDatasheetInstructions_InterfaceIC.txt`, `PartNumberDecodingPrompt_InterfaceIC.txt`)
- ? **TODO:** Azure Prompts nach Blob Storage hochladen

---

## ?? Setup (Einmalig)

### 1. Azure Prompts hochladen

```powershell
# Setze Umgebungsvariablen (falls noch nicht geschehen)
$env:AZURE_STORAGE_ACCOUNT = "your-storage-account"
$env:AZURE_STORAGE_CONTAINER = "prompts"
$env:AZURE_STORAGE_SAS_TOKEN = "sv=2024-01-01&..."

# Upload Interface IC Prompts
.\upload_interface_ic_prompts.ps1
```

**Alternative: Manueller Upload via Azure Portal**
1. Öffne Azure Portal ? Storage Account ? Container "prompts"
2. Upload `SeriesDatasheetInstructions_InterfaceIC.txt`
3. Upload `PartNumberDecodingPrompt_InterfaceIC.txt`

### 2. Blazor App starten

```powershell
cd DatasheetAnalyzer.Blazor
dotnet run
```

Browser öffnet automatisch: `https://localhost:5001`

---

## ?? Testen mit Interface IC Datasheet

### Test 1: MAX3232 (RS-232 Transceiver)

**1. Datasheet herunterladen:**
- Hersteller: Maxim Integrated (jetzt Analog Devices)
- Part Number: `MAX3232ECUE+T`
- URL: https://www.analog.com/media/en/technical-documentation/data-sheets/MAX3232.pdf

**2. In Blazor App hochladen:**
- Drag & Drop PDF auf Upload-Bereich
- Warte auf Stage 1 (Identification)

**3. Erwartetes Ergebnis (Stage 1):**
```json
{
  "DocumentType": "COMPONENT",
  "ComponentCategory": "Interface IC",
  "ComponentName": "RS-232 Transceiver",
  "PartNumber": "MAX3232ECUE+T",
  "Manufacturer": "Maxim Integrated"
}
```

**4. Bestätige Identification:**
- Dialog zeigt "Series Datasheet erkannt" Badge
- Prüfe Part Number: `MAX3232ECUE+T` ?
- Klicke "Analyse starten"

**5. Erwartetes Ergebnis (Stage 2 - Hybrid):**
```json
{
  "ComponentType": "Interface IC",
  "PartNumber": "MAX3232ECUE+T",
  "Manufacturer": "Maxim Integrated",
  
  "Funktion": "Driver , Receiver , Transceiver",
  "Interface_internal": "UART",
  "Interface_external": "RS-232",
  "NumberOfPin": "16",
  "PackageType": "TSSOP-16",
  "Pitch": "0.65mm",
  
  "Max. case temp. °C": "85",
  "Max. ambient temp. °C": "70",
  "Min. case temp. °C": "-40",
  "Min. ambient temp. °C": "0",
  
  "_DataSource": "Hybrid (PartNumberDecoding + PDF)",
  "_PropertySchemaUsed": "Interface IC"
}
```

**6. Validierung:**
- ? `Funktion` = "Driver , Receiver , Transceiver" (aus Dropdown)
- ? `Interface_internal` = "UART" (Part Number Decoding)
- ? `Interface_external` = "RS-232" (Part Number Decoding)
- ? `PackageType` = "TSSOP-16" (Part Number Decoding)
- ? `Pitch` = "0.65mm" (PDF oder Part Number Decoding)

---

### Test 2: MCP2515 (CAN Controller)

**Part Number:** `MCP2515-I/SO`  
**Erwartete Funktion:** "Interface Controller"  
**Erwartete Interfaces:** Internal=SPI, External=CAN  
**Erwartetes Package:** SOIC-18 (Pitch: 1.27mm)

---

### Test 3: FT232RL (USB-to-UART Bridge)

**Part Number:** `FT232RL-REEL`  
**Erwartete Funktion:** "Bridge"  
**Erwartete Interfaces:** Internal=USB, External=UART  
**Erwartetes Package:** SSOP-28 (Pitch: 0.65mm)

---

## ?? Troubleshooting

### Problem: "Could not load SeriesDatasheetInstructions_InterfaceIC.txt"

**Ursache:** Prompt-Datei nicht in Azure Blob Storage vorhanden

**Lösung:**
```powershell
# Prüfe ob Datei existiert
az storage blob list --account-name <account> --container-name prompts --query "[?name=='SeriesDatasheetInstructions_InterfaceIC.txt']"

# Falls leer: Upload erneut durchführen
.\upload_interface_ic_prompts.ps1
```

---

### Problem: "No matching component found for category: 'Interface IC'"

**Ursache:** `get_classes.csv` enthält keine Interface IC Definition

**Lösung:**
1. Öffne Azure Portal ? Blob Storage ? Container "prompts"
2. Download `get_classes.csv`
3. Füge Interface IC Zeilen hinzu:
   ```csv
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Funktion,1
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Interface_internal,2
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Interface_external,3
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,NumberOfPin,4
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Pitch,5
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Max. case temp. °C,6
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Max. ambient temp. °C,7
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Min. case temp. °C,8
   /IC/Interface IC,Interface IC,IC,Interface IC,Interface IC,Min. ambient temp. °C,9
   ```
4. Upload aktualisierte `get_classes.csv`
5. Blazor App neu starten

---

### Problem: Part Number Decoding liefert "nicht vorhanden"

**Ursache:** Part Number Chart nicht im Datasheet gefunden (ChartFound: false)

**Erwartetes Verhalten:**
- System fällt zurück auf PDF-only Extraction
- Warnung im Log: "Part Number Chart not found - cannot decode"
- Analyse läuft trotzdem weiter (nur ohne Part Number Decoding)

**Lösung:**
- Prüfe ob Datasheet tatsächlich eine "Ordering Information" Tabelle hat
- Falls ja: Prüfe Azure Logs für Fehlerdetails
- Falls nein: Erwartetes Verhalten (nicht alle Datasheets haben Part Number Charts)

---

## ?? Unterstützte Funktions-Typen

Beim Testen darauf achten, dass `Funktion` zu einer dieser Optionen gemappt wird:

- ? **Interface Controller** (CAN Controller, Ethernet Controller)
- ? **Driver , Receiver , Transceiver** (RS-232, RS-485 Transceiver)
- ? **Switch , Mux , Demultiplexer** (Analog Multiplexer, Signal Switch)
- ? **Signal Buffer , Repeater , Splitter** (Line Driver, Buffer IC)
- ? **Serializer , Deserializer** (LVDS SerDes, HDMI SerDes)
- ? **Coder , Decoder ( CODEC )** (Video Encoder/Decoder, Audio CODEC)
- ? **I/O Expander** (I2C I/O Expander, Port Expander)
- ? **Bridge** (USB-to-UART, USB-to-SPI, Protocol Translator)

---

## ?? Erfolgs-Kriterien

Ein erfolgreicher Test liegt vor wenn:

1. ? **Identification** erkennt "Interface IC" korrekt
2. ? **Series Datasheet Badge** erscheint im Confirmation Dialog
3. ? **Part Number Decoding** extrahiert `Interface_internal` und `Interface_external`
4. ? **Funktion** wird auf EXAKTE Dropdown-Option gemappt (nicht "Transceiver" sondern "Driver , Receiver , Transceiver")
5. ? **PackageType** und **Pitch** werden korrekt dekodiert
6. ? **Hybrid Merge** bevorzugt Part Number Decoding für Interfaces, aber PDF für Funktion

---

## ?? Logging

Wichtige Log-Meldungen zum Debuggen:

```
[INFO] INTERFACE IC WITH PART NUMBER DETECTED
[INFO] Component: Interface IC
[INFO] Part Number: MAX3232ECUE+T
[INFO] Mode: HYBRID (Part Number Decoding + PDF Extraction)

[INFO] STAGE 1: Extracting Part Number Chart structure...
[INFO] Loading SeriesDatasheetInstructions_InterfaceIC.txt from Azure
[INFO] Stage 1 complete: Part Number Chart extracted

[INFO] STAGE 2: Decoding specific part number...
[INFO] Loading PartNumberDecodingPrompt_InterfaceIC.txt from Azure
[INFO] Stage 2 complete: Part Number decoded
[INFO]   Funktion: Driver , Receiver , Transceiver
[INFO]   Interface_internal: UART
[INFO]   Interface_external: RS-232
[INFO]   PackageType: TSSOP-16
[INFO]   Pitch: 0.65mm

[INFO] Combining Part Number Decoding results with PDF extraction
[DEBUG]   Using decoded Interface_internal: UART
[DEBUG]   Using decoded Interface_external: RS-232
[DEBUG]   Using PDF Funktion: Driver , Receiver , Transceiver
[DEBUG]   Using decoded PackageType: TSSOP-16
[INFO] Hybrid combination complete
```

---

## ?? Weitere Ressourcen

- **Vollständige Dokumentation:** `INTERFACE_IC_PART_NUMBER_DECODING_COMPLETE.md`
- **Azure Prompts:** 
  - `Azure_Prompts/SeriesDatasheetInstructions_InterfaceIC.txt`
  - `Azure_Prompts/PartNumberDecodingPrompt_InterfaceIC.txt`
- **Code-Änderungen:**
  - `DatasheetAnalyzer.Core/Services/DatasheetAnalysisService.cs`
  - `DatasheetAnalyzer.Core/Services/DMSCatalogService.cs`
  - `DatasheetAnalyzer.Blazor/Components/IdentificationDialog.razor`

---

**Ready to test!** ??

Viel Erfolg beim Testen der Interface IC Funktionalität!
