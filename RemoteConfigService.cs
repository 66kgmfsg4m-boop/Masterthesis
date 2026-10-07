using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// SICHERHEITSHINWEIS: Diese Klasse ist DEPRECATED und sollte nicht mehr verwendet werden.
    /// Verwenden Sie stattdessen die Version in DatasheetAnalyzer.Core mit Dependency Injection.
    /// 
    /// WARNUNG: Hardcodierte Credentials wurden aus Sicherheitsgründen entfernt!
    /// Diese WPF-Version wird nicht mehr aktiv gewartet.
    /// </summary>
    [Obsolete("Diese Klasse ist veraltet. Verwenden Sie DatasheetAnalyzer.Core.Services.RemoteConfigService")]
    public class RemoteConfigService
    {
        private readonly BlobContainerClient _containerClient;
        private readonly string _clientId;

        public RemoteConfigService(string clientId)
        {
            _clientId = clientId;

            // SICHERHEIT: Credentials wurden entfernt!
            // Für die WPF-Anwendung erstellen Sie eine App.config mit:
            // <appSettings>
            //   <add key="AzureStorage:ConnectionString" value="DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net"/>
            //   <add key="AzureStorage:ContainerName" value="data"/>
            // </appSettings>

            string connectionString = System.Configuration.ConfigurationManager.AppSettings["AzureStorage:ConnectionString"];
            string containerName = System.Configuration.ConfigurationManager.AppSettings["AzureStorage:ContainerName"] ?? "data";

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Azure Storage ConnectionString ist nicht konfiguriert!\n\n" +
                    "Für WPF-Anwendung: Erstellen Sie eine App.config mit:\n" +
                    "<appSettings>\n" +
                    "  <add key=\"AzureStorage:ConnectionString\" value=\"DefaultEndpointsProtocol=https;AccountName=storage3csd2rus;AccountKey=IHR_KEY;EndpointSuffix=core.windows.net\"/>\n" +
                    "  <add key=\"AzureStorage:ContainerName\" value=\"data\"/>\n" +
                    "</appSettings>\n\n" +
                    "Oder verwenden Sie die neue Blazor-Anwendung (DatasheetAnalyzer.Blazor)");
            }

            var blobServiceClient = new BlobServiceClient(connectionString);
            _containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            Console.WriteLine($"RemoteConfigService (WPF Legacy) mit Client-ID {clientId} initialisiert");
        }

        public Dictionary<string, string> LoadConfig(string configName)
        {
            string blobName = $"{configName}.properties";

            Console.WriteLine("================== KONFIGURATION LADEN ==================");
            Console.WriteLine($"Container: {_containerClient.Name}");
            Console.WriteLine($"Lade Konfiguration aus Azure: {configName}");

            var azureProps = LoadFromAzure(blobName, configName);
            if (azureProps == null || azureProps.Count == 0)
            {
                throw new IOException($"Azure-Konfiguration leer oder nicht geladen: {blobName}");
            }

            Console.WriteLine($"? Konfiguration aus Azure geladen: {configName}");
            return azureProps;
        }

        public async Task<Dictionary<string, string>> LoadConfigAsync(string configName)
        {
            return await Task.Run(() => LoadConfig(configName));
        }

        private Dictionary<string, string> LoadFromAzure(string blobName, string configName)
        {
            try
            {
                Console.WriteLine($"Lade {configName} aus Azure Blob Storage...");
                BlobClient blobClient = _containerClient.GetBlobClient(blobName);

                if (blobClient.Exists())
                {
                    var properties = new Dictionary<string, string>();
                    using (var stream = blobClient.OpenRead())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            line = line.Trim();
                            if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                                continue;

                            int separatorIndex = line.IndexOf('=');
                            if (separatorIndex > 0)
                            {
                                string key = line.Substring(0, separatorIndex).Trim();
                                string value = line.Substring(separatorIndex + 1).Trim();
                                properties[key] = value;
                            }
                        }
                    }
                    Console.WriteLine($"? {configName} aus Azure geladen ({properties.Count} Properties)");
                    return properties;
                }
                else
                {
                    Console.WriteLine($"? Fehler beim Laden aus Azure - Blob nicht gefunden: {blobName}");
                    return null;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"? Exception beim Laden aus Azure: {e.Message}");
                return null;
            }
        }

        public string LoadBlobContent(string blobName)
        {
            try
            {
                Console.WriteLine($"Lade Blob '{blobName}' aus Azure Storage...");
                BlobClient blobClient = _containerClient.GetBlobClient(blobName);

                if (blobClient.Exists())
                {
                    using (var stream = new MemoryStream())
                    {
                        blobClient.DownloadTo(stream);
                        string content = Encoding.UTF8.GetString(stream.ToArray());
                        Console.WriteLine($"? Blob '{blobName}' erfolgreich geladen.");
                        return content;
                    }
                }
                else
                {
                    Console.WriteLine($"? Fehler beim Laden aus Azure - Blob nicht gefunden: {blobName}");
                    throw new IOException($"Blob nicht gefunden: {blobName}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"? Exception beim Laden von Blob '{blobName}': {e.Message}");
                throw new IOException($"Fehler beim Laden von Blob '{blobName}'", e);
            }
        }

        public void DiagnoseConnection()
        {
            Console.WriteLine("=== AZURE BLOB STORAGE DIAGNOSE (WPF Legacy) ===");
            Console.WriteLine($"Client-ID: {_clientId}");
            Console.WriteLine($"Container: {_containerClient.Name}");
            Console.WriteLine($"Endpoint: {_containerClient.Uri}");

            try
            {
                if (_containerClient.Exists())
                {
                    Console.WriteLine("? Verbindung zu Azure Blob Storage erfolgreich");
                    Console.WriteLine("Verfügbare Blobs:");
                    foreach (var blob in _containerClient.GetBlobs())
                    {
                        Console.WriteLine($"  - {blob.Name}");
                    }
                }
                else
                {
                    Console.WriteLine("? Verbindungsfehler - Container nicht gefunden oder keine Berechtigung.");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"? Ausnahme bei Verbindungstest: {e.Message}");
                Console.WriteLine(e.StackTrace);
            }
            Console.WriteLine("=== ENDE DIAGNOSE ===");
        }
    }
}
