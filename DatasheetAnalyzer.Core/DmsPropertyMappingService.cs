using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Liefert benutzerpflegbare Alias-Mappings PDF-Property -> DMS-Spaltenname(n).
///
/// Datenquelle: Azure Blob "dms_property_map.json" (siehe RemoteConfigService).
/// Struktur:
/// {
///   "*": {                           // Default fuer alle Komponentenkategorien
///     "FMinHz": ["FMIN", "F_MIN"],
///     "FMaxHz": ["FMAX", "F_MAX"]
///   },
///   "RF Amplifier": {                // Kategoriespezifische Ueberschreibung/Ergaenzung
///     "GainTypDb": ["GAIN", "GTYP"],
///     "VsSpecifV": ["VS", "VCC", "VDD", "USUP"]
///   }
/// }
///
/// Die Map wird beim ersten Zugriff geladen und gecached. Faellt das Laden
/// aus (Blob nicht vorhanden, Azure nicht erreichbar, JSON-Fehler), liefert
/// der Service eine leere Map - der Aufrufer faellt dann auf seinen eigenen
/// eingebauten Synonym-Katalog zurueck.
/// </summary>
public interface IDmsPropertyMappingService
{
    /// <summary>
    /// Gibt die Alias-Liste fuer eine PDF-Property zurueck, ggf. mit
    /// kategoriespezifischer Ueberschreibung. Reihenfolge:
    ///   1. Kategorie-spezifische Aliase (falls Kategorie angegeben)
    ///   2. Default-Aliase aus "*"
    /// Duplikate werden entfernt.
    /// </summary>
    IReadOnlyList<string> GetAliases(string? category, string propertyName);

    /// <summary>
    /// Erzwingt ein Nachladen aus Azure Blob beim naechsten Aufruf.
    /// </summary>
    void Invalidate();
}

public sealed class DmsPropertyMappingService : IDmsPropertyMappingService
{
    private const string BlobName = "dms_property_map.json";

    private readonly IRemoteConfigService _remote;
    private readonly ILogger<DmsPropertyMappingService> _logger;
    private readonly object _lock = new();

    // categoryKey (upper-invariant) -> propertyName (case-insensitive) -> aliases
    private Dictionary<string, Dictionary<string, string[]>>? _cache;

    public DmsPropertyMappingService(
        IRemoteConfigService remote,
        ILogger<DmsPropertyMappingService> logger)
    {
        _remote = remote;
        _logger = logger;
    }

    public void Invalidate()
    {
        lock (_lock) { _cache = null; }
    }

    public IReadOnlyList<string> GetAliases(string? category, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return Array.Empty<string>();

        var map = EnsureLoaded();
        if (map.Count == 0) return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Kategorie-spezifisch
        if (!string.IsNullOrWhiteSpace(category))
        {
            var catKey = category.Trim().ToUpperInvariant();
            if (map.TryGetValue(catKey, out var byProp)
                && byProp.TryGetValue(propertyName, out var arr))
            {
                foreach (var a in arr)
                    if (!string.IsNullOrWhiteSpace(a) && seen.Add(a))
                        result.Add(a);
            }
        }

        // 2) Default "*"
        if (map.TryGetValue("*", out var defaultProps)
            && defaultProps.TryGetValue(propertyName, out var defaultArr))
        {
            foreach (var a in defaultArr)
                if (!string.IsNullOrWhiteSpace(a) && seen.Add(a))
                    result.Add(a);
        }

        return result;
    }

    private Dictionary<string, Dictionary<string, string[]>> EnsureLoaded()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;

            _cache = Load();
            return _cache;
        }
    }

    private Dictionary<string, Dictionary<string, string[]>> Load()
    {
        var empty = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);

        string? json = null;
        try
        {
            json = _remote.LoadBlobContent(BlobName);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "DMS-Property-Mapping: Blob '{Blob}' nicht ladbar ({Reason}). "
                + "Der eingebaute Fallback in Results.razor wird verwendet.",
                BlobName, ex.Message);
            return empty;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogInformation("DMS-Property-Mapping: Blob '{Blob}' ist leer.", BlobName);
            return empty;
        }

        try
        {
            var root = JObject.Parse(json);
            var result = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);
            int totalAliases = 0;

            foreach (var catProp in root.Properties())
            {
                if (catProp.Value is not JObject props) continue;

                var catKey = catProp.Name == "*"
                    ? "*"
                    : catProp.Name.Trim().ToUpperInvariant();

                var byProp = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in props.Properties())
                {
                    var list = new List<string>();
                    if (p.Value is JArray arr)
                    {
                        foreach (var v in arr)
                        {
                            var s = v?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                        }
                    }
                    else
                    {
                        var s = p.Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                    }

                    if (list.Count > 0)
                    {
                        byProp[p.Name] = list.ToArray();
                        totalAliases += list.Count;
                    }
                }

                if (byProp.Count > 0)
                    result[catKey] = byProp;
            }

            _logger.LogInformation(
                "DMS-Property-Mapping geladen: {Categories} Kategorien, {Aliases} Aliase.",
                result.Count, totalAliases);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DMS-Property-Mapping: JSON in '{Blob}' konnte nicht geparst werden.", BlobName);
            return empty;
        }
    }
}
