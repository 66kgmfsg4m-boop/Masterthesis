using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DatasheetAnalyzer.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Read-only Lookup-Service, der das neue DMS-REST-Interface
/// (https://dms-api-prod.cloud.rsint.net) nutzt und dieselbe
/// <see cref="IDmsLookupService"/>-Vertragsflaeche erfuellt wie der
/// bisherige Oracle-basierte Service. Damit ist die UI (Results.razor)
/// unveraendert einsatzfaehig.
///
/// Ablauf:
///   1) POST /request/send_query mit einem SELECT-Statement pro Materialtabelle
///      -&gt; Antwort enthaelt eine Query-/Task-ID
///   2) Polling: POST /request/receive mit der ID, bis Status "ready" o.ae.
///   3) Ergebnis-Rows werden ueber alle Tabellen zu einem flachen Dictionary
///      gemergt (analog zu <see cref="DmsLookupService"/>).
///
/// Auth: HTTP Basic gegen den registrierten API-User.
///
/// Wichtig: Der Service greift ausschliesslich lesend zu (nur SELECT).
/// </summary>
public class DmsApiLookupService : IDmsLookupService
{
    private readonly DmsApiOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DmsApiLookupService> _logger;
    private readonly DMSCatalogService? _catalog;

    public const string HttpClientName = "DmsApi";

    // Cache: skn_key -> (PropertyName -> DmsColumn). Wird prozessweit
    // wiederverwendet, damit dieselbe Kataloggruppe nicht bei jedem
    // Material-Lookup neu ueber TE_CLS_MERK abgefragt wird.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, (IReadOnlyDictionary<string, string> Map, HashSet<string> Tables)> _propertyMapCache =
        new(StringComparer.OrdinalIgnoreCase);

