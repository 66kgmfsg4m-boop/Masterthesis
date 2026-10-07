# Changelog – Datasheet Analyzer (Blazor SHAPE .NET webfähig)

Alle wesentlichen Änderungen an der Migration von der WPF-Programmlogik zur Blazor-SHAPE-Webanwendung werden hier dokumentiert.

---

## [Unreleased]

### Geplant / Nächste Schritte
- Vollständige Migration: DatasheetAnalysisService, AzureAIService, IdentificationValidationService, PropertyValidationService, PartNumberDecoderService, DMSCatalogService, PDFFileManagerService, UiPathOrchestratorService (in Core, mit ILogger).
- Analyse-Workflow anbinden: Nach PDF-Upload Azure AI bzw. DatasheetAnalysis aufrufen, Token-Callback an AnalyzeStateService, Ergebnis in ResultLog/CurrentJsonResult.
- Blazor-Dialoge als Komponenten: IdentificationConfirmationDialog, InputDialog, ResistorParameterDialog, CorrectionWindow (modal mit Bootstrap/SHAPE).
- Auth-Anbindung: authorized-users aus Azure Blob gegen SHAPE/OpenID-Identity prüfen.

---

## [0.1.0] – 2025-03-12

### Hinzugefügt
- **CHANGELOG.md** – Changelog für die Migrationsaufgaben.
- **DatasheetAnalyzer.Core** – Neue .NET-10-Klassenbibliothek für die gemeinsame Service-Logik.
- **Core: Options** – `AzureStorageOptions` für Azure Blob (ConnectionString, ContainerName); keine hardcodierten Keys mehr in der Bibliothek.
- **Core: RemoteConfigService** – Übernommen aus WPF; Konfiguration aus Azure Blob; ConnectionString/Container über Konstruktor/IOptions; Ausgabe über `ILogger` statt `Console`.
- **Core: JsonParser** – Aus `CheckOrderConfirmationFromSupplier.Util` übernommen; Namespace `DatasheetAnalyzer.Core.Util`.
- **Core: PDFProcessor** – Aus WPF übernommen; `ProcessPdf(Stream stream)` hinzugefügt (temporäre Datei, dann bestehende Logik); `ILogger` statt `Console`; Namespace `DatasheetAnalyzer.Core.Services`.
- **Core: AzureConfig** – Übernommen; abhängig von `IRemoteConfigService` und `ILogger`; Konfiguration nur aus Azure Blob.
- **Core: UiPathConfig** – Übernommen; optional (leere Config = UiPath deaktiviert); `ILogger`.
- **Blazor: Referenz auf DatasheetAnalyzer.Core** – Projektreferenz in `DatasheetAnalyzer.Blazor.csproj`.
- **Blazor: DI-Registrierung** – In `Program.cs` vorbereitet: `AzureStorageOptions` aus Config, `RemoteConfigService`, `AzureConfig`, `UiPathConfig`, `PDFProcessor`, `JsonParser` (Scoped/Singleton je nach Nutzung).
- **Blazor: Analyze-Seite** – Neue Seite `Pages/Analyze.razor` (Route `/Analyze`): Zwei-Spalten-Layout, Modus-Umschaltung (Auftragsbestätigung/Datenblatt), PDF-Upload per `<InputFile>`, Ergebnisbereich (formatiertes JSON/Log), Token-Statistik, Buttons Kopieren und Zurücksetzen, Statuszeile.
- **Blazor: AnalyzeStateService** – Scoped-Service für pro-User-State: aktueller Modus, aktuelles JSON, Token-Statistik (Prompt/Completion, Anzahl PDFs, ∅ Tokens/PDF), Status-Text; Methoden zum Setzen/ Zurücksetzen und für Token-Updates.
- **Blazor: Clipboard-JS-Interop** – `wwwroot/js/clipboard.js` und Aufruf aus der Analyse-Seite für „Kopieren“ (Clipboard API).
- **Blazor: Layout** – NavLink „Analyse“ in `Layout.razor` (NavBarSecondary) auf `/Analyze` gesetzt.

### Geändert
- **FUNKTIONSÜBERSICHT_UND_BLAZOR_MIGRATION.md** – Abschnitt 4 (Aufgabenliste) um konkrete Schritte ergänzt; nach Umsetzung werden erledigte Punkte im Changelog abgearbeitet.

### Technische Hinweise
- **RemoteConfigService**: Azure Blob ConnectionString muss von der Host-Anwendung (Blazor) über `AzureStorageOptions` (z. B. aus `appsettings.yaml` oder Key Vault) übergeben werden. Kein Key im Core-Projekt.
- **PDFProcessor**: iTextSharp 5.x vorerst beibehalten; langfristig Prüfung Lizenz/.NET-10 und ggf. Wechsel auf iText 7 oder QuestPDF.
- **Analyse-Workflow**: Die eigentliche Verarbeitung (Azure AI, DatasheetAnalysis) wird in einem folgenden Update an die Analyze-Seite und an den AnalyzeStateService angebunden.
- **NuGet**: Core-Pakete (Azure.Storage.Blobs, iTextSharp, …) werden von nuget.org bzw. dem konfigurierten Feed bezogen. Bei 403 vom Feed: Zugriff prüfen oder nur nuget.org verwenden.

---

[Unreleased]: https://github.com/.../compare/v0.1.0...HEAD
[0.1.0]: https://github.com/.../releases/tag/v0.1.0
