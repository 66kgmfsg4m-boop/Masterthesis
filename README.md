# Datasheet Analyzer

Blazor Server unter .NET 8. Die Anwendung liest ein technisches Datenblatt, schlägt die Katalogwerte vor und zeigt sie zum Prüfen an. Zurückgeschrieben wird nichts.

## Ordner

- `DatasheetAnalyzer.Blazor` ist die Oberfläche.
- `DatasheetAnalyzer.Core` liest das PDF, den Katalog und die DMS-Daten.
- `CheckOrderConfirmationFromSupplier` ist die ältere Desktop-Oberfläche. Die Dienste daraus liegen inzwischen in Core.
- `Azure_Prompts` enthält die Prompt-Texte. Zur Laufzeit kommen sie aus dem Blob, diese Dateien sind die Vorlagen.
- `tests` prüft Seitenauswahl, Feldvergleich und die Suche.
- `docs` enthält Deployment und die kurzen Arbeitsanleitungen.

## Start

Zugangsdaten bleiben außerhalb des Repository. Vorlagen sind `.env.example` und `nuget.config.template`.

```bash
dotnet run --project DatasheetAnalyzer.Blazor
```

```bash
dotnet test tests/DatasheetAnalyzer.Tests.csproj
```

Container: `docker compose up`, danach http://localhost:8080. Der Ablauf steht in `docs/DEPLOYMENT_README.md` und `docs/HANDOVER.md`.
