namespace DatasheetAnalyzer.Core.Services;

public interface IRemoteConfigService
{
    Dictionary<string, string> LoadConfig(string configName);
    Task<Dictionary<string, string>> LoadConfigAsync(string configName);
    string LoadBlobContent(string blobName);
    void DiagnoseConnection();
}
