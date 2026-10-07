using DatasheetAnalyzer.Core.Services;
using Microsoft.Extensions.Logging;

namespace DatasheetAnalyzer.Core.Config;

public class AzureConfig
{
    private readonly IRemoteConfigService _remoteConfigService;
    private readonly ILogger<AzureConfig> _logger;
    private Dictionary<string, string>? _properties;
    private bool _loadAttempted;
    private Exception? _lastLoadError;

    public AzureConfig(IRemoteConfigService remoteConfigService, ILogger<AzureConfig> logger)
    {
        _remoteConfigService = remoteConfigService;
        _logger = logger;
        // Konfiguration NICHT im Konstruktor laden, damit die App auch bei
        // fehlendem/blockiertem Azure-Zugriff (z.B. Firewall beim Endkunden)
        // startet und die Web-UI ueberhaupt ausgeliefert werden kann.
        // Die Properties werden erst beim ersten Zugriff geladen
        // (siehe EnsureLoaded()).
    }

    /// <summary>
    /// Letzter Fehler beim Laden der Azure-Konfiguration (oder null, wenn ok).
    /// Kann von der UI verwendet werden, um eine sprechende Meldung anzuzeigen.
    /// </summary>
    public Exception? LastLoadError => _lastLoadError;

    /// <summary>
    /// Versucht die Konfiguration zu laden, falls noch nicht geschehen.
    /// Wirft KEINE Exception nach aussen - Fehler werden geloggt und
    /// koennen ueber <see cref="LastLoadError"/> bzw.
    /// <see cref="IsConfigurationValid"/> ausgewertet werden.
    /// </summary>
    public bool EnsureLoaded()
    {
        if (_loadAttempted) return _properties != null && _properties.Count > 0;
        _loadAttempted = true;
        try
        {
            _properties = _remoteConfigService.LoadConfig("azure-config");
            if (_properties == null || _properties.Count == 0)
            {
                _logger.LogError("Azure-Konfiguration konnte nicht geladen werden oder ist leer");
                _lastLoadError = new InvalidOperationException("Azure-Konfiguration ist nicht verfügbar");
                return false;
            }
            _logger.LogInformation("Azure-Konfiguration geladen: {Count} Einträge, Endpoint: {Endpoint}", _properties.Count, GetEndpoint());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der Azure-Konfiguration (App laeuft im eingeschraenkten Modus weiter)");
            _lastLoadError = ex;
            _properties = null;
            return false;
        }
    }

    private string? Get(string key)
    {
        EnsureLoaded();
        return _properties?.GetValueOrDefault(key);
    }

    public string? GetEndpoint() => Get("azure.endpoint");
    public string? GetApiKey() => Get("azure.api.key");
    public string? GetApiVersion() => Get("azure.api.version");
    public string? GetDeploymentName() => Get("azure.deployment.name");

    public bool IsConfigurationValid()
    {
        EnsureLoaded();
        return _properties != null
            && !string.IsNullOrEmpty(GetEndpoint())
            && !string.IsNullOrEmpty(GetApiKey())
            && !string.IsNullOrEmpty(GetDeploymentName());
    }
}
