using DatasheetAnalyzer.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Read-only Lookup-Service fuer das DMS (Oracle).
/// Holt zu einer gegebenen Materialnummer alle Properties aus den
/// CMSLIB.TE_SACHNR*-Tabellen und mergt sie zu einem flachen Dictionary.
///
/// Datenmodell-Hintergrund (siehe Referenz-Skript dms.py):
/// - Properties sind ueber mehrere Tabellen (TE_SACHNR, TE_SACHNR1, ...)
///   verteilt; jede Tabelle hat OBJ_ID als Primaerschluessel.
/// - OBJ_ID = Praefix (Default "001000") + User-Eingabe (Materialnummer).
/// - Spaltennamen entsprechen 1:1 den Property-Namen im PDF-Analyse-Ergebnis
///   (siehe Azure-Blob "get_classes.csv").
///
/// WICHTIG: Dieser Service ist ausdruecklich read-only. Es werden ausschliesslich
/// SELECT-Statements gegen den gebundenen User (LLM-VIEW-1GD) abgesetzt.
/// </summary>
public interface IDmsLookupService
{
    /// <summary>True, wenn Host + Service + User + Password gesetzt sind.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Holt alle Spalten aus allen TE_SACHNR*-Tabellen fuer die uebergebene
    /// Materialnummer (ohne Praefix). NULL-Werte werden uebersprungen,
    /// damit das Dictionary nur tatsaechlich gepflegte Felder enthaelt.
    /// </summary>
    Task<DmsLookupResult> GetMaterialInfoAsync(string materialNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Wie <see cref="GetMaterialInfoAsync(string, CancellationToken)"/>, koppelt
    /// zusaetzlich per ORDNUM die CSV-Properties der uebergebenen Kategorie
    /// (z.B. "Coil") an die DMS-Zielspalten und liefert die Zuordnung als
    /// <see cref="DmsLookupResult.PropertyMap"/> (PDF-Property -&gt; DMS-Spalte).
    /// Wenn die Kategorie leer ist, verhaelt sich die Methode wie ohne Mapping.
    /// </summary>
    Task<DmsLookupResult> GetMaterialInfoAsync(string materialNumber, string? categoryDisplayName, CancellationToken cancellationToken = default);
}

public class DmsLookupResult
{
    /// <summary>True wenn mindestens eine Tabelle einen Datensatz lieferte.</summary>
    public bool Found { get; init; }

    /// <summary>Spaltenname -> Wert (gemergt ueber alle TE_SACHNR*-Tabellen).</summary>
    public IReadOnlyDictionary<string, string?> Values { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// Alle Spaltennamen aller TE_SACHNR*-Tabellen (auch NULL-Spalten des Datensatzes).
    /// Wird fuer den Property-Match herangezogen, damit auch leere DMS-Felder als
    /// mappbare Zielspalte erkannt werden.
    /// </summary>
    public IReadOnlyCollection<string> AllColumns { get; init; } = Array.Empty<string>();

    /// <summary>Optionale Fehlermeldung, falls die Abfrage scheiterte.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>OBJ_ID, mit der tatsaechlich gesucht wurde (Praefix + Materialnummer).</summary>
    public string? ObjectId { get; init; }

    /// <summary>
    /// Optionale Zuordnung von logischem Property-Namen (wie im PDF-JSON,
    /// z.B. "Pitch", "PackageType") zum konkreten DMS-Spaltennamen
    /// (z.B. "PITCH___1", "GEH_TYP1"). Wird aus TE_CLS_MERK gelesen, falls
    /// DmsApi:Metadata:Enabled=true ist. Leer, wenn kein Mapping ermittelt
    /// werden konnte - dann faellt die UI auf Namensheuristik zurueck.
    /// </summary>
    public IReadOnlyDictionary<string, string> PropertyMap { get; init; } =
        new Dictionary<string, string>();
}

public class DmsLookupService : IDmsLookupService
{
    private readonly DmsOracleOptions _options;
    private readonly ILogger<DmsLookupService> _logger;
    private readonly DMSCatalogService? _catalog;

    public DmsLookupService(IOptions<DmsOracleOptions> options, ILogger<DmsLookupService> logger, DMSCatalogService? catalog = null)
    {
        _options = options.Value;
        _logger = logger;
        _catalog = catalog;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Host) &&
        !string.IsNullOrWhiteSpace(_options.ServiceName) &&
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
                ErrorMessage = "DMS-Anbindung ist nicht konfiguriert " +
                               "(DmsOracle:Host/ServiceName/User/Password fehlen)."
            };
        }

