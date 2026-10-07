using DatasheetAnalyzer.Core.Config;
using DatasheetAnalyzer.Core.Util;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace DatasheetAnalyzer.Core.Services
{
    /// <summary>
    /// Service for analyzing technical datasheets using Azure OpenAI.
    /// Simplified version for Blazor with core functionality.
    /// </summary>
    public class DatasheetAnalysisService
    {
        private readonly AzureConfig _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IRemoteConfigService _remoteConfigService;
        private readonly DMSCatalogService _catalogService;
        private readonly ILogger<DatasheetAnalysisService> _logger;
        
        private string? _cachedIdentificationPrompt;
        private string? _cachedSafetyDatasheetPrompt;

        public delegate void TokenStatsHandler(int promptTokens, int completionTokens);
        public event TokenStatsHandler? TokenStatsCallback;

        private const string FALLBACK_IDENTIFICATION_PROMPT = @"Analyze this technical document and identify:
1. Document Type (COMPONENT or MIXTURE)
2. Component Category (be specific!)
3. Part Number(s)
4. Manufacturer

Return JSON with: DocumentType, ComponentCategory, ComponentName, ManufacturerInfo, PartNumber, ConfidenceLevel";

        public DatasheetAnalysisService(
            AzureConfig config,
            IHttpClientFactory httpClientFactory,
            IRemoteConfigService remoteConfigService,
            DMSCatalogService catalogService,
            ILogger<DatasheetAnalysisService> logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _remoteConfigService = remoteConfigService ?? throw new ArgumentNullException(nameof(remoteConfigService));
            _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Public API Methods

        /// <summary>
        /// Erzeugt aus dem bereits extrahierten Datenblatt-Text ein SKILL.md
        /// (AI-Skill im Markdown-Format). Nutzt denselben Azure-OpenAI-Endpoint
        /// wie die uebrige Analyse, aber OHNE response_format=json_object, weil
        /// die Ausgabe reines Markdown ist.
        /// </summary>
        public async Task<string> GenerateSkillMarkdownAsync(string datasheetText)
        {
            if (string.IsNullOrWhiteSpace(datasheetText))
                throw new InvalidOperationException("PDF-Text ist leer - keine Grundlage fuer SKILL.md.");

            const string prompt =
                "Create a complete skill from this extracted datasheet as a Markdown file. " +
                "Include all tables, technical data, pin descriptions, limit values, application notes, " +
                "response rules, example dialogues, and test questions. " +
                "Do not invent any values. Mark unclear extractions as 'unclear'. " +
                "The Markdown file must be in English. " +
                "Return ONLY the Markdown content (no code fences, no explanations).";

            var message = new
            {
                role = "user",
                content = prompt + "\n\nDocument Content:\n" + datasheetText
            };

            var requestBody = new
            {
                messages = new[] { message },
                max_completion_tokens = 16000
                // kein response_format: wir wollen Markdown, kein JSON.
            };

            string requestJson = JsonConvert.SerializeObject(requestBody);
            return await MakeApiCallAsync(requestJson);
        }

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
                _logger.LogError(ex, "Error in Stage 1 (Identification)");
                throw;
            }
        }

        public async Task<string> AnalyzeWithConfirmedIdentificationAsync(PDFProcessor.PDFContent pdfContent, JObject confirmedIdentification)
        {
            try
            {
                string documentType = confirmedIdentification["DocumentType"]?.Value<string>() ?? "UNKNOWN";
                string componentCategory = confirmedIdentification["ComponentCategory"]?.Value<string>() ?? "";
                string componentName = confirmedIdentification["ComponentName"]?.Value<string>() ?? "";
                string? partNumber = confirmedIdentification["PartNumber"]?.Value<string>();

                if (documentType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase))
                {
                    return await AnalyzeSafetyDatasheetAsync(pdfContent);
                }
                else if (documentType.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase))
                {
                    var component = FindMatchingComponent(componentCategory, componentName);

                    if (component == null)
                    {
                        string errorMsg = $"No matching component found for category: '{componentCategory}'";
                        _logger.LogError(errorMsg);
                        throw new InvalidOperationException(errorMsg);
                    }

                    var properties = _catalogService.GetPropertiesForComponent(component.FullPath);
                    if (properties.Count == 0)
                    {
                        properties = _catalogService.GetPropertiesForComponent(component.InternalName);
                    }

                    if (properties.Count == 0)
                    {
                        throw new InvalidOperationException($"Component '{component.DisplayName}' has no properties defined");
                    }

                    _logger.LogInformation("Property schema found: {ComponentName} ({PropertyCount} properties)", 
                        component.DisplayName, properties.Count);

                    // Bei Components mit Part Number -> Part Number Decoding + PDF-Extraktion kombinieren
                    bool hasPartNumberForDecoding = RequiresPartNumberDecoding(component) && !string.IsNullOrWhiteSpace(partNumber);
                    
                    JObject? decodedProperties = null;
                    if (hasPartNumberForDecoding)
                    {
                        _logger.LogInformation("===========================================");
                        _logger.LogInformation("{ComponentType} WITH PART NUMBER DETECTED", component.DisplayName.ToUpperInvariant());
                        _logger.LogInformation("===========================================");
                        _logger.LogInformation("Component: {DisplayName}", component.DisplayName);
                        _logger.LogInformation("Part Number: {PartNumber}", partNumber);
                        _logger.LogInformation("Mode: HYBRID (Part Number Decoding + PDF Extraction)");
                        _logger.LogInformation("===========================================");
                        
                        // Dekodiere Part Number (generisch für Memory + Interface IC)
                        decodedProperties = await DecodePartNumberAsync(pdfContent, partNumber, component);
                        
                        if (decodedProperties != null)
                        {
                            _logger.LogInformation("Part Number Decoding successful:");
                            // Logge alle dekodierten Properties
                            foreach (var prop in decodedProperties.Properties())
                            {
                                _logger.LogInformation("   {PropertyName}: {Value}", prop.Name, prop.Value ?? "N/A");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Part Number Decoding failed - falling back to PDF-only extraction");
                        }
                    }
                    
                    // PDF-Extraktion durchführen
                    string extractionResult = await AnalyzeComponentWithCatalogAsync(pdfContent, component, partNumber);

                    try
                    {
                        var resultJson = JObject.Parse(extractionResult);
                        resultJson["_PropertySchemaUsed"] = component.DisplayName;
                        resultJson["_RequestedCategory"] = componentCategory;
                        
                        // Wenn Part Number Decoding erfolgreich war, kombiniere intelligent mit PDF-Extraktion
                        if (decodedProperties != null)
                        {
                            _logger.LogInformation("Combining Part Number Decoding results with PDF extraction");
                            
                            // Kombiniere dekodierte Properties mit PDF-Extraktion
                            // Strategie: Part Number Decoding überschreibt nur spezifische Properties (nicht Funktion!)
                            CombineDecodedPropertiesWithPdf(resultJson, decodedProperties, component);

                            _logger.LogInformation("Hybrid combination complete");
                        }

                        // ===========================================
                        // BESTÄTIGTE IDENTIFIKATION ÜBERNEHMEN
                        // ===========================================
                        // Vom User im Identification-Dialog bestätigte Werte
                        // (PartNumber, Manufacturer, Description) müssen IMMER im
                        // Endergebnis landen – die KI-Extraktion darf sie nicht
                        // mit "nicht vorhanden" überschreiben.
                        ApplyConfirmedIdentificationFields(resultJson, confirmedIdentification);

                        // FINALE NORMALISIERUNG: Alias-Mapping (z.B. tAA_Max -> TaaMaxS, Voltage -> VccVddTypV)
                        // + Schema-Whitelist: alles was nicht im Schema steht wird entfernt.
                        NormalizeAndFilterToSchema(resultJson, properties, component);

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
                _logger.LogError(ex, "Error in Stage 2 (Extraction)");
                throw;
            }
        }

        /// <summary>
        /// Dekodiert Part Number für Components mit Series Datasheets (Memory, Interface IC, etc.)
        /// STAGE 1: Extrahiert Part Number Chart Struktur aus dem Datasheet
        /// STAGE 2: Dekodiert spezifische Part Number basierend auf Chart
        /// </summary>
        private async Task<JObject?> DecodePartNumberAsync(PDFProcessor.PDFContent pdfContent, string partNumber, CatalogComponent component)
        {
            // Bestimme den richtigen Prompt-Typ basierend auf Komponente (vor try-catch, damit im catch verfügbar)
            string componentType = GetPartNumberDecodingType(component);
            
            try
            {
                _logger.LogInformation("===========================================");
                _logger.LogInformation("{ComponentType} PART NUMBER DECODING (2-STAGE)", component.DisplayName.ToUpperInvariant());
                _logger.LogInformation("===========================================");
                _logger.LogInformation("Component: {DisplayName}", component.DisplayName);
                _logger.LogInformation("Part Number: {PartNumber}", partNumber);
                
                // ===========================================
                // STAGE 1: Extrahiere Part Number Chart Struktur
                // ===========================================
                _logger.LogInformation("STAGE 1: Extracting Part Number Chart structure...");
                
                string seriesPrompt;
                try
                {
                    string seriesPromptFile = $"SeriesDatasheetInstructions_{componentType}.txt";
                    _logger.LogDebug("Loading {PromptFile} from Azure", seriesPromptFile);
                    seriesPrompt = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent(seriesPromptFile));
                    _logger.LogInformation("Series instructions loaded ({Length} characters)", seriesPrompt.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not load series instructions for {ComponentType} - skipping decoding", componentType);
                    return null;
                }
                
                // KI-Call für Chart Extraktion (Stage 1)
                string chartResult;
                if (pdfContent.IsTextBased)
                {
                    chartResult = await AnalyzeTextContentAsync(pdfContent.TextContent, seriesPrompt);
                }
                else
                {
                    chartResult = await AnalyzeImageContentAsync(pdfContent.Base64Images, seriesPrompt);
                }
                
                var chartJson = JObject.Parse(chartResult);
                _logger.LogInformation("Stage 1 complete: Part Number Chart extracted");
                
                // Prüfe ob Chart gefunden wurde.
                // ACHTUNG: Auch ohne Chart wird Stage 2 weiterhin versucht – der Decoder
                // kann anhand der PartNumber + dem Datenblatt-Inhalt häufig auch ohne
                // formale Decoder-Tabelle Properties ableiten (typischerweise bei
                // eMMC/UFS, wo Kapazität/Package direkt in den Features stehen).
                bool chartFound = chartJson["ChartFound"]?.Value<bool>() ?? false;
                if (!chartFound)
                {
                    _logger.LogWarning("Part Number Chart not found in datasheet - trying chart-less decoding fallback");
                    chartJson = new JObject
                    {
                        ["ChartFound"] = false,
                        ["Note"] = "No formal Part Number Decoder table was found in the datasheet. "
                                 + "Use the part number itself, ordering tables, feature lists and "
                                 + "any package/capacity info from the document to derive the variant-specific properties."
                    };
                }
                
                // ===========================================
                // STAGE 2: Dekodiere spezifische Part Number
                // ===========================================
                _logger.LogInformation("STAGE 2: Decoding specific part number...");
                
                string decodingPrompt;
                try
                {
                    string decodingPromptFile = $"PartNumberDecodingPrompt_{componentType}.txt";
                    _logger.LogDebug("Loading {PromptFile} from Azure", decodingPromptFile);
                    decodingPrompt = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent(decodingPromptFile));
                    _logger.LogInformation("{ComponentType} Part Number decoding prompt loaded ({Length} characters)", componentType, decodingPrompt.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not load decoding prompt for {ComponentType} - skipping decoding", componentType);
                    return null;
                }
                
                // Ersetze ALLE Platzhalter (inklusive Part Number Chart Struktur!)
                decodingPrompt = decodingPrompt.Replace("{PART_NUMBER}", partNumber);
                decodingPrompt = decodingPrompt.Replace("{COMPONENT_CATEGORY}", component.DisplayName);
                decodingPrompt = decodingPrompt.Replace("{PART_NUMBER_STRUCTURE}", chartJson.ToString(Formatting.Indented));
                
                // KI-Call für Dekodierung (Stage 2)
                string decodingResult;
                if (pdfContent.IsTextBased)
                {
                    decodingResult = await AnalyzeTextContentAsync(pdfContent.TextContent, decodingPrompt);
                }
                else
                {
                    decodingResult = await AnalyzeImageContentAsync(pdfContent.Base64Images, decodingPrompt);
                }
                
                // Parse Ergebnis
                var decodedJson = JObject.Parse(decodingResult);
                _logger.LogDebug("Decoding result: {Result}", decodedJson.ToString(Formatting.None));
                
                // Extrahiere relevante Properties
                var extractedProps = decodedJson["ExtractedProperties"] as JObject;
                if (extractedProps == null)
                {
                    _logger.LogWarning("No ExtractedProperties in decoding result");
                    return null;
                }
                
                _logger.LogInformation("Stage 2 complete: Part Number decoded");
                foreach (var prop in extractedProps.Properties())
                {
                    _logger.LogInformation("  {PropertyName}: {Value}", prop.Name, prop.Value ?? "N/A");
                }
                
                return extractedProps;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during part number decoding for {ComponentType} - continuing with standard extraction", componentType);
                return null;
            }
        }

        /// <summary>
        /// Prüft ob Component Part Number Decoding benötigt (Memory, Interface IC, etc.)
        /// </summary>
        private bool RequiresPartNumberDecoding(CatalogComponent component)
        {
            var keywords = new[]
            {
                // Memory Components
                "Non-Volatile Memory Serial",
                "Non-Volatile Memory Parallel",
                "NV Memory Serial",
                "NV Memory Parallel",
                "EEPROM Serial",
                "EEPROM Parallel",
                "Flash Serial",
                "Flash Parallel",
                "SRAM",
                // Interface ICs
                "Interface IC"
            };
            
            return keywords.Any(keyword =>
                component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Bestimmt den Komponententyp für Part Number Decoding (für Prompt-Auswahl)
        /// </summary>
        private string GetPartNumberDecodingType(CatalogComponent component)
        {
            // Memory Components ? "Memory"
            var memoryKeywords = new[]
            {
                "Non-Volatile Memory", "NV Memory", "EEPROM", "Flash", "SRAM"
            };
            
            if (memoryKeywords.Any(kw => 
                component.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                return "Memory";
            }
            
            // Interface IC ? "InterfaceIC"
            if (component.DisplayName.Contains("Interface IC", StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains("Interface IC", StringComparison.OrdinalIgnoreCase))
            {
                return "InterfaceIC";
            }
            
            // Fallback: Verwende DisplayName
            return component.DisplayName.Replace(" ", "");
        }
        
        /// <summary>
        /// Kombiniert dekodierte Properties mit PDF-Extraktion
        /// Strategie: Part Number Decoding überschreibt spezifische Properties, aber NICHT "Funktion"
        /// </summary>
        private void CombineDecodedPropertiesWithPdf(JObject resultJson, JObject decodedProperties, CatalogComponent component)
        {
            // Properties die aus Part Number Decoding bevorzugt werden (präziser!)
            var preferDecoded = new[] { "SizeByte", "Width", "PackageType", "Interface_internal", "Interface_external" };
            
            // Properties die aus PDF bevorzugt werden (spezifischer für exakte Variante)
            var preferPdf = new[] { "Funktion", "NumberOfPin", "Pitch", "tAA_Max", "tRC_Min" };
            
            // "Leere" Werte aus PDF-Extraktion - bei diesen MUSS der Decoded-Fallback greifen
            var emptyMarkers = new[] { "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar", "mehrdeutig", "nicht relevant", "n/a", "-" };
            bool IsEmptyValue(string? v) => string.IsNullOrWhiteSpace(v)
                || emptyMarkers.Any(m => string.Equals(v.Trim(), m, StringComparison.OrdinalIgnoreCase));
            
            foreach (var prop in decodedProperties.Properties())
            {
                string propName = prop.Name;
                string? decodedValue = prop.Value?.Value<string>();
                
                if (IsEmptyValue(decodedValue))
                    continue;
                
                // Funktion IMMER aus PDF behalten (Dropdown-Klassifikation!) - aber nur wenn PDF Wert hat
                if (propName.Equals("Funktion", StringComparison.OrdinalIgnoreCase))
                {
                    string? pdfFunktion = resultJson[propName]?.Value<string>();
                    if (!IsEmptyValue(pdfFunktion))
                    {
                        _logger.LogDebug("  Skipping {PropertyName} (PDF classification preferred)", propName);
                        continue;
                    }
                    // PDF hatte keine Funktion -> nutze decoded als Fallback
                    resultJson[propName] = decodedValue;
                    _logger.LogDebug("  Using decoded {PropertyName} (PDF had no value): {Value}", propName, decodedValue);
                    continue;
                }
                
                // Properties die aus Part Number Decoding bevorzugt werden
                if (preferDecoded.Contains(propName, StringComparer.OrdinalIgnoreCase))
                {
                    resultJson[propName] = decodedValue;
                    _logger.LogDebug("  Using decoded {PropertyName}: {Value}", propName, decodedValue);
                    continue;
                }
                
                // Für alle anderen (inkl. preferPdf): Nur überschreiben wenn PDF keinen brauchbaren Wert hat
                string? pdfValue = resultJson[propName]?.Value<string>();
                if (IsEmptyValue(pdfValue))
                {
                    resultJson[propName] = decodedValue;
                    _logger.LogDebug("  Using decoded {PropertyName} (PDF had no value '{PdfValue}'): {Value}",
                        propName, pdfValue ?? "<null>", decodedValue);
                }
                else
                {
                    _logger.LogDebug("  Using PDF {PropertyName}: {Value}", propName, pdfValue);
                }
            }
            
            // Bereinige alternative Property-Namen (z.B. "Size" wenn "SizeByte" existiert)
            resultJson.Remove("Size");
            resultJson.Remove("WidthMax");
        }

        /// <summary>
        /// Synonym-Gruppen: equivalente Bezeichnungen fuer dieselbe physikalische Eigenschaft.
        /// Innerhalb jeder Gruppe findet <see cref="NormalizeAndFilterToSchema"/> automatisch
        /// den tatsaechlichen Schema-Namen (aus get_classes.csv) und mappt alle anderen
        /// Bezeichner darauf. Dadurch ist es egal, ob das Schema "Width" oder "WidthMax"
        /// heisst und ob das Modell "Width", "DataWidth" o.ae. zurueckgibt.
        /// </summary>
        private static readonly string[][] PropertySynonyms = new[]
        {
            // Memory width / data bus width
            new[] { "Width", "WidthMax", "WidthBit", "WidthMaxBit", "Width_Max", "DataWidth", "BusWidth", "DataBusWidth", "Organization" },
            // Address access time
            new[] { "TaaMaxS", "tAA_Max", "tAA", "tAAMax", "AddressAccessTime", "AddressAccessTimeMax" },
            new[] { "TaaMinS", "tAA_Min", "tAAMin" },
            // Read cycle time
            new[] { "TrcMinS", "tRC_Min", "tRC", "tRCMin", "ReadCycleTime", "ReadCycleTimeMin" },
            // Supply voltage
            new[] { "VccVddTypV", "Voltage", "Vcc", "Vdd", "VccVdd", "SupplyVoltage", "SupplyVoltageTyp" },
            new[] { "VccVddMinV", "VccMin", "VddMin", "SupplyVoltageMin" },
            new[] { "VccVddMaxV", "VccMax", "VddMax", "SupplyVoltageMax" },
            // Memory size
            new[] { "SizeByte", "Size", "MemorySize", "Capacity", "Density" },
            // Pin count
            new[] { "NumberOfPin", "NumberOfPins", "PinCount", "Pins", "BallCount", "NumberOfBalls" },
            // Function / memory technology
            new[] { "Function", "Funktion", "MemoryType", "MemoryTechnology", "Type" },
            // Package
            new[] { "PackageType", "Package", "PackageName" },
        };

        /// <summary>
        /// Meta-Felder, die NICHT aus dem Property-Schema kommen, aber im Ergebnis erlaubt sind.
        /// </summary>
        private static readonly HashSet<string> AllowedMetaProperties =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "ComponentType", "PartNumber", "Manufacturer", "ManufacturerInfo",
                "Description", "DocumentType", "ComponentCategory", "ComponentName",
                "_PropertySchemaUsed", "_RequestedCategory",
                "WasCorrected", "OriginalCategory", "CorrectionTimestamp",
                "IsSeriesDatasheet", "CategoryWasChanged"
            };

        /// <summary>
        /// 1) Mappt Synonym-Felder auf den tatsaechlichen Schema-Namen
        ///    (Schema-Wert wird nur ueberschrieben, wenn er leer / "nicht vorhanden" ist).
        /// 2) Entfernt anschliessend alle Felder, die weder im Property-Schema
        ///    noch in <see cref="AllowedMetaProperties"/> stehen.
        /// </summary>
        private void NormalizeAndFilterToSchema(JObject resultJson, IList<ComponentProperty> properties, CatalogComponent? component = null)
        {
            var emptyMarkers = new[] { "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar", "mehrdeutig", "nicht relevant", "n/a", "-" };
            bool IsEmpty(JToken? token)
            {
                if (token == null || token.Type == JTokenType.Null) return true;
                var s = token.Value<string>();
                if (string.IsNullOrWhiteSpace(s)) return true;
                return emptyMarkers.Any(m => string.Equals(s.Trim(), m, StringComparison.OrdinalIgnoreCase));
            }

            // Schema-Whitelist (case-insensitive) - genau das, was get_classes.csv liefert
            var schemaNames = new HashSet<string>(
                properties.Select(p => p.PropertyInternalName).Where(n => !string.IsNullOrWhiteSpace(n)),
                StringComparer.OrdinalIgnoreCase);

            // -------- Phase 0: Varianten-Suffix-Konsolidierung --------
            // Manche Datenblaetter (typisch bei Multi-Package Memory-ICs)
            // beschreiben mehrere Package-/Varianten in einem Dokument.
            // Das Modell tendiert dann dazu, pro Variante eigene Felder zu
            // liefern:
            //     "NumberOfPin_1": "28", "NumberOfPin_2": "48",
            //     "PackageType_1": "SOIC", "PackageType_2": "TSOP",
            //     "SizeByte_1": "32 KB",  "SizeByte_2": "32 KB", ...
            // Diese _1/_2/_3-Felder stehen NICHT im Schema und wuerden vom
            // Whitelist-Filter (Phase 2) verworfen -> im Ergebnis waeren
            // dann alle Werte leer, obwohl das Modell sie eigentlich hatte.
            //
            // Loesung: Alle "<Feld>_<Zahl>"-Felder auf "<Feld>" konsolidieren.
            // Regel: ersten nicht-leeren Wert nehmen (deterministisch nach
            // aufsteigendem Suffix), Rest der Suffix-Felder entfernen.
            var suffixRegex = new System.Text.RegularExpressions.Regex(
                @"^(?<base>.+?)_(?<n>\d+)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            var suffixGroups = new Dictionary<string, List<(int Idx, string Prop)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in resultJson.Properties())
            {
                var m = suffixRegex.Match(prop.Name);
                if (!m.Success) continue;
                string baseName = m.Groups["base"].Value;
                int idx = int.Parse(m.Groups["n"].Value);
                if (!suffixGroups.TryGetValue(baseName, out var list))
                    suffixGroups[baseName] = list = new List<(int, string)>();
                list.Add((idx, prop.Name));
            }
            foreach (var kv in suffixGroups)
            {
                string baseName = kv.Key;
                var ordered = kv.Value.OrderBy(x => x.Idx).ToList();

                // Wenn das Basisfeld bereits gefuellt ist, nur die Suffix-Felder entfernen.
                bool baseAlreadyFilled = !IsEmpty(resultJson[baseName]);

                if (!baseAlreadyFilled)
                {
                    foreach (var (_, name) in ordered)
                    {
                        var token = resultJson[name];
                        if (IsEmpty(token)) continue;
                        var value = token!.Value<string>();
                        resultJson[baseName] = value;
                        _logger.LogInformation(
                            "  Varianten-Konsolidierung: '{Suffix}' -> '{Base}' = '{Value}'",
                            name, baseName, value);
                        break;
                    }
                }

                foreach (var (_, name) in ordered)
                    resultJson.Remove(name);
            }

            // -------- Phase 1: Synonym-Mapping --------
            // Pro Synonym-Gruppe: finde den Namen, der WIRKLICH im Schema steht,
            // und uebernimm Werte aus jedem anderen Synonym, falls Schema-Feld leer.
            foreach (var family in PropertySynonyms)
            {
                string? schemaName = family.FirstOrDefault(n => schemaNames.Contains(n));
                if (schemaName == null)
                    continue; // keiner aus dieser Familie ist im Schema -> nichts zu tun

                if (!IsEmpty(resultJson[schemaName]))
                    continue; // Schema-Feld bereits gefuellt -> nicht ueberschreiben

                foreach (var alias in family)
                {
                    if (string.Equals(alias, schemaName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var aliasToken = resultJson[alias];
                    if (IsEmpty(aliasToken)) continue;

                    var value = aliasToken!.Value<string>();
                    resultJson[schemaName] = value;
                    _logger.LogDebug("  Synonym-Mapping: '{Alias}' -> '{SchemaName}' = '{Value}'",
                        alias, schemaName, value);
                    break;
                }
            }

            // -------- Phase 1b: Spezial-Fallback NumberOfPin aus PackageType ableiten --------
            // Beispiele: "48-ball FBGA" -> 48, "TSOP-56" -> 56, "LBGA-64" -> 64, "SOIC-8" -> 8
            string? pinSchemaName = new[] { "NumberOfPin", "NumberOfPins" }
                .FirstOrDefault(n => schemaNames.Contains(n));
            if (pinSchemaName != null && IsEmpty(resultJson[pinSchemaName]))
            {
                var pkg = resultJson["PackageType"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(pkg))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        pkg, @"(?<n>\d{1,4})\s*(?:-?\s*(?:ball|pin|lead))?",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!match.Success)
                        match = System.Text.RegularExpressions.Regex.Match(pkg, @"\d{1,4}");
                    if (match.Success && int.TryParse(match.Groups["n"].Success ? match.Groups["n"].Value : match.Value, out int pinCount) && pinCount > 0)
                    {
                        resultJson[pinSchemaName] = pinCount.ToString();
                        _logger.LogDebug("  {Field} aus PackageType '{Pkg}' abgeleitet: {Pins}", pinSchemaName, pkg, pinCount);
                    }
                }
            }

            // -------- Phase 1c: Wert-Normalisierung 'Function' fuer Memory Serial --------
            // DMS-Dropdown fuer Non-Volatile Memory Serial akzeptiert nur:
            //   EEPROM, PROM, ROM, Flash, FRAM, MRAM, eMMC
            // Falls das Modell trotz Prompt z.B. 'NAND Flash SLC' liefert,
            // mappen wir hier auf den erlaubten Sammelwert.
            if (component != null && IsMemorySerialComponent(component))
            {
                string? functionField = new[] { "Function", "Funktion" }
                    .FirstOrDefault(n => schemaNames.Contains(n));
                if (functionField != null)
                {
                    var current = resultJson[functionField]?.Value<string>();
                    var mapped = NormalizeMemorySerialFunctionValue(current);
                    if (mapped != null && !string.Equals(mapped, current, StringComparison.Ordinal))
                    {
                        _logger.LogDebug("  Function-Wert '{Old}' -> DMS-Dropdown '{New}' (Memory Serial)", current, mapped);
                        resultJson[functionField] = mapped;
                    }
                }
            }

            // -------- Phase 2: Schema-Whitelist anwenden --------
            var allowed = new HashSet<string>(schemaNames, StringComparer.OrdinalIgnoreCase);
            foreach (var meta in AllowedMetaProperties)
                allowed.Add(meta);

            var keysToRemove = resultJson.Properties()
                .Select(p => p.Name)
                .Where(name => !allowed.Contains(name))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _logger.LogDebug("  Removing non-schema property '{Key}'", key);
                resultJson.Remove(key);
            }
        }

        #endregion

        #region Azure Prompt Loading

        private async Task<string> GetIdentificationPromptAsync()
        {
            if (_cachedIdentificationPrompt == null)
            {
                try
                {
                    _logger.LogDebug("Loading IdentificationPrompt.txt from Azure");
                    _cachedIdentificationPrompt = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent("IdentificationPrompt.txt"));
                    _logger.LogInformation("Identification prompt loaded ({Length} characters)", _cachedIdentificationPrompt.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error loading IdentificationPrompt.txt, using fallback");
                    _cachedIdentificationPrompt = FALLBACK_IDENTIFICATION_PROMPT;
                }
            }
            return _cachedIdentificationPrompt;
        }

        private async Task<string> GetSafetyDatasheetPromptAsync()
        {
            if (_cachedSafetyDatasheetPrompt == null)
            {
                try
                {
                    _logger.LogDebug("Loading SafetyDatasheetPrompt.txt from Azure");
                    _cachedSafetyDatasheetPrompt = await Task.Run(() => 
                        _remoteConfigService.LoadBlobContent("SafetyDatasheetPrompt.txt"));
                    _logger.LogInformation("Safety datasheet prompt loaded");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error loading SafetyDatasheetPrompt.txt");
                    _cachedSafetyDatasheetPrompt = "Extract safety data from SDS. Return compact JSON.";
                }
            }
            return _cachedSafetyDatasheetPrompt;
        }

        #endregion

        #region Helper Methods

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

        private CatalogComponent? FindMatchingComponent(string category, string name)
        {
            if (string.IsNullOrWhiteSpace(category))
                return null;

            if (category.Equals("Amplifier", StringComparison.OrdinalIgnoreCase))
            {
                var opAmp = _catalogService.FindComponent("OP Amplifier");
                if (opAmp != null)
                {
                    var props = _catalogService.GetPropertiesForComponent(opAmp.FullPath);
                    if (props.Count == 0) props = _catalogService.GetPropertiesForComponent(opAmp.InternalName);
                    if (props.Count > 0) return opAmp;
                }
            }

            var match = _catalogService.FindComponentByPropertyKey(category);
            if (match != null) return match;

            match = _catalogService.FindComponent(category);
            if (match == null && !string.IsNullOrWhiteSpace(name))
            {
                match = _catalogService.FindComponent(name);
            }

            return match;
        }

        /// <summary>
        /// Prüft ob Komponente Vision API benötigt (für technische Zeichnungen)
        /// </summary>
        private bool RequiresVisionAnalysis(CatalogComponent component, PDFProcessor.PDFContent pdfContent)
        {
            if (pdfContent.Base64Images == null || pdfContent.Base64Images.Count == 0)
                return false;

            var visionRequiredComponents = new[]
            {
                "RF Amplifier", "Amplifier", "OP Amplifier", "Power Amplifier",
                "Connector", "IC", "Microcontroller",
                "Non-Volatile Memory Serial", "Non-Volatile Memory Parallel",
                "NV Memory", "Memory",
                "Interface IC"  // Pitch + NumberOfPin sind oft nur in Package Drawings sichtbar
            };

            bool isVisionRequiredComponent = visionRequiredComponents.Any(keyword =>
                component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (isVisionRequiredComponent)
            {
                _logger.LogDebug("Component {Name} requires vision analysis for technical drawings",
                    component.DisplayName);
                return true;
            }

            var properties = _catalogService.GetPropertiesForComponent(component.FullPath);
            if (properties.Count == 0)
                properties = _catalogService.GetPropertiesForComponent(component.InternalName);

            bool hasPitchProperty = properties.Any(p =>
                p.PropertyInternalName.Equals("Pitch", StringComparison.OrdinalIgnoreCase));

            if (hasPitchProperty)
            {
                _logger.LogDebug("Component {Name} has Pitch property - requires vision analysis",
                    component.DisplayName);
                return true;
            }

            if (pdfContent.IsTextBased && pdfContent.Base64Images.Count >= 5)
            {
                _logger.LogDebug("PDF has {ImageCount} images - enabling hybrid mode for better accuracy",
                    pdfContent.Base64Images.Count);
                return true;
            }

            return false;
        }

        private async Task<string> AnalyzeComponentWithCatalogAsync(PDFProcessor.PDFContent pdfContent, CatalogComponent component, string? partNumber)
        {
            string dynamicPrompt = _catalogService.GenerateDynamicPrompt(component);

            // Wenn der User eine konkrete PartNumber bestaetigt/eingetragen hat,
            // MUSS die Extraktion sich exakt auf diese Variante beziehen.
            // Ohne diesen Block waehlt das Modell bei Serien-Datenblaettern
            // (Part-Number-Matrix) haeufig eine falsche Zeile.
            if (!string.IsNullOrWhiteSpace(partNumber))
            {
                string pnBlock =
                    "\n\n" +
                    "????????????????????????????????????????????????????????????????\n" +
                    "MANDATORY VARIANT SELECTION (HIGHEST PRIORITY)\n" +
                    "????????????????????????????????????????????????????????????????\n" +
                    $"The user has explicitly selected the following manufacturer part number:\n" +
                    $"    ? {partNumber}\n" +
                    "\n" +
                    "STEP 0 – CLASSIFY THE DATASHEET FIRST (do this before anything else):\n" +
                    "  (A) SINGLE-PRODUCT datasheet: the datasheet describes exactly ONE\n" +
                    "      device / base part number. There is NO variant matrix that\n" +
                    "      changes electrical values per row. Typical indicators:\n" +
                    "        - Only ONE base part number is printed on the title page.\n" +
                    "        - The 'Ordering Information' table only lists packaging /\n" +
                    "          marking / tape-and-reel codes (e.g. 'E6327', 'FTSA1',\n" +
                    "          'TR', 'T7', '-7', 'REEL', 'TAPE', 'TRAY') for the SAME\n" +
                    "          electrical device.\n" +
                    "        - All electrical values are given ONCE in 'Features',\n" +
                    "          'Electrical Characteristics' or 'Specifications'.\n" +
                    "      ? In this case: IGNORE any packaging/marking suffix in the\n" +
                    "        user's part number (parenthesised suffixes such as\n" +
                    $"        '(E6327)', '(FTSA1)' in '{partNumber}' are ORDERING codes,\n" +
                    "        NOT electrical variants) and extract ALL values from the\n" +
                    "        general Features / Electrical Characteristics section.\n" +
                    "        DO NOT return 'nicht klar erkennbar' just because the exact\n" +
                    "        parenthesised suffix is not repeated next to every value.\n" +
                    "  (B) MULTI-VARIANT datasheet: the datasheet contains a part-number\n" +
                    "      matrix where DIFFERENT rows have DIFFERENT electrical values\n" +
                    "      (e.g. inductance, tolerance, voltage class, frequency band).\n" +
                    "      ? In this case follow rules 1–7 below strictly.\n" +
                    "\n" +
                    "RULES FOR MULTI-VARIANT DATASHEETS (case B) – NON-NEGOTIABLE:\n" +
                    "  1. Locate the exact row in the matrix whose part number matches\n" +
                    $"     '{partNumber}'. Match is done character-by-character on the\n" +
                    "     base code; wildcard/placeholder characters in the datasheet\n" +
                    "     (e.g. '_', 'X', '[Y|W]', trailing packaging suffix) are ignored\n" +
                    "     ONLY if they are not part of the user's selection.\n" +
                    "  2. ALL variant-specific values (inductance, tolerance, current,\n" +
                    "     DCR, SRF, Q, frequency, height, …) MUST come from THAT row.\n" +
                    "     NEVER return values from a different row.\n" +
                    "  3. If a technical value is not per-variant but given once for the\n" +
                    "     whole series (e.g. mechanical dimensions, package type,\n" +
                    "     surface finish), take it from the shared block.\n" +
                    "  4. Only if you truly cannot locate the correct row AND the value\n" +
                    "     really differs between rows, return 'nicht klar erkennbar'\n" +
                    "     rather than guessing from a different row. Do NOT abuse this\n" +
                    "     rule for values that are shared across the whole series.\n" +
                    "  5. Apply part-number decoding using the datasheet's own\n" +
                    "     'Part Numbering' / 'Ordering Information' legend if the\n" +
                    "     variant is encoded in the part number itself (e.g. '4N7' = 4.7,\n" +
                    "     '0N5' = 0.5, tolerance letter, packaging letter).\n" +
                    "\n" +
                    "GENERAL:\n" +
                    "  6. Values printed in a 'Features' bullet list or in a general\n" +
                    "     'Electrical Characteristics' / 'Specifications' section are\n" +
                    "     VALID sources. You do NOT need a Min/Typ/Max table to accept\n" +
                    "     a value – a single stated value (e.g. 'Gain: 20 dB',\n" +
                    "     'Noise figure: 0.65 dB', 'Supply voltage: 1.5 V to 3.6 V') is\n" +
                    "     sufficient and MUST be extracted.\n" +
                    "  7. Return 'PartNumber' in the JSON exactly as given above.\n" +
                    "????????????????????????????????????????????????????????????????\n";
                dynamicPrompt = pnBlock + dynamicPrompt;
                _logger.LogInformation(
                    "VARIANT-LOCK: Extraktion fuer {ComponentName} auf PartNumber '{PartNumber}' fixiert.",
                    component.DisplayName, partNumber);
            }

            // Memory-Komponenten: Function-Klassifikation immer im Hauptprompt erzwingen
            // (auch ohne Part Number, damit "Function" nie "nicht vorhanden" bleibt).
            string? functionEnumBlock = BuildFunctionEnumBlockIfApplicable(component);
            if (!string.IsNullOrEmpty(functionEnumBlock))
            {
                dynamicPrompt += "\n\n" + functionEnumBlock;
                _logger.LogDebug("Function-Klassifikations-Block fuer {Name} an Hauptprompt angehaengt", component.DisplayName);
            }

            // ---------------------------------------------------------------
            // STRATEGIE (bewusst einfach & vorhersagbar):
            //   1. Wenn Text extrahierbar ist -> IMMER zuerst Text-Extraktion.
            //      Der Text aus iTextSharp ist praezise fuer Zahlenwerte
            //      (Frequenzen, Gain, Spannungen, Package-Groessen) und liefert
            //      auf beiden Datenblaettern reproduzierbar dieselben Werte.
            //   2. Wenn das Property-Schema mechanische Angaben enthaelt, die
            //      typisch NUR im Package-Drawing stehen ('Pitch'),
            //      dann DANACH einen kleinen, gezielten Vision-Zusatzcall
            //      machen (nur fuer diese Properties) und die Ergebnisse
            //      in das Text-Resultat mergen.
            //   3. Nur wenn ueberhaupt kein Text extrahiert werden konnte
            //      (echtes Scan-PDF), faellt die Analyse auf Vision-only zurueck.
            //
            // WICHTIG (Compare-Flow): Beide PDFs muessen ueber DENSELBEN Pfad
            // laufen, sonst kommen inkonsistente Ergebnisse (mal Text, mal
            // Vision) und der Vergleich wird unbrauchbar. Deshalb genuegt hier
            // "hat ueberhaupt Text extrahiert" - kein Mindest-Zeichen-Schwellwert.
            // ---------------------------------------------------------------

            int textLength = pdfContent.TextContent?.Length ?? 0;
            bool hasUsableText = !string.IsNullOrWhiteSpace(pdfContent.TextContent);

            // Reine Scan-PDFs ohne Text -> Vision-only
            if (!hasUsableText)
            {
                _logger.LogWarning(
                    "VISION-ONLY: {ComponentName} - kein Text extrahierbar (0 chars), {ImageCount} Bilder. "
                    + "Wenn das andere PDF Text-basiert ist, werden die Ergebnisse NICHT vergleichbar sein.",
                    component.DisplayName, pdfContent.Base64Images.Count);
                return await AnalyzeImageContentAsync(pdfContent.Base64Images, dynamicPrompt);
            }

            // Schritt 1: Text-basierte Extraktion (Haupt-Ergebnis)
            _logger.LogInformation(
                "TEXT-Extraktion: {ComponentName} - {TextLength} chars, IsTextBased={IsTextBased}",
                component.DisplayName, textLength, pdfContent.IsTextBased);
            string textResult = await AnalyzeTextContentAsync(pdfContent.TextContent, dynamicPrompt);

            // Kurzer Blick auf das Roh-Ergebnis loggen, damit man beim Compare
            // sofort sehen kann, ob das Modell fuer dieses PDF wirklich Werte
            // geliefert hat oder nur "nicht vorhanden" fuellt.
            LogExtractionSummary(textResult, component.DisplayName);

            // Schritt 2: Gezielter Vision-Zusatzcall NUR fuer mechanische
            // Properties, die im Text meist nicht vorkommen (Pitch, ...).
            var properties = _catalogService.GetPropertiesForComponent(component.FullPath);
            if (properties.Count == 0)
                properties = _catalogService.GetPropertiesForComponent(component.InternalName);

            var visionOnlyProps = GetVisionOnlyPropertyNames(properties);
            if (visionOnlyProps.Count > 0 && pdfContent.Base64Images.Count > 0)
            {
                _logger.LogInformation(
                    "VISION-ZUSATZ: {ComponentName} - Package-Drawing-Auswertung fuer {Props}",
                    component.DisplayName, string.Join(", ", visionOnlyProps));

                try
                {
                    var visionExtras = await ExtractPackageDrawingPropertiesAsync(
                        pdfContent.Base64Images, visionOnlyProps);
                    if (visionExtras != null)
                        textResult = MergeVisionExtras(textResult, visionExtras, visionOnlyProps);
                }
                catch (Exception ex)
                {
                    // Vision-Zusatz darf die Haupt-Extraktion nicht kippen.
                    _logger.LogWarning(ex,
                        "Vision-Zusatzcall fuer Package-Drawing-Properties fehlgeschlagen - Text-Ergebnis bleibt unveraendert");
                }
            }

            // Schritt 3 (VISION-RESCUE): Bei komplexen Datenblaettern zerreisst die
            // reine Textextraktion oft mehrspaltige Spezifikationstabellen
            // (Frequenz-Matrix mit Min/Typ/Max pro Zeile, Absolute Maximum
            // Ratings usw.). Das fuehrt zu "nicht vorhanden" fuer Werte, die
            // im Bild eindeutig sichtbar sind (z.B. GainTypDb, NfTypDb,
            // Op1DbDbm, IsupplyMaxA, PdissW, RfinMaxDbm).
            //
            // Wir sammeln daher ALLE Properties, die im Text leer geblieben
            // sind, und fragen sie gezielt per Vision nach. Text-Werte bleiben
            // Vorrang (MergeVisionExtras ersetzt nur leere Slots), daher gibt
            // es keine Regression fuer sauber extrahierte Datenblaetter.
            if (pdfContent.Base64Images.Count > 0)
            {
                try
                {
                    var missingProps = GetMissingPropertyNames(textResult, properties);
                    if (missingProps.Count > 0)
                    {
                        _logger.LogInformation(
                            "VISION-RESCUE: {ComponentName} - {Count} Properties fehlten im Text: {Props}",
                            component.DisplayName, missingProps.Count, string.Join(", ", missingProps));

                        var visionRescue = await ExtractMissingPropertiesFromImagesAsync(
                            pdfContent.Base64Images, missingProps, component.DisplayName);
                        if (visionRescue != null)
                            textResult = MergeVisionExtras(textResult, visionRescue, missingProps);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Vision-Rescue fuer fehlende Properties fehlgeschlagen - Text-Ergebnis bleibt unveraendert");
                }
            }

            return textResult;
        }

        /// <summary>
        /// Ermittelt Properties, die im Text-Ergebnis leer bzw. mit einem
        /// Empty-Marker ("nicht vorhanden", "nicht gefunden", ...) belegt sind
        /// und daher per Vision nachgereicht werden sollen. Meta-Felder
        /// (ComponentType, PartNumber, Manufacturer, Description, Function)
        /// werden ausgeklammert.
        /// </summary>
        private static List<string> GetMissingPropertyNames(string textResultJson, IList<ComponentProperty> properties)
        {
            var result = new List<string>();
            if (properties == null || properties.Count == 0) return result;

            JObject? json = null;
            try { json = JObject.Parse(textResultJson); }
            catch { return result; }

            var emptyMarkers = new[]
            {
                "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar",
                "mehrdeutig", "nicht relevant", "n/a", "-"
            };
            bool IsEmpty(string? v) => string.IsNullOrWhiteSpace(v)
                || emptyMarkers.Any(m => string.Equals(v.Trim(), m, StringComparison.OrdinalIgnoreCase));

            foreach (var p in properties)
            {
                var name = p.PropertyInternalName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var token = json[name];
                var value = token?.ToString();
                if (IsEmpty(value)) result.Add(name);
            }
            return result;
        }

        /// <summary>
        /// Fragt das Modell per Vision nach beliebigen Properties, die aus dem
        /// Text nicht extrahierbar waren. Anders als der reine Package-Drawing
        /// -Call darf sich das Modell hier bewusst auf ALLE Sektionen des PDF
        /// stuetzen (Features, Nominal Operating Parameters, Absolute Maximum
        /// Ratings, Package Drawing).
        /// </summary>
        private async Task<JObject?> ExtractMissingPropertiesFromImagesAsync(
            List<string> base64Images, List<string> propertyNames, string componentName)
        {
            if (base64Images == null || base64Images.Count == 0) return null;
            if (propertyNames == null || propertyNames.Count == 0) return null;

            var propList = new StringBuilder();
            foreach (var name in propertyNames)
                propList.AppendLine($"  - {name}");

            string prompt =
                "Du siehst die gerenderten Seiten eines Elektronik-Datenblatts fuer die Komponente '"
                + componentName + "'.\n\n"
                + "Aus der reinen Textextraktion fehlen die folgenden Properties. Bitte suche sie\n"
                + "IN DEN BILDERN (Features-Liste, Nominal Operating Parameters, Absolute Maximum\n"
                + "Ratings, Electrical Characteristics, Package Drawing) und gib die Werte zurueck:\n\n"
                + propList
                + "\nREGELN:\n"
                + "  1. Properties mit Suffix 'Max' -> Wert aus 'Absolute Maximum Ratings' (Rating/Max-Spalte).\n"
                + "     NIEMALS aus 'Test Conditions' (z.B. 'Icc = 50 mA' ist eine Bias-Bedingung, kein Max).\n"
                + "  2. Properties mit Suffix 'Typ' -> Typ-Spalte der Nominal-Operating-Parameters-Tabelle.\n"
                + "     Bei einer Frequenz-Matrix mit >=3 Zeilen: mittlere Zeile (Median-Frequenz) waehlen.\n"
                + "  3. VsSpecifV = 'Device Voltage' / 'Vd' / 'Vcc' / 'Vdd', typischer Betriebswert (Typ),\n"
                + "     KEIN Rating-Bereich, KEINE Absolute-Max-Grenze.\n"
                + "  4. SpecifiedHz = die Test-/Median-Frequenz, bei der die Typ-Werte gemessen wurden.\n"
                + "     Wenn du in Regel 2 die Median-Zeile waehlst, muss SpecifiedHz zu DIESER Zeile passen\n"
                + "     (NICHT die hoechste Frequenz der Tabelle).\n"
                + "  5. FMinHz / FMaxHz = untere / obere Grenze des ANGEGEBENEN Frequenzbereichs\n"
                + "     (Operating Frequency Range oder erste/letzte Zeile der Frequenz-Matrix).\n"
                + "  6. Einheiten exakt so uebernehmen wie im Datenblatt (dBm, dB, mW, mA, V, GHz, ...).\n"
                + "  7. Nichts erfinden. Wenn eine Property in den Bildern wirklich nicht steht:\n"
                + "     'nicht vorhanden'.\n\n"
                + "Antwortformat (striktes JSON, keine Erklaerung, kein Markdown):\n"
                + "{\n"
                + string.Join(",\n", propertyNames.Select(n => $"  \"{n}\": \"<Wert oder 'nicht vorhanden'>\""))
                + "\n}\n";

            string rawResult = await AnalyzeImageContentAsync(base64Images, prompt);

            try
            {
                return JObject.Parse(rawResult);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vision-Rescue lieferte kein gueltiges JSON: {Raw}",
                    rawResult?.Length > 400 ? rawResult.Substring(0, 400) : rawResult);
                return null;
            }
        }

        /// <summary>
        /// Liefert die Namen der Properties, die typischerweise NUR aus dem
        /// Package-Drawing / technischen Zeichnungen ablesbar sind und daher
        /// einen separaten Vision-Call rechtfertigen.
        /// </summary>
        private static List<string> GetVisionOnlyPropertyNames(IList<ComponentProperty> properties)
        {
            // Nur echte Vision-Only-Kandidaten. Alles andere (FMinHz, Gain, ...)
            // ist im Text vorhanden und braucht keinen Vision-Zusatz.
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Pitch"
            };

            return properties
                .Select(p => p.PropertyInternalName)
                .Where(n => !string.IsNullOrWhiteSpace(n) && candidates.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Fragt das Modell in einem separaten Vision-Call ausschliesslich
        /// nach den mechanischen Properties, die im Text meist fehlen
        /// (Pitch, ...). Der Prompt ist minimal, damit die Antwort kompakt
        /// bleibt und wenig Reasoning-Tokens frisst.
        /// </summary>
        private async Task<JObject?> ExtractPackageDrawingPropertiesAsync(
            List<string> base64Images, List<string> propertyNames)
        {
            if (base64Images == null || base64Images.Count == 0) return null;
            if (propertyNames == null || propertyNames.Count == 0) return null;

            // Beschreibungen fuer die typischen mechanischen Properties.
            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pitch"] = "Abstand zwischen zwei benachbarten Pin-/Ball-Mittelpunkten im Package-Drawing, "
                          + "meistens in mm angegeben (z.B. '0.5 mm', '0.8 mm', '1.27 mm'). "
                          + "Steht im Package Outline Drawing als Bemassung 'e' oder 'pitch'."
            };

            var propList = new StringBuilder();
            foreach (var name in propertyNames)
            {
                string desc = descriptions.TryGetValue(name, out var d) ? d : name;
                propList.AppendLine($"  - {name}: {desc}");
            }

            string prompt =
                "Du bekommst die gerenderten Seiten eines Elektronik-Datenblatts.\n"
                + "Extrahiere AUSSCHLIESSLICH die folgenden mechanischen Properties aus dem\n"
                + "Package-Drawing / Package Outline / Mechanical Dimensions:\n\n"
                + propList
                + "\nAntwortformat (striktes JSON, keine Erklaerung):\n"
                + "{\n"
                + string.Join(",\n", propertyNames.Select(n => $"  \"{n}\": \"<Wert oder 'nicht vorhanden'>\""))
                + "\n}\n\n"
                + "Wenn ein Wert in den Bildern NICHT eindeutig ablesbar ist, verwende 'nicht vorhanden'.\n"
                + "Bitte KEINEN Wert erfinden.";

            string rawResult = await AnalyzeImageContentAsync(base64Images, prompt);

            try
            {
                return JObject.Parse(rawResult);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vision-Zusatzcall lieferte kein gueltiges JSON: {Raw}",
                    rawResult?.Length > 400 ? rawResult.Substring(0, 400) : rawResult);
                return null;
            }
        }

        /// <summary>
        /// Merged die Werte aus dem Vision-Zusatzcall in das Text-Ergebnis.
        /// Regel: Text-Werte gewinnen, AUSSER der Text hat kein/leeres Ergebnis
        /// fuer dieses Feld - dann uebernehmen wir den Vision-Wert.
        /// </summary>
        private string MergeVisionExtras(string textResultJson, JObject visionExtras, List<string> propertyNames)
        {
            var emptyMarkers = new[] { "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar", "mehrdeutig", "nicht relevant", "n/a", "-" };
            bool IsEmpty(string? v) => string.IsNullOrWhiteSpace(v)
                || emptyMarkers.Any(m => string.Equals(v.Trim(), m, StringComparison.OrdinalIgnoreCase));

            JObject textJson;
            try
            {
                textJson = JObject.Parse(textResultJson);
            }
            catch
            {
                // Wenn das Text-Ergebnis nicht als JSON parsebar ist,
                // koennen wir nicht mergen. Rueckgabe unveraendert.
                return textResultJson;
            }

            foreach (var name in propertyNames)
            {
                string? textValue = textJson[name]?.Value<string>();
                string? visionValue = visionExtras[name]?.Value<string>();

                if (!IsEmpty(visionValue) && IsEmpty(textValue))
                {
                    textJson[name] = visionValue;
                    _logger.LogInformation(
                        "  Vision-Merge: '{Name}' = '{Value}' (aus Package-Drawing)", name, visionValue);
                }
                else if (!IsEmpty(visionValue) && !IsEmpty(textValue))
                {
                    _logger.LogDebug(
                        "  Vision-Merge: '{Name}' im Text bereits vorhanden ('{Text}') - Vision-Wert '{Vision}' ignoriert",
                        name, textValue, visionValue);
                }
            }

            return textJson.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Logt eine Kurzuebersicht des Extraktions-Ergebnisses:
        /// wie viele Felder wurden vom Modell mit "nicht vorhanden" o.ae.
        /// gefuellt vs. mit einem echten Wert. Wichtig fuer Compare-Debugging,
        /// weil man beim Vergleich zweier PDFs sonst nicht sieht, WELCHES
        /// PDF Werte liefert und WELCHES nicht.
        /// </summary>
        private void LogExtractionSummary(string extractionResultJson, string componentDisplayName)
        {
            var emptyMarkers = new[] { "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar", "mehrdeutig", "nicht relevant", "n/a", "-" };
            bool IsEmpty(string? v) => string.IsNullOrWhiteSpace(v)
                || emptyMarkers.Any(m => string.Equals(v.Trim(), m, StringComparison.OrdinalIgnoreCase));

            JObject json;
            try
            {
                json = JObject.Parse(extractionResultJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extraktions-Ergebnis fuer {Component} ist kein JSON: {Preview}",
                    componentDisplayName,
                    extractionResultJson?.Length > 300 ? extractionResultJson.Substring(0, 300) : extractionResultJson);
                return;
            }

            int filled = 0;
            int empty = 0;
            var emptyFields = new List<string>();
            foreach (var prop in json.Properties())
            {
                if (prop.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                string? v = prop.Value?.ToString();
                if (IsEmpty(v))
                {
                    empty++;
                    emptyFields.Add(prop.Name);
                }
                else
                {
                    filled++;
                }
            }

            _logger.LogInformation(
                "  -> {Component}: {Filled} Felder gefuellt, {Empty} 'nicht vorhanden' [{Fields}]",
                componentDisplayName, filled, empty,
                emptyFields.Count <= 20 ? string.Join(", ", emptyFields) : $"{emptyFields.Count} Felder");
        }

        /// <summary>
        /// Stempelt die im Identification-Dialog vom Nutzer bestätigten Felder
        /// (PartNumber, Manufacturer/ManufacturerInfo, Description, ComponentCategory)
        /// in das finale Ergebnis. Damit überschreibt die KI-Extraktion sie nicht
        /// mit Werten wie 'nicht vorhanden', wenn z.B. die PartNumber nicht im
        /// PDF-Text auftaucht (interne Bestellnummern, OEM-Customizing, …).
        /// </summary>
        private void ApplyConfirmedIdentificationFields(JObject resultJson, JObject confirmedIdentification)
        {
            var emptyMarkers = new[] { "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar", "mehrdeutig", "nicht relevant", "n/a", "-" };
            bool IsEmpty(string? v) => string.IsNullOrWhiteSpace(v)
                || emptyMarkers.Any(m => string.Equals(v.Trim(), m, StringComparison.OrdinalIgnoreCase));

            // PartNumber: User-Eingabe gewinnt IMMER, wenn vorhanden.
            var confirmedPartNumber = confirmedIdentification["PartNumber"]?.Value<string>();
            if (!IsEmpty(confirmedPartNumber))
            {
                var pdfPartNumber = resultJson["PartNumber"]?.Value<string>();
                if (!string.Equals(confirmedPartNumber, pdfPartNumber, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Overriding PartNumber from PDF extraction '{Pdf}' with user-confirmed value '{Conf}'",
                        pdfPartNumber ?? "<null>", confirmedPartNumber);
                }
                resultJson["PartNumber"] = confirmedPartNumber;
            }

            // Manufacturer / ManufacturerInfo: nur überschreiben wenn KI-Wert leer ist.
            var confirmedManufacturer = confirmedIdentification["ManufacturerInfo"]?.Value<string>()
                                     ?? confirmedIdentification["Manufacturer"]?.Value<string>();
            if (!IsEmpty(confirmedManufacturer))
            {
                if (IsEmpty(resultJson["Manufacturer"]?.Value<string>()))
                    resultJson["Manufacturer"] = confirmedManufacturer;
                if (IsEmpty(resultJson["ManufacturerInfo"]?.Value<string>()))
                    resultJson["ManufacturerInfo"] = confirmedManufacturer;
            }

            // ComponentCategory + Description: ergänzen wenn leer (User-Korrektur respektieren).
            var confirmedCategory = confirmedIdentification["ComponentCategory"]?.Value<string>();
            if (!IsEmpty(confirmedCategory) && IsEmpty(resultJson["ComponentCategory"]?.Value<string>()))
            {
                resultJson["ComponentCategory"] = confirmedCategory;
            }

            var confirmedDescription = confirmedIdentification["Description"]?.Value<string>();
            if (!IsEmpty(confirmedDescription) && IsEmpty(resultJson["Description"]?.Value<string>()))
            {
                resultJson["Description"] = confirmedDescription;
            }
        }

        /// <summary>
        /// Prueft ob es sich um eine 'Non-Volatile Memory Serial'-Komponente handelt
        /// (inkl. Aliase wie EEPROM Serial, Flash Serial, NV Memory Serial).
        /// </summary>
        private static bool IsMemorySerialComponent(CatalogComponent component)
        {
            string name = (component.DisplayName ?? "") + " " + (component.InternalName ?? "") + " " + (component.FullPath ?? "");
            return name.Contains("Non-Volatile Memory Serial", StringComparison.OrdinalIgnoreCase)
                || name.Contains("NV Memory Serial", StringComparison.OrdinalIgnoreCase)
                || name.Contains("EEPROM Serial", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Flash Serial", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Mappt einen vom Modell gelieferten Function-Wert auf einen der DMS-Dropdown-
        /// Werte fuer Non-Volatile Memory Serial:
        ///   EEPROM, PROM, ROM, Flash, FRAM, MRAM, eMMC
        /// Liefert null, wenn der Wert leer/unbekannt ist (dann nicht ueberschreiben).
        /// </summary>
        private static string? NormalizeMemorySerialFunctionValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();

            // Bereits erlaubte DMS-Werte unveraendert lassen.
            var allowed = new[] { "EEPROM", "PROM", "ROM", "Flash", "FRAM", "MRAM", "eMMC" };
            foreach (var a in allowed)
            {
                if (string.Equals(v, a, StringComparison.OrdinalIgnoreCase))
                    return a;
            }

            string upper = v.ToUpperInvariant();

            // Reihenfolge wichtig: spezifische Treffer zuerst.
            if (upper.Contains("MAGNETORESISTIVE") || upper.Contains("MRAM")) return "MRAM";
            if (upper.Contains("FERROELECTRIC")   || upper.Contains("F-RAM") || upper.Contains("FRAM")) return "FRAM";
            if (upper.Contains("EMMC") || upper.Contains("E-MMC")
                || upper.Contains("MULTIMEDIACARD") || upper.Contains("MULTI MEDIA CARD")
                || upper.Contains("MANAGED NAND") || upper.Contains("UFS")) return "eMMC";
            if (upper.Contains("NAND") || upper.Contains("NOR") || upper.Contains("FLASH")
                || upper.Contains("QSPI") || upper.Contains("SPI FLASH")) return "Flash";
            if (upper.Contains("EEPROM")) return "EEPROM";
            if (upper.Contains("OTP") || upper.Contains("ONE TIME PROGRAMMABLE") || upper.Contains("PROM")) return "PROM";
            if (upper.Contains("MASK ROM") || upper == "ROM") return "ROM";

            // Unbekannter Wert -> unveraendert lassen, damit das Schema-Filter ihn ggf. wegwirft.
            return v;
        }

        /// <summary>
        /// Liefert einen "Function"-Klassifikations-Block (Dropdown-Werte + Decision-Tree),
        /// der dem Hauptprompt fuer Memory-Komponenten angehaengt wird.
        /// Damit ist "Function" auch ohne Part Number Decoding garantiert klassifiziert.
        /// Gibt null zurueck wenn die Komponente keine bekannte Function-Enum hat.
        /// </summary>
        private static string? BuildFunctionEnumBlockIfApplicable(CatalogComponent component)
        {
            string name = (component.DisplayName ?? "") + " " + (component.InternalName ?? "") + " " + (component.FullPath ?? "");

            // ---- Non-Volatile Memory Parallel ----
            if (name.Contains("Non-Volatile Memory Parallel", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("NV Memory Parallel",          StringComparison.OrdinalIgnoreCase) ||
                name.Contains("EEPROM Parallel",             StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Flash Parallel",              StringComparison.OrdinalIgnoreCase))
            {
                return BuildMemoryParallelFunctionBlock("Non-Volatile Memory Parallel");
            }

            // ---- Non-Volatile Memory Serial ----
            // ACHTUNG: Serial hat im DMS ein ANDERES Function-Dropdown als Parallel!
            // Erlaubte Werte (DMS-Dropdown):
            //   EEPROM, PROM, ROM, Flash, FRAM, MRAM, eMMC
            // Werte wie 'NAND Flash SLC' / 'NOR Flash' werden vom DMS abgelehnt.
            if (name.Contains("Non-Volatile Memory Serial", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("NV Memory Serial",          StringComparison.OrdinalIgnoreCase) ||
                name.Contains("EEPROM Serial",             StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Flash Serial",              StringComparison.OrdinalIgnoreCase))
            {
                return BuildMemorySerialFunctionBlock("Non-Volatile Memory Serial");
            }

            return null;
        }

        private static string BuildMemoryParallelFunctionBlock(string componentLabel) => $@"
================================================================
 ZUSAETZLICHE PFLICHT-REGEL: 'Function' (Memory-Technologie)
================================================================
Fuer die Komponente '{componentLabel}' ist das Property 'Function'
eine KLASSIFIKATION (kein Freitext).

Du MUSST GENAU EINEN der folgenden Werte zurueckgeben (DMS-Dropdown):
  * NOR Flash
  * NAND Flash SLC          (Single-Level Cell)
  * NAND Flash MLC          (Multi-Level Cell)
  * EEPROM
  * MRAM                    (Magnetoresistive RAM)
  * FRAM                    (Ferroelectric RAM)

DECISION-TREE (in dieser Reihenfolge pruefen):
  1. Datenblatt-Titel/Beschreibung enthaelt 'MRAM' oder
     'Magnetoresistive'                              -> 'MRAM'
  2. Enthaelt 'FRAM', 'F-RAM' oder 'Ferroelectric'   -> 'FRAM'
  3. Enthaelt 'NAND' UND ('MLC' oder 'Multi-Level')  -> 'NAND Flash MLC'
  4. Enthaelt 'NAND' (ohne MLC-Hinweis)              -> 'NAND Flash SLC'
  5. Enthaelt 'NOR Flash', 'Parallel NOR' oder
     'Serial NOR'                                    -> 'NOR Flash'
  6. Enthaelt 'EEPROM' (und NICHT 'Flash')           -> 'EEPROM'
  7. Sonst                                           -> 'nicht vorhanden'

WICHTIG:
  - Der Wert MUSS EXAKT einer der oben genannten Strings sein.
  - KEINE eigenen Bezeichnungen erfinden ('Magnetoresistive RAM',
    'NAND Flash', 'Flash Memory' etc. sind NICHT erlaubt).
  - Schreibe das Ergebnis in das Property 'Function'.

================================================================
 ZUSAETZLICHE PFLICHT-REGEL: 'SizeByte' (Memory-Kapazitaet)
================================================================
Memory-Datenblaetter geben die Kapazitaet meistens in BIT an
(Mb, Mbit, Kb, Kbit, Gb, Gbit). Das Property 'SizeByte' verlangt
aber den Wert in BYTE (KB, MB, GB).

VORGEHEN:
  1. Suche im Titel / 'Features' / 'Density' nach Werten wie:
       '16 Mbit', '16 Mb', '256 Kbit', '1 Gb', '128KB', '2MB'
     Beachte: 'Mb' = Megabit, 'MB' = Megabyte (KEIN Tippfehler!).
  2. Rechne in BYTE um (1 Byte = 8 Bit):
       16 Mbit  -> 2 MB    (16 / 8)
       128 Kbit -> 16 KB   (128 / 8)
       512 Mbit -> 64 MB   (512 / 8)
       1 Gbit   -> 128 MB  (1024 / 8)
       2 Gb     -> 256 MB
  3. Schreibe das Ergebnis in 'SizeByte' im Format '<Zahl> <Einheit>'
     mit den Einheiten 'KB', 'MB' oder 'GB' (NIE 'Mbit'/'Kbit'!).
  4. Wenn Datenblatt bereits in Byte angibt: uebernimm direkt.
  5. Nur wenn KEINE Kapazitaet auffindbar ist, nutze 'nicht vorhanden'.

BEISPIELE (gut):
  Datenblatt 'MR4A16B 16Mb MRAM'           -> SizeByte: '2 MB'
  Datenblatt '256Kbit Serial EEPROM'       -> SizeByte: '32 KB'
  Datenblatt '1 Gigabit NAND Flash'        -> SizeByte: '128 MB'

ANTI-BEISPIELE (falsch, NICHT machen):
  '16 Mbit' (Bit-Einheit im SizeByte-Feld)        - falsch
  '16777216' (Bytes als rohe Zahl ohne Einheit)   - falsch
================================================================
";

        /// <summary>
        /// Function-Klassifikations-Block fuer 'Non-Volatile Memory Serial'.
        /// WICHTIG: DMS-Dropdown fuer Serial unterscheidet sich von Parallel!
        /// Erlaubte Werte: EEPROM, PROM, ROM, Flash, FRAM, MRAM, eMMC
        /// </summary>
        private static string BuildMemorySerialFunctionBlock(string componentLabel) => $@"
================================================================
 ZUSAETZLICHE PFLICHT-REGEL: 'Function' (Memory-Technologie)
================================================================
Fuer die Komponente '{componentLabel}' ist das Property 'Function'
eine KLASSIFIKATION (kein Freitext, kein Detail-Subtyp).

Du MUSST GENAU EINEN der folgenden Werte zurueckgeben (DMS-Dropdown):
  * EEPROM     (Electrically Erasable Programmable Read-Only Memory)
  * PROM       (Programmable Read-Only Memory, OTP)
  * ROM        (Mask-programmed Read-Only Memory)
  * Flash      (alle Flash-Varianten: NOR, NAND SLC/MLC/TLC, Serial NOR, QSPI, ...)
  * FRAM       (Ferroelectric RAM, F-RAM)
  * MRAM       (Magnetoresistive RAM)
  * eMMC       (embedded Multi Media Card / managed NAND, inkl. UFS)

DECISION-TREE (in dieser Reihenfolge pruefen, ERSTER Treffer gewinnt):
  1. Datenblatt enthaelt 'MRAM' oder 'Magnetoresistive'                     -> 'MRAM'
  2. Enthaelt 'FRAM', 'F-RAM' oder 'Ferroelectric'                          -> 'FRAM'
  3. Enthaelt 'eMMC', 'e-MMC', 'embedded MultiMediaCard',
     'Managed NAND', 'UFS' (Universal Flash Storage) oder 'JEDEC e-MMC'      -> 'eMMC'
  4. Enthaelt 'NAND', 'NOR Flash', 'Serial NOR', 'Serial Flash',
     'SPI Flash', 'QSPI', 'Quad SPI Flash' oder einfach 'Flash Memory'      -> 'Flash'
  5. Enthaelt 'EEPROM' (und KEIN 'Flash'/'NAND'/'NOR')                       -> 'EEPROM'
  6. Enthaelt 'OTP', 'One Time Programmable' oder 'PROM'                     -> 'PROM'
  7. Enthaelt nur 'ROM' / 'Mask ROM' (KEINE der obigen Begriffe)            -> 'ROM'
  8. Sonst                                                                   -> 'nicht vorhanden'

WICHTIG:
  - Der Wert MUSS EXAKT einer der oben genannten Strings sein.
  - KEINE eigenen Detail-Bezeichnungen erfinden:
      'NAND Flash SLC', 'NAND Flash MLC', 'NOR Flash', 'Serial NOR Flash',
      'SPI NOR Flash', 'QSPI NAND', 'NAND Flash' etc. sind ALLE NICHT erlaubt
      -> bei diesen IMMER den Sammelwert 'Flash' verwenden.
  - 'UFS 2.1', 'UFS 3.0', 'UFS Device' werden ALLE auf 'eMMC' gemappt
     (managed NAND mit Controller).
  - Schreibe das Ergebnis in das Property 'Function'.

BEISPIELE:
  'UFS 2.1 Device with NAND Flash and M-PHY 3.0'        -> 'eMMC'
  '64 Mbit Serial Flash, SPI'                            -> 'Flash'
  'W25Q128JV 128M-bit Serial Flash Memory with QSPI'     -> 'Flash'
  'AT25SF641B 64-Mbit SPI Serial Flash'                  -> 'Flash'
  'AT24C256 256K Serial EEPROM'                          -> 'EEPROM'
  'MR25H40 4Mb Serial SPI MRAM'                          -> 'MRAM'
  'FM25V20A 2Mb Serial F-RAM'                            -> 'FRAM'
  'KLM8G1GETF e-MMC 5.1 NAND Flash'                      -> 'eMMC'

================================================================
 ZUSAETZLICHE PFLICHT-REGEL: 'PartNumber' bei Serial Memory
================================================================
Bei Serial-Memory-/eMMC-/UFS-Datenblaettern ist die exakte Hersteller-
Ordering-Nummer fast immer im Datenblatt enthalten (Cover, 'Ordering
Information', 'Part Number Decoder', Marking-Tabelle).

VORGEHEN:
  1. Suche nach Tabellen mit Spaltennamen 'Part Number',
     'Ordering Code', 'Order Code', 'Marking', 'Device Number'.
  2. Akzeptiere typische Hersteller-Praefixe wie z.B.:
       AT.., W..Q.., MX25.., S25FL.., MT29.., KLM.., H26M.., THGB.., SDIN..
  3. Nimm die VOLLSTAENDIGE Bestellnummer (mit Package-/Speed-/Temp-Suffix),
     z.B. 'AT25SF641B-MHB-T', 'W25Q128JVSIQ', 'KLM8G1GETF-B041'.
  4. Liefere KEINE generischen Familiennamen ('UFS 2.1 Device',
     'NAND Flash', 'Serial Flash') als PartNumber.
  5. Nur wenn wirklich keine Ordering-Nummer im Dokument steht,
     verwende 'nicht vorhanden'.

================================================================
 ZUSAETZLICHE PFLICHT-REGEL: 'SizeByte' (Memory-Kapazitaet)
================================================================
Memory-Datenblaetter geben die Kapazitaet meistens in BIT an
(Mb, Mbit, Kb, Kbit, Gb, Gbit). Das Property 'SizeByte' verlangt
aber den Wert in BYTE (KB, MB, GB).

VORGEHEN:
  1. Suche im Titel / 'Features' / 'Density' nach Werten wie:
       '16 Mbit', '16 Mb', '256 Kbit', '1 Gb', '128KB', '2MB', '64GB'
     Beachte: 'Mb' = Megabit, 'MB' = Megabyte (KEIN Tippfehler!).
  2. Rechne in BYTE um (1 Byte = 8 Bit):
       16 Mbit  -> 2 MB    (16 / 8)
       128 Kbit -> 16 KB   (128 / 8)
       512 Mbit -> 64 MB   (512 / 8)
       1 Gbit   -> 128 MB  (1024 / 8)
       2 Gb     -> 256 MB
  3. Bei eMMC/UFS sind Kapazitaeten haeufig direkt in GB angegeben
     (z.B. '64GB', '128GB') - dann direkt uebernehmen.
  4. Schreibe das Ergebnis in 'SizeByte' im Format '<Zahl> <Einheit>'
     mit den Einheiten 'KB', 'MB' oder 'GB' (NIE 'Mbit'/'Kbit'!).
  5. Nur wenn KEINE Kapazitaet auffindbar ist, nutze 'nicht vorhanden'.
================================================================
";

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

        #endregion

        #region Azure OpenAI Communication

        private async Task<string> AnalyzeTextContentAsync(string textContent, string prompt)
        {
            var requestBody = CreateTextRequestBody(textContent, prompt);
            return await MakeApiCallAsync(requestBody);
        }

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
        /// ECHTER Hybrid-Modus: schickt den extrahierten PDF-Text UND die
        /// gerenderten Bilder gemeinsam an das Modell.
        /// Wichtig fuer Komponenten wie RF Amplifier, wo:
        ///   - numerische Parameter (FMinHz, FMaxHz, Gain, ...) in TABELLEN stehen
        ///     und aus dem Text zuverlaessig lesbar sind, aber
        ///   - PackageType/Pitch/NumberOfPin nur aus BILDERN (Package Drawing)
        ///     abgeleitet werden koennen.
        /// Reines Vision (nur Bilder) verpasst haeufig die Tabellenwerte,
        /// reines Text (ohne Bilder) verpasst die mechanischen Angaben.
        /// </summary>
        private async Task<string> AnalyzeHybridContentAsync(string textContent, List<string> base64Images, string prompt)
        {
            var requestBody = CreateHybridRequestBody(textContent, base64Images, prompt);
            return await MakeApiCallAsync(requestBody);
        }

        private string CreateTextRequestBody(string textContent, string prompt)
        {
            // Anti-Suffix-Regel: verhindert, dass das Modell bei Multi-Package-
            // Datenblaettern Varianten-Suffixe erzeugt (NumberOfPin_1, _2, ...).
            // Solche Feldnamen wuerden von der Schema-Whitelist verworfen und
            // die Werte gingen verloren.
            string effectivePrompt = prompt + @"

================================================================
 PFLICHT-REGEL: KEINE VARIANTEN-SUFFIXE
================================================================
Wenn das Datenblatt mehrere Packages / Varianten beschreibt
(z.B. SOIC-28, TSOP-48, DIP-28), DARFST DU NICHT pro Variante
eigene Felder wie 'NumberOfPin_1', 'NumberOfPin_2', 'PackageType_1',
'PackageType_2' zurueckgeben.

STATTDESSEN:
  - Nutze GENAU die Feldnamen aus dem oben definierten Schema
    (kein Suffix '_1', '_2', '_3', ' A', ' Variant', ...).
  - Wenn mehrere Varianten existieren, WAEHLE die haeufigste/
    hervorgehobene Variante (typisch die im Titel bzw. auf dem
    Cover genannte).
  - Alternativ trenne mehrere Werte per Komma innerhalb desselben
    Feldes (z.B. NumberOfPin: '28, 48'), aber niemals ueber
    zusaetzliche Feldnamen mit Suffix.
================================================================
";

            var message = new
            {
                role = "user",
                content = effectivePrompt + "\n\nDocument Content:\n" + textContent
            };

            var requestBody = new
            {
                messages = new[] { message },
                max_completion_tokens = 16000,
                // 'temperature' bewusst NICHT setzen: neuere Azure-OpenAI-Modelle
                // (o1/o3/gpt-5-Serie) akzeptieren nur den Default-Wert 1 und
                // liefern sonst 'BadRequest: unsupported_value' fuer temperature.
                response_format = new { type = "json_object" }
            };

            return JsonConvert.SerializeObject(requestBody);
        }

        private string CreateImageRequestBody(List<string> base64Images, string prompt)
        {
            var content = new List<object>();

            content.Add(new { type = "text", text = prompt });

            // Limit fuer Vision-API. Muss mit PDFProcessor.MAX_RENDERED_PAGES konsistent sein!
            // 15 ist ein guter Kompromiss aus Erkennungsqualitaet, Geschwindigkeit und Token-Kosten.
            // Die Auswahl WELCHE 15 Seiten geschickt werden, macht PDFProcessor.SelectRelevantPages.
            const int MaxImagesToSend = 15;
            int imageCount = Math.Min(base64Images.Count, MaxImagesToSend);
            _logger.LogInformation("Sending {ImageCount} images to Vision API (detail=high)", imageCount);
            
            for (int i = 0; i < imageCount; i++)
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new { url = base64Images[i], detail = "high" }
                });
            }

            var message = new { role = "user", content };

            var requestBody = new
            {
                messages = new[] { message },
                // WICHTIG bei Reasoning-Modellen (o1/o3/gpt-5-Serie):
                // 'max_completion_tokens' zaehlt REASONING-Tokens + OUTPUT-Tokens
                // gemeinsam. Mit 4096 hat das Modell nach dem internen Reasoning
                // kaum noch Budget fuer das tatsaechliche JSON und liefert
                // ueberall "nicht vorhanden". Deshalb hier grosszuegig auf
                // 16000 setzen (analog zum Text-Pfad).
                max_completion_tokens = 16000,
                // JSON erzwingen, damit die Antwort auch im Bild-Pfad ohne
                // Nach-Parsing/Regex direkt als JObject einlesbar ist.
                response_format = new { type = "json_object" }
                // 'temperature' bewusst NICHT setzen (siehe CreateTextRequestBody).
            };

            return JsonConvert.SerializeObject(requestBody);
        }

        /// <summary>
        /// Kombiniertes Text+Bild-Request-Payload. Der Prompt wird durch einen
        /// klaren Hinweis erweitert, DASS Text-Tabellen den Bildern bei
        /// numerischen Werten vorzuziehen sind - andernfalls tendiert das
        /// Modell dazu, Zahlenwerte aus (unscharf gerenderten) Package-
        /// Drawings ablesen zu wollen und liefert dann "nicht vorhanden".
        /// </summary>
        private string CreateHybridRequestBody(string textContent, List<string> base64Images, string prompt)
        {
            var content = new List<object>();

            // Prompt-Erweiterung: Reihenfolge Text -> Bild explizit machen.
            string hybridPrompt = prompt + @"

================================================================
 HYBRID-MODUS: TEXT + BILDER
================================================================
Du bekommst zusaetzlich zum PDF-Text die gerenderten Seitenbilder.

VORRANG-REGEL:
  1. NUMERISCHE PARAMETER (Frequenzen in Hz/GHz, Gain in dB, Spannungen,
     Stroeme, Zeiten, Kapazitaeten) IMMER aus dem TEXT lesen.
     Der Text stammt aus dem originalen PDF und ist praeziser als OCR.
  2. Nutze die BILDER fuer:
       * Package-Drawing (PackageType, NumberOfPin, Pitch)
       * Block-Diagramme (Function/Beschreibung)
       * Pinout / Labels
       * Tabellen, die im Text unvollstaendig oder verschluesselt sind.
  3. Wenn ein Wert im Text steht: NIMM den Text-Wert.
     Wenn ein Wert nur im Bild sichtbar ist: nimm den Bild-Wert.
  4. Nur wenn WEDER Text NOCH Bild einen Wert liefern, verwende
     'nicht vorhanden'.

WICHTIG: 'nicht vorhanden' ist nur zulaessig, wenn beide Quellen
das Feld nicht enthalten. NICHT wenn nur eine Quelle es nicht zeigt.
================================================================
";

            content.Add(new { type = "text", text = hybridPrompt });

            // Text-Inhalt als eigener Text-Block anhaengen, damit das Modell ihn
            // klar von der Instruktion trennt.
            if (!string.IsNullOrWhiteSpace(textContent))
            {
                content.Add(new { type = "text", text = "PDF-TEXT-INHALT:\n" + textContent });
            }

            const int MaxImagesToSend = 15;
            int imageCount = Math.Min(base64Images?.Count ?? 0, MaxImagesToSend);
            _logger.LogInformation(
                "Hybrid mode: sending {TextLength} chars text + {ImageCount} images",
                textContent?.Length ?? 0, imageCount);

            for (int i = 0; i < imageCount; i++)
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new { url = base64Images![i], detail = "high" }
                });
            }

            var message = new { role = "user", content };

            var requestBody = new
            {
                messages = new[] { message },
                max_completion_tokens = 16000,
                response_format = new { type = "json_object" }
                // temperature bewusst nicht gesetzt (o1/o3/gpt-5-Kompatibilitaet).
            };

            return JsonConvert.SerializeObject(requestBody);
        }

        private async Task<string> MakeApiCallAsync(string requestBodyJson)
        {
            try
            {
                string url = $"{_config.GetEndpoint()}/openai/deployments/{_config.GetDeploymentName()}/chat/completions?api-version={_config.GetApiVersion()}";

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("api-key", _config.GetApiKey());
                request.Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Azure API Error: {response.StatusCode} - {responseBody}");
                }

                var jsonResponse = JObject.Parse(responseBody);

                var usage = jsonResponse["usage"];
                if (usage != null)
                {
                    int promptTokens = usage["prompt_tokens"]?.Value<int>() ?? 0;
                    int completionTokens = usage["completion_tokens"]?.Value<int>() ?? 0;
                    // Reasoning-Modelle (o1/o3/gpt-5) melden zusaetzlich
                    // 'reasoning_tokens'. Wenn diese Zahl fast das gesamte
                    // completion_tokens-Budget aufbraucht, bleibt fuer den
                    // tatsaechlichen JSON-Output kaum was uebrig -> deswegen
                    // zur Diagnose mitloggen.
                    int reasoningTokens = usage["completion_tokens_details"]?["reasoning_tokens"]?.Value<int>() ?? 0;
                    if (reasoningTokens > 0)
                    {
                        _logger.LogInformation(
                            "Azure OpenAI Tokens: prompt={Prompt}, completion={Completion} (davon reasoning={Reasoning})",
                            promptTokens, completionTokens, reasoningTokens);
                    }
                    TokenStatsCallback?.Invoke(promptTokens, completionTokens);
                }

                // 'finish_reason' pruefen: 'length' bedeutet, das Modell wurde
                // abgeschnitten -> der Response-JSON ist unvollstaendig.
                // Das ist bei Reasoning-Modellen ein sehr haeufiger Grund fuer
                // "nicht vorhanden"-Werte in der Extraktion.
                string finishReason = jsonResponse["choices"]?[0]?["finish_reason"]?.Value<string>() ?? "";
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Azure OpenAI hat die Antwort abgeschnitten (finish_reason=length). "
                        + "max_completion_tokens ist zu klein fuer dieses Modell/Prompt.");
                }

                string result = jsonResponse["choices"]?[0]?["message"]?["content"]?.Value<string>() ?? "";
                return result;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception during Azure API call");
                throw;
            }
        }

        #endregion
    }
}
