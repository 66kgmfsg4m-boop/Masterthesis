namespace DatasheetAnalyzer.Core.Options;

/// <summary>
/// Konfiguration für Azure Blob Storage (RemoteConfigService).
/// ConnectionString und ContainerName aus appsettings/Key Vault, nicht hardcodieren.
/// </summary>
public class AzureStorageOptions
{
    public const string SectionName = "AzureStorage";

    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "data";
}
