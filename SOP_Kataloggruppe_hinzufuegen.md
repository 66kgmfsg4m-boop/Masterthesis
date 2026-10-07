# SOP – Neue Kataloggruppe im Datasheet Analyzer hinzufügen

**Zielgruppe:** Kolleg:innen ohne C#-/Entwicklerkenntnisse, die den Bauteil-Katalog des Datasheet Analyzers pflegen.
**Version:** 1.0
**Letzte Aktualisierung:** 2025

---

## 1. Zweck dieser Anleitung

Der Datasheet Analyzer weiß **nicht von sich aus**, welche Bauteil-Kategorien (z. B. „Non-Volatile Memory Parallel", „EMI Filter", „Voltage Regulator LDO", …) existieren und welche Properties (z. B. `Vcc`, `NumberOfPin`, `Pitch`, `FMinHz`) darin extrahiert werden sollen.

Diese Informationen liest der Analyzer beim Start aus **einer einzigen CSV-Datei** in Azure Blob Storage:

> **`get_classes.csv`** – Katalog aller Kataloggruppen und Properties (Quelle der Wahrheit).

Das SOP beschreibt Schritt für Schritt, wie eine neue Kataloggruppe (z. B. „Crystal Oscillator") sicher hinzugefügt wird, ohne die Analyse für bestehende Gruppen zu brechen.

---

## 2. Voraussetzungen

Bevor du beginnst, brauchst du:

| Voraussetzung | Beschreibung |
|---|---|
| Zugriff auf Azure Blob Storage | Container, in dem `get_classes.csv` liegt (siehe `AzureStorage:ContainerName` in der App-Konfiguration). |
| Berechtigung „Blob-Daten hochladen" | Rolle `Storage Blob Data Contributor` oder vergleichbar. |
| Ein CSV-fähiges Werkzeug | Empfohlen: **Notepad++** oder **VS Code** (**nicht Excel** – siehe Kapitel 8). |
| Backup der aktuellen Datei | Immer eine Kopie von `get_classes.csv` lokal speichern, bevor du Änderungen hochlädst. |
| Kenntnis der DMS-Struktur | Vollständiger DMS-Pfad und interner Name der neuen Kataloggruppe (aus dem DMS). |

---

## 3. Aufbau der `get_classes.csv`

Die Datei hat **7 Spalten**. Trennzeichen ist entweder `,` oder `;` – der Analyzer erkennt es automatisch anhand der Kopfzeile. **Wichtig: In einer Datei nur EIN Trennzeichen verwenden.**

| # | Spalte | Beispielwert | Pflicht? | Bedeutung |
|---|---|---|---|---|
| 0 | `FullPath` | `/COMPONENT/Memory/NonVolatileMemoryParallel` | ja | Vollständiger DMS-Pfad der Gruppe (nur bei Component-Zeilen wichtig). |
| 1 | `InternalName` | `NonVolatileMemoryParallel` | ja | Interner (technischer) Name aus dem DMS. |
| 2 | `Category` | `Memory` | empfohlen | Übergeordnete Kategorie. |
| 3 | `DisplayName` | `Non-Volatile Memory Parallel` | **ja – Schlüsselfeld** | Anzeigename. **Muss bei Property-Zeilen exakt gleich sein wie in der zugehörigen Component-Zeile.** |
| 4 | `ComponentName` | `MemNVParallel` | empfohlen | Kurzname / E-Name aus dem DMS. |
| 5 | `PropertyName` | *leer* bei Component, `Vcc` bei Property | siehe unten | **Entscheidet, ob die Zeile eine Kataloggruppe (leer) oder eine Property (befüllt) ist.** |
| 6 | `PropertyNumber` | `1001` | optional | Property-Nummer aus dem DMS. |

### Zwei Zeilentypen

Die Datei enthält **zwei Zeilentypen**, die sich **nur** durch Spalte 5 (`PropertyName`) unterscheiden:

1. **Component-Zeile** (Kataloggruppe definieren) ? Spalte 5 **leer lassen**.
2. **Property-Zeile** (Eigenschaft dieser Gruppe) ? Spalte 5 **befüllt**; Spalte 3 (`DisplayName`) muss **exakt** dem `DisplayName` der zugehörigen Component-Zeile entsprechen.

> ?? Das Feld, das Properties an ihre Gruppe bindet, ist **`DisplayName` (Spalte 3)** – **nicht** der `FullPath`, **nicht** der `InternalName`. Tippfehler hier führen dazu, dass die Gruppe „leer" wirkt und die KI keine Properties extrahiert.

---

## 4. Ablauf: Neue Kataloggruppe hinzufügen

### Schritt 1 – Datei herunterladen

1. Öffne das Azure Portal ? Storage Account ? Container.
2. Suche die Datei `get_classes.csv` und lade sie herunter.
3. Lege eine **Sicherheitskopie** an: `get_classes_backup_YYYY-MM-DD.csv`.

### Schritt 2 – Datei in Notepad++ / VS Code öffnen

- **Nicht** in Excel öffnen – Excel verändert Zahlen, führt Anführungszeichen ein und kann die Kodierung zerstören.
- Achte darauf, dass die Datei **UTF-8** kodiert ist.
- Prüfe die erste Zeile (Header): Welches Trennzeichen wird verwendet? `,` oder `;`? ? Dieses Trennzeichen **konsequent** übernehmen.

### Schritt 3 – Component-Zeile für die neue Gruppe ergänzen

Am Ende der Datei eine neue Zeile hinzufügen. Beispiel (Trennzeichen `;`, neue Gruppe „Crystal Oscillator"):

```
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;;
```

Erklärung (in dieser Reihenfolge):

| Spalte | Wert | Kommentar |
|---|---|---|
| `FullPath` | `/COMPONENT/Oscillators/CrystalOscillator` | Muss zum DMS passen. |
| `InternalName` | `CrystalOscillator` | Aus dem DMS übernehmen. |
| `Category` | `Oscillators` | Übergeordnete Kategorie. |
| `DisplayName` | `Crystal Oscillator` | **Diesen exakten String für alle Property-Zeilen wiederverwenden!** |
| `ComponentName` | `OscXtal` | DMS-Kurzname. |
| `PropertyName` | *(leer)* | ? Signalisiert: Das ist eine Component-Zeile. |
| `PropertyNumber` | *(leer)* | Optional. |

> ?? Am Ende **zwei Trennzeichen hintereinander** (`;;` am Zeilenende) sind korrekt – sie stehen für die leeren Spalten 5 und 6.

### Schritt 4 – Property-Zeilen ergänzen

Für **jede** Eigenschaft der neuen Kataloggruppe **eine eigene Zeile**. `DisplayName` (Spalte 3) muss **identisch** zur Component-Zeile sein.

Beispiel (Fortsetzung „Crystal Oscillator"):

```
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;FrequencyHz;2001
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;FrequencyToleranceppm;2002
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;LoadCapacitancepF;2003
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;NumberOfPin;2004
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;Package;2005
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;PitchInch;2006
```

Regeln für Property-Namen:

- **Keine Suffixe** wie `_1`, `_2`, `_3` verwenden (Varianten werden automatisch konsolidiert).
- **Keine Leerzeichen** in Property-Namen – stattdessen CamelCase (`NumberOfPin` statt `Number of Pin`).
- Einheit als Suffix mit anfügen, wenn sinnvoll: `FrequencyHz`, `PitchInch`, `LoadCapacitancepF`.
- Property-Namen **eindeutig** je Gruppe halten.

### Schritt 5 – (Optional, aber empfohlen) Property-Definitionen ergänzen

Wenn du der KI erklären willst, **was** eine Property genau bedeutet, gibt es eine zweite Datei im gleichen Container:

> **`property_definitions.txt`**

Format: `PropertyName=Erklärung | (optional weitere Metainfos)`

Beispiel:

```
FrequencyHz=Nominal oscillation frequency of the crystal in Hertz.
FrequencyToleranceppm=Frequency tolerance at 25 °C in parts per million (ppm).
LoadCapacitancepF=Load capacitance in picofarads that the crystal was specified for.
PitchInch=Distance between adjacent pin centers, expressed in inches.
```

Die Erklärungen erscheinen anschließend im dynamisch generierten Prompt und verbessern die Extraktionsqualität deutlich.

### Schritt 6 – Datei validieren (vor dem Upload)

Vor dem Upload prüfen:

- [ ] Es gibt **genau eine Component-Zeile** für die neue Gruppe (Spalte 5 leer).
- [ ] Es gibt **? 1 Property-Zeile** für die neue Gruppe (Spalte 5 befüllt).
- [ ] Der `DisplayName` (Spalte 3) ist in **allen** Zeilen der neuen Gruppe **identisch** – auch Groß-/Kleinschreibung, Leerzeichen und Bindestriche zählen.
- [ ] Alle Zeilen haben **? 5 Spalten**. Zeilen mit weniger Spalten werden ignoriert.
- [ ] Es wird nur **ein** Trennzeichen in der ganzen Datei verwendet (`,` **oder** `;`).
- [ ] Keine kaputten Umlaute (Datei ist UTF-8 gespeichert).
- [ ] Keine leeren Zeilen zwischen den Datenzeilen.
- [ ] Property-Namen enthalten **kein** `_1`, `_2`, `_3` etc.

### Schritt 7 – Datei nach Azure Blob Storage hochladen

1. Azure Portal ? Storage Account ? Container.
2. Datei `get_classes.csv` auswählen ? **Overwrite** aktivieren ? hochladen.
3. Neue Datei kurz herunterladen und stichprobenartig prüfen, ob deine Änderungen tatsächlich enthalten sind (Datei-Caches sind heimtückisch).

### Schritt 8 – Analyzer neu starten / neu laden

Der Katalog wird **beim Start** der Anwendung geladen (Methode `LoadCatalogData()` in `DMSCatalogService`). Damit die neue Kataloggruppe wirksam wird:

- **Lokal:** Anwendung stoppen und neu starten.
- **Server / Cloud:** App-Service bzw. Container-Instanz neu starten (bei Kubernetes/AKS: Pods neu rollen).

---

## 5. Verifikation nach dem Upload

Nach dem Neustart im Log prüfen (`stdout` bzw. Application Insights):

```
Loading DMS catalog data
Detected delimiter: ';'
X catalog components loaded
Properties for Y components loaded
Catalog loaded: X components, Y property groups
```

**Erfolgs-Check:**

- [ ] `X` (Anzahl Components) hat sich um **1 erhöht**.
- [ ] `Y` (Anzahl Property-Groups) hat sich um **1 erhöht**.
- [ ] Es gibt **keine** Fehlermeldung `Error loading get_classes.csv`.

Zusätzlich in der Anwendung testen:

1. Ein PDF der neuen Kategorie hochladen (z. B. Crystal-Oscillator-Datenblatt).
2. **Stage 1 (Identifikation):** Die KI soll die neue Kataloggruppe (`Crystal Oscillator`) korrekt identifizieren.
3. **Stage 2 (Extraktion):** Im Ergebnis-JSON sollen die neuen Properties (`FrequencyHz`, `PitchInch`, …) enthalten sein.
4. **DMS-Vergleich** auf der Results-Seite: Nur die neuen Properties werden als Vergleichszeilen angezeigt (Filter erfolgt automatisch).

---

## 6. Beispiel: Vollständiger Ausschnitt aus `get_classes.csv`

```
FullPath;InternalName;Category;DisplayName;ComponentName;PropertyName;PropertyNumber
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;;
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;FrequencyHz;2001
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;FrequencyToleranceppm;2002
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;LoadCapacitancepF;2003
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;NumberOfPin;2004
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;Package;2005
/COMPONENT/Oscillators/CrystalOscillator;CrystalOscillator;Oscillators;Crystal Oscillator;OscXtal;PitchInch;2006
```

---

## 7. Kataloggruppe **ändern** oder **entfernen**

### Ändern (z. B. Property umbenennen oder neue Property nachziehen)

1. Zeile in `get_classes.csv` anpassen bzw. neue Zeile für die zusätzliche Property einfügen.
2. `DisplayName` **niemals** ändern, ohne alle Property-Zeilen der Gruppe **gleichzeitig** anzupassen – sonst „verlieren" die Properties ihre Gruppe.
3. Upload + Neustart wie oben.

### Entfernen

1. Alle Zeilen mit dem betroffenen `DisplayName` löschen (Component-Zeile **und** Property-Zeilen).
2. Sicherstellen, dass keine bestehende Report-/Vergleichsfunktion die alten Property-Namen fest referenziert.
3. Upload + Neustart wie oben.

> ?? Vorsicht: Wird eine Kataloggruppe entfernt, während im DMS noch Bauteile dieser Gruppe existieren, funktioniert nur der DMS-Vergleich für neue Analysen nicht mehr. Bestehende DMS-Datensätze werden **nicht** verändert – der Analyzer hat nur Lesezugriff.

---

## 8. Häufige Fehler & Lösungen

| Symptom | Ursache | Lösung |
|---|---|---|
| Log: `get_classes.csv is empty or has only header` | Datei enthält nur die Kopfzeile oder wurde leer hochgeladen. | Datei erneut prüfen und hochladen. |
| Log: `Error loading get_classes.csv` | Datei fehlt im Container oder Berechtigung fehlt. | Blob-Name & RBAC-Rolle prüfen. |
| Neue Gruppe wird im Log gezählt, aber Analyse extrahiert keine Properties | `DisplayName` in Component-Zeile ? `DisplayName` in Property-Zeilen. | Beide exakt (auch Groß-/Kleinschreibung, Leerzeichen) angleichen. |
| Fehler „Detected delimiter: ','" obwohl du `;` verwendest | Kopfzeile enthält mehr `,` als `;`. | Alle Trennzeichen in der ganzen Datei vereinheitlichen. |
| Umlaute im Log verstümmelt (`Ã¼`, `Ã¤`) | Datei nicht in UTF-8 gespeichert. | In Notepad++/VS Code: „Save with Encoding" ? **UTF-8** (ohne BOM). |
| Excel hat aus `1,5` plötzlich `1.5` gemacht oder aus `01001` `1001` | Excel formatiert automatisch. | Nur Notepad++/VS Code verwenden, **nicht** Excel. |
| Neue Gruppe erscheint nicht, obwohl Datei hochgeladen ist | Anwendung wurde nicht neu gestartet. | App-Service / Pods / lokalen Prozess neu starten. |
| Zeile wird komplett ignoriert | Zeile hat weniger als 5 Spalten (z. B. fehlender `ComponentName`). | Alle 5 Pflichtspalten befüllen, notfalls Platzhalter. |
| Properties tauchen als `PropertyName_1`, `PropertyName_2` im Ergebnis auf | Datenblatt hat Multi-Package-Tabellen. | Kein CSV-Fix nötig – Suffixe werden im Analyzer automatisch konsolidiert. |

---

## 9. Checkliste zum Ausdrucken

- [ ] Datei `get_classes.csv` aus Azure heruntergeladen
- [ ] Backup `get_classes_backup_YYYY-MM-DD.csv` gespeichert
- [ ] Component-Zeile (Spalte 5 leer) für neue Gruppe eingefügt
- [ ] Alle Property-Zeilen eingefügt, `DisplayName` identisch
- [ ] Trennzeichen einheitlich (`,` **oder** `;`)
- [ ] Datei als UTF-8 gespeichert
- [ ] (Optional) `property_definitions.txt` um neue Properties ergänzt
- [ ] Datei nach Azure hochgeladen (Overwrite)
- [ ] Anwendung neu gestartet
- [ ] Log geprüft: Component- und Property-Count sind gestiegen
- [ ] Test-Datenblatt der neuen Gruppe erfolgreich analysiert

---

## 10. Ansprechpartner / Support

| Thema | Ansprechpartner |
|---|---|
| Azure-Zugriff / Berechtigungen | *(bitte eintragen)* |
| Fachliche Fragen zum DMS-Katalog | *(bitte eintragen)* |
| Technische Probleme mit dem Analyzer | *(bitte eintragen)* |

---

**Ende des SOP.**
