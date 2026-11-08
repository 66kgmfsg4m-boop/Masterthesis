# Datasheet Analyzer – Funktionsübersicht & .NET 10 Blazor Web Migration

## 1. Funktionsübersicht der Applikation

### 1.1 Technischer Überblick

| Aspekt | Beschreibung |
|--------|--------------|
| **Typ** | WPF-Desktopanwendung (Windows Presentation Foundation) |
| **Framework** | .NET Framework 4.7.2 (Hauptprojekt) / 4.0 (alternatives Projekt) |
| **Ausgabe** | `WinExe` – klassische Windows-EXE |
| **Zweck** | Analyse von PDF-Dokumenten (Auftragsbestätigungen und technische/Sicherheits-Datenblätter) mit Azure OpenAI; Anbindung an UiPath und DMS-Katalog |

---

### 1.2 Kernfunktionen

#### A) Zwei Analysemodi

1. **Auftragsbestätigung (Order Confirmation)**  
   - PDF-Upload (Drag & Drop oder Dateiauswahl)  
   - Text- oder bildbasierte Analyse via Azure OpenAI  
   - Prompt aus Azure Blob Storage (`Prompt.txt`)  
   - Ergebnis: strukturierte JSON-Daten  
   - Automatisches Kopieren der PDF in ein Archivverzeichnis (lokal/Netzwerk)  
   - Optional: Weitergabe der JSON-Daten an **UiPath Orchestrator** (Queue)  

2. **Datenblatt (Datasheet)**  
   - Zwei Stufen:  
     - **Stufe 1:** Komponenten-Identifikation (DocumentType: COMPONENT oder MIXTURE, Kategorie, Part Number, Hersteller)  
     - **Stufe 2:** Detail-Extraktion mit bestätigter Identifikation  
   - Prompts aus Azure Blob (Identification, Safety, PartNumberDecoding, SeriesDatasheet)  
   - Bei **Resistoren:** Part-Number-Dekodierung (KI + Muster) oder manuelle Parameter-Eingabe  
   - **Safety Data Sheets (MIXTURE):** vereinfachter Ablauf ohne Komponenten-Bestätigung  
   - Validierung: `IdentificationValidationService`, `PropertyValidationService`  
   - Optional: Korrektur-Dialog für Kategorie/Attribute mit DMS-Katalog  

#### B) Benutzeroberfläche (WPF)

- **Hauptfenster:** Zwei-Spalten-Layout (links: Modus, Upload, Status; rechts: Analyseergebnisse als formatiertes JSON/Log)  
- **Drag & Drop** für PDF-Dateien  
- **Dateiauswahl** per `OpenFileDialog`  
- **Token-Statistik** (Prompt/Completion, Durchschnitt pro PDF)  
- **Kopieren** des aktuellen JSON in die Zwischenablage  
- **Zurücksetzen** (Ergebnisse und Token-Statistik löschen)  
- **Modale Dialoge:**  
  - `IdentificationConfirmationDialog` – Bestätigung/Korrektur der KI-Identifikation, Part-Number-Eingabe  
  - `InputDialog` – Freitext (z. B. Kategorie korrigieren)  
  - `ResistorParameterDialog` – manuelle Resistor-Parameter  
  - `CorrectionWindow` – Korrektur mit DMS-Katalog  

#### C) Services & externe Anbindungen

