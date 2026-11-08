using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CheckOrderConfirmationFromSupplier.Services
{
    public class UiPathOrchestratorService
    {
        private readonly HttpClient _httpClient;
        private readonly string _orchestratorUrl;
        private readonly string _tenantName;
        private readonly string _queueName;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _organizationUnitId;

        private string _authToken;
        private DateTime _tokenExpiryTime;

        public UiPathOrchestratorService(string orchestratorUrl, string tenantName,
            string queueName, string clientId, string clientSecret, string organizationUnitId)
        {
            _orchestratorUrl = orchestratorUrl.EndsWith("/") ? orchestratorUrl : orchestratorUrl + "/";
            _tenantName = tenantName;
            _queueName = queueName;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _organizationUnitId = organizationUnitId;

            // HttpClient mit SSL-Konfiguration
            var handler = new HttpClientHandler();
            try
            {
                // Akzeptiere alle SSL-Zertifikate (nur für Entwicklung!)
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Warnung: SSL-Konfiguration fehlgeschlagen: {e.Message}");
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            Console.WriteLine("UiPath Service initialisiert:");
            Console.WriteLine($"- URL: {_orchestratorUrl}");
            Console.WriteLine($"- Tenant: {_tenantName}");
            Console.WriteLine($"- Queue: {_queueName}");
            Console.WriteLine($"- Org Unit ID: {_organizationUnitId}");
        }

        public async Task<bool> SendJsonToQueue(string jsonData, string reference)
        {
            Console.WriteLine("\n=== UiPath Queue Item Übertragung ===");
            Console.WriteLine($"Reference: {reference}");
            Console.WriteLine($"JSON Daten (erste 200 Zeichen): {jsonData.Substring(0, Math.Min(200, jsonData.Length))}");

            if (!IsTokenValid())
            {
                Console.WriteLine("Token ungültig oder abgelaufen - authentifiziere neu...");
                await AuthenticateAsync();
            }
            else
            {
                Console.WriteLine("Vorhandener Token ist noch gültig");
            }

            return await SendQueueItemAsync(jsonData, reference);
        }

        private bool IsTokenValid()
        {
            return !string.IsNullOrEmpty(_authToken) && DateTime.Now < _tokenExpiryTime;
        }

        private async Task AuthenticateAsync()
        {
            if (await TryOAuthAuthenticationAsync())
            {
                return;
            }

            Console.WriteLine("OAuth fehlgeschlagen, versuche Standard-Authentifizierung...");
            await TryStandardAuthenticationAsync();
        }

        private async Task<bool> TryOAuthAuthenticationAsync()
        {
            try
            {
                string authUrl = _orchestratorUrl + "identity/connect/token";
                Console.WriteLine($"Versuche OAuth-Authentifizierung bei: {authUrl}");

                var formData = new Dictionary<string, string>
                {
                    { "grant_type", "client_credentials" },
                    { "client_id", _clientId },
                    { "client_secret", _clientSecret },
                    { "scope", "OR.Queues" }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, authUrl);
                request.Headers.Add("X-UIPATH-TenantName", _tenantName);
                request.Content = new FormUrlEncodedContent(formData);

                var response = await _httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var authResponse = JObject.Parse(responseBody);
                    _authToken = authResponse["access_token"]?.ToString();
                    int expiresIn = authResponse["expires_in"]?.Value<int>() ?? 3600;
                    _tokenExpiryTime = DateTime.Now.AddSeconds(expiresIn);

                    Console.WriteLine("OAuth-Authentifizierung erfolgreich!");
                    return true;
                }
                else
                {
                    Console.WriteLine($"OAuth-Authentifizierung fehlgeschlagen mit Status {(int)response.StatusCode}: {responseBody}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"OAuth-Authentifizierung fehlgeschlagen: {e.Message}");
            }

            return false;
        }

        private async Task TryStandardAuthenticationAsync()
        {
            string authUrl = _orchestratorUrl + "api/Account/Authenticate";
            Console.WriteLine($"Versuche Standard-Authentifizierung bei: {authUrl}");

            var authData = new
            {
                tenancyName = _tenantName,
                usernameOrEmailAddress = _clientId,
                password = _clientSecret
            };

            string authBody = JsonConvert.SerializeObject(authData);

            var request = new HttpRequestMessage(HttpMethod.Post, authUrl);
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(authBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"Auth Response Status: {(int)response.StatusCode}");
            Console.WriteLine($"Auth Response Body: {responseBody}");

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Standard-Authentifizierung fehlgeschlagen: {(int)response.StatusCode} - {responseBody}");
            }

            var authResponse = JObject.Parse(responseBody);
            _authToken = authResponse["result"]?.ToString();

            if (string.IsNullOrEmpty(_authToken))
            {
                throw new Exception("Kein Auth Token in der Antwort erhalten");
            }

            _tokenExpiryTime = DateTime.Now.AddHours(1);
            Console.WriteLine($"Standard-Authentifizierung erfolgreich! Token erhalten: {_authToken.Substring(0, Math.Min(20, _authToken.Length))}...");
        }

        private async Task<bool> SendQueueItemAsync(string jsonData, string reference)
        {
            Console.WriteLine("Sende Queue Item an UiPath Orchestrator...");

            string cleanedJsonData = ExtractCleanJsonFromAzureResponse(jsonData);
            Console.WriteLine("=== BEREINIGTES JSON ===");
            Console.WriteLine(cleanedJsonData);
            Console.WriteLine("=== ENDE BEREINIGTES JSON ===");

            var specificContent = new Dictionary<string, object>();

            try
            {
                var parsedJson = JToken.Parse(cleanedJsonData);
                Console.WriteLine("? JSON-Parsing erfolgreich - Valid JSON detected");

                if (parsedJson["position_details"] != null)
                {
                    var positionDetails = parsedJson["position_details"];
                    Console.WriteLine($"Position Details Array Größe: {(positionDetails as JArray)?.Count ?? 0}");
                }

                specificContent["ExtractedJSON"] = cleanedJsonData;
                specificContent["SourceFile"] = reference;
                specificContent["ProcessedBy"] = "Azure_AI_PDF_Analyzer";
            }
            catch (Exception e)
            {
                Console.WriteLine($"JSON-Parsing fehlgeschlagen: {e.Message}");
                specificContent["RawData"] = cleanedJsonData;
                specificContent["ParseError"] = e.Message;
                specificContent["SourceFile"] = reference;
            }

            return await SendSimpleQueueItemAsync(specificContent, reference);
        }

        private string ExtractCleanJsonFromAzureResponse(string azureResponse)
        {
            try
            {
                if (azureResponse.Contains("```json"))
                {
                    int startJson = azureResponse.IndexOf("```json") + 7;
                    int endJson = azureResponse.IndexOf("```", startJson);

                    if (startJson > 7 && endJson > startJson)
                    {
                        string extractedJson = azureResponse.Substring(startJson, endJson - startJson).Trim();
                        Console.WriteLine($"Extrahiertes JSON aus Markdown-Block: {extractedJson.Substring(0, Math.Min(50, extractedJson.Length))}...");
                        return extractedJson;
                    }
                }

                if (azureResponse.Contains("{") && azureResponse.Contains("}"))
                {
                    int startBrace = azureResponse.IndexOf('{');
                    int bracketCount = 1;
                    int endBrace = -1;

                    for (int i = startBrace + 1; i < azureResponse.Length && bracketCount > 0; i++)
                    {
                        char c = azureResponse[i];
                        if (c == '{') bracketCount++;
                        else if (c == '}') bracketCount--;

                        if (bracketCount == 0)
                        {
                            endBrace = i + 1;
                            break;
                        }
                    }

                    if (endBrace > startBrace)
                    {
                        string extractedJson = azureResponse.Substring(startBrace, endBrace - startBrace).Trim();
                        Console.WriteLine($"Extrahiertes JSON-Objekt: {extractedJson.Substring(0, Math.Min(50, extractedJson.Length))}...");
                        return extractedJson;
                    }
                }

                Console.WriteLine("Kein gültiges JSON-Format gefunden, Original-String wird zurückgegeben");
                return azureResponse;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fehler beim Extrahieren des JSON: {e.Message}");
                return azureResponse;
            }
        }

        private async Task<bool> SendSimpleQueueItemAsync(Dictionary<string, object> specificContent, string reference)
        {
            string apiUrl = _orchestratorUrl + "odata/Queues/UiPathODataSvc.AddQueueItem";
            Console.WriteLine($"API URL: {apiUrl}");
            Console.WriteLine($"Queue Name: {_queueName}");

            var itemData = new
            {
                Name = _queueName,
                Priority = "Normal",
                SpecificContent = specificContent,
                Reference = reference
            };

            var requestBody = new { itemData };
            string requestBodyJson = JsonConvert.SerializeObject(requestBody);

            Console.WriteLine("\n=== PAYLOAD ===");
            Console.WriteLine($"{requestBodyJson.Substring(0, Math.Min(300, requestBodyJson.Length))}...");
            Console.WriteLine("=== ENDE PAYLOAD ===\n");

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Add("Authorization", $"Bearer {_authToken}");
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("X-UIPATH-TenantName", _tenantName);
            request.Headers.Add("X-UIPATH-OrganizationUnitId", _organizationUnitId);
            request.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"Response Status: {(int)response.StatusCode}");
            Console.WriteLine($"Response Body: {responseBody.Substring(0, Math.Min(300, responseBody.Length))}...");

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("? Queue Item erfolgreich erstellt!");
                return true;
            }
            else
            {
                Console.WriteLine("? Queue Item Fehler:");
                Console.WriteLine($"Status: {(int)response.StatusCode}");
                Console.WriteLine($"Body: {responseBody}");
                throw new Exception($"Queue Item konnte nicht erstellt werden: {(int)response.StatusCode} - {responseBody}");
            }
        }
    }
}
