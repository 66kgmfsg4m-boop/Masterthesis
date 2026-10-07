using CheckOrderConfirmationFromSupplier.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// Service for analyzing technical datasheets using Azure OpenAI.
    /// Supports two analysis modes:
    /// 1. MIXTURE: Safety Data Sheets for mixtures/chemicals
    /// 2. COMPONENT: Technical datasheets for electronic components
    /// Uses Chain-of-Thought prompting for better results.
    /// All prompts are loaded from Azure Blob Storage for maximum flexibility.
    /// </summary>
    public class DatasheetAnalysisService
    {
        private readonly AzureConfig _config;
        private readonly HttpClient _client;
        private readonly RemoteConfigService _remoteConfigService;
        private readonly DMSCatalogService _catalogService;
        
        // Cached prompts loaded from Azure Blob Storage (loaded once, reused for performance)
        private string _cachedIdentificationPrompt = null;
        private string _cachedSafetyDatasheetPrompt = null;
        private string _cachedPartNumberDecodingPrompt = null;
        private string _cachedSeriesDatasheetInstructions = null;

        // Event for token usage tracking (allows UI to display token consumption)
        public delegate void TokenStatsHandler(int promptTokens, int completionTokens);
        public event TokenStatsHandler TokenStatsCallback;

        // Minimal fallback prompts (only used if Azure Blob Storage is unreachable)
        private const string FALLBACK_IDENTIFICATION_PROMPT = @"Analyze this technical document and identify:
1. Document Type (COMPONENT or MIXTURE)
2. Component Category (be specific!)
3. Part Number(s)
4. Manufacturer

Return JSON with: DocumentType, ComponentCategory, ComponentName, ManufacturerInfo, PartNumber, ConfidenceLevel";

        private const string FALLBACK_SAFETY_DATASHEET_PROMPT = @"Extract safety data from SDS:
- Component Name, UN-Number, Storage Class, Hazard Statements
Return compact JSON.";

        /// <summary>
        /// Initializes the datasheet analysis service.
        /// Creates HTTP client with 5 minute timeout for large PDF processing.
        /// Loads DMS catalog data for component schema matching.
        /// </summary>
        public DatasheetAnalysisService(AzureConfig config)
        {
            _config = config;
            _client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5) // Long timeout for complex datasheet analysis
            };
            _remoteConfigService = new RemoteConfigService("DatasheetAnalysis-" + Guid.NewGuid());

            // Initialize DMS catalog service for component matching
            _catalogService = new DMSCatalogService();
            try
            {
                _catalogService.LoadCatalogData();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not load catalog data: {ex.Message}");
            }
        }

        #region Azure Prompt Loading Methods

        /// <summary>
        /// Loads identification prompt from Azure Blob Storage with caching.
        /// The identification prompt is used in Stage 1 to determine document type and component category.
        /// Cached after first load to improve performance on subsequent analyses.
        /// Falls back to minimal hardcoded prompt if Azure is unreachable.
        /// </summary>
        private async Task<string> GetIdentificationPromptAsync()
        {
            if (_cachedIdentificationPrompt == null)
            {
                try
                {
                    Console.WriteLine("Loading IdentificationPrompt.txt from Azure...");
                    _cachedIdentificationPrompt = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent("IdentificationPrompt.txt"));
                    Console.WriteLine($"Identification prompt loaded ({_cachedIdentificationPrompt.Length} characters)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading IdentificationPrompt.txt: {ex.Message}");
                    Console.WriteLine("Using fallback prompt");
                    _cachedIdentificationPrompt = FALLBACK_IDENTIFICATION_PROMPT;
                }
            }
            return _cachedIdentificationPrompt;
        }

        /// <summary>
        /// Loads safety datasheet prompt from Azure Blob Storage with caching.
        /// Used for MIXTURE document type (Safety Data Sheets for chemicals/mixtures).
        /// Extracts hazard statements, UN numbers, storage classifications, etc.
        /// Falls back to minimal hardcoded prompt if Azure is unreachable.
        /// </summary>
        private async Task<string> GetSafetyDatasheetPromptAsync()
        {
            if (_cachedSafetyDatasheetPrompt == null)
            {
                try
                {
                    Console.WriteLine("Loading SafetyDatasheetPrompt.txt from Azure...");
                    _cachedSafetyDatasheetPrompt = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent("SafetyDatasheetPrompt.txt"));
                    Console.WriteLine($"Safety datasheet prompt loaded ({_cachedSafetyDatasheetPrompt.Length} characters)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading SafetyDatasheetPrompt.txt: {ex.Message}");
                    Console.WriteLine("Using fallback prompt");
                    _cachedSafetyDatasheetPrompt = FALLBACK_SAFETY_DATASHEET_PROMPT;
                }
            }
            return _cachedSafetyDatasheetPrompt;
        }

        /// <summary>
        /// Loads part number decoding prompt from Azure Blob Storage with caching.
        /// Used for AI-based part number decoding from datasheet legends.
        /// Template variables {PART_NUMBER}, {MANUFACTURER}, {COMPONENT_CATEGORY} are replaced at runtime.
        /// Returns null if not available (will use minimal inline fallback).
        /// </summary>
        private async Task<string> GetPartNumberDecodingPromptAsync()
        {
            if (_cachedPartNumberDecodingPrompt == null)
            {
                try
                {
                    Console.WriteLine("Loading PartNumberDecodingPrompt.txt from Azure...");
                    _cachedPartNumberDecodingPrompt = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent("PartNumberDecodingPrompt.txt"));
                    Console.WriteLine($"Part number decoding prompt loaded ({_cachedPartNumberDecodingPrompt.Length} characters)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading PartNumberDecodingPrompt.txt: {ex.Message}");
                    _cachedPartNumberDecodingPrompt = null; // Will use minimal inline template
                }
            }
            return _cachedPartNumberDecodingPrompt;
        }

        /// <summary>
        /// Loads series datasheet instructions from Azure Blob Storage with caching.
        /// Used when analyzing series datasheets (multiple part numbers in one document).
        /// Instructs AI to extract only the specific part number requested, not ranges.
        /// Template variable {PART_NUMBER} is replaced at runtime.
        /// Returns null if not available (series instructions will be skipped).
        /// </summary>
        private async Task<string> GetSeriesDatasheetInstructionsAsync()
        {
            if (_cachedSeriesDatasheetInstructions == null)
            {
                try
                {
                    Console.WriteLine("Loading SeriesDatasheetInstructions.txt from Azure...");
                    _cachedSeriesDatasheetInstructions = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent("SeriesDatasheetInstructions.txt"));
                    Console.WriteLine($"Series datasheet instructions loaded ({_cachedSeriesDatasheetInstructions.Length} characters)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading SeriesDatasheetInstructions.txt: {ex.Message}");
                    _cachedSeriesDatasheetInstructions = null; // Will not add instructions
                }
            }
            return _cachedSeriesDatasheetInstructions;
        }

        #endregion

        #region Main Analysis Methods

        /// <summary>
        /// Main entry point for datasheet analysis.
        /// Implements two-stage process:
        /// Stage 1: Identifies document type (COMPONENT or MIXTURE), category, and part number
        /// Stage 2: Extracts properties based on identified category using DMS catalog schema
        /// Throws exception if component not found in catalog (strict enforcement of catalog maintenance).
        /// </summary>
        public async Task<string> AnalyzeContentAsync(PDFProcessor.PDFContent pdfContent)
        {
            try
            {
                // STAGE 1: Identify component type using Chain-of-Thought prompting
                string prompt = await GetIdentificationPromptAsync();
                string identificationResult = await IdentifyComponentTypeAsync(pdfContent, prompt);

                // Parse identification result
                JObject identificationJson = null;
                try
                {
                    identificationJson = JObject.Parse(identificationResult);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"Failed to parse identification result: {ex.Message}");
                    throw new InvalidOperationException("Component identification failed. Cannot proceed with extraction.");
                }

                // Extract identification fields
                string documentType = identificationJson["DocumentType"]?.Value<string>() ?? "UNKNOWN";
                string componentCategory = identificationJson["ComponentCategory"]?.Value<string>() ?? "";
                string componentName = identificationJson["ComponentName"]?.Value<string>() ?? "";
                string partNumber = identificationJson["PartNumber"]?.Value<string>();

                // Check confidence level and warn if low
                string confidenceLevel = identificationJson["ConfidenceLevel"]?.Value<string>() ?? "MEDIUM";
                if (confidenceLevel == "LOW")
                {
                    Console.WriteLine("WARNING: AI has low confidence - user confirmation recommended");
                }

                // STAGE 2: Route to appropriate extraction method based on document type
                if (documentType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase))
                {
                    // Handle Safety Data Sheets (chemicals/mixtures)
                    return await AnalyzeSafetyDatasheetAsync(pdfContent);
                }
                else if (documentType.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase))
                {
                    // Find matching component in DMS catalog (REQUIRED - no generic extraction)
                    CatalogComponent component = FindMatchingComponent(componentCategory, componentName);

                    if (component == null)
                    {
                        string errorMsg = $"No matching component found in DMS catalog for category: '{componentCategory}'";
                        Console.WriteLine(errorMsg);
                        throw new InvalidOperationException(errorMsg + "\nPlease add this component to get_classes.csv or correct the category.");
                    }

                    // Get properties for component from catalog
                    var properties = _catalogService.GetPropertiesForComponent(component.FullPath);
                    if (properties.Count == 0)
                    {
                        properties = _catalogService.GetPropertiesForComponent(component.InternalName);
                    }

                    // Validate properties exist
                    if (properties.Count == 0)
                    {
                        string errorMsg = $"Component '{component.DisplayName}' found but has no properties defined";
                        Console.WriteLine(errorMsg);
                        throw new InvalidOperationException(errorMsg + "\nPlease add properties to get_classes.csv for this component.");
                    }

                    Console.WriteLine($"\nProperty schema found: '{component.DisplayName}' ({properties.Count} properties)");

                    // Extract properties using catalog schema
                    return await AnalyzeComponentWithCatalogAsync(
                        pdfContent,
                        component,
                        partNumber,
                        componentCategory
                    );
                }
                else
                {
                    throw new InvalidOperationException($"Unknown document type: '{documentType}'. Expected COMPONENT or MIXTURE.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during analysis: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stage 1 only: Identifies component type without extracting properties.
        /// Used for confirmation dialog to allow user review before full extraction.
        /// Returns JSON with DocumentType, ComponentCategory, PartNumber, ConfidenceLevel.
        /// </summary>
        public async Task<string> IdentifyComponentOnlyAsync(PDFProcessor.PDFContent pdfContent)
        {
            try
            {
                string prompt = await GetIdentificationPromptAsync();
                string identificationResult = await IdentifyComponentTypeAsync(pdfContent, prompt);

                return identificationResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Stage 1 (Identification): {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stage 2: Analyzes with confirmed identification (after user confirmation).
        /// Uses user-confirmed category and part number instead of AI-detected values.
        /// Skips Stage 1 identification, goes directly to property extraction.
        /// Adds metadata to result JSON (_PropertySchemaUsed, _RequestedCategory).
        /// </summary>
        public async Task<string> AnalyzeWithConfirmedIdentificationAsync(PDFProcessor.PDFContent pdfContent, JObject confirmedIdentification)
        {
            try
            {
                // Extract confirmed values from user dialog
                string documentType = confirmedIdentification["DocumentType"]?.Value<string>() ?? "UNKNOWN";
                string componentCategory = confirmedIdentification["ComponentCategory"]?.Value<string>() ?? "";
                string componentName = confirmedIdentification["ComponentName"]?.Value<string>() ?? "";
                string partNumber = confirmedIdentification["PartNumber"]?.Value<string>();

                // Route based on document type
                if (documentType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase))
                {
                    return await AnalyzeSafetyDatasheetAsync(pdfContent);
                }
                else if (documentType.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase))
                {
                    // Find matching component (MUST succeed - strict catalog enforcement)
                    CatalogComponent component = FindMatchingComponent(componentCategory, componentName);

                    if (component == null)
                    {
                        string errorMsg = $"No matching component found for category: '{componentCategory}'";
                        Console.WriteLine(errorMsg);
                        throw new InvalidOperationException(errorMsg + "\nComponent must exist in DMS catalog (get_classes.csv).");
                    }

                    // Get and validate properties
                    var properties = _catalogService.GetPropertiesForComponent(component.FullPath);
                    if (properties.Count == 0)
                    {
                        properties = _catalogService.GetPropertiesForComponent(component.InternalName);
                    }

                    if (properties.Count == 0)
                    {
                        string errorMsg = $"Component '{component.DisplayName}' has no properties defined";
                        Console.WriteLine(errorMsg);
                        throw new InvalidOperationException(errorMsg + "\nAdd properties to get_classes.csv.");
                    }

                    Console.WriteLine($"\nProperty schema found: '{component.DisplayName}' ({properties.Count} properties)");

                    // Extract properties
                    string extractionResult = await AnalyzeComponentWithCatalogAsync(
                        pdfContent,
                        component,
                        partNumber,
                        componentCategory
                    );

                    // Add metadata to result JSON
                    try
                    {
                        var resultJson = JObject.Parse(extractionResult);
                        resultJson["_PropertySchemaUsed"] = component.DisplayName;
                        resultJson["_RequestedCategory"] = componentCategory;
                        return resultJson.ToString(Formatting.Indented);
                    }
                    catch
                    {
                        return extractionResult;
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unknown document type: '{documentType}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Stage 2 (Extraction): {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Decodes part number from datasheet using AI.
        /// Searches for part number legend/breakdown tables in the datasheet.
        /// Extracts component-specific values (resistance, tolerance, power, package, etc.).
        /// Returns JObject with decoded properties or null if decoding failed.
        /// </summary>
        public async Task<JObject> DecodePartNumberFromDatasheetAsync(PDFProcessor.PDFContent pdfContent, string partNumber, string manufacturer, string componentCategory)
        {
            try
            {
                Console.WriteLine($"\nAI-BASED PART NUMBER DECODING");
                Console.WriteLine($"   Part Number: {partNumber}");
                Console.WriteLine($"   Manufacturer: {manufacturer}");
                Console.WriteLine($"   Category: {componentCategory}");

                // Load prompt template from Azure
                await GetPartNumberDecodingPromptAsync();

                // Generate prompt with template variable replacement
                string decodingPrompt = GeneratePartNumberDecodingPrompt(partNumber, manufacturer, componentCategory);

                // Analyze based on PDF content type
                string result;
                if (pdfContent.IsTextBased)
                {
                    result = await AnalyzeTextContentAsync(pdfContent.TextContent, decodingPrompt);
                }
                else
                {
                    result = await AnalyzeImageContentAsync(pdfContent.Base64Images, decodingPrompt);
                }

                // Parse result
                var decoded = JObject.Parse(result);

                // Check if decoding was successful
                bool success = decoded["DecodingSuccess"]?.Value<bool>() ?? false;

                if (success)
                {
                    Console.WriteLine("AI decoding successful!");
                    Console.WriteLine($"   Resistance: {decoded["ResistaOhm"]} Ohm");
                    Console.WriteLine($"   Tolerance: +/-{decoded["TolerancePct"]}%");
                    Console.WriteLine($"   Power: {decoded["PowerW"]} W");
                    Console.WriteLine($"   Package: {decoded["PackageType"]}");
                    return decoded;
                }
                else
                {
                    Console.WriteLine("AI decoding failed");
                    string reason = decoded["FailureReason"]?.Value<string>() ?? "Unknown";
                    Console.WriteLine($"   Reason: {reason}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during AI decoding: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Routes identification to appropriate analysis method based on PDF content type.
        /// Text-based PDFs: Uses extracted text content.
        /// Image-based PDFs: Uses first 3 pages as images for vision analysis.
        /// </summary>
        private async Task<string> IdentifyComponentTypeAsync(PDFProcessor.PDFContent pdfContent, string prompt)
        {
            if (pdfContent.IsTextBased)
            {
                return await AnalyzeTextContentAsync(pdfContent.TextContent, prompt);
            }
            else
            {
                var previewImages = pdfContent.Base64Images.Take(3).ToList();
                return await AnalyzeImageContentAsync(previewImages, prompt);
            }
        }

        /// <summary>
        /// Finds matching component in DMS catalog using category and name.
        /// Tries multiple matching strategies:
        /// 1. Special handling for generic "Amplifier" (tries "OP Amplifier" first)
        /// 2. Exact match by property key
        /// 3. Fuzzy match by display name
        /// 4. Match by component name
        /// 5. Alternative name mappings (e.g., "RF Power Amplifier" -> "rfAmplifier")
        /// Returns null if no match found.
        /// </summary>
        private CatalogComponent FindMatchingComponent(string category, string name)
        {
            if (string.IsNullOrWhiteSpace(category))
                return null;

            // Special handling for generic "Amplifier" category
            if (category.Equals("Amplifier", StringComparison.OrdinalIgnoreCase))
            {
                CatalogComponent opAmp = _catalogService.FindComponent("OP Amplifier");
                if (opAmp != null)
                {
                    var opAmpProps = _catalogService.GetPropertiesForComponent(opAmp.FullPath);
                    if (opAmpProps.Count == 0)
                        opAmpProps = _catalogService.GetPropertiesForComponent(opAmp.InternalName);

                    if (opAmpProps.Count > 0)
                    {
                        return opAmp;
                    }
                }
            }

            // Try exact match by property key
            CatalogComponent match = _catalogService.FindComponentByPropertyKey(category);

            if (match != null)
            {
                return match;
            }

            // Try fuzzy match by display name
            match = _catalogService.FindComponent(category);

            // Try component name if category didn't work
            if (match == null && !string.IsNullOrWhiteSpace(name))
            {
                match = _catalogService.FindComponent(name);
            }

            // Try alternative name mappings
            if (match == null)
            {
                string[] alternatives = GenerateAlternativeNames(category);
                foreach (var alt in alternatives)
                {
                    match = _catalogService.FindComponent(alt);
                    if (match != null) break;
                }
            }

            return match;
        }

        /// <summary>
        /// Generates alternative names for component category.
        /// Tries different naming conventions:
        /// - Original name
        /// - No spaces (e.g., "RF Amplifier" -> "RFAmplifier")
        /// - camelCase (e.g., "RF Amplifier" -> "rfAmplifier")
        /// - Known mappings (e.g., "RF Power Amplifier" -> "rfAmplifier")
        /// Used to improve matching when exact category name doesn't match catalog entries.
        /// </summary>
        private string[] GenerateAlternativeNames(string category)
        {
            var alternatives = new List<string>();
            alternatives.Add(category);

            // Try camelCase version
            string noSpaces = category.Replace(" ", "");
            if (noSpaces != category)
            {
                alternatives.Add(noSpaces);
                alternatives.Add(char.ToLower(noSpaces[0]) + noSpaces.Substring(1));
            }

            // Known mappings from display names to internal names
            var mappings = new Dictionary<string, string>
            {
                {"RF Power Amplifier", "rfAmplifier"},
                {"Audio Amplifier", "audioAmplifier"},
                {"Instrumentation Amplifier", "instrumentAmplifier"},
                {"RF Detector", "rfDetector"},
                {"RF Components", "rfComponents"},
                {"Nonlinear Circuits", "nonlinearCircuits"},
                {"Digital Circuit", "digitalCircuit"},
                {"Analog Circuit", "analogCircuit"}
            };

            foreach (var mapping in mappings)
            {
                if (category.IndexOf(mapping.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    alternatives.Add(mapping.Value);
                }
            }

            return alternatives.Distinct().ToArray();
        }

        /// <summary>
        /// Analyzes component with DMS catalog schema.
        /// Generates dynamic prompt from catalog schema for the specific component.
        /// Optionally adds series datasheet instructions if part number is specified.
        /// Routes to text or image analysis based on PDF content type.
        /// </summary>
        private async Task<string> AnalyzeComponentWithCatalogAsync(PDFProcessor.PDFContent pdfContent, CatalogComponent component, string partNumber = null, string componentType = null)
        {
            // Generate dynamic prompt from DMS catalog schema
            string dynamicPrompt = _catalogService.GenerateDynamicPrompt(component);

            // Add series datasheet instructions if part number is specified
            if (!string.IsNullOrEmpty(partNumber))
            {
                string seriesInstructions = await GetSeriesDatasheetInstructionsAsync();
                
                if (!string.IsNullOrEmpty(seriesInstructions))
                {
                    // Replace template variable with actual part number
                    seriesInstructions = seriesInstructions.Replace("{PART_NUMBER}", partNumber);
                    dynamicPrompt = seriesInstructions + "\n" + dynamicPrompt;
                    Console.WriteLine($"Series datasheet instructions added for part number: {partNumber}");
                }
            }

            // Route to appropriate analysis method
            if (pdfContent.IsTextBased)
            {
                return await AnalyzeTextContentAsync(pdfContent.TextContent, dynamicPrompt);
            }
            else
            {
                return await AnalyzeImageContentAsync(pdfContent.Base64Images, dynamicPrompt);
            }
        }

        /// <summary>
        /// Analyzes safety datasheet (MIXTURE document type).
        /// Uses safety datasheet prompt loaded from Azure.
        /// Extracts hazard statements, UN numbers, storage classifications, etc.
        /// Routes to text or image analysis based on PDF content type.
        /// </summary>
        private async Task<string> AnalyzeSafetyDatasheetAsync(PDFProcessor.PDFContent pdfContent)
        {
            string prompt = await GetSafetyDatasheetPromptAsync();

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
        /// Generates part number decoding prompt with template variable replacement.
        /// Uses prompt from Azure if available, otherwise uses minimal fallback.
        /// Replaces {PART_NUMBER}, {MANUFACTURER}, {COMPONENT_CATEGORY} with actual values.
        /// </summary>
        private string GeneratePartNumberDecodingPrompt(string partNumber, string manufacturer, string componentCategory)
        {
            // Use template from Azure if available
            string basePrompt = _cachedPartNumberDecodingPrompt;
            
            if (!string.IsNullOrEmpty(basePrompt))
            {
                // Template variable replacement
                basePrompt = basePrompt.Replace("{PART_NUMBER}", partNumber);
                basePrompt = basePrompt.Replace("{MANUFACTURER}", manufacturer);
                basePrompt = basePrompt.Replace("{COMPONENT_CATEGORY}", componentCategory);
                return basePrompt;
            }

            // Minimal fallback (only if Azure unreachable)
            return $@"Decode part number ""{partNumber}"" for {componentCategory} from {manufacturer}.
Extract: ResistaOhm, TolerancePct, PowerW, PackageType, TempCoeffPPM, MaxOperatingTemp, MaxOverloadVoltage
Return JSON with DecodingSuccess (true/false) and extracted values.";
        }

        #endregion

        #region Azure OpenAI Communication

        /// <summary>
        /// Analyzes text content using Azure OpenAI.
        /// Creates request body with text content and sends to Azure API.
        /// </summary>
        private async Task<string> AnalyzeTextContentAsync(string textContent, string prompt)
        {
            var requestBody = CreateTextRequestBody(textContent, prompt);
            return await MakeApiCallAsync(requestBody);
        }

        /// <summary>
        /// Analyzes image content using Azure OpenAI vision capabilities.
        /// Creates request body with base64 images and sends to Azure API.
        /// Throws exception if no images provided.
        /// </summary>
        private async Task<string> AnalyzeImageContentAsync(List<string> base64Images, string prompt)
        {
            if (base64Images == null || base64Images.Count == 0)
            {
                throw new InvalidOperationException("No images available for analysis.");
            }

            var requestBody = CreateImageRequestBody(base64Images, prompt);
            return await MakeApiCallAsync(requestBody);
        }

        /// <summary>
        /// Creates Azure OpenAI request body for text-based analysis.
        /// Combines prompt with document content.
        /// Uses temperature=0 for deterministic results.
        /// Forces JSON response format.
        /// </summary>
        private string CreateTextRequestBody(string textContent, string prompt)
        {
            var message = new
            {
                role = "user",
                content = prompt + "\n\nDocument Content:\n" + textContent
            };

            var requestBody = new
            {
                messages = new[] { message },
                max_completion_tokens = 16000,
                temperature = 0,
                response_format = new { type = "json_object" }
            };

            return JsonConvert.SerializeObject(requestBody);
        }

        /// <summary>
        /// Creates Azure OpenAI request body for image-based analysis.
        /// Constructs multi-part content with text prompt and base64 images.
        /// Limits to maximum 10 images for API constraints.
        /// Uses temperature=0 for deterministic results.
        /// </summary>
        private string CreateImageRequestBody(List<string> base64Images, string prompt)
        {
            var content = new List<object>();

            // Add text prompt
            content.Add(new
            {
                type = "text",
                text = prompt
            });

            // Add images (max 10)
            int imageCount = Math.Min(base64Images.Count, 10);

            for (int i = 0; i < imageCount; i++)
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = base64Images[i],
                        detail = "high"
                    }
                });
            }

            // Construct message
            var message = new
            {
                role = "user",
                content
            };

            var requestBody = new
            {
                messages = new[] { message },
                max_tokens = 4096,
                temperature = 0
            };

            return JsonConvert.SerializeObject(requestBody);
        }

        /// <summary>
        /// Executes API call to Azure OpenAI.
        /// Handles authentication, error responses, and token usage tracking.
        /// Throws HttpRequestException if API call fails.
        /// Invokes TokenStatsCallback for token usage monitoring.
        /// </summary>
        private async Task<string> MakeApiCallAsync(string requestBodyJson)
        {
            try
            {
                // Construct Azure OpenAI endpoint URL
                string url = $"{_config.GetEndpoint()}/openai/deployments/{_config.GetDeploymentName()}/chat/completions?api-version={_config.GetApiVersion()}";

                // Create HTTP request with API key authentication
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("api-key", _config.GetApiKey());
                request.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");

                // Send request to Azure
                var response = await _client.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                // Check if request was successful
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Azure API Error: {response.StatusCode} - {responseBody}");
                }

                // Parse response
                var jsonResponse = JObject.Parse(responseBody);

                // Extract and report token usage
                var usage = jsonResponse["usage"];
                if (usage != null)
                {
                    int promptTokens = usage["prompt_tokens"]?.Value<int>() ?? 0;
                    int completionTokens = usage["completion_tokens"]?.Value<int>() ?? 0;

                    TokenStatsCallback?.Invoke(promptTokens, completionTokens);
                }

                // Extract content from response
                string result = jsonResponse["choices"]?[0]?["message"]?["content"]?.Value<string>() ?? "";

                return result;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception during Azure API call: {e.Message}");
                throw;
            }
        }

        #endregion
    }
}
