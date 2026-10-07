# DatasheetAnalyzer.Blazor – Verteilung & Start

Zwei Wege, das Tool **ohne SDK / NuGet / GitLab** beim Empfänger lauffähig zu machen.

---

## Variante A: Self-Contained EXE (Windows, empfohlen)

### Auf dem Build-Rechner
```powershell
.\publish-selfcontained.ps1
```
Ergebnis: `publish\win-x64.zip` – enthält EXE, .NET 8 Runtime, `Azure_Prompts`, `appsettings.yaml`, `start.cmd`, `README.txt`.

### Beim Empfänger
1. ZIP entpacken (z. B. `C:\DatasheetAnalyzer\`)
2. Doppelklick auf **`start.cmd`**
3. Browser öffnet sich automatisch unter `http://localhost:5080`

Keine Installation nötig. Kein .NET, kein NuGet, kein GitLab.

### Andere Plattformen
```powershell
.\publish-selfcontained.ps1 -Runtime linux-x64
.\publish-selfcontained.ps1 -Runtime osx-arm64
```

---

## Variante B: Docker (plattformunabhängig)

Voraussetzung beim Empfänger: nur **Docker Desktop** oder Docker Engine.

### Variante B1 – Image mitgeben (kein Quellcode-Zugriff nötig)

Auf dem Build-Rechner:
```powershell
docker build -t datasheet-analyzer:latest .
docker save datasheet-analyzer:latest -o datasheet-analyzer.tar
```
`datasheet-analyzer.tar` weitergeben. Empfänger:
```powershell
docker load -i datasheet-analyzer.tar
docker run --rm -p 8080:8080 datasheet-analyzer:latest
```
Browser: `http://localhost:8080`

### Variante B2 – mit Quellcode + Compose

```powershell
docker compose up
```

---

## Konfiguration / Secrets

`appsettings.yaml` enthält aktuell die Azure-Zugangsdaten. Für den Versand entweder:

- **drin lassen** (intern, vertrauenswürdiger Empfänger), oder
- **entfernen** und vom Empfänger per Umgebungsvariable setzen lassen:
  ```cmd
  set AzureStorage__ConnectionString=DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...
  set AzureStorage__ContainerName=source
  ```

> ?? Siehe `SECURITY_ALERT_AZURE_CREDENTIALS.md` – der aktuell in `appsettings.yaml` eingecheckte Account-Key sollte rotiert werden, bevor das Paket nach extern geht.
