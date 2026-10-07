# ?? Blazor Migration - Service Migration Status

## **Aktuelle Situation**

Die Blazor-Anwendung kann nicht gebaut werden, weil kritische Services noch im WPF-Projekt sind und nicht im `DatasheetAnalyzer.Core` Projekt.

---

## ? **Bereits migriert nach DatasheetAnalyzer.Core:**

1. ? **RemoteConfigService** - Azure Blob Storage Service mit DI
2. ? **AzureConfig** - Azure OpenAI Konfiguration
3. ? **PDFProcessor** - PDF-Verarbeitung (Text/Bild-Extraktion)
4. ? **JsonParser** - JSON-Parsing und Formatierung
5. ? **DMSCatalogService** - DMS Katalog-Daten (NEU MIGRIERT!)

---

## ? **Noch zu migrieren:**

### **Kritische Services (für Blazor erforderlich):**

1. ? **DatasheetAnalysisService** - Hauptanalyse-Service
   - **Größe:** ~800 Zeilen
   - **Abhängigkeiten:** AzureConfig, RemoteConfigService, DMSCatalogService, PDFProcessor
   - **Status:** Muss mit DI umgeschrieben werden

2. ? **PartNumberDecoderService** - Part Number Dekodierung
   - **Größe:** ~400 Zeilen
   - **Abhängigkeiten:** RemoteConfigService, DMSCatalogService
   - **Status:** Muss mit DI umgeschrieben werden

### **Optionale Services (nur für erweiterte Features):**

3. ?? **IdentificationValidationService** - Post-Validation der KI-Ergebnisse
   - **Nutzung:** Nur in WPF-UI für Validierungs-Dialoge
   - **Status:** Kann später migriert werden

4. ?? **PropertyValidationService** - Property-Validierung
   - **Nutzung:** Nur in WPF-UI für Warnungen
   - **Status:** Kann später migriert werden

5. ?? **AuthorizationService** - Benutzer-Authorisierung
   - **Nutzung:** WPF-spezifisch (lädt authorized-users.txt)
   - **Status:** Für Blazor: SHAPE Authentication wird verwendet

---

## ?? **Migrations-Strategie**

### **Phase 1: Minimale Blazor-Funktionalität (JETZT)**

Ziel: Blazor-App zum Laufen bringen mit Basis-Funktionalität

1. ? DMSCatalogService nach Core migriert
2. ?? DatasheetAnalysisService vereinfacht migrieren
3. ?? PartNumberDecoderService vereinfacht migrieren
4. ? Services in Program.cs registrieren
5. ? Blazor-Seiten anpassen

### **Phase 2: Erweiterte Features (SPÄTER)**

- Validation Services migrieren
- Erweiterte UI-Dialoge implementieren
- Property-Warnings anzeigen

---

## ?? **Technische Herausforderungen**

### **Problem 1: Konstruktor-Abhängigkeiten**

**WPF-Version (alt):**
```csharp
public DatasheetAnalysisService(AzureConfig config)
{
    _config = config;
    _remoteConfigService = new RemoteConfigService("DatasheetAnalysis-" + Guid.NewGuid());
    _catalogService = new DMSCatalogService();
}
```

**Blazor-Version (neu):**
```csharp
public DatasheetAnalysisService(
    IRemoteConfigService remoteConfigService,
    DMSCatalogService catalogService,
    AzureConfig config,
    ILogger<DatasheetAnalysisService> logger)
{
    _remoteConfigService = remoteConfigService;
    _catalogService = catalogService;
    _config = config;
    _logger = logger;
}
```

### **Problem 2: Console.WriteLine vs. ILogger**

Alle `Console.WriteLine()` Aufrufe müssen durch `ILogger` ersetzt werden.

### **Problem 3: HttpClient Management**

**WPF:** Jeder Service erstellt eigenen HttpClient
**Blazor:** `IHttpClientFactory` verwenden (Best Practice)

---

## ?? **Nächste Schritte**

### **Schritt 1: DatasheetAnalysisService vereinfachen**

Erstelle vereinfachte Version ohne erweiterte Features:
- ? Basis-Identifikation
- ? Property-Extraktion mit Katalog
- ? Validation (später)
- ? Part Number Decoding (später - separater Service)

### **Schritt 2: Services in Program.cs registrieren**

```csharp
builder.Services.AddScoped<DMSCatalogService>();
builder.Services.AddScoped<DatasheetAnalysisService>();
builder.Services.AddScoped<PartNumberDecoderService>();
```

### **Schritt 3: Blazor-Seiten testen**

- Home.razor: PDF-Upload und Analyse
- Analyze.razor: Detailansicht

---

## ?? **Migrations-Fortschritt**

| Komponente | Status | Priorität |
|------------|--------|-----------|
| RemoteConfigService | ? Migriert | Hoch |
| AzureConfig | ? Migriert | Hoch |
| PDFProcessor | ? Migriert | Hoch |
| JsonParser | ? Migriert | Mittel |
| DMSCatalogService | ? Migriert | Hoch |
| DatasheetAnalysisService | ?? In Arbeit | **KRITISCH** |
| PartNumberDecoderService | ? Ausstehend | Hoch |
| IdentificationValidationService | ? Ausstehend | Niedrig |
| PropertyValidationService | ? Ausstehend | Niedrig |

---

## ?? **Breaking Changes**

### **1. AzureConfig Instanziierung**

**Alt (WPF):**
```csharp
var azureConfig = new AzureConfig();
```

**Neu (Blazor):**
```csharp
// Wird über DI injected
public MyService(AzureConfig config) { }
```

### **2. RemoteConfigService Instanziierung**

**Alt (WPF):**
```csharp
var service = new RemoteConfigService("MyClient-" + Guid.NewGuid());
```

**Neu (Blazor):**
```csharp
// Wird über DI injected
public MyService(IRemoteConfigService remoteConfigService) { }
```

---

## ?? **Was funktioniert bereits:**

1. ? Azure Storage ConnectionString Konfiguration
2. ? launchSettings.json mit Credentials
3. ? Git-Sicherheit (credentials nicht getrackt)
4. ? RemoteConfigService mit DI
5. ? AzureConfig lädt Konfiguration aus Azure
6. ? PDFProcessor verarbeitet PDFs
7. ? DMSCatalogService lädt Katalogdaten

---

## ?? **Was noch fehlt:**

1. ? DatasheetAnalysisService Migration
2. ? PartNumberDecoderService Migration
3. ? Program.cs Service-Registrierung
4. ? Blazor-Seiten Build-Fehler beheben

---

**Nächster Schritt:** DatasheetAnalysisService in vereinfachter Form nach Core migrieren.

Soll ich fortfahren?
