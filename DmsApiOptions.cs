namespace DatasheetAnalyzer.Core.Config;

/// <summary>
/// Konfiguration fuer die READ-ONLY-Anbindung an das neue DMS-REST-Interface
/// (https://dms-api-prod.cloud.rsint.net). Loest die bisherige direkte
/// Oracle-Anbindung ab. Der API-Service verhaelt sich absichtlich langsam
/// (asynchrones send_query -> receive-Polling), daher die Polling-Optionen.
///
/// Override-Reihenfolge (hoch -> niedrig):
///   1. Umgebungsvariablen (DmsApi__User etc.)
///   2. appsettings.json
///   3. Eingebettete Defaults in Program.cs
/// </summary>
public class DmsApiOptions
{
    public const string SectionName = "DmsApi";

    /// <summary>Base-URL des DMS-Interfaces (z.B. https://dms-api-prod.cloud.rsint.net).</summary>
    public string BaseUrl { get; set; } = "https://dms-api-prod.cloud.rsint.net";

    /// <summary>Registrierter API-User.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Passwort des API-Users.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Timeout fuer einen einzelnen HTTP-Call in Sekunden.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Wartezeit zwischen Poll-Versuchen (in ms).</summary>
    public int PollDelayMilliseconds { get; set; } = 1500;

    /// <summary>Maximale Anzahl Poll-Versuche fuer eine Query.</summary>
    public int MaxPollAttempts { get; set; } = 40;

    /// <summary>
    /// Schema fuer die Material-Tabellen (Default: CMSLIB). Wird in das
    /// SQL-Statement eingesetzt (nur SELECT, ausschliesslich lesend).
    /// </summary>
    public string Schema { get; set; } = "CMSLIB";

    /// <summary>
    /// Praefix zur Auswahl der Material-Tabellen (LIKE-Filter,
    /// Default: TE_SACHNR -&gt; "TE_SACHNR%").
    /// </summary>
    public string TableNamePrefix { get; set; } = "TE_SACHNR";

    /// <summary>
    /// Praefix fuer die OBJ_ID (Materialnummer). Beispiel: "1234567"
    /// -&gt; "0010001234567".
    /// </summary>
    public string MaterialNumberPrefix { get; set; } = "001000";

    /// <summary>
    /// Wenn true, wird "force_refresh" beim Aufruf von /request/send_query
    /// gesetzt und der Cache der DMS-API umgangen. Default: false.
    /// </summary>
    public bool ForceRefresh { get; set; } = false;

    /// <summary>
    /// Optional: explizite Liste der Material-Tabellen. Wenn gesetzt, wird
    /// der Discovery-Schritt (SELECT auf ALL_TABLES) uebersprungen. Notwendig,
    /// wenn der DMS-API-User keine Rechte auf ALL_TABLES hat.
    /// </summary>
    public List<string> Tables { get; set; } = new();

    /// <summary>
    /// Metadaten-Konfiguration fuer das dynamische Property-Mapping
    /// (Material -&gt; Kataloggruppe -&gt; Merkmal -&gt; Zieltabelle+Spalte).
    /// Wenn <see cref="MetadataOptions.Enabled"/>=false ist, wird auf die
    /// alte Namensheuristik in der UI zurueckgefallen.
    /// </summary>
    public MetadataOptions Metadata { get; set; } = new();

    public class MetadataOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Zuordnung Material -&gt; Kataloggruppe.</summary>
        public string TeObkTable        { get; set; } = "TE_OBK";
        public string TeObkObjIdColumn  { get; set; } = "OBJ_ID";
        public string TeObkSknKeyColumn { get; set; } = "SKN_KEY";

        /// <summary>Merkmal -&gt; Zieltabelle/Zielspalte.</summary>
        public string TeClsMerkTable         { get; set; } = "TE_CLS_MERK";
        public string TeClsMerkSknKeyColumn  { get; set; } = "SKN_KEY";
        public string TeClsMerkMerkmalColumn { get; set; } = "MERKMAL";
        public string TeClsMerkSmtTabColumn  { get; set; } = "SMT_TAB";
        public string TeClsMerkValColumn     { get; set; } = "VAL_COLUMN";
    }
}
