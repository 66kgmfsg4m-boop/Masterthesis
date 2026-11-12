namespace DatasheetAnalyzer.Core.Config;

/// <summary>
/// Konfiguration fuer die READ-ONLY-Anbindung an die DMS-Oracle-Datenbank.
/// </summary>
public class DmsOracleOptions
{
    public const string SectionName = "DmsOracle";

    /// <summary>Primaerer Oracle-Host (z.B. amm31.rsint.net).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Optionaler Failover-Host (z.B. amm41.rsint.net).</summary>
    public string? FailoverHost { get; set; }

    public int Port { get; set; } = 1521;

    /// <summary>Oracle Service Name (z.B. ORAMB_RW.rsint.net).</summary>
    public string ServiceName { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Schema fuer die Material-Tabellen (Default: CMSLIB).</summary>
    public string Schema { get; set; } = "CMSLIB";

    /// <summary>
    /// Praefix fuer das LIKE-Filter zur Auswahl der Material-Tabellen
    /// (Default: TE_SACHNR -> "TE_SACHNR%").
    /// </summary>
    public string TableNamePrefix { get; set; } = "TE_SACHNR";

    /// <summary>
    /// Praefix, das vor jede Materialnummer gestellt wird, um die OBJ_ID zu bilden (Default: "001000").
    /// </summary>
    public string MaterialNumberPrefix { get; set; } = "001000";

    /// <summary>Verbindungs-Timeout in Sekunden.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 15;
}
