# Memory Extraction - Quick Reference Guide

## ?? Das Wichtigste auf einen Blick

### ?? KRITISCH: Property-Bedeutungen bei Memory Components

| Property | Bei Memory Components | Bei Standard Components |
|----------|-----------------------|-------------------------|
| **Size** | ?? Memory Capacity (Mbit, Kbit) | ?? Physical Dimension (mm) |
| **Width** | ?? Data Bus Width (8 bit, 16 bit) | ?? Package Width (mm) |
| **tAA_Max** | ?? Address Access Time (nur Parallel!) | - |
| **Pitch** | ?? Pin Spacing (mm) - gleich! | ?? Pin Spacing (mm) |

---

## ?? Component Type Hierarchie

```
/COMPONENT
  ?? /MemoryComponents (GENERISCH - nicht verwenden!)
      ?? Non-Volatile Memory Serial     ? Verwenden! ?
      ?? Non-Volatile Memory Parallel   ? Verwenden! ?
```

**Regel:**
- ? **NIEMALS** "Memory Components" verwenden
- ? **IMMER** spezifische Subkategorie verwenden

---

## ? Richtige Extraktion - Beispiele

### Serial EEPROM (SPI/I2C)
```json
{
  "ComponentType": "Non-Volatile Memory Serial",  // ? Spezifisch!
  "Size": "1 Mbit",                                // ? Memory Capacity!
  "Width": "1 bit",                                // ? Serial Data Line!
  "Interface": "SPI",
  "tAA_Max": "nicht relevant",                     // ? Serial hat kein tAA!
  "Pitch": "1.27 mm"                               // ? Pin Spacing
}
```

**Wo finden:**
- Size: Titel "1-Mbit Serial EEPROM" oder Features
- Width: Organization "128K x 8" ? 1 bit (serial)
- tAA: NICHT relevant für Serial Memory!

### Parallel EEPROM
```json
{
  "ComponentType": "Non-Volatile Memory Parallel", // ? Spezifisch!
  "Size": "256 Kbit",                              // ? Memory Capacity!
  "Width": "8 bit",                                // ? Data Bus Width!
  "Interface": "Parallel",
  "tAA_Max": "150 ns",                             // ? Address Access Time
  "Pitch": "2.54 mm"                               // ? Pin Spacing (DIP)
}
```

**Wo finden:**
- Size: Organization "32K x 8" ? 256 Kbit
- Width: Organization "32K x 8" ? 8 bit (zweite Zahl!)
- tAA_Max: AC Characteristics, MAX Spalte

---

## ? Häufige Fehler

### Fehler 1: Size mit Package Width verwechselt
```json
? FALSCH:
"Size": "3.9 mm"  // ? Das ist Package Width aus Mechanical Drawing!

? RICHTIG:
"Size": "1 Mbit"  // ? Memory Capacity aus Title/Features!
```

### Fehler 2: Width mit physischer Dimension verwechselt
```json
? FALSCH:
"Width": "5.0 mm"  // ? Das ist Package Length!

? RICHTIG:
"Width": "8 bit"   // ? Data Bus Width aus Organization!
```

### Fehler 3: tAA für Serial Memory extrahiert
```json
? FALSCH:
"tAA_Max": "120 ns"  // ? Serial hat kein Address Access Time!

? RICHTIG:
"tAA_Max": "nicht relevant"  // ? Serial Memory hat keinen Address Bus!
```

### Fehler 4: Generischer Component Type
```json
? FALSCH:
"ComponentType": "Memory Components"  // ? Zu generisch!

? RICHTIG:
"ComponentType": "Non-Volatile Memory Serial"  // ? Spezifisch!
```

---

## ?? Wo finde ich was?

### Size (Memory Capacity)
?? **Erste Seite:**
- Title: "1-Mbit Serial EEPROM"
- Features: "64 Kbit of EEPROM"
- Product Description

?? **NICHT hier suchen:**
- ? Package Dimensions Section
- ? Mechanical Drawing
- ? Outline Dimensions

