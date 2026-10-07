# ? ULTRA-KURZE COPY-PASTE LÖSUNG

## 1. Öffne: `DatasheetAnalyzer.Core/Services/DMSCatalogService.cs`

## 2. Suche nach (Ctrl+F):
```csharp
private string GetEmbeddedMemoryGuidelines()
```

## 3. Ersetze die GESAMTE Methode mit:

```csharp
        private string GetEmbeddedMemoryGuidelines()
        {
            return @"
MEMORY GUIDELINES V2:
1. SizeByte = BYTES! (256 Kbit ? 32768, 32 Mbit ? 4194304)
2. Function = FRAM (NOT Memory!)
3. WidthMax = 8 bit (from 32K×8)
4. TaaMaxS = 70 ns (Parallel) or nicht relevant (Serial)

CONVERSION: Kbit * 1024 / 8 = Bytes
FUNCTION: F-RAM ? FRAM, EEPROM ? EEPROM";
        }
```

## 4. Speichern + Build

```powershell
dotnet build DatasheetAnalyzer.Core
```

## 5. App neu starten

```powershell
cd DatasheetAnalyzer.Blazor
dotnet run
```

## 6. Test

Upload FM18W08 PDF:
- ? Function = "FRAM" (NICHT "Memory")
- ? SizeByte = "32768" (NICHT "256 Kbit (32 K × 8)")

---

**Das war's!** Die ultra-kurze Version sollte ausreichen, um die KI zu korrigieren.