        if (string.IsNullOrWhiteSpace(materialNumber))
        {
            return new DmsLookupResult { Found = false, ErrorMessage = "Keine Materialnummer angegeben." };
        }

        // Praefix nur dann anhaengen, wenn die User-Eingabe ihn nicht schon enthaelt.
        string trimmed = materialNumber.Trim();
        string objectId = trimmed.StartsWith(_options.MaterialNumberPrefix, StringComparison.Ordinal)
            ? trimmed
            : _options.MaterialNumberPrefix + trimmed;

        try
        {
            string connectionString = BuildConnectionString();
            using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // 1) Alle TE_SACHNR*-Tabellen im konfigurierten Schema ermitteln.
            var tables = await DiscoverTablesAsync(connection, cancellationToken);
            if (tables.Count == 0)
            {
                _logger.LogWarning("Keine Tabellen mit Praefix '{Prefix}' im Schema '{Schema}' gefunden.",
                    _options.TableNamePrefix, _options.Schema);
                return new DmsLookupResult
                {
                    Found = false,
                    ObjectId = objectId,
                    ErrorMessage = $"Keine Tabellen mit Praefix '{_options.TableNamePrefix}' im Schema '{_options.Schema}' gefunden."
                };
            }

            _logger.LogDebug("DMS: {Count} Material-Tabellen gefunden ({Tables})",
                tables.Count, string.Join(",", tables));

            // 2) Pro Tabelle SELECT * WHERE OBJ_ID = :id, Ergebnis-Spalten mergen.
            var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var allColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyFound = false;

            foreach (var table in tables)
            {
                string sql = $"SELECT * FROM \"{_options.Schema}\".\"{table}\" WHERE OBJ_ID = :objId";
                using var cmd = new OracleCommand(sql, connection);
                cmd.CommandTimeout = _options.ConnectionTimeoutSeconds;
                cmd.Parameters.Add(new OracleParameter("objId", objectId));

                try
                {
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

                    // Spaltennamen der Tabelle sammeln (auch wenn keine Zeile fuer diese OBJ_ID existiert).
                    for (int i = 0; i < reader.FieldCount; i++)
                        allColumns.Add(reader.GetName(i));

                    if (await reader.ReadAsync(cancellationToken))
                    {
                        anyFound = true;
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (reader.IsDBNull(i)) continue;

                            string colName = reader.GetName(i);
                            // OBJ_ID nicht ueberschreiben, sondern als eigenes Feld behalten.
                            string? value = Convert.ToString(reader.GetValue(i));
                            if (string.IsNullOrWhiteSpace(value)) continue;

                            // Spaeter gefundene Werte ueberschreiben fruehere (analog Python).
                            merged[colName] = value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DMS: Fehler beim Lesen von Tabelle '{Table}' (uebersprungen)", table);
                }
            }

            if (!anyFound)
            {
                _logger.LogInformation("DMS: kein Datensatz fuer OBJ_ID '{ObjectId}' gefunden.", objectId);
                return new DmsLookupResult { Found = false, ObjectId = objectId, AllColumns = allColumns };
            }

            // 3) Dynamisches Property-Mapping via ORDNUM koppeln:
            //    - Kataloggruppe des Materials: TE_OBJ.OBJ_COD (SKN_COD)
            //    - DMS-Merkmale sortiert nach ORDNUM aus TR_SMT + TE_CLS_MERK
            //    - CSV-Merkmale sortiert nach ORDNUM aus get_classes.csv (DMSCatalogService)
            //    Beide Listen werden per ORDNUM gekoppelt -> PropertyMap[PDF-Name] = DMS-Spalte.
            var propertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (_catalog != null && !string.IsNullOrWhiteSpace(categoryDisplayName))
                {
                    var dmsProps = await DiscoverDmsPropertiesAsync(connection, objectId, cancellationToken);
                    if (dmsProps.Count > 0)
                    {
                        var csvProps = _catalog.GetOrderedPropertiesForComponent(categoryDisplayName!);
                        int matchCount = Math.Min(dmsProps.Count, csvProps.Count);
                        for (int i = 0; i < matchCount; i++)
                        {
                            var pdfName = csvProps[i].PropertyInternalName;
                            var dmsCol  = dmsProps[i].ValColumn;
                            if (string.IsNullOrWhiteSpace(pdfName) || string.IsNullOrWhiteSpace(dmsCol))
                                continue;
                            propertyMap[pdfName] = dmsCol;
                        }
                        _logger.LogDebug("DMS: PropertyMap via ORDNUM: {N} Eintraege (Kategorie '{Cat}')",
                            propertyMap.Count, categoryDisplayName);
                    }
                    else
                    {
                        _logger.LogDebug("DMS: keine Merkmale in TR_SMT/TE_CLS_MERK fuer OBJ_ID '{ObjectId}' gefunden.", objectId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DMS: PropertyMap-Discovery uebersprungen ({Msg})", ex.Message);
            }

            return new DmsLookupResult
            {
                Found = true,
                Values = merged,
                ObjectId = objectId,
                AllColumns = allColumns,
                PropertyMap = propertyMap
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim DMS-Lookup fuer Materialnummer '{Material}'", materialNumber);
            return new DmsLookupResult
            {
                Found = false,
                ObjectId = objectId,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Liest aus ALL_TABLES alle Tabellen mit dem konfigurierten Praefix
    /// (typisch: TE_SACHNR%) im konfigurierten Schema.
    /// </summary>
    private async Task<List<string>> DiscoverTablesAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT TABLE_NAME FROM ALL_TABLES " +
            "WHERE OWNER = :owner AND TABLE_NAME LIKE :pattern " +
            "ORDER BY TABLE_NAME";

        using var cmd = new OracleCommand(sql, connection);
        cmd.CommandTimeout = _options.ConnectionTimeoutSeconds;
        cmd.Parameters.Add(new OracleParameter("owner", _options.Schema));
        cmd.Parameters.Add(new OracleParameter("pattern", _options.TableNamePrefix + "%"));

        var tables = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private sealed record DmsProperty(int Ordnum, string SmtSmw, string SmtTab, string ValColumn);

    /// <summary>
    /// Ermittelt fuer die uebergebene OBJ_ID die Merkmalsliste in der Reihenfolge
    /// aus TR_SMT (ORDNUM). Dazu wird zunaechst die Kataloggruppe (SKN_COD)
    /// via TE_OBJ.OBJ_COD ermittelt und anschliessend TR_SMT + TE_CLS_MERK
    /// gejoined. Rueckgabe ist sortiert nach ORDNUM.
    /// </summary>
    private async Task<List<DmsProperty>> DiscoverDmsPropertiesAsync(OracleConnection connection, string objectId, CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT sm.ORDNUM, cm.SMT_SMW, cm.SMT_TAB, cm.VAL_COLUMN " +
            "FROM   CMSLIB.TE_OBJ      o " +
            "JOIN   CMSLIB.TR_SMT      sm ON sm.SKN_COD = o.OBJ_COD " +
            "JOIN   CMSLIB.TE_CLS_MERK cm ON cm.SMT_SMW = sm.SMT_SMW " +
            "WHERE  o.OBJ_ID = :objId " +
            "ORDER  BY sm.ORDNUM";

        using var cmd = new OracleCommand(sql, connection);
        cmd.CommandTimeout = _options.ConnectionTimeoutSeconds;
        cmd.Parameters.Add(new OracleParameter("objId", objectId));

        var list = new List<DmsProperty>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int ordnum   = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
            string smw   = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string tab   = reader.IsDBNull(2) ? "" : reader.GetString(2);
            string vcol  = reader.IsDBNull(3) ? "" : reader.GetString(3);
            list.Add(new DmsProperty(ordnum, smw, tab, vcol));
        }
        return list;
    }

    private string BuildConnectionString()
    {
        // Oracle "Easy Connect"-Format mit optionalem Failover via DESCRIPTION/ADDRESS_LIST.
        string dataSource;
        if (!string.IsNullOrWhiteSpace(_options.FailoverHost))
        {
            dataSource =
                "(DESCRIPTION=" +
                "(FAILOVER=ON)" +
                "(ADDRESS_LIST=" +
                $"(ADDRESS=(PROTOCOL=TCP)(HOST={_options.Host})(PORT={_options.Port}))" +
                $"(ADDRESS=(PROTOCOL=TCP)(HOST={_options.FailoverHost})(PORT={_options.Port}))" +
                ")" +
                $"(CONNECT_DATA=(SERVICE_NAME={_options.ServiceName}))" +
                ")";
        }
        else
        {
            dataSource = $"{_options.Host}:{_options.Port}/{_options.ServiceName}";
        }

        return $"User Id={_options.User};Password={_options.Password};Data Source={dataSource};Connection Timeout={_options.ConnectionTimeoutSeconds};";
    }
}
