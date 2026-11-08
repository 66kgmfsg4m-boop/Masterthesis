# Deployment-Handover – DatasheetAnalyzer.Blazor

Kurzübersicht für die IT / das Deployment-Team. Die App ist eine
**Blazor-Server**-Anwendung (.NET 8), die als Container-Image gebaut wird.

---

## 1. Laufzeit-/Container-Fakten

| Thema | Wert |
|-------|------|
| Framework | .NET 8, Blazor Server (SignalR) |
| HTTP-Port | **8080** (`ASPNETCORE_URLS=http://+:8080`) |
| TLS | **NICHT** im Container – wird vom Ingress/Reverse-Proxy terminiert |
| Health-Endpoint | **`GET /health`** (liefert 200 wenn gesund) |
| User im Container | non-root (`app`, uid 10001) |
| Temp-Verzeichnis | `/app/tmp` (PDF-Zwischendateien) |

### Wichtige Deployment-Hinweise
- **Sticky Sessions / Session-Affinity ERFORDERLICH.**
  Blazor Server hält den User-Circuit (SignalR) in *einem* Pod. Ohne
  Affinity brechen laufende Sessions, sobald es >1 Replica gibt.
  Bei nginx-Ingress z.B.:
  ```
  nginx.ingress.kubernetes.io/affinity: "cookie"
  nginx.ingress.kubernetes.io/session-cookie-name: "ANALYZER_AFFINITY"
  nginx.ingress.kubernetes.io/session-cookie-max-age: "3600"
  ```
- **WebSockets müssen am Ingress erlaubt sein** (SignalR nutzt WebSocket-Transport).
- **Read-only-Root-Filesystem:** Falls per Pod Security erzwungen, muss
  `/app/tmp` als beschreibbares `emptyDir`-Volume gemountet werden.
- **Replicas:** Ohne externen State-Store (Redis) bitte mit Sticky Sessions
  fahren. In-Memory-State (`AnalyzeStateService`) ist prozesslokal.

---

## 2. Benötigte Umgebungsvariablen / Secrets

Die App liest Konfiguration primär aus Umgebungsvariablen (Doppel-Unterstrich
`__` = Verschachtelung). **Keine** dieser Werte gehört ins Image – bitte als
k8s-Secret bzw. Deployment-Env setzen.

### 2.1 Sensitiv ? als **Secret**
| Env-Variable | Zweck |
|--------------|-------|
| `AzureStorage__ConnectionString` | Zugriff auf Azure Blob (Katalog, Prompts, Guidelines, `dms_property_map.json`) |
| `DmsApi__User` | DMS-REST-API Basic-Auth User |
| `DmsApi__Password` | DMS-REST-API Basic-Auth Passwort |
| `AzureOpenAI__ApiKey` *(falls verwendet)* | API-Key für Azure OpenAI |
| `DmsOracle__Password` *(nur falls Oracle-Backend aktiv)* | DB-Passwort |

### 2.2 Nicht sensitiv ? als **ConfigMap / Env**
| Env-Variable | Beispielwert |
|--------------|--------------|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `AzureStorage__ContainerName` | `source` |
| `DmsApi__BaseUrl` | `https://dms-api-prod.cloud.rsint.net` |
| `DmsIntegration__Backend` | `Api` (Default) oder `Oracle` |
| `DmsApi__Tables__0` | `TE_SACHNR` |
| `DmsApi__Tables__1` | `TE_SACHNR2` |
| `DmsApi__Tables__2` | `TE_SACHNR3` |
| `DmsApi__Tables__3` | `TR_IHS_SUB` |
| `DmsOracle__Host` *(nur Oracle)* | `amm31.rsint.net` |
| `DmsOracle__ServiceName` *(nur Oracle)* | `ORAMB_RW.rsint.net` |
| `DmsOracle__User` *(nur Oracle)* | `LLM-VIEW-1GD` |

> Hinweis: `DmsApi__Tables` ist eine Liste. Als Env-Variablen wird sie über
> indizierte Keys (`DmsApi__Tables__0`, `__1`, …) abgebildet. Alternativ kann
> die Liste in einer gemounteten `appsettings.json`/`appsettings.yaml` stehen.

---

## 3. Deployment-Checkliste

- [ ] Image aus Pipeline erfolgreich in Registry gepusht
      (`$CI_REGISTRY_IMAGE:$CI_COMMIT_SHORT_SHA`).
- [ ] k8s-Secret `datasheet-analyzer-secrets` mit den Werten aus **2.1** angelegt.
- [ ] ConfigMap mit den Werten aus **2.2** angelegt.
- [ ] Deployment referenziert das Image + Secret + ConfigMap.
- [ ] `containerPort: 8080`, Liveness/Readiness-Probe auf `GET /health`.
- [ ] Service (ClusterIP) auf Port 8080.
- [ ] Ingress mit **Session-Affinity (Cookie)** + WebSocket erlaubt + TLS.
- [ ] `/app/tmp` als `emptyDir` gemountet (falls read-only-rootfs).
- [ ] Nach Rollout: `https://<host>/health` liefert 200.

### Beispiel-Probe (Deployment-Snippet)
```yaml
readinessProbe:
  httpGet: { path: /health, port: 8080 }
  initialDelaySeconds: 20
  periodSeconds: 15
livenessProbe:
  httpGet: { path: /health, port: 8080 }
  initialDelaySeconds: 30
  periodSeconds: 30
```

---

## 4. Sicherheitshinweis (Build)

Die `nuget.config` mit dem SHAPE-Feed-Token ist **nicht** mehr im Repo
(steht in `.gitignore`). Der CI-Job muss die Datei zur Build-Zeit aus
maskierten CI/CD-Variablen (`SHAPE_USER`, `SHAPE_TOKEN`) erzeugen – Vorlage:
`nuget.config.template`. Der alte Token sollte rotiert/widerrufen werden.

Ausführliche k8s-Manifest-Beispiele: siehe
`docs/deployment/CloudDeployment_Docker_Kubernetes.md`.
