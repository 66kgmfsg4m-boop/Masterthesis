# SOP – Neuen Extraction-Guideline-Prompt per Blob-Upload hinzufügen

**Zielgruppe:** Fach- und Produktkolleg:innen ohne Entwicklerzugriff, die die Extraktionsqualität einer bestehenden Kataloggruppe verbessern wollen.
**Voraussetzung:** Die Kataloggruppe existiert bereits in `get_classes.csv`. Falls nicht: erst `SOP_Kataloggruppe_hinzufuegen.md` durchgehen.
**Version:** 2.0 – ab Umbau *„generischer Guideline-Loader"* (siehe Abschnitt „Ergänzt am Ende von `GenerateDynamicPrompt`" in `DMSCatalogService.cs`).

---

## 1. Was ändert sich gegenüber Version 1?

Der Analyzer lädt zusätzliche Extraction-Guidelines für eine Kataloggruppe jetzt **automatisch aus Azure Blob Storage** – **ohne Code-Änderung**. Es reicht:

1. **Datei nach Namenskonvention erstellen**
2. **In den Blob-Container hochladen** (derselbe wie `get_classes.csv`)
3. **App neu starten**

Der bisherige Code-Weg (`Requires…` + `Load…` + `AppendLine`) bleibt weiterhin **nur** für die vier historisch gewachsenen Spezial-Guidelines relevant (Pitch, Memory, Coil, EMI-Filter), weil diese *properties-basiert* schalten (z. B. „Pitch nur wenn Property `Pitch` existiert"). Alles andere läuft ab jetzt über die Namenskonvention.

---

## 2. Namenskonvention

Der Blob-Name wird aus dem **`DisplayName`** der Kataloggruppe abgeleitet (Spalte 3 in `get_classes.csv`):

```
<slug(DisplayName)>_extraction_guidelines.txt
```

**Slug-Regeln (exakt wie im Code implementiert):**

- Buchstaben und Ziffern bleiben erhalten, werden **kleingeschrieben**.
- Alle anderen Zeichen (Leerzeichen, Bindestriche, Slashes, Umlaute, Sonderzeichen) werden durch **`_`** ersetzt.
- Mehrere `_` in Folge werden zu **einem** `_` zusammengefasst.
- Führende und abschließende `_` werden entfernt.

### Beispiele

| DisplayName in `get_classes.csv` | Blob-Datei |
|---|---|
| `Voltage Regulator LDO` | `voltage_regulator_ldo_extraction_guidelines.txt` |
| `Crystal Oscillator` | `crystal_oscillator_extraction_guidelines.txt` |
| `Non-Volatile Memory Serial` | `non_volatile_memory_serial_extraction_guidelines.txt` |
| `RF Amplifier` | `rf_amplifier_extraction_guidelines.txt` |
| `Optocoupler / Photocoupler` | `optocoupler_photocoupler_extraction_guidelines.txt` |
| `USB 3.0 Transceiver` | `usb_3_0_transceiver_extraction_guidelines.txt` |

> ?? Umlaute (`ä`, `ö`, `ü`, `ß`) werden **ersetzt**, nicht transliteriert. Wenn dein `DisplayName` Umlaute enthält, wird der Slug an dieser Stelle einen `_` haben. Empfehlung: `DisplayName` in `get_classes.csv` immer ohne Umlaute schreiben.

---

## 3. Ablauf in 4 Schritten

### Schritt 1 – Guideline-Text erstellen

- Format und Inhalt wie in `SOP_Extraction_Guidelines_hinzufuegen.md` **Kapitel 3** beschrieben (Titel, „Where to find", „Definition", „Extraction rules", „Validation rules", „Common mistakes", „Acceptable/Unacceptable responses").
- **Reine UTF-8-Textdatei ohne BOM.**
- Empfohlene Länge: **3–8 KB**, hartes Limit: **12 000 Zeichen** (siehe unten).
- Property-Namen exakt wie in `get_classes.csv` (Spalte `PropertyName`).
- „nicht gefunden" (deutsch) als vorgesehene Fallback-Antwort verwenden – der restliche Extractor erwartet das so.
- Kein Markdown, keine Codeblöcke – nur ASCII-Text.

### Schritt 2 – Datei nach Konvention benennen

Slug aus dem `DisplayName` bilden (Regeln siehe Kapitel 2) und `_extraction_guidelines.txt` anhängen.

> ?? Wenn du unsicher bist, ob dein Slug stimmt: schau in die Logs nach dem ersten Analyseaufruf einer Komponente dieser Gruppe – dort steht der erwartete Blob-Name (siehe Kapitel 5).

### Schritt 3 – In den Azure-Blob-Container hochladen

1. Azure Portal ? Storage Account ? **denselben** Container wie `get_classes.csv`.
2. **Upload** ? Datei auswählen ? **Overwrite** aktivieren, falls schon vorhanden.
3. Kurz herunterladen und stichprobenartig prüfen (Cache-Falle vermeiden).

### Schritt 4 – App neu starten

Der Loader **cached** Guidelines pro Prozess-Instanz (positiv **und** negativ). Damit eine **neu hochgeladene** Datei berücksichtigt wird, muss die App neu starten:

- **Lokal:** Prozess neu starten.
- **App Service / Container Apps:** *Restart*.
- **AKS / Kubernetes:** Pods rollen (`kubectl rollout restart deployment/<name>`).

---

## 4. Verhalten des Loaders

Damit klar ist, worauf du dich verlassen kannst:

| Fall | Verhalten |
|---|---|
| Blob **existiert und ist nicht leer** | Text wird 1:1 an den Basis-Prompt angehängt, danach für die Session gecacht. |
| Blob **existiert, ist aber leer** | Wird ignoriert, negativ gecacht, keine Fehlermeldung. |
| Blob **existiert nicht** | Wird ignoriert, negativ gecacht, kein Fehler, kein Prompt-Bloat. |
| Blob **> 12 000 Zeichen** | Wird auf 12 000 Zeichen **gekürzt** und eine Warnung ins Log geschrieben. |
| Datei hochgeladen, App **nicht** neu gestartet | Wird erst nach Neustart berücksichtigt (Cache). |
| Für dieselbe Komponente existiert eine **Spezial-Guideline** (Pitch/Memory/Coil/EMI) | Spezial-Guideline **und** generische Guideline werden beide angehängt – ohne Duplikat-Erkennung. Achte darauf, keine widersprüchlichen Regeln zu formulieren. |

Reihenfolge im finalen Prompt:

1. Basis-Prompt (Property-Liste, Forbidden, …)
2. Pitch-Guideline (falls anwendbar)
3. Memory-Guideline (falls anwendbar)
4. Coil-Guideline (falls anwendbar)
5. EMI-Filter-Guideline (falls anwendbar)
6. **Generische Guideline aus Blob (neu)**
7. JSON-Struktur, Extraction-Rules, Min/Typ/Max, Frequenz-Bereichsregeln

---

## 5. Verifikation über Logs

Nach dem Neustart und einer Analyse einer Komponente dieser Gruppe erscheint einer der folgenden Log-Einträge:

**Erfolg:**
```
Generic guideline 'voltage_regulator_ldo_extraction_guidelines.txt' loaded from Azure (4123 characters)
```

**Datei zu groß (wurde gekürzt):**
```
Generic guideline 'voltage_regulator_ldo_extraction_guidelines.txt' is 18234 chars (limit 12000) - truncated
```

**Datei nicht vorhanden (still ignoriert – nur im Debug-Log sichtbar):**
```
No generic guideline found for 'Voltage Regulator LDO' (Blob: voltage_regulator_ldo_extraction_guidelines.txt)
```

Diese Log-Zeile ist auch dein **verlässlichster Weg**, den exakt erwarteten Blob-Namen für einen `DisplayName` zu erfahren.

---

## 6. End-to-End-Test

1. Ein Testdatenblatt der Kategorie in `Home.razor` hochladen.
2. Stage 1 (Identifikation) bestätigen.
3. Stage 2 (Extraktion) durchlaufen lassen.
4. Log prüfen: `Generic guideline '…' loaded from Azure (N characters)` sollte für das Testdatenblatt erscheinen.
5. Ergebnis-JSON auf `Results.razor` prüfen:
   - Werte für die Properties, für die du Extraktions-Regeln geschrieben hast, wirken plausibel.
   - Kein neuer JSON-Key, den es in `get_classes.csv` nicht gibt (Schema-Whitelist bleibt aktiv).
6. DMS-Vergleich (falls konfiguriert) zeigt weiterhin nur die im Katalog definierten Properties.

---

## 7. Fehler-Tabelle

| Symptom | Ursache | Lösung |
|---|---|---|
| Keine Log-Zeile mit dem Blob-Namen | Slug stimmt nicht mit dem `DisplayName` überein. | Slug-Regeln aus Kapitel 2 nachrechnen. Ggf. `DisplayName` in `get_classes.csv` prüfen. |
| Log sagt „No generic guideline found" | Datei existiert im falschen Container oder Dateiname weicht ab. | Blob-Container und exakten Dateinamen prüfen. |
| Guideline wird nach Upload nicht sichtbar | Cache: App wurde nicht neu gestartet. | App neu starten / Pods rollen. |
| Log warnt „truncated" | Datei > 12 000 Zeichen. | Text kürzen; nur ein Thema pro Datei. |
| KI ignoriert Regeln aus der Guideline | Widerspruch zum Basis-Prompt oder zu langer/verwaschener Text. | Guideline straffen, Property-Namen exakt schreiben, keine neuen JSON-Keys erfinden. |
| Sonderzeichen im Log falsch | Datei nicht als UTF-8 ohne BOM gespeichert. | In Notepad++/VS Code neu speichern („UTF-8 without BOM"). |
| Zwei ähnliche `DisplayName`s ergeben denselben Slug | Slug-Kollision. | `DisplayName` in `get_classes.csv` eindeutiger machen. |

---

## 8. Beispiel – Ende zu Ende

**Ziel:** LDO-Datenblätter besser extrahieren.

1. Kataloggruppe im DMS: `Voltage Regulator LDO` – existiert bereits in `get_classes.csv`.
2. Text in `voltage_regulator_ldo_extraction_guidelines.txt` schreiben (Struktur aus `SOP_Extraction_Guidelines_hinzufuegen.md` Kapitel 3):
   - „Where to find" ? *Electrical Characteristics*, *DC Specifications*
   - „Definition" ? z. B. Dropout Voltage bei `I_out = 100 mA`, `T = 25 °C`
   - „Extraction rules" ? immer TYP-Spalte, außer Name enthält Min/Max
   - „Validation rules" ? Werte plausibel zwischen 50 mV und 2 V; sonst „nicht gefunden"
   - „Common mistakes" ? *Do not confuse Dropout Voltage with Vout*
3. Datei als **UTF-8 ohne BOM** speichern.
4. In den Blob-Container hochladen.
5. App neu starten.
6. Testdatenblatt hochladen ? Log zeigt:
   ```
   Generic guideline 'voltage_regulator_ldo_extraction_guidelines.txt' loaded from Azure (3812 characters)
   ```
7. Ergebnis-JSON prüfen – Dropout-Werte plausibler als vorher.

---

## 9. Checkliste

- [ ] Kataloggruppe existiert in `get_classes.csv`
- [ ] Slug aus `DisplayName` korrekt gebildet
- [ ] Datei `<slug>_extraction_guidelines.txt` erstellt
- [ ] Datei ? 12 000 Zeichen
- [ ] UTF-8 ohne BOM
- [ ] Property-Namen exakt wie in `get_classes.csv`
- [ ] „nicht gefunden" als Fallback vorgesehen
- [ ] In Azure Blob Storage hochgeladen (Overwrite)
- [ ] Anwendung neu gestartet
- [ ] Log zeigt „Generic guideline '…' loaded from Azure (N characters)"
- [ ] Testdatenblatt liefert plausibleres Ergebnis

---

## 10. Anhang – Wann darf/muss man doch in den Code?

Die neue Blob-Konvention deckt **die meisten Fälle** ab. In folgenden Ausnahmen musst du weiterhin eine Spezial-Guideline im Code ergänzen (siehe `SOP_Extraction_Guidelines_hinzufuegen.md` Kapitel 5):

1. Die Guideline soll **nicht** allein anhand der Kataloggruppe schalten, sondern zusätzlich anhand einer **Property** (z. B. „nur wenn Property `Pitch` existiert").
2. Die Guideline soll für **mehrere** Kataloggruppen mit **unterschiedlichen** `DisplayName`s wirken (z. B. eine gemeinsame Memory-Guideline für Serial und Parallel Memory).
3. Die Guideline soll **vor** den bestehenden Spezial-Guidelines im Prompt stehen (Reihenfolge).

Für alle anderen Fälle: **Blob-Upload reicht.**

**Ende des SOP.**