### Width (Data Bus Width)
?? **Organization Section:**
```
Organization: 128K x 8
              ^^^^^^^^^^
              Depth × Width
```
- Zweite Zahl = Width in Bits!
- "128K x 8" ? Width = "8 bit"
- "16K x 16" ? Width = "16 bit"

?? **NICHT hier suchen:**
- ? Package Dimensions (das wäre physische Breite!)
- ? Mechanical Drawing

### tAA (Address Access Time)
?? **AC Characteristics:**
```
Symbol | Min | Typ | Max | Unit | Description
tAA    | -   | -   | 150 | ns   | Address Access Time
```
- Property "tAA_Max" ? Lese **MAX** Spalte!
- Nur für **Parallel Memory**!
- Serial Memory ? "nicht relevant"

### Interface
?? **Features Section:**
- Serial: "SPI Interface", "I2C Compatible"
- Parallel: "Parallel Interface", "Asynchronous"

?? **Pin Names:**
- Serial: SCK, MOSI, MISO, CS (SPI) oder SDA, SCL (I2C)
- Parallel: A0-A15 (Address), D0-D7 (Data)

---

## ?? Schnell-Test

### Schritt 1: Memory Type bestimmen
```
Pin Count < 10  ?  Serial Memory
Pin Count > 20  ?  Parallel Memory
```

### Schritt 2: Component Type
```
Serial  ? "Non-Volatile Memory Serial"
Parallel ? "Non-Volatile Memory Parallel"
```

### Schritt 3: Size extrahieren
```
? Suche: First Page, Title/Features
? Nicht: Package Dimensions!

Beispiel: "1-Mbit EEPROM" ? Size = "1 Mbit"
```

### Schritt 4: Width extrahieren
```
? Suche: Organization (z.B. "128K x 8")
? Nicht: Package Width in Mechanical Drawing!

Organization: "8K x 8" ? Width = "8 bit"
```

### Schritt 5: tAA extrahieren
```
IF Serial:
   tAA_Max = "nicht relevant"
ELSE (Parallel):
   Lese AC Characteristics, MAX Spalte
   tAA_Max = "150 ns"
```

---

## ?? Validation Rules

### Size
? Valid: "64 Kbit", "1 Mbit", "256 Kbit"  
? Invalid: "3.9 mm" (das ist Package Width!)

### Width
? Valid: "1 bit", "8 bit", "16 bit"  
? Invalid: "5.0 mm" (das ist Package Length!)

### tAA_Max
? Valid (Parallel): "45 ns", "120 ns", "150 ns"  
? Valid (Serial): "nicht relevant"  
? Invalid: "120 ns" für Serial Memory!

### Interface
? Valid: "SPI", "I2C", "Parallel", "Asynchronous"  
? Invalid: "Serial" (zu generisch!)

---

## ?? Wenn unsicher

| Situation | Lösung |
|-----------|--------|
| Size unklar | Suche im **Title** (erste Seite) |
| Width unklar | Lese **Organization** (z.B. "8K x 8" ? "8 bit") |
| Memory Type unklar | Zähle Pins: <10 = Serial, >20 = Parallel |
| tAA bei Serial? | Immer "nicht relevant" |
| Component Type? | Niemals "Memory Components" - immer Serial/Parallel! |

---

## ?? Remember

? **Size** = Memory Capacity (Bits/Bytes) - NOT physical dimension!  
? **Width** = Data Bus Width (Bits) - NOT package width!  
? **tAA** = Only for Parallel Memory - Serial: "nicht relevant"  
? **Component Type** = Specific subcategory (Serial/Parallel) - NOT generic "Memory Components"!

---

**Quick Lookup:**
- Memory Type? ? Pin count (<10 = Serial, >20 = Parallel)
- Size? ? First page title/features (NOT package dimensions!)
- Width? ? Organization second number (NOT mechanical drawing!)
- tAA? ? AC Characteristics MAX (ONLY Parallel!)

---

**Erstellt:** 2025-01-XX  
**Autor:** GitHub Copilot  
**Version:** 1.0