    public DmsApiLookupService(
        IOptions<DmsApiOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<DmsApiLookupService> logger,
        DMSCatalogService? catalog = null)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _catalog = catalog;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_options.User) &&
        !string.IsNullOrWhiteSpace(_options.Password);

    public Task<DmsLookupResult> GetMaterialInfoAsync(string materialNumber, CancellationToken cancellationToken = default)
        => GetMaterialInfoAsync(materialNumber, null, cancellationToken);

    public async Task<DmsLookupResult> GetMaterialInfoAsync(string materialNumber, string? categoryDisplayName, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new DmsLookupResult
            {
                Found = false,
                ErrorMessage = "DMS-API ist nicht konfiguriert (DmsApi:BaseUrl/User/Password fehlen)."
            };
        }

        if (string.IsNullOrWhiteSpace(materialNumber))
        {
            return new DmsLookupResult { Found = false, ErrorMessage = "Keine Materialnummer angegeben." };
        }

        string trimmed = materialNumber.Trim();
        string objectId = trimmed.StartsWith(_options.MaterialNumberPrefix, StringComparison.Ordinal)
            ? trimmed
            : _options.MaterialNumberPrefix + trimmed;

        try
        {
            using var http = CreateClient();

            // 1) Tabellenliste via Metadatenabfrage holen (SELECT gegen ALL_TABLES).
            var tables = await DiscoverTablesAsync(http, cancellationToken);

            // 1b) Property-Mapping vorziehen, damit wir die von TE_CLS_MERK
            //     referenzierten Zieltabellen (SMT_TAB, z.B. TE_SACHNR1/2/3)
            //     mit in die Datenabfrage aufnehmen koennen.
            IReadOnlyDictionary<string, string> propertyMap =
                new Dictionary<string, string>();
            HashSet<string> referencedTables = new(StringComparer.OrdinalIgnoreCase);
            if (_catalog != null && !string.IsNullOrWhiteSpace(categoryDisplayName))
            {
                try
                {
                    var mapResult = await LoadPropertyMapByOrdnumAsync(http, objectId, categoryDisplayName!, cancellationToken);
                    if (mapResult.HasValue)
                    {
                        propertyMap = mapResult.Value.Map ?? propertyMap;
                        referencedTables = mapResult.Value.ReferencedTables ?? referencedTables;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DMS-API: Property-Mapping (ORDNUM) fuer OBJ_ID '{ObjectId}' konnte nicht geladen werden.", objectId);
                }
            }

            // Referenzierte SMT_TAB-Tabellen in die Ladeliste aufnehmen, falls
            // sie nicht ohnehin schon durch die Discovery gefunden wurden.
            foreach (var t in referencedTables)
            {
                if (!tables.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                    tables.Add(t);
            }

            if (tables.Count == 0)
            {
                _logger.LogWarning("DMS-API: Keine Tabellen mit Praefix '{Prefix}' im Schema '{Schema}' gefunden.",
                    _options.TableNamePrefix, _options.Schema);
                return new DmsLookupResult
                {
                    Found = false,
                    ObjectId = objectId,
                    ErrorMessage = $"Keine Tabellen mit Praefix '{_options.TableNamePrefix}' im Schema '{_options.Schema}' gefunden."
                };
            }

            // 2) Pro Tabelle SELECT-Query PARALLEL absetzen, Ergebnisse mergen.
            //    Die DMS-API ist bewusst langsam (jeder Poll dauert Sekunden),
            //    deshalb bringt Parallelisierung hier den groessten Effekt:
            //    N Tabellen laufen gleichzeitig statt sequenziell.
            var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            bool anyFound = false;

            var tasks = tables.Select(async table =>
            {
                string sql = $"SELECT * FROM \"{_options.Schema}\".\"{table}\" WHERE OBJ_ID = '{EscapeSql(objectId)}'";
                try
                {
                    var row = await RunSelectAsync(http, sql, cancellationToken);
                    return (Table: table, Row: row);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DMS-API: Fehler beim Lesen von Tabelle '{Table}' (uebersprungen)", table);
                    return (Table: table, Row: (Dictionary<string, object?>?)null);
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);

            // Reihenfolge der Tabellen aus der Config beibehalten -> spaetere
            // Tabellen ueberschreiben Werte frueherer (analog dem alten Verhalten).
            foreach (var (_, row) in results)
            {
                if (row is null || row.Count == 0) continue;
                anyFound = true;
                foreach (var kv in row)
                {
                    if (kv.Value is null) continue;
                    string value = kv.Value.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    merged[kv.Key] = value;
                }
            }

            if (!anyFound)
            {
                _logger.LogInformation("DMS-API: kein Datensatz fuer OBJ_ID '{ObjectId}' gefunden.", objectId);
                return new DmsLookupResult { Found = false, ObjectId = objectId };
            }

            return new DmsLookupResult { Found = true, Values = merged, ObjectId = objectId, PropertyMap = propertyMap };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMS-API-Lookup fehlgeschlagen fuer Materialnummer '{Material}'", materialNumber);
            return new DmsLookupResult
            {
                Found = false,
                ObjectId = objectId,
                ErrorMessage = ex.Message
            };
        }
    }

    private HttpClient CreateClient()
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);

        string token = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.User}:{_options.Password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return http;
    }

    private async Task<List<string>> DiscoverTablesAsync(HttpClient http, CancellationToken ct)
    {
        // Falls die Tabellenliste per Konfiguration gesetzt ist, Discovery ueberspringen.
        // Notwendig, wenn der DMS-API-User keine Rechte auf ALL_TABLES hat.
        if (_options.Tables is { Count: > 0 })
        {
            return _options.Tables
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        string sql =
            $"SELECT TABLE_NAME FROM ALL_TABLES " +
            $"WHERE OWNER = '{EscapeSql(_options.Schema)}' " +
            $"AND TABLE_NAME LIKE '{EscapeSql(_options.TableNamePrefix)}%' " +
            $"ORDER BY TABLE_NAME";

        var rows = await RunSelectManyAsync(http, sql, ct);
        var tables = new List<string>();
        foreach (var row in rows)
        {
            if (row.TryGetValue("TABLE_NAME", out var name) && name is not null)
                tables.Add(name.ToString() ?? string.Empty);
        }
        return tables.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
    }

    private async Task<Dictionary<string, object?>?> RunSelectAsync(HttpClient http, string sql, CancellationToken ct)
    {
        var rows = await RunSelectManyAsync(http, sql, ct);
        return rows.FirstOrDefault();
    }

    /// <summary>
    /// Neuer Weg: PropertyMap ueber ORDNUM-Kopplung.
    ///   1. TE_OBJ.OBJ_COD    -> Kataloggruppe (SKN_COD) fuer die OBJ_ID
    ///   2. TR_SMT + TE_CLS_MERK sortiert nach ORDNUM
    ///   3. get_classes.csv (DMSCatalogService) sortiert nach ORDNUM
    ///   4. Beide Listen positional koppeln:
    ///      PropertyMap[csv.PropertyInternalName] = db.VAL_COLUMN
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string>? Map, HashSet<string> ReferencedTables)?> LoadPropertyMapByOrdnumAsync(
        HttpClient http, string objectId, string categoryDisplayName, CancellationToken ct)
    {
        var referencedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1) SKN_COD des Materials
        string objSql =
            $"SELECT OBJ_COD FROM \"{_options.Schema}\".TE_OBJ WHERE OBJ_ID = '{EscapeSql(objectId)}'";
        var objRow = await RunSelectAsync(http, objSql, ct);
        if (objRow is null || !objRow.TryGetValue("OBJ_COD", out var sknVal) || sknVal is null)
        {
            _logger.LogInformation("DMS-API Metadata (ORDNUM): kein OBJ_COD in TE_OBJ fuer OBJ_ID '{ObjectId}'.", objectId);
            return null;
        }
        string sknCod = sknVal.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sknCod)) return null;

        string cacheKey = sknCod + "|" + categoryDisplayName;
        if (_propertyMapCache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogDebug("DMS-API Metadata (ORDNUM): Cache-Hit fuer '{Key}' ({Count} Eintraege).",
                cacheKey, cached.Map.Count);
            return (cached.Map, cached.Tables);
        }

        // 2) DB-Merkmale mit ORDNUM (Key -> VAL_COLUMN) + Zieltabellen
        string smtSql =
            "SELECT sm.ORDNUM, cm.SMT_TAB, cm.VAL_COLUMN " +
            $"FROM \"{_options.Schema}\".TR_SMT sm " +
            $"JOIN \"{_options.Schema}\".TE_CLS_MERK cm ON cm.SMT_SMW = sm.SMT_SMW " +
            $"WHERE sm.SKN_COD = '{EscapeSql(sknCod)}' " +
            "ORDER BY sm.ORDNUM";
        var dmsRows = await RunSelectManyAsync(http, smtSql, ct);

        var dmsByOrdnum = new Dictionary<int, string>();
        foreach (var row in dmsRows)
        {
            if (!row.TryGetValue("ORDNUM", out var oVal) || oVal is null) continue;
            if (!row.TryGetValue("VAL_COLUMN", out var v) || v is null) continue;
            if (!int.TryParse(oVal.ToString(), out int ord)) continue;
            string col = v.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(col)) continue;
            dmsByOrdnum[ord] = col;

            if (row.TryGetValue("SMT_TAB", out var tabVal) && tabVal is not null)
            {
                string tab = tabVal.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(tab))
                    referencedTables.Add(tab);
            }
        }

        if (dmsByOrdnum.Count == 0 || _catalog is null)
        {
            _logger.LogInformation("DMS-API Metadata (ORDNUM): keine Merkmale in TR_SMT fuer SKN_COD '{Skn}'.", sknCod);
            return null;
        }

        // 3) CSV-Merkmale mit ORDNUM (Key -> PDF-PropertyName)
        var csvProps = _catalog.GetOrderedPropertiesForComponent(categoryDisplayName);
        if (csvProps.Count == 0)
        {
            _logger.LogInformation("DMS-API Metadata (ORDNUM): kein CSV-Mapping fuer Kategorie '{Cat}'.", categoryDisplayName);
            return null;
        }

        // 4) Per ORDNUM koppeln (nicht per Array-Index, um Off-by-one/Luecken zu vermeiden)
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in csvProps)
        {
            if (!int.TryParse(p.PropertyNumber, out int ord)) continue;
            if (!dmsByOrdnum.TryGetValue(ord, out var dmsCol)) continue;
            if (string.IsNullOrWhiteSpace(p.PropertyInternalName)) continue;
            map[p.PropertyInternalName] = dmsCol;
            _logger.LogDebug("DMS-API Metadata (ORDNUM): #{Ord} {Pdf} -> {Col}",
                ord, p.PropertyInternalName, dmsCol);
        }

        _logger.LogInformation(
            "DMS-API Metadata (ORDNUM): PropertyMap SKN_COD='{Skn}' Kategorie='{Cat}' -> {Count} Eintraege, Zieltabellen: {Tables}.",
            sknCod, categoryDisplayName, map.Count, string.Join(",", referencedTables));
        _propertyMapCache[cacheKey] = (map, referencedTables);
        return (map, referencedTables);
    }

    /// <summary>
    /// Ermittelt ueber TE_OBK und TE_CLS_MERK die Zuordnung
    /// (logischer Property-Name -&gt; DMS-Spaltenname) fuer ein Material.
    ///
    /// Ablauf:
    ///   1. SELECT SKN_KEY  FROM TE_OBK      WHERE OBJ_ID = &lt;objectId&gt;
    ///   2. SELECT MERKMAL, SMT_TAB, VAL_COLUMN FROM TE_CLS_MERK WHERE SKN_KEY = ...
    ///   3. Rueckgabe als Dictionary { "Pitch" -&gt; "PITCH___1", ... }
    ///
    /// Ergebnisse werden pro SKN_KEY gecached, damit gleiche Kataloggruppen
    /// nicht bei jedem Material erneut geladen werden.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> LoadPropertyMapAsync(
        HttpClient http, string objectId, CancellationToken ct)
    {
        var m = _options.Metadata;

        string objSql =
            $"SELECT \"{m.TeObkSknKeyColumn}\" AS SKN_KEY " +
            $"FROM \"{_options.Schema}\".\"{m.TeObkTable}\" " +
            $"WHERE \"{m.TeObkObjIdColumn}\" = '{EscapeSql(objectId)}'";
        var obkRow = await RunSelectAsync(http, objSql, ct);
        if (obkRow is null || !obkRow.TryGetValue("SKN_KEY", out var sknVal) || sknVal is null)
        {
            _logger.LogInformation("DMS-API Metadata: kein SKN_KEY in {Table} fuer OBJ_ID '{ObjectId}'.",
                m.TeObkTable, objectId);
            return null;
        }
        string sknKey = sknVal.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sknKey)) return null;

        if (_propertyMapCache.TryGetValue(sknKey, out var cached))
        {
            _logger.LogDebug("DMS-API Metadata: Property-Mapping fuer SKN_KEY '{Skn}' aus Cache ({Count} Eintraege).",
                sknKey, cached.Map.Count);
            return cached.Map;
        }

        string merkSql =
            $"SELECT \"{m.TeClsMerkMerkmalColumn}\" AS MERKMAL, " +
            $"\"{m.TeClsMerkSmtTabColumn}\" AS SMT_TAB, " +
            $"\"{m.TeClsMerkValColumn}\"    AS VAL_COLUMN " +
            $"FROM \"{_options.Schema}\".\"{m.TeClsMerkTable}\" " +
            $"WHERE \"{m.TeClsMerkSknKeyColumn}\" = '{EscapeSql(sknKey)}'";
        var merkRows = await RunSelectManyAsync(http, merkSql, ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in merkRows)
        {
            if (!row.TryGetValue("MERKMAL", out var mVal) || mVal is null) continue;
            if (!row.TryGetValue("VAL_COLUMN", out var cVal) || cVal is null) continue;
            string merkmal = mVal.ToString() ?? string.Empty;
            string column  = cVal.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(merkmal) || string.IsNullOrWhiteSpace(column)) continue;
            map[merkmal] = column;
        }

        _logger.LogInformation(
            "DMS-API Metadata: Property-Mapping fuer SKN_KEY '{Skn}' geladen ({Count} Eintraege).",
            sknKey, map.Count);
        _propertyMapCache[sknKey] = (map, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return map;
    }

    /// <summary>
    /// Setzt einen SELECT ueber /request/send_query ab, pollt /request/receive
    /// und liefert die Ergebniszeilen als Liste von Spalten-Dictionaries.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> RunSelectManyAsync(HttpClient http, string sql, CancellationToken ct)
    {
        // ---- 1) send_query (Hybrid-Endpoint) ----
        // Laut OpenAPI-Schema (https://dms-api-prod.cloud.rsint.net/docs) erwartet
        // /request/send_query genau { "sql_query": "...", "force_refresh": bool }.
        // Die Response ist entweder ein synchrones Ergebnis (Cache-Hit) ODER
        // ein String-Token, das dann ueber /request/receive gepollt werden muss.
        var sendPayload = new { sql_query = sql, force_refresh = _options.ForceRefresh };
        _logger.LogInformation("DMS-API SQL: {Sql}", sql);
        using var sendResp = await http.PostAsJsonAsync("request/send_query", sendPayload, ct);
        sendResp.EnsureSuccessStatusCode();

        string sendBody = await sendResp.Content.ReadAsStringAsync(ct);
        string sendPreview = sendBody.Length > 4000 ? sendBody.Substring(0, 4000) + "...<truncated>" : sendBody;
        _logger.LogInformation("DMS-API send_query RAW: {Body}", sendPreview);

        using var sendDoc = JsonDocument.Parse(sendBody);
        var sendRoot = sendDoc.RootElement;

        // Fall 1: Antwort ist ein reiner String -> Token fuer /receive.
        if (sendRoot.ValueKind == JsonValueKind.String)
        {
            string? tok = sendRoot.GetString();
            if (string.IsNullOrWhiteSpace(tok))
                throw new InvalidOperationException("DMS-API: send_query lieferte leeren String.");
            return await PollReceiveAsync(http, tok!, ct);
        }

        // Fall 2: Antwort ist ein Objekt. Priorisierung:
        //   a) enthaelt ein Token-Feld -> polling
        //   b) enthaelt bereits Rows (Sync/Cache-Hit) -> direkt zurueck
        if (sendRoot.ValueKind == JsonValueKind.Object)
        {
            // API meldet Fehler oft als 200er mit { status_code: 5xx, detail: "..." }.
            if (sendRoot.TryGetProperty("status_code", out var sc) &&
                sc.ValueKind == JsonValueKind.Number &&
                sc.TryGetInt32(out var scInt) && scInt >= 400)
            {
                string? detail = ReadString(sendRoot, "detail") ?? ReadString(sendRoot, "message") ?? ReadString(sendRoot, "error");
                throw new InvalidOperationException($"DMS-API-Fehler {scInt}: {detail}");
            }

            string? tokenFromObject = ExtractQueryId(sendRoot);
            if (!string.IsNullOrWhiteSpace(tokenFromObject))
                return await PollReceiveAsync(http, tokenFromObject!, ct);

            // Kein Token -> synchrone Payload; ExtractRows kann sowohl
            // Objekt-mit-result als auch Objekt-mit-columns/rows verarbeiten.
            var syncRows = ExtractRows(sendRoot);
            bool apiSaysEmpty = sendRoot.TryGetProperty("empty", out var emptyEl) &&
                                 emptyEl.ValueKind == JsonValueKind.True;
            _logger.LogInformation(
                "DMS-API send_query sync: empty={Empty} extractedRows={Rows}",
                apiSaysEmpty, syncRows.Count);
            return syncRows;
        }

        // Fall 3: direktes Array
        if (sendRoot.ValueKind == JsonValueKind.Array)
            return ExtractRows(sendRoot);

        throw new InvalidOperationException(
            $"DMS-API: unerwartete send_query-Antwort (ValueKind={sendRoot.ValueKind}).");
    }

    private async Task<List<Dictionary<string, object?>>> PollReceiveAsync(HttpClient http, string queryId, CancellationToken ct)
    {

        // ---- 2) receive-Polling ----
        for (int attempt = 0; attempt < _options.MaxPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var recvPayload = new { token = queryId };
            using var recvResp = await http.PostAsJsonAsync("request/receive", recvPayload, ct);
            recvResp.EnsureSuccessStatusCode();

            // Fuer Diagnose einmal den Rohtext lesen (ReadAsStringAsync bleibt
            // auch fuer JsonDocument.Parse verwendbar).
            string rawBody = await recvResp.Content.ReadAsStringAsync(ct);
            using var recvDoc = JsonDocument.Parse(rawBody);
            var root = recvDoc.RootElement;

            // Die DMS-API liefert Fehler oft als 200er mit { status_code: 5xx, detail: "..." }.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("status_code", out var sc) &&
                sc.ValueKind == JsonValueKind.Number &&
                sc.TryGetInt32(out var scInt) && scInt >= 400)
            {
                string? detail = ReadString(root, "detail") ?? ReadString(root, "message") ?? ReadString(root, "error");
                throw new InvalidOperationException($"DMS-API-Fehler {scInt}: {detail}");
            }

            // Status-Feld akzeptiert typische Auspraegungen ("ready", "done",
            // "completed", "success"). Alles andere -> weiter pollen.
            string status = ReadString(root, "status") ?? string.Empty;
            _logger.LogDebug("DMS-API poll attempt {Attempt}/{Max}: status='{Status}'",
                attempt + 1, _options.MaxPollAttempts, status);
            if (IsTerminalStatus(status))
            {
                var extracted = ExtractRows(root);
                bool apiSaysEmpty = root.ValueKind == JsonValueKind.Object &&
                                     root.TryGetProperty("empty", out var emptyEl) &&
                                     emptyEl.ValueKind == JsonValueKind.True;
                string preview = rawBody.Length > 4000 ? rawBody.Substring(0, 4000) + "...<truncated>" : rawBody;
                _logger.LogInformation(
                    "DMS-API receive terminal: status={Status} empty={Empty} extractedRows={Rows} RAW={Body}",
                    status, apiSaysEmpty, extracted.Count, preview);
                return extracted;
            }

            if (IsErrorStatus(status))
            {
                string? errMsg = ReadString(root, "error") ?? ReadString(root, "message") ?? ReadString(root, "detail");
                throw new InvalidOperationException($"DMS-API meldet Fehlerstatus '{status}': {errMsg}");
            }

            // Progressive Backoff: Die ersten Polls kommen schnell hintereinander
            // (viele Queries sind bei Cache-Hit nach 1-2 Versuchen fertig), erst
            // bei laenger laufenden Queries wird das Intervall groesser. So sinkt
            // die typische Wartezeit deutlich, ohne die API bei langsamen
            // Abfragen mit Requests zu fluten. Das Delay ist bei PollDelayMs
            // gedeckelt (Config-Wert = Obergrenze).
            int baseDelay = Math.Min(100 + attempt * 100, _options.PollDelayMilliseconds);
            await Task.Delay(baseDelay, ct);
        }

        throw new TimeoutException(
            $"DMS-API: Polling nach {_options.MaxPollAttempts} Versuchen ohne Ergebnis abgebrochen.");
    }

    private static string? ExtractQueryId(JsonElement root)
    {
        foreach (var name in new[] { "token", "id", "query_id", "task_id", "request_id" })
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return v.ToString();
        }
        // Manche DMS-Antworten packen das Token in "result" als String,
        // wenn kein synchrones Ergebnis vorliegt. Nur akzeptieren, wenn der
        // String NICHT nach eingebettetem JSON aussieht.
        if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
        {
            string? s = r.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s) && s[0] != '[' && s[0] != '{')
                return s;
        }
        return null;
    }

    // Die DMS-API meldet den finalen Zustand als 3-Buchstaben-Kuerzel
    // (siehe API-Doku: "Repeat until status is COM (completed),
    // DEN (denied), or an error is returned."). Wir akzeptieren zusaetzlich
    // die ausgeschriebenen Varianten, falls die API sie an anderer Stelle
    // (z.B. in send_query-Antworten) benutzt.
    private static bool IsTerminalStatus(string status) =>
        status.Equals("COM", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("ready", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("done", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("finished", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorStatus(string status) =>
        status.Equals("DEN", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("ERR", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("denied", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Extrahiert Result-Rows aus der /receive-Antwort. Akzeptiert die
    /// typischen Layout-Varianten:
    ///   { "result": [ { "COL": "VAL", ... }, ... ] }
    ///   { "data":   [ { "COL": "VAL", ... }, ... ] }
    ///   { "rows":   [ { "COL": "VAL", ... }, ... ] }
    /// oder ein direktes Array. Zusaetzlich wird das "columns"+"rows"-Format
    /// (Array-of-Arrays) unterstuetzt.
    /// </summary>
    private static List<Dictionary<string, object?>> ExtractRows(JsonElement root)
    {
        var rows = new List<Dictionary<string, object?>>();

        JsonElement payload = root;
        JsonDocument? nestedDoc = null;
        foreach (var name in new[] { "result", "data", "rows" })
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out var nested))
            {
                // Die DMS-API liefert "result" haeufig als STRING, der JSON enthaelt
                // (doppelt kodiert). Beispiel:  "result": "[{\"OBJ_ID\":\"...\"}]"
                // Solche Strings hier einmal parsen, damit wir wieder ein
                // JsonArray/JsonObject in der Hand haben.
                if (nested.ValueKind == JsonValueKind.String)
                {
                    string? inner = nested.GetString();
                    if (!string.IsNullOrWhiteSpace(inner))
                    {
                        try
                        {
                            nestedDoc = JsonDocument.Parse(inner);
                            payload = nestedDoc.RootElement;
                        }
                        catch (JsonException)
                        {
                            // Kein JSON -> stehen lassen, wird spaeter verworfen.
                            payload = nested;
                        }
                    }
                }
                else
                {
                    payload = nested;
                }
                break;
            }
        }

        try
        {
            // Variante A: Array von Objekten (Spalte -> Wert)
            if (payload.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in payload.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in item.EnumerateObject())
                            row[p.Name] = ReadJsonScalar(p.Value);
                        if (row.Count > 0) rows.Add(row);
                    }
                }
                if (rows.Count > 0) return rows;
            }

            // Variante A2: Einzelnes Objekt statt Array
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.EnumerateObject().Any())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in payload.EnumerateObject())
                    row[p.Name] = ReadJsonScalar(p.Value);
                if (row.Count > 0) rows.Add(row);
                if (rows.Count > 0) return rows;
            }

            // Variante B: { columns: [...], rows: [[...], [...]] }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("columns", out var columnsEl) &&
                columnsEl.ValueKind == JsonValueKind.Array &&
                root.TryGetProperty("rows", out var rowsEl) &&
                rowsEl.ValueKind == JsonValueKind.Array)
            {
                var columns = columnsEl.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToList();
                foreach (var rowEl in rowsEl.EnumerateArray())
                {
                    if (rowEl.ValueKind != JsonValueKind.Array) continue;
                    var values = rowEl.EnumerateArray().ToList();
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < Math.Min(columns.Count, values.Count); i++)
                    {
                        if (string.IsNullOrWhiteSpace(columns[i])) continue;
                        row[columns[i]] = ReadJsonScalar(values[i]);
                    }
                    if (row.Count > 0) rows.Add(row);
                }
            }

            return rows;
        }
        finally
        {
            nestedDoc?.Dispose();
        }
    }

    private static object? ReadJsonScalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => el.ToString()
    };

    /// <summary>Nur fuer eingebettete Metadaten/Materialnummern-Werte. Verhindert simple SQL-Injection.</summary>
    private static string EscapeSql(string input) => (input ?? string.Empty).Replace("'", "''");
}
