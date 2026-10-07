using CheckOrderConfirmationFromSupplier.Services;
using System;
using System.Collections.Generic;

namespace CheckOrderConfirmationFromSupplier.Config
{
    public class AzureConfig
    {
        private Dictionary<string, string> _properties;
        private readonly RemoteConfigService _remoteConfigService;

        public AzureConfig()
        {
            _remoteConfigService = new RemoteConfigService(Guid.NewGuid().ToString());
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            try
            {
                // Load Configuration directly from RemoteConfigService 
                _properties = _remoteConfigService.LoadConfig("azure-config");

                if (_properties == null || _properties.Count == 0)
                {
                    Console.WriteLine("Azure-Konfiguration konnte nicht geladen werden oder ist leer");
                    throw new Exception("Azure-Konfiguration ist nicht verfügbar");
                }

                Console.WriteLine($"Azure-Konfiguration erfolgreich geladen: {_properties.Count} Einträge");
                Console.WriteLine($"Azure Endpoint: {GetEndpoint()}");
                Console.WriteLine($"Azure API Version: {GetApiVersion()}");
                Console.WriteLine($"Azure Deployment Name: {GetDeploymentName()}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fehler beim Laden der Azure-Konfiguration: {e.Message}");
                throw new Exception($"Fehler beim Laden der Azure-Konfiguration: {e.Message}", e);
            }
        }

        public string GetEndpoint()
        {
            return _properties.ContainsKey("azure.endpoint") ? _properties["azure.endpoint"] : null;
        }

        public string GetApiKey()
        {
            return _properties.ContainsKey("azure.api.key") ? _properties["azure.api.key"] : null;
        }

        public string GetApiVersion()
        {
            return _properties.ContainsKey("azure.api.version") ? _properties["azure.api.version"] : null;
        }

        public string GetDeploymentName()
        {
            return _properties.ContainsKey("azure.deployment.name") ? _properties["azure.deployment.name"] : null;
        }

        public bool IsConfigurationValid()
        {
            return _properties != null &&
                   !string.IsNullOrEmpty(GetEndpoint()) &&
                   !string.IsNullOrEmpty(GetApiKey()) &&
                   !string.IsNullOrEmpty(GetDeploymentName());
        }
    }
}