| Service | Aufgabe |
|---------|--------|
| **AzureAIService** | Azure OpenAI: Text- und Bildanalyse (Vision), Token-Tracking |
| **DatasheetAnalysisService** | Datenblatt-Analyse, Identifikation, Detail-Extraktion, Prompts aus Azure Blob |
| **RemoteConfigService** | Azure Blob Storage: Konfiguration (z. B. `azure-config.properties`, `authorized-users.properties`), Prompts (TXT), Diagnose |
| **AuthorizationService** | Prüfung des **Windows-Benutzernamens** (`Environment.UserName`) gegen Liste aus Azure Blob; Zugriff nur für autorisierte User |
| **PDFProcessor** | PDF-Verarbeitung (iTextSharp): Text-Extraktion, Bild-Extraktion (JPEG/PNG), Base64 für Vision-API |
| **PDFFileManagerService** | Kopieren der PDF in Archiv (Pfad aus Azure oder Fallback `C:\Temp\PDFArchiv\`) |
| **UiPathOrchestratorService** | OAuth2, Queue-Item an UiPath Orchestrator senden (nur Auftragsbestätigung) |
| **DMSCatalogService** | DMS-Katalog laden (z. B. Komponentenkategorien) für Korrektur-Dialog |
| **PartNumberDecoderService** | Part-Number-Dekodierung (KI + Muster) für Resistoren |
| **IdentificationValidationService** | Objektive Regeln zur Bewertung der Identifikation (Confidence, Issues) |
| **PropertyValidationService** | Validierung der extrahierten Properties je Komponentenkategorie |
| **JsonParser** | JSON parsen und für Anzeige aufbereiten |

#### D) Konfiguration

- **Azure:** Endpoint, API-Key, Deployment, API-Version aus Azure Blob (`azure-config.properties`)  
- **UiPath:** Orchestrator-URL, Tenant, Queue, ClientId/Secret, OrganizationUnitId (z. B. aus `UiPathConfig` / Konfigurationsdatei)  
- **Autorisierung:** Benutzerliste aus Azure Blob (`authorized-users.properties`)  
- **Archiv-Pfad:** aus Konfiguration oder Standard `C:\Temp\PDFArchiv\`  

#### E) Abhängigkeiten (Auswahl)

- **WPF:** `PresentationCore`, `PresentationFramework`, `WindowsBase`, `System.Xaml`  
- **Desktop:** `Microsoft.Win32.OpenFileDialog`, `System.Windows.MessageBox`, `Clipboard`, `Dispatcher`  
- **Azure:** `Azure.AI.OpenAI`, `Azure.Storage.Blobs`, `Azure.Core`  
- **PDF:** `iTextSharp` (itextsharp)  
- **JSON:** `Newtonsoft.Json`, ggf. `System.Text.Json`  
- **HTTP:** `System.Net.Http`  

---

### 1.3 Datenfluss (vereinfacht)

```
[Benutzer] → PDF auswählen/ziehen
    → AuthorizationService (Windows-User prüfen)
    → PDFProcessor (Text/Bilder extrahieren)
    → AzureConfig / RemoteConfigService (Prompts & Config aus Azure Blob)
    → AzureAIService oder DatasheetAnalysisService (OpenAI)
    → ggf. IdentificationConfirmationDialog / InputDialog / ResistorParameterDialog
    → IdentificationValidationService / PropertyValidationService
    → Ergebnis anzeigen (MainWindow), ggf. UiPath senden
    → ggf. PDFFileManagerService (Archiv-Kopie)
```

---

## 2. Vorschlagsliste: Migration auf .NET 10 Blazor Web

Damit die Applikation **.NET 10 – Blazor webfähig** wird, sind folgende Schritte und Entscheidungen nötig.

---

### 2.1 Projekt & Zielplattform

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 1 | Neues Projekt anlegen: **Blazor Web App** (.NET 10), z. B. „Blazor Web App“-Template mit Server + optional WebAssembly/Interaktiv. | Hoch |
| 2 | Ziel-Framework auf **net10.0** stellen; keine .NET Framework-Referenzen mehr. | Hoch |
| 3 | Bestehende **Service-Logik** (Azure AI, Datasheet, Validierung, JSON, Katalog, PartNumberDecoder, RemoteConfig, PDFProcessor-Kern) in eine **klassische Klassenbibliothek** (.NET 10) auslagern, die von der Blazor-App referenziert wird. | Hoch |
| 4 | NuGet-Pakete auf .NET 10-kompatible Versionen umstellen (Azure.*, PDF-Bibliothek, Newtonsoft.Json/System.Text.Json). | Hoch |

---

### 2.2 UI: WPF → Blazor

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 5 | **MainWindow** durch Blazor-Seiten/Komponenten ersetzen: z. B. eine Seite „Analyse“ mit zwei Spalten (Upload/Bereich links, Ergebnisse rechts), Modus-Umschaltung (Auftragsbestätigung / Datenblatt). | Hoch |
| 6 | **Drag & Drop** im Browser abbilden (HTML5 Drag and Drop API oder Blazor-Komponente); **Datei-Upload** per `<InputFile>` bzw. `IJSObjectReference` für Drag & Drop. | Hoch |
| 7 | **OpenFileDialog** entfällt im Web: nur Upload über Browser (Datei auswählen oder Drag & Drop). | Hoch |
| 8 | **MessageBox** ersetzen durch Blazor-UI: z. B. modale Dialoge (eigene Komponente oder Bibliothek wie MudBlazor/Radzen), Toast-Meldungen oder Inline-Hinweise. | Hoch |
| 9 | **IdentificationConfirmationDialog**, **InputDialog**, **ResistorParameterDialog**, **CorrectionWindow** als **Blazor-Komponenten** (modale oder inline Dialoge) neu implementieren; gleiche fachliche Abläufe, andere UI-Bindung. | Hoch |
| 10 | **Dispatcher.Invoke/InvokeAsync** entfernen: in Blazor läuft UI-Update im Synchronisationskontext des Renderers; async Services mit `StateHasChanged()` oder `InvokeAsync(StateHasChanged)` nutzen. | Hoch |
| 11 | **Clipboard**: im Browser per Clipboard API (JavaScript Interop) umsetzen, da direkter Zugriff aus C# eingeschränkt ist. | Mittel |

---

### 2.3 Autorisierung & Identität

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 12 | **AuthorizationService:** `Environment.UserName` funktioniert im Web nicht (Server kennt ggf. den Windows-User nur bei Windows-Auth). **Strategie wählen:** z. B. ASP.NET Core Identity, Azure AD / Entra ID, oder weiterhin „Benutzerliste aus Azure Blob“, aber Identität aus Auth-System (Claims/User.Identity.Name) statt `Environment.UserName`. | Hoch |
| 13 | Wenn die bestehende „authorized-users“-Liste aus Azure Blob bleiben soll: Blazor Server/Backend liest diese Liste und vergleicht sie mit dem angemeldeten Benutzer (z. B. User.Identity.Name). | Hoch |
| 14 | .NET 10 bietet **Passkey/WebAuthn**; optional für zukünftige Anmeldung nutzbar. | Niedrig |

---

### 2.4 Dateien & Dateisystem

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 15 | **PDF-Upload:** PDF kommt als Stream aus dem Browser; Verarbeitung im Blazor Server oder in einer Web-API. `PDFProcessor` sollte mit `Stream` statt nur mit Dateipfad arbeiten (oder Pfad zu temporärer Datei aus hochgeladenem Stream). | Hoch |
| 16 | **PDFFileManagerService:** Archivierung auf „C:\Temp\PDFArchiv\“ oder Netzwerkpfad ist serverseitig möglich; Konfiguration (z. B. aus Azure) beibehalten. Sicherheit und Berechtigungen auf dem Server prüfen. | Hoch |
| 17 | Kein direkter Zugriff auf beliebige Client-Dateipfade – nur hochgeladene Dateien verarbeiten. | Hoch |

---

### 2.5 PDF-Bibliothek

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 18 | **iTextSharp (5.x)** prüfen: Lizenz (AGPL) und .NET 10-Kompatibilität. Alternativen: **iText 7** (.NET), **QuestPDF**, **Docnet** oder andere .NET-Standard-Bibliotheken für Text-/Bildextraktion. Gegebenenfalls `PDFProcessor` auf neue Bibliothek und Stream-basierte API umstellen. | Hoch |

---

### 2.6 Konfiguration & Geheimnisse

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 19 | **RemoteConfigService:** Verbindung zu Azure Blob und Speicher-Keys **nicht** im Frontend (Blazor WebAssembly) verwenden. Nur im **Blazor Server** oder in einer **Backend-API** laufen lassen; Konfiguration/Keys aus **Azure Key Vault**, User Secrets oder Umgebungsvariablen. | Hoch |
| 20 | **Hardcodierte Keys** in `RemoteConfigService` (Storage Account Key) entfernen und durch sichere Konfiguration ersetzen (z. B. Managed Identity + Key Vault oder App Configuration). | Hoch |

---

### 2.7 Azure OpenAI & HTTP

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 21 | **AzureAIService** und **DatasheetAnalysisService** im Backend (Blazor Server oder API) betreiben; Aufrufe von Blazor-UI aus per Service/HttpClient. Timeout (z. B. 5 Min) und große Payloads (Bilder) berücksichtigen. | Hoch |
| 22 | **HttpClient** mit `IHttpClientFactory` registrieren und keine Zertifikat-Umgehung wie im UiPath-Service in Produktion nutzen. | Mittel |

---

### 2.8 UiPath

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 23 | **UiPathOrchestratorService** nur serverseitig aufrufen (Blazor Server oder API); gleiche Logik, Aufruf aus dem Request-Kontext. SSL-Validierung für Produktion wieder aktivieren. | Hoch |

---

### 2.9 State & Parallelität

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 24 | **Parallele Verarbeitung** (SemaphoreSlim, MAX_PARALLEL_TASKS): im Web pro Benutzer/Session begrenzen; ggf. zentrale Queue (z. B. Background Jobs) für viele gleichzeitige Uploads. | Mittel |
| 25 | **Token-Statistik** und **currentJsonResult** pro Benutzer/Session halten (z. B. in Scoped Services oder Session-State); in .NET 10 optional **Persistent State** für bessere UX bei Verbindungsunterbrechungen. | Mittel |

---

### 2.10 Weitere technische Anpassungen

| Nr. | Maßnahme | Priorität |
|-----|----------|-----------|
| 26 | **Resources.resx / System.Windows.Forms:** Wenn noch genutzt, durch Blazor-Ressourcen oder lokalisierte Strings ersetzen. | Mittel |
| 27 | **Console.WriteLine** durch strukturierte **ILogger**-Logs ersetzen (ASP.NET Core Logging). | Mittel |
| 28 | **Blazor-Rendering-Modus:** Server, WebAssembly oder Auto je nach Latenz- und Skalierungsanforderungen wählen; für schwere PDF/OpenAI-Last ist **Blazor Server** naheliegend. | Mittel |
| 29 | **Lange laufende Analysen:** Ggf. SignalR/Progress-Updates an den Client, damit der Nutzer Status (z. B. „Stufe 1 …“, „Stufe 2 …“) sieht, ohne die Seite neu zu laden. | Niedrig |

---

### 2.11 Übersicht nach Bereichen

| Bereich | Kurzfassung |
|--------|--------------|
| **Projekt & Ziel** | Neues Blazor-Web-Projekt (.NET 10), Services in Bibliothek auslagern. |
| **UI** | WPF durch Blazor ersetzen; Dialoge und Upload (Drag & Drop/InputFile) webbasiert. |
| **Auth** | Windows-User durch Web-Auth ersetzen; authorized-users-Logik an Identity anbinden. |
| **Dateien** | Upload per Stream; Archivierung nur serverseitig. |
| **PDF** | iTextSharp prüfen/ersetzen; Stream-basierte Verarbeitung. |
| **Config & Secrets** | Nur Backend; Keys/Secrets sicher (Key Vault, etc.). |
| **Azure OpenAI / UiPath** | Nur im Backend; HttpClient und SSL sauber konfigurieren. |
| **State & Logging** | Session/Scoped State, ILogger, ggf. Persistent State (.NET 10). |

---

## 3. Empfohlene Reihenfolge (Phasen)

1. **Phase 1:** Neues .NET 10 Blazor Web App-Projekt; Service-Layer in .NET 10-Bibliothek auslagern; Konfiguration/Secrets und Azure Blob/OpenAI nur im Backend.  
2. **Phase 2:** PDFProcessor auf Stream + ggf. neue PDF-Bibliothek umstellen; Authorization auf Web-Identity umstellen.  
3. **Phase 3:** Haupt-UI in Blazor (Upload, Modus, Ergebnisanzeige, Token-Statistik, Kopieren, Zurücksetzen).  
4. **Phase 4:** Alle modalen Dialoge (Identifikation, Input, Resistor, Korrektur) als Blazor-Komponenten; MessageBox durch Blazor-Dialoge ersetzen.  
5. **Phase 5:** UiPath, Archivierung, Feinschliff (Logging, State, ggf. SignalR für Fortschritt).

Mit dieser Funktionsübersicht und der Vorschlagsliste kann die Migration der Applikation auf .NET 10 Blazor Web systematisch geplant und umgesetzt werden.

---

## 4. Aufgabenliste: Programmlogik für Blazor SHAPE .NET webfähig machen

Die folgende Liste baut auf dem bestehenden **DatasheetAnalyzer.Blazor**-Projekt (inkl. SHAPE-Pakete) auf und beschreibt die konkreten Aufgaben, um die **aktuelle WPF-Programmlogik** (CheckOrderConfirmationFromSupplier) webfähig zu machen.

### 4.1 Service-Layer (Backend)

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 1 | **Klassenbibliothek anlegen** | Neues Projekt z. B. `DatasheetAnalyzer.Core` (.NET 10), referenziert von `DatasheetAnalyzer.Blazor`. |
| 2 | **Services aus WPF übernehmen** | Aus `CheckOrderConfirmationFromSupplier`: `AzureAIService`, `DatasheetAnalysisService`, `RemoteConfigService`, `PDFProcessor`, `PDFFileManagerService`, `UiPathOrchestratorService`, `DMSCatalogService`, `PartNumberDecoderService`, `IdentificationValidationService`, `PropertyValidationService`, `JsonParser`, Config-Klassen (`AzureConfig`, `UiPathConfig`). Keine WPF-Referenzen (kein `System.Windows`, kein `MessageBox`). |
| 3 | **PDFProcessor stream-fähig machen** | API erweitern/ersetzen: z. B. `ProcessPdf(Stream stream)` oder temporäre Datei aus Upload-Stream. Nur serverseitig aufrufen. |
| 4 | **PDF-Bibliothek prüfen/ersetzen** | iTextSharp (Lizenz/.NET 10) prüfen; ggf. auf iText 7, QuestPDF o. Ä. umstellen. |
| 5 | **RemoteConfigService & Secrets** | Azure Blob-Zugriff nur im Backend; Storage-Key/Connection nicht im Frontend. Keys aus Key Vault, User Secrets oder `appsettings` (nicht committen). Hardcodierte Keys in `RemoteConfigService` entfernen. |
| 6 | **Services in Blazor registrieren** | In `Program.cs`: `AzureConfig`, `RemoteConfigService`, `AzureAIService`, `DatasheetAnalysisService`, `PDFProcessor`, `PDFFileManagerService`, `DMSCatalogService`, `UiPathOrchestratorService` (ggf. scoped), Validierungs-Services, `JsonParser` – alle als Scoped/Singleton je nach Nutzung. |
| 7 | **ILogger statt Console** | In allen übernommenen Services `Console.WriteLine` durch `ILogger` ersetzen und Logger per DI injizieren. |

### 4.2 Autorisierung (SHAPE-konform)

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 8 | **AuthorizationService an SHAPE anbinden** | Statt `Environment.UserName`: aktuellen Benutzer aus SHAPE/OpenID (z. B. `HttpContext.User`, Claims, `User.Identity.Name`) ermitteln. |
| 9 | **authorized-users mit Identity verbinden** | Liste aus Azure Blob (`authorized-users.properties`) weiter nutzen: Backend lädt sie und prüft sie gegen den per SHAPE angemeldeten Benutzer. Bei nicht autorisiert: Redirect auf AccessDenied oder 403. |
| 10 | **Optional: Rollen** | Falls „Administrator“ o. Ä. benötigt wird: mit SHAPE Identity/Keycloak-Rollen abbilden (bereits `Roles.Administrator` im Layout genutzt). |

### 4.3 Konfiguration

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 11 | **Azure-Config ins Backend** | `azure-config.properties`-Inhalte (Endpoint, API-Key, Deployment) aus Azure Blob laden – nur in Server-Logik, nie an den Browser senden. |
| 12 | **UiPath-Config** | Orchestrator-URL, Tenant, Queue, ClientId/Secret, OrganizationUnitId aus `appsettings.yaml`/Umgebungsvariablen/Key Vault; in `Program.cs` oder Options-Pattern laden und an `UiPathOrchestratorService` übergeben. |
| 13 | **Archiv-Pfad** | `PDFFileManagerService`: Zielverzeichnis aus Konfiguration (wie bisher Azure oder Fallback); Berechtigungen auf dem Server prüfen. |

### 4.4 Haupt-UI (Analyse-Seite)

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 14 | **Analyse-Seite anlegen** | Neue Seite z. B. `Pages/Analyze.razor` (Route `/Analyze` oder `/`) mit zwei-Spalten-Layout: links Modus, Upload, Status; rechts Ergebnis (wie MainWindow). |
| 15 | **Modus-Umschaltung** | Zwei Modi wie in WPF: „Auftragsbestätigung“ und „Datenblatt“ (z. B. Radio-Buttons oder Tabs); Zustand in Komponenten-State oder Scoped-Service. |
| 16 | **PDF-Upload** | `<InputFile>` für Dateiauswahl; optional Drag & Drop per JS Interop. Hochgeladene Datei als Stream an Backend übergeben (API-Endpunkt oder direkt an Service in Blazor Server). |
| 17 | **Ergebnis-Anzeige** | Bereich für formatiertes JSON/Log (wie `jsonResultArea`); Inhalte aus Backend-Verarbeitung füllen, ggf. mit `@key` und `StateHasChanged` für Updates. |
| 18 | **Token-Statistik** | Prompt-/Completion-Tokens und „∅ Tokens/PDF“ anzeigen; Werte aus Azure-Services (Callback/Event oder Rückgabe) in Scoped-Service oder Komponenten-State halten und anzeigen. |
| 19 | **Kopieren-Button** | Aktuelles JSON in Zwischenablage: per JS Interop (Clipboard API) umsetzen; Button nur aktiv, wenn Ergebnis vorhanden. |
| 20 | **Zurücksetzen-Button** | Ergebnis und Token-Statistik löschen; State zurücksetzen. |
| 21 | **Fortschritt/Status** | Während der Analyse Ladeanzeige oder Statuszeile (z. B. „Stufe 1 …“, „Stufe 2 …“); bei langen Läufen optional SignalR/Progress-Updates. |

### 4.5 Dialoge als Blazor-Komponenten

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 22 | **IdentificationConfirmationDialog** | Als Blazor-Komponente (modal oder inline): Anzeige der KI-Identifikation, Part-Number-Eingabe, Buttons Bestätigen/Korrigieren/Abbrechen. Rückgabe wie in WPF (Confirmed/NeedsCorrection, Identifikation, ggf. ManualResistorProperties). |
| 23 | **InputDialog** | Einfacher Modal für Freitext (z. B. Kategorie korrigieren): Titel, Prompt, Vorwert, OK/Abbrechen; Rückgabe als string. |
| 24 | **ResistorParameterDialog** | Modal für manuelle Resistor-Parameter; gleiche Felder/Logik wie WPF, Ausgabe als strukturierte Daten (z. B. JObject oder DTO) für die weitere Pipeline. |
| 25 | **CorrectionWindow** | Korrektur-UI mit DMS-Katalog (Kategorie/Attribute): als Blazor-Seite oder großes Modal; `DMSCatalogService` und `CorrectionWindow`-Logik übernehmen, ohne WPF. |
| 26 | **Modale Umsetzung** | Dialoge mit SHAPE-Komponenten oder eigenem Modal (z. B. Bootstrap Modal + `@rendermode InteractiveServer`); keine `MessageBox` – stattdessen Blazor-UI und Callbacks. |

### 4.6 Analyse-Workflow im Backend

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 27 | **Upload → Verarbeitung** | Nach PDF-Upload: Stream an `PDFProcessor`; Ergebnis an `AzureAIService` oder `DatasheetAnalysisService` (je nach Modus). Alle Aufrufe im Blazor Server-Kontext (oder in API-Controller), nicht im Browser. |
| 28 | **Auftragsbestätigung** | Wie in WPF: Prompt aus Azure Blob, Text- oder Bildanalyse, JSON zurück; optional PDF archivieren (`PDFFileManagerService`), optional an UiPath senden. |
| 29 | **Datenblatt zweistufig** | Stufe 1: Identifikation → `IdentificationValidationService` → bei COMPONENT: Blazor-Dialog „Identifikation bestätigen“ (IdentificationConfirmationDialog). Stufe 2: Detail-Extraktion mit bestätigter Identifikation; bei Resistoren ggf. Part-Number-Dekodierung oder ResistorParameterDialog. |
| 30 | **Safety Data Sheet (MIXTURE)** | Wie in WPF: Kein Bestätigungsdialog, direkte Weiterverarbeitung; kein ValidationResult für Anzeige. |
| 31 | **Property-Validierung** | Nach Extraktion `PropertyValidationService` aufrufen; Warnungen/Fehler ins ValidationResult und in der UI anzeigen. |
| 32 | **UiPath-Anbindung** | Nur bei Modus „Auftragsbestätigung“: nach erfolgreicher Analyse `UiPathOrchestratorService.SendJsonToQueue` aufrufen (serverseitig); SSL-Validierung in Produktion aktiv lassen. |

### 4.7 State & Nebenläufigkeit

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 33 | **Pro-User-State** | Token-Statistik, aktuelles JSON, aktueller Modus, ggf. „current task“ pro Benutzer/Session (Scoped-Service oder Session-State), damit parallele Nutzer sich nicht überschreiben. |
| 34 | **Parallele Verarbeitung** | SemaphoreSlim/MAX_PARALLEL_TASKS aus WPF: pro User/Session begrenzen oder zentrale Queue; keine unbegrenzte Parallelität pro Server. |
| 35 | **Dispatcher ersetzen** | Kein `Dispatcher.Invoke`/`InvokeAsync`: UI-Updates in Blazor über normale async-Methoden und `StateHasChanged()` bzw. `InvokeAsync(StateHasChanged)` auf der Komponente. |

### 4.8 Weitere Anpassungen

| Nr. | Aufgabe | Details |
|-----|--------|--------|
| 36 | **Navigation/Link zur Analyse** | Im SHAPE-Layout (NavBarSecondary) einen Link „Analyse“ oder „Start“ auf die neue Analyse-Seite setzen. |
| 37 | **Fehlerbehandlung** | Exceptions in Services mit ILogger loggen; in der UI Fehlermeldungen per Blazor-Komponente (z. B. Alert oder SHAPE-Toaster) anzeigen, keine MessageBox. |
| 38 | **Ressourcen/Lokalisierung** | Falls noch `.resx` genutzt wird: durch SHAPE-Lokalisierung oder eigene Ressourcen ersetzen; Texte in Komponenten oder IStringLocalizer. |

### 4.9 Kurzüberblick nach Bereichen

| Bereich | Was zu tun ist |
|--------|-----------------|
| **Services** | In .NET 10-Bibliothek auslagern, stream-basierter PDFProcessor, keine WPF-Refs, ILogger, DI in Program.cs. |
| **Auth** | Windows-User durch SHAPE/OpenID ersetzen; authorized-users-Liste aus Azure Blob gegen Identity prüfen. |
| **Config** | Azure/UiPath/Archiv nur Backend, Secrets aus Key Vault/Config, keine Keys im Frontend. |
| **Haupt-UI** | Analyse-Seite mit Modus, Upload (InputFile), Ergebnis, Token-Statistik, Kopieren, Zurücksetzen, Status. |
| **Dialoge** | IdentificationConfirmation, Input, ResistorParameter, Correction als Blazor-Komponenten (modal). |
| **Workflow** | Upload → PDF → Azure/Datasheet-Services → ggf. Dialoge → Validierung → Anzeige, UiPath, Archiv. |
| **State** | Scoped/Session für User, begrenzte Parallelität, kein Dispatcher. |

Diese Aufgabenliste ergänzt die allgemeine Migrationsvorschlagsliste (Abschnitt 2) um **konkrete Schritte** für die Überführung der bestehenden Programmlogik in die Blazor-SHAPE-.NET-Webanwendung.
