# Non-Volatile Memory - Quick Start Guide ??

## ?? Übersicht

Ab sofort unterstützt der Datasheet Analyzer **Non-Volatile Memory Serial** und **Non-Volatile Memory Parallel** Komponenten mit automatischer Part Number Eingabe.

---

## ? Quick Start (3 Schritte)

### Schritt 1: PDF hochladen

```
1. Öffne Blazor App: https://localhost:5001
2. Wähle Memory Datasheet (z.B. AT25SF641B.pdf)
3. Klicke "Analyse starten"
```

### Schritt 2: Part Number eingeben

```
Dialog öffnet:
????????????????????????????????????????????
? Komponenten-Identifikation bestätigen    ?
????????????????????????????????????????????
? Kategorie: Non-Volatile Memory Serial    ?
? Part Number: [________________] ? HIER!  ?
?                                          ?
? ?? Series Datasheet erkannt!             ?
? Bitte exakte Herstellernummer eingeben   ?
????????????????????????????????????????????

Gib ein: AT25SF641B-MHB-T
```

### Schritt 3: Analyse läuft automatisch

```
? Memory Guidelines werden automatisch angewendet
? Size = Memory Capacity (z.B. "64 Mbit")
? Width = Data Bus Width (z.B. "1 bit" für Serial)
? Interface wird erkannt (SPI/I2C oder Parallel)
```

---

## ?? Unterstützte Komponenten

| Komponenten-Typ                  | Part Number Input | Beispiel                      |
|----------------------------------|-------------------|-------------------------------|
| Non-Volatile Memory Serial       | ? Erforderlich    | AT25SF641B-MHB-T              |
| Non-Volatile Memory Parallel     | ? Erforderlich    | AT28C256-15PU                 |
| EEPROM Serial                    | ? Erforderlich    | 25LC256-I/P                   |
| EEPROM Parallel                  | ? Erforderlich    | 28C64B-15                     |
| Flash Serial                     | ? Erforderlich    | W25Q128JVSIQ                  |
| Flash Parallel                   | ? Erforderlich    | 29F040B-55                    |

---

## ? Was wird automatisch erkannt?

### Serial Memory (SPI/I2C)

```json
{
  "Size": "64 Mbit",          // ? Memory Capacity
  "Width": "1 bit",           // ? Serial = 1 Bit
  "Interface": "SPI",         // ? Serial Interface
  "Organization": "8M x 8",
  "Package": "SOIC-8",
  "Pitch": "1.27 mm",
  "tAA_Max": "nicht relevant" // ? Korrekt: Serial hat kein tAA
}
```

### Parallel Memory

```json
{
  "Size": "256 Kbit",         // ? Memory Capacity
  "Width": "8 bit",           // ? Parallel = 8 Bit Data Bus
  "Interface": "Parallel",    // ? Parallel Interface
  "Organization": "32K x 8",
  "Package": "DIP-28",
  "Pitch": "2.54 mm",
  "tAA_Max": "150 ns"         // ? Korrekt: Parallel hat tAA
}
```

---

## ?? Häufige Fehler vermeiden

### ? FALSCH: Part Number leer lassen

```
Dialog öffnet:
Part Number: [________________] ? LEER!

? Bestätigen-Button ist DEAKTIVIERT
? User MUSS Part Number eingeben!
```

### ? FALSCH: Size mit Package Dimensions verwechseln

```json
{
  "Size": "3.9 mm"  // ? FALSCH! (Package Width)
}

? Richtig:
{
  "Size": "64 Mbit"  // ? Memory Capacity
}
```

### ? FALSCH: Width mit Package Width verwechseln

```json
{
  "Width": "5.0 mm"  // ? FALSCH! (Package Length)
}

? Richtig:
{
  "Width": "8 bit"  // ? Data Bus Width
}
```

---

## ?? Debug: Ist Part Number Input aktiv?

### Browser Console prüfen

```javascript
// F12 ? Console
?? Component Category from AI: Non-Volatile Memory Serial
? Component found in catalog:
   DisplayName: Non-Volatile Memory Serial
   InternalName: NV_Memory_Serial
   FullPath: /Memory Components/Non-Volatile Memory Serial
   IsSeriesDatasheet: True  ? MUSS TRUE sein!
? Series Datasheet detected: Non-Volatile Memory Serial
   Part Number input will be required
```

### Dialog prüfen

```
Herstellernummer: [AT25SF641B-MHB-T] ? Feld ist EDITIERBAR

?? Series Datasheet erkannt!  ? Diese Warnung MUSS erscheinen
Bitte geben Sie die exakte Herstellernummer ein.
```

---

## ?? Test-Beispiele

### Beispiel 1: AT25SF641B (Serial Flash)

```
PDF: AT25SF641B_Datasheet.pdf
Part Number: AT25SF641B-MHB-T

Erwartete Ausgabe:
? Size: 64 Mbit
? Width: 1 bit
? Interface: SPI
? Package: SOIC-8
? Pitch: 1.27 mm
? tAA_Max: nicht relevant
```

### Beispiel 2: AT28C256 (Parallel EEPROM)

```
PDF: AT28C256_Datasheet.pdf
Part Number: AT28C256-15PU

Erwartete Ausgabe:
? Size: 256 Kbit
? Width: 8 bit
? Interface: Parallel
? Package: DIP-28
? Pitch: 2.54 mm
? tAA_Max: 150 ns
```

### Beispiel 3: 25LC256 (Serial EEPROM)

```
PDF: 25LC256_Datasheet.pdf
Part Number: 25LC256-I/P

Erwartete Ausgabe:
? Size: 256 Kbit
? Width: 1 bit
? Interface: SPI
? Package: DIP-8
? Pitch: 2.54 mm
? tAA_Max: nicht relevant
```

---

## ?? Weitere Dokumentation

- **Technische Details:** `MEMORY_SERIAL_PARALLEL_IMPLEMENTATION.md`
- **Memory Guidelines:** `Azure_Prompts/memory_extraction_guidelines.txt`
- **Property Definitions:** `Azure_Prompts/property_definitions.txt`

---

## ?? Support

Bei Problemen:

1. **Part Number nicht editierbar?** ? Prüfe Browser Console: `IsSeriesDatasheet: true`?
2. **Size = "3.9 mm"?** ? Memory Guidelines nicht geladen (Azure Blob prüfen)
3. **tAA für Serial Memory extrahiert?** ? Guidelines nicht korrekt angewendet

---

**Status:** ? Ready to Use  
**Version:** 1.0  
**Erstellt:** 2025-01-15
