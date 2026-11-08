using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using DatasheetAnalyzer.Core.Options;

namespace DatasheetAnalyzer.Core.Services;

public class RemoteConfigService : IRemoteConfigService
{
    private readonly BlobContainerClient _containerClient;
    private readonly string _clientId;
    private readonly ILogger<RemoteConfigService> _logger;

    public RemoteConfigService(
        IOptions<AzureStorageOptions> options,
        ILogger<RemoteConfigService> logger,
        string? clientId = null)
    {
        _logger = logger;
        _clientId = clientId ?? Guid.NewGuid().ToString();
        var opt = options?.Value ?? throw new ArgumentException("AzureStorageOptions must be configured.");
        if (string.IsNullOrWhiteSpace(opt.ConnectionString))
            throw new InvalidOperationException("AzureStorage:ConnectionString must be set.");
        
        // Validiere, dass keine Platzhalterwerte verwendet werden
        if (opt.ConnectionString.Contains("DEIN_ACCOUNT") || opt.ConnectionString.Contains("DEIN_KEY"))
        {
            throw new InvalidOperationException(
                "Azure Storage ConnectionString enthält Platzhalterwerte. " +
                "Bitte setzen Sie echte Credentials über:\n" +
                "1. Umgebungsvariable: AzureStorage__ConnectionString=<Ihre Connection String>\n" +
                "2. Oder erstellen Sie eine appsettings.Development.yaml mit echten Werten (nicht committen!)");
        }
        
        try
        {
            var blobServiceClient = new BlobServiceClient(opt.ConnectionString);
            _containerClient = blobServiceClient.GetBlobContainerClient(opt.ContainerName);
            _logger.LogInformation("RemoteConfigService mit Client-ID {ClientId} initialisiert", _clientId);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Ungültige Azure Storage ConnectionString");
            throw new InvalidOperationException(
                "Ungültige Azure Storage ConnectionString. " +
                "Bitte überprüfen Sie das Format und die Credentials.", ex);
        }
    }

    public Dictionary<string, string> LoadConfig(string configName)
    {
        string blobName = string.IsNullOrEmpty(configName) ? ".properties" : $"{configName}.properties";
        _logger.LogDebug("Lade Konfiguration aus Azure: {ConfigName} (Blob: {BlobName})", configName, blobName);
        var azureProps = LoadFromAzure(blobName, configName);
        if (azureProps == null || azureProps.Count == 0)
            throw new IOException($"Azure-Konfiguration leer oder nicht geladen: {blobName}");
        _logger.LogInformation("Konfiguration aus Azure geladen: {ConfigName}", configName);
        return azureProps;
    }

    public async Task<Dictionary<string, string>> LoadConfigAsync(string configName)
    {
        return await Task.Run(() => LoadConfig(configName)).ConfigureAwait(false);
    }

    private Dictionary<string, string>? LoadFromAzure(string blobName, string configName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            if (!blobClient.Exists())
            {
                _logger.LogWarning("Blob nicht gefunden: {BlobName}", blobName);
                return null;
            }
            var properties = new Dictionary<string, string>();
            using (var stream = blobClient.OpenRead())
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;
                    int sep = line.IndexOf('=');
                    if (sep > 0)
                    {
                        string key = line.Substring(0, sep).Trim();
                        string value = line.Substring(sep + 1).Trim();
                        properties[key] = value;
                    }
                }
            }
            _logger.LogDebug("{ConfigName} aus Azure geladen ({Count} Properties)", configName, properties.Count);
            return properties;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception beim Laden aus Azure: {BlobName}", blobName);
            return null;
        }
    }

    public string LoadBlobContent(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            if (!blobClient.Exists())
            {
                _logger.LogWarning("Blob nicht gefunden: {BlobName}", blobName);
                throw new IOException($"Blob nicht gefunden: {blobName}");
            }
            using var stream = new MemoryStream();
            blobClient.DownloadTo(stream);
            string content = Encoding.UTF8.GetString(stream.ToArray());
            _logger.LogInformation("Blob {BlobName} erfolgreich geladen", blobName);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden von Blob {BlobName}", blobName);
            throw new IOException($"Fehler beim Laden von Blob '{blobName}'", ex);
        }
    }

    public void DiagnoseConnection()
    {
        _logger.LogInformation("Azure Blob Storage Diagnose – Client-ID: {ClientId}", _clientId);
        try
        {
            if (_containerClient.Exists())
            {
                _logger.LogInformation("Verbindung zu Azure Blob Storage erfolgreich");
                foreach (var blob in _containerClient.GetBlobs())
                    _logger.LogDebug("  Blob: {Name}", blob.Name);
            }
            else
                _logger.LogWarning("Container nicht gefunden oder keine Berechtigung");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ausnahme bei Verbindungstest");
        }
    }
}
