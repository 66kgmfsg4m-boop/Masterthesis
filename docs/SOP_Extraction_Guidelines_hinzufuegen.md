# SOP – Neuen Extraction-Guideline-Prompt für eine Kataloggruppe hinzufügen

**Zielgruppe:** Kolleg:innen, die die Extraktionsqualität einer bestehenden Kataloggruppe verbessern wollen, indem sie einen **zusätzlichen** gruppen-spezifischen Prompt-Baustein bereitstellen (z. B. „für Voltage-Regulator-Datenblätter zusätzlich diese Extraktionshinweise").
**Voraussetzung:** Die Kataloggruppe existiert bereits in `get_classes.csv`. Wenn sie fehlt, siehe zuerst `SOP_Kataloggruppe_hinzufuegen.md`.
**Version:** 1.0

---

## 1. Was ist ein Extraction-Guideline-Prompt?

Der Datasheet Analyzer baut den Prompt für die KI in zwei Schichten auf:

1. **Basis-Prompt** – automatisch aus `get_classes.csv` generiert (Liste der erlaubten Properties, JSON-Struktur, Min/Typ/Max-Regeln usw.).
2. **Zusatz-Guideline** – **optional** pro Kataloggruppe. Wird nur angehängt, wenn eine passende Bedingung erfüllt ist, und enthält Extraktionswissen, das über die reine Property-Liste hinausgeht (z. B. „wo im PDF steht das", „welche Fehlerquellen gibt es", „welche Einheit ist erlaubt").

Aktuell existierende Guidelines:

| Guideline | Blob-Datei | Aktiviert für |
|---|---|---|
| Pitch | `pitch_extraction_guidelines.txt` | Amplifier, Connector, IC, Memory, Interface IC … (Komponenten mit `Pitch`-Property) |
| Memory | `memory_extraction_guidelines.txt` | Non-Volatile Memory Serial/Parallel, EEPROM, Flash, SRAM |
| Coil / Inductor | `coil_extraction_guidelines.txt` | Coils/Inductors |
| EMI-Filter | `emi_filter_extraction_guidelines.txt` | EMI-Filter |

Alle Dateien liegen im **gleichen** Azure-Blob-Container wie `get_classes.csv`.

---

## 2. Wichtig: Reicht es, die Datei nur hochzuladen?

**Ja – für die meisten Fälle.** Seit dem Umbau *„generischer Guideline-Loader"* in `DMSCatalogService.GenerateDynamicPrompt` gilt:

- **Standardfall:** Datei nach Namenskonvention (`<slug(DisplayName)>_extraction_guidelines.txt`) hochladen + App neu starten. ? Siehe hierfür **`SOP_Guideline_Blob_Upload.md`** (empfohlen).
- **Sonderfälle:** Du musst weiterhin `Requires…` / `Load…` im Code ergänzen (Kapitel 5), wenn
  1. die Guideline zusätzlich zur Kataloggruppe **auch von einer Property** abhängt (z. B. „nur wenn `Pitch` existiert"),
  2. sie für **mehrere** unterschiedliche `DisplayName`s greifen soll (z. B. gemeinsame Memory-Guideline für Serial + Parallel), oder
  3. sie im Prompt **vor** einer der bestehenden Spezial-Guidelines stehen soll.

Kapitel 3 (Aufbau) und Kapitel 4 (Datei erstellen) gelten für **beide** Wege.

---



Ein Guideline-Prompt ist eine reine **UTF-8-Text-Datei** (`.txt`). Sie wird 1:1 an den Basis-Prompt angehängt, ohne weitere Verarbeitung. Damit die KI sie zuverlässig verwendet, folge diesem Muster (abgeleitet aus den existierenden Guidelines):

```
===============================================================
 <TITEL IN GROSSBUCHSTABEN> — z. B. LDO EXTRACTION GUIDELINES
===============================================================

? CRITICAL: <Eine Zeile, warum dieser Kontext wichtig ist>
   z. B. "Do NOT confuse dropout voltage with output voltage!"

===============================================================
1) WHERE TO FIND <PROPERTY / TOPIC>
===============================================================

• Typische Abschnitts-Überschriften im Datenblatt
  - "Electrical Characteristics"
  - "DC Specifications"
  - "Package Information"

• Typische Seitenlage im Datenblatt (z. B. Seite 3–8)

• Visuelle Indikatoren (Symbol, Tabelle, Kurve …)

===============================================================
2) DEFINITION
===============================================================

• Klare fachliche Definition der Property
• Bezugsgröße (z. B. "bei I_out = 100 mA und T = 25 °C")
• Einheit und erlaubte Formate (z. B. "mV", "µA", "%")

===============================================================
3) EXTRACTION RULES
===============================================================

1. Konkrete Regel (z. B. "Immer TYP-Spalte lesen, außer wenn Name
   'Min' oder 'Max' enthält.")
2. Wie mit mehreren Bedingungen umgehen (Temperatur, Last, Frequenz)?
3. Was tun bei Bereichen ("min to max"-Format)?
4. Einheiten-Normalisierung (z. B. immer Grundgröße, keine Multiplikatoren
   wie "milli", "kilo" ausschreiben).

===============================================================
4) VALIDATION RULES
===============================================================

• Plausibilitätsgrenzen: Wert muss zwischen X und Y liegen
• Typische Fehlmatches ausschließen (z. B. "Verwechsle nicht
   Dropout-Voltage mit Output-Voltage")
• Was passiert, wenn der Wert nicht gefunden wird?
  ? Immer: return "nicht gefunden" (deutsch, exakt so!)

===============================================================
5) PACKAGE-SPECIFIC / CATEGORY-SPECIFIC HINTS   (optional)
===============================================================

Tabelle mit typischen Werten je Gehäuse-/Typklasse.

===============================================================
6) COMMON MISTAKES TO AVOID
===============================================================

? Häufige Fehler mit Beispielen
? Was stattdessen gemacht werden soll

===============================================================
7) ACCEPTABLE / UNACCEPTABLE RESPONSES
===============================================================

? Akzeptable Antworten (inkl. "nicht gefunden")
? Nicht akzeptable Antworten (Raten, Extrapolieren, Erfinden)

===============================================================
 END OF <TITEL> GUIDELINES
===============================================================
```

### Muss-Regeln für den Inhalt

1. **Sprache:** Englisch für Fachinhalt, deutsche „nicht gefunden"-Rückgabe (der restliche Extractor erwartet das exakt so).
2. **Keine Widersprüche** zum Basis-Prompt (z. B. keine neuen JSON-Keys erlauben, keine neuen Property-Namen einführen).
3. **Keine `_1`/`_2`/`_3`-Suffixe** in Beispielen (werden intern konsolidiert).
4. **Property-Namen exakt** so schreiben wie in `get_classes.csv` (Spalte 5, `PropertyInternalName`).
5. **Fokus auf 1 Themengebiet** pro Datei (z. B. „LDO", nicht „Voltage Regulator + Reference").
6. **Länge** typischerweise 3–8 KB reiner Text. Alles darüber verwässert den Prompt und kostet Tokens.
7. **Kein Markdown** und keine Codeblöcke – ASCII-Kästen und Zeilenumbrüche reichen.

---

## 4. Datei erstellen und hochladen

### 4.1 Namenskonvention

`<thema>_extraction_guidelines.txt`

Beispiele:

- `ldo_extraction_guidelines.txt`
- `oscillator_extraction_guidelines.txt`
- `optocoupler_extraction_guidelines.txt`

Nur Kleinbuchstaben, Unterstriche, keine Leerzeichen, keine Umlaute.

### 4.2 Encoding

UTF-8 **ohne BOM**. In Notepad++ oder VS Code speichern, **nicht** in Word/Excel bearbeiten.

### 4.3 Upload

1. Azure Portal ? Storage Account ? Container (derselbe, in dem `get_classes.csv` liegt).
2. **Upload** ? Datei auswählen ? **Overwrite** aktivieren, falls sie schon existiert.
3. Nach dem Upload einmal herunterladen und stichprobenartig prüfen, ob der Inhalt stimmt (Datei-Cache-Falle).

---

## 5. Code-Regel ergänzen (heute noch Pflicht)

Ort: `DatasheetAnalyzer.Core/Services/DMSCatalogService.cs`

Für jede neue Guideline brauchst du **drei** Änderungen. Beispiel: neue Guideline für **LDO Voltage Regulators**.

### 5.1 Neue `Requires…`-Methode hinzufügen

In der `#region`-Nähe der anderen `Requires…`-Methoden:

```csharp
private bool RequiresLdoGuidelines(CatalogComponent component, List<ComponentProperty> properties)
{
    // Aktiviere Guideline für LDO-Komponenten
    var ldoKeywords = new[]
    {
        "Voltage Regulator LDO",
        "LDO",
        "Low Dropout Regulator"
    };

    bool isLdo = ldoKeywords.Any(keyword =>
        component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
        component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    if (!isLdo) return false;

    // Zusätzlich prüfen, ob mindestens eine LDO-typische Property gesetzt ist
    bool hasLdoProperties = properties.Any(p =>
        p.PropertyInternalName.Contains("Dropout", StringComparison.OrdinalIgnoreCase) ||
        p.PropertyInternalName.Contains("Vout", StringComparison.OrdinalIgnoreCase) ||
        p.PropertyInternalName.Contains("PSRR", StringComparison.OrdinalIgnoreCase));

    return hasLdoProperties;
}
```

### 5.2 Neuen `Load…`-Loader mit Cache hinzufügen

```csharp
private string? _cachedLdoGuidelines;

private string LoadLdoGuidelinesIfNeeded()
{
    if (_cachedLdoGuidelines != null)
        return _cachedLdoGuidelines;

    try
    {
        _logger.LogDebug("Loading ldo_extraction_guidelines.txt from Azure");
        _cachedLdoGuidelines = _remoteConfigService.LoadBlobContent("ldo_extraction_guidelines.txt");
        _logger.LogInformation("LDO extraction guidelines loaded from Azure ({Length} characters)",
            _cachedLdoGuidelines.Length);
        return _cachedLdoGuidelines;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Could not load ldo_extraction_guidelines.txt from Azure");
        _cachedLdoGuidelines = string.Empty;
        return _cachedLdoGuidelines;
    }
}
```

### 5.3 Aufruf in `GenerateDynamicPrompt` einhängen

Innerhalb `GenerateDynamicPrompt`, direkt nach den bestehenden Blöcken für Pitch/Memory/Coil/EMI:

```csharp
if (RequiresLdoGuidelines(component, properties))
{
    var ldoGuidelines = LoadLdoGuidelinesIfNeeded();
    if (!string.IsNullOrEmpty(ldoGuidelines))
    {
        promptBuilder.AppendLine();
        promptBuilder.AppendLine(ldoGuidelines);
        promptBuilder.AppendLine();
    }
}
```

### 5.4 Kompilieren, deployen, neu starten

1. Lokal Build laufen lassen.
2. Änderung committen und deployen.
3. App neu starten – die Guideline wird beim nächsten Prompt-Build automatisch angehängt.

---

## 6. Verifikation

Nach Upload + Deploy:

1. In den Logs beim ersten Analyseaufruf einer LDO-Komponente erscheinen:
   ```
   Loading ldo_extraction_guidelines.txt from Azure
   LDO extraction guidelines loaded from Azure (nnnn characters)
   ```
2. Ein Testdatenblatt der Kategorie hochladen und Stage 2 durchlaufen lassen.
3. Extrahiertes JSON prüfen: Enthält es plausible Werte für die Properties, für die du in der Guideline explizite Regeln gegeben hast?
4. Log der Tokenverwendung ansehen – der Prompt wächst um die Größe der Guideline (Richtwert: 1 000 Zeichen ? 250 Tokens).

Falls die Guideline **nicht** greift:

| Symptom | Ursache | Fix |
|---|---|---|
| Kein „Loading … from Azure" im Log | `Requires…` liefert `false` | Component-Keywords/Properties in `Requires…` prüfen |
| Blob nicht gefunden (Log-Warning) | Datei-Name falsch geschrieben oder falscher Container | Dateiname 1:1 mit `LoadBlobContent(...)`-Argument abgleichen |
| Guideline geladen, aber KI ignoriert sie | Widerspruch zum Basis-Prompt oder zu lang | Datei kürzen, Konflikte mit „ALLOWED PROPERTIES" auflösen |
| Zeichen kaputt | Nicht UTF-8 gespeichert | Datei in Notepad++ „Save with Encoding UTF-8 (without BOM)" |

---

## 7. Checkliste

- [ ] Kataloggruppe existiert bereits in `get_classes.csv`
- [ ] Guideline-Datei nach Muster aus Kapitel 3 erstellt
- [ ] Dateiname folgt `<thema>_extraction_guidelines.txt`
- [ ] Datei als UTF-8 ohne BOM gespeichert
- [ ] Datei in Azure-Blob-Container hochgeladen (Overwrite)
- [ ] Neue `Requires…`-Methode ergänzt
- [ ] Neuer `Load…`-Loader mit Cache-Feld ergänzt
- [ ] Aufruf in `GenerateDynamicPrompt` eingehängt
- [ ] Build erfolgreich, Anwendung neu gestartet
- [ ] Log zeigt Laden der neuen Guideline
- [ ] Test-Datenblatt liefert plausiblere Extraktion

---

## 8. Generischer Guidelines-Loader (bereits umgesetzt)

Der beschriebene Umbau ist im Code umgesetzt (`DMSCatalogService.LoadGenericGuidelineFor`, `SlugifyForBlobName`, Aufruf in `GenerateDynamicPrompt`). Damit gilt für den **Standardfall** (Guideline hängt nur an einer Kataloggruppe):

1. Datei nach Konvention `<slug(DisplayName)>_extraction_guidelines.txt` erstellen.
2. In den Blob-Container hochladen.
3. App neu starten.

Schritt-für-Schritt-Anleitung dafür: **`SOP_Guideline_Blob_Upload.md`**.

Das hier beschriebene Verfahren (Kapitel 5 mit `Requires…` / `Load…` / `AppendLine`) ist nur noch für die drei in Kapitel 2 genannten Sonderfälle nötig.

---

## 9. Kurzfassung

- Guidelines sind **zusätzliche, gruppen-spezifische Textbausteine** für den Prompt.
- **Standardfall:** Blob-Datei nach Namenskonvention hochladen ? siehe `SOP_Guideline_Blob_Upload.md`.
- **Sonderfall** (properties-abhängig / mehrere `DisplayName`s / Reihenfolge im Prompt): zusätzlich `Requires…`/`Load…`-Methode im Code ergänzen (dieses SOP, Kapitel 5).
- Struktur (Wo, Definition, Regeln, Validierung, Fehler, Antwortformat) einhalten, dann greift die Guideline zuverlässig.

**Ende des SOP.**
