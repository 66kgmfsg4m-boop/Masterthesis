using CheckOrderConfirmationFromSupplier.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// Service for analyzing order confirmation PDFs using Azure OpenAI.
    /// Loads prompts from Azure Blob Storage and communicates with Azure OpenAI API.
    /// Tracks token usage and provides performance monitoring.
    /// </summary>
    public class AzureAIService
    {
        private readonly AzureConfig _config;
        private readonly HttpClient _client;
        private readonly RemoteConfigService _remoteConfigService;
        
        // Cached prompt to avoid reloading from Azure on every request
        private string _cachedPrompt = null;

        // Callback for token usage statistics
        public delegate void TokenStatsHandler(int promptTokens, int completionTokens);
        public event TokenStatsHandler TokenStatsCallback;

        /// <summary>
        /// Initializes the Azure AI service with configuration and HTTP client.
        /// Creates RemoteConfigService with unique client ID for Azure communication.
        /// </summary>
        public AzureAIService(AzureConfig config)
        {
            _config = config;
            _client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5) // 5 minute timeout for large PDFs
            };
            _remoteConfigService = new RemoteConfigService("AzureAI-" + Guid.NewGuid());
        }

        /// <summary>
        /// Main entry point for analyzing order confirmation content.
        /// Automatically selects text-based or image-based analysis depending on PDF type.
        /// </summary>
        public async Task<string> AnalyzeContentAsync(PDFProcessor.PDFContent pdfContent)
        {
            // Load prompt from Azure (cached after first load)
            string prompt = await GetOrderConfirmationPromptAsync();

            // Choose analysis method based on PDF content type
            if (pdfContent.IsTextBased)
            {
                return await AnalyzeTextContentAsync(pdfContent.TextContent, prompt);
            }
            else
            {
                return await AnalyzeImageContentAsync(pdfContent.Base64Images, prompt);
            }
        }

        /// <summary>
        /// Loads order confirmation prompt from Azure Blob Storage.
        /// Cached after first load to improve performance.
        /// </summary>
        private async Task<string> GetOrderConfirmationPromptAsync()
        {
            if (_cachedPrompt == null)
            {
                Console.WriteLine("Loading OrderConfirmationPrompt.txt from Azure...");
                _cachedPrompt = await Task.Run(() => _remoteConfigService.LoadBlobContent("Prompt.txt"));
                Console.WriteLine($"? Order confirmation prompt loaded ({_cachedPrompt.Length} characters)");
            }
            return _cachedPrompt;
        }

        /// <summary>
        /// Analyzes text-based PDF content using Azure OpenAI.
        /// Uses text extraction from PDFProcessor.
        /// </summary>
        public async Task<string> AnalyzeTextContentAsync(string textContent, string prompt)
        {
            // Create request body for text-based analysis
            var requestBody = CreateTextRequestBody(textContent, prompt);
            return await MakeApiCallAsync(requestBody);
        }

        /// <summary>
        /// Analyzes image-based PDF content using Azure OpenAI vision capabilities.
        /// Sends base64-encoded images for analysis.
        /// </summary>
        public async Task<string> AnalyzeImageContentAsync(List<string> base64Images, string prompt)
        {
            // Create request body for image-based analysis
            var requestBody = CreateImageRequestBody(base64Images, prompt);
            return await MakeApiCallAsync(requestBody);
        }

        /// <summary>
        /// Creates Azure OpenAI request body for text-based analysis.
        /// Combines prompt with extracted text content.
        /// </summary>
        private string CreateTextRequestBody(string textContent, string prompt)
        {
            // Construct user message with prompt and document content
            var message = new
            {
                role = "user",
                content = prompt + "\n\nDocument Content:\n" + textContent
            };

            // Configure Azure OpenAI parameters
            var requestBody = new
            {
                messages = new[] { message },
                max_completion_tokens = 16000,  // Maximum response length
                temperature = 1                  // Creativity level (1 = balanced)
            };

            // Log request configuration for debugging
            Console.WriteLine("--- Request Configuration ---");
            Console.WriteLine("Max completion tokens: 16000");
            Console.WriteLine("Temperature: 1");
            Console.WriteLine($"Content length: {textContent.Length} characters");

            return JsonConvert.SerializeObject(requestBody);
        }

        /// <summary>
        /// Creates Azure OpenAI request body for image-based analysis.
        /// Constructs multi-part content with text prompt and base64 images.
        /// </summary>
        private string CreateImageRequestBody(List<string> base64Images, string prompt)
        {
            var content = new List<object>();

            // Add text prompt
            content.Add(new { type = "text", text = prompt });

            // Add all images as base64 data URLs
            foreach (var base64Image in base64Images)
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:image/png;base64,{base64Image}" }
                });
            }

            // Construct message with multi-part content
            var message = new { role = "user", content };
            
            // Configure Azure OpenAI parameters
            var requestBody = new
            {
                messages = new[] { message },
                max_completion_tokens = 16000,  // Maximum response length
                temperature = 1                  // Creativity level (1 = balanced)
            };

            // Log request configuration for debugging
            Console.WriteLine("--- Request Configuration (Image) ---");
            Console.WriteLine("Max completion tokens: 16000");
            Console.WriteLine("Temperature: 1");
            Console.WriteLine($"Images count: {base64Images.Count}");

            return JsonConvert.SerializeObject(requestBody);
        }

        /// <summary>
        /// Executes the API call to Azure OpenAI.
        /// Handles authentication, error responses, and token usage tracking.
        /// Provides detailed logging for debugging and monitoring.
        /// </summary>
        private async Task<string> MakeApiCallAsync(string requestBodyJson)
        {
            try
            {
                // Construct Azure OpenAI API endpoint URL
                string url = $"{_config.GetEndpoint()}/openai/deployments/{_config.GetDeploymentName()}/chat/completions?api-version={_config.GetApiVersion()}";

                // Log request details for debugging
                Console.WriteLine("\n--- Azure AI Request ---");
                Console.WriteLine($"POST {url}");
                Console.WriteLine("Body (truncated if very long):");
                if (requestBodyJson.Length > 4000)
                {
                    Console.WriteLine($"{requestBodyJson.Substring(0, 4000)}... [TRUNCATED] ({requestBodyJson.Length} chars)");
                }
                else
                {
                    Console.WriteLine(requestBodyJson);
                }

                // Prepare HTTP request with API key authentication
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("api-key", _config.GetApiKey());
                request.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");

                // Execute request and measure duration
                var startTime = DateTime.Now;
                var response = await _client.SendAsync(request);
                var durationMs = (DateTime.Now - startTime).TotalMilliseconds;

                // Get raw response body
                string rawBody = await response.Content.ReadAsStringAsync();

                // Log response details
                Console.WriteLine($"--- Azure AI Response ({(int)response.StatusCode}, {durationMs} ms) ---");
                Console.WriteLine("Full response body:");
                Console.WriteLine(rawBody);

                if (response.IsSuccessStatusCode)
                {
                    // Parse JSON response
                    var responseJson = JObject.Parse(rawBody);
                    var contentNode = responseJson["choices"]?[0]?["message"]?["content"];
                    var finishReason = responseJson["choices"]?[0]?["finish_reason"]?.ToString();

                    // Extract and log token usage statistics
                    var usage = responseJson["usage"];
                    if (usage != null)
                    {
                        int promptTokens = usage["prompt_tokens"]?.Value<int>() ?? -1;
                        int completionTokens = usage["completion_tokens"]?.Value<int>() ?? -1;
                        int totalTokens = usage["total_tokens"]?.Value<int>() ?? -1;
                        Console.WriteLine($"Tokens: prompt={promptTokens}, completion={completionTokens}, total={totalTokens}");

                        // Notify subscribers of token usage
                        TokenStatsCallback?.Invoke(promptTokens, completionTokens);

                        // Warn if approaching token limit
                        if (completionTokens >= 15000)
                        {
                            Console.WriteLine($"? Warning: Completion tokens ({completionTokens}) approaching limit of 16000!");
                        }
                    }

                    // Check finish reason
                    Console.WriteLine($"Finish Reason: {finishReason}");

                    if (finishReason == "length")
                    {
                        Console.WriteLine("? Response was truncated due to token limit (finish_reason=length).");
                    }

                    // Validate content is not empty
                    if (contentNode == null || string.IsNullOrWhiteSpace(contentNode.ToString()))
                    {
                        Console.WriteLine("? Azure AI returned empty content.");
                        throw new Exception("Azure AI returned an empty or invalid response.");
                    }

                    // Extract final text content
                    string finalText = contentNode.ToString();
                    Console.WriteLine($"--- Extracted Content ---\n{finalText}\n--- End Content ---");

                    // Validate JSON structure
                    try
                    {
                        JToken.Parse(finalText);
                        Console.WriteLine("? JSON validation successful");
                    }
                    catch
                    {
                        Console.WriteLine("? JSON validation failed");
                    }

                    return finalText;
                }
                else
                {
                    // Handle API error response
                    throw new Exception($"API call failed: {(int)response.StatusCode} - {rawBody}");
                }
            }
            catch (TaskCanceledException e)
            {
                // Handle timeout after 5 minutes
                Console.WriteLine("? Azure AI API call timed out after 5 minutes");
                throw new Exception($"Azure AI API call timed out: {e.Message}", e);
            }
            catch (Exception e)
            {
                // Handle general errors
                Console.WriteLine($"? Error during Azure AI API call: {e.Message}");
                throw new Exception($"Error during Azure AI API call: {e.Message}", e);
            }
        }
    }
}
