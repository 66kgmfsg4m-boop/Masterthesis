using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DatasheetAnalyzer.Core.Services
{
    /// <summary>
    /// Service zum Laden und Verarbeiten der DMS-Katalogdaten aus CSV-Dateien.
    /// </summary>
    public class DMSCatalogService
    {
        private readonly IRemoteConfigService _remoteConfigService;
        private readonly ILogger<DMSCatalogService> _logger;
        private Dictionary<string, CatalogComponent> _catalogComponents;
        private Dictionary<string, List<ComponentProperty>> _componentProperties;
        private Dictionary<string, Dictionary<string, string>> _valueLists;
        private string? _cachedPitchGuidelines;

        // Cache für generische, per Namenskonvention geladene Kataloggruppen-Guidelines.
        // Wert = null bedeutet "Blob existiert nicht" (negativ gecacht, damit Azure nicht mehrfach angefragt wird).
        private readonly Dictionary<string, string?> _cachedGenericGuidelines =
            new(StringComparer.OrdinalIgnoreCase);

        // Maximale Länge einer generischen Guideline, um Prompt-Bloat und Token-Kosten zu begrenzen.
        private const int MaxGenericGuidelineLength = 12000;

        public DMSCatalogService(IRemoteConfigService remoteConfigService, ILogger<DMSCatalogService> logger)
        {
            _remoteConfigService = remoteConfigService ?? throw new ArgumentNullException(nameof(remoteConfigService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _catalogComponents = new Dictionary<string, CatalogComponent>();
            _componentProperties = new Dictionary<string, List<ComponentProperty>>();
            _valueLists = new Dictionary<string, Dictionary<string, string>>();
        }

        public void LoadCatalogData()
        {
            _logger.LogInformation("Loading DMS catalog data");
            try
            {
                LoadClasses();
                _logger.LogInformation("Catalog loaded: {ComponentCount} components, {PropertyGroupCount} property groups",
                    _catalogComponents.Count, _componentProperties.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading catalog data");
                throw;
            }
        }

        private void LoadClasses()
        {
            try
            {
                _logger.LogDebug("Loading get_classes.csv from Azure");
                string csvContent = _remoteConfigService.LoadBlobContent("get_classes.csv");
                var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (lines.Length < 2)
                {
                    _logger.LogWarning("get_classes.csv is empty or has only header");
                    return;
                }
                
                char delimiter = DetectDelimiter(lines[0]);
                _logger.LogDebug("Detected delimiter: '{Delimiter}'", delimiter);
                
                for (int i = 1; i < lines.Length; i++)
                {
                    ParseClassLine(lines[i], delimiter, i);
                }
                
                _logger.LogInformation("{ComponentCount} catalog components loaded", _catalogComponents.Count);
                _logger.LogInformation("Properties for {PropertyGroupCount} components loaded", _componentProperties.Count);
                
                foreach (var kvp in _componentProperties.Take(5))
                {
                    _logger.LogDebug("  - {ComponentKey}: {PropertyCount} properties", kvp.Key, kvp.Value.Count);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error loading get_classes.csv");
                throw;
            }
        }

        private void ParseClassLine(string line, char delimiter, int lineIndex)
        {
            var columns = ParseCsvLine(line, delimiter);
            if (columns.Count < 5)
                return;

            string fullPath = CleanCsvValue(columns[0]);
            string internalName = CleanCsvValue(columns[1]);
            string category = CleanCsvValue(columns[2]);
            string displayName = CleanCsvValue(columns[3]);
            string componentName = CleanCsvValue(columns[4]);
            string propertyName = columns.Count > 5 ? CleanCsvValue(columns[5]) : "";
            string propertyNumber = columns.Count > 6 ? CleanCsvValue(columns[6]) : "";

            if (string.IsNullOrWhiteSpace(displayName))
                displayName = internalName;

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                AddComponent(fullPath, internalName, displayName, componentName, lineIndex);
            }
            else
            {
                AddProperty(displayName, propertyName, propertyNumber, lineIndex);
            }
        }

        private void AddComponent(string fullPath, string internalName, string displayName, string componentName, int lineIndex)
        {
            var component = new CatalogComponent
            {
                RowNumber = lineIndex + 1,
                FullPath = fullPath,
                ComponentType = "COMPONENT",
                Category = displayName,
                DisplayName = displayName,
                ComponentName = componentName,
                InternalName = internalName,
                Description = "",
                AdditionalInfo = ""
            };

            string key = !string.IsNullOrEmpty(component.DisplayName) ? component.DisplayName : component.InternalName;
            
            if (!string.IsNullOrEmpty(key) && !_catalogComponents.ContainsKey(key))
            {
                _catalogComponents[key] = component;
                _logger.LogDebug("[Line {LineNumber}] Component: D={DisplayName}, E={ComponentName}, Internal={InternalName}",
                    lineIndex + 1, displayName, componentName, internalName);
            }
        }

        private void AddProperty(string componentPath, string propertyName, string propertyNumber, int lineIndex)
        {
            var property = new ComponentProperty
            {
                ComponentPath = componentPath,
                PropertyGroup = "",
                PropertyNumber = propertyNumber,
                PropertyInternalName = propertyName,
                PropertyDisplayName = propertyName,
                PropertyType = ""
            };

            if (!_componentProperties.ContainsKey(componentPath))
            {
                _componentProperties[componentPath] = new List<ComponentProperty>();
            }

            _componentProperties[componentPath].Add(property);

            if (_componentProperties[componentPath].Count <= 3)
            {
                _logger.LogDebug("[Line {LineNumber}] Property: {ComponentPath}.{PropertyName} = {PropertyNumber}",
                    lineIndex + 1, componentPath, propertyName, propertyNumber);
            }
        }

        private char DetectDelimiter(string headerLine)
        {
            int commaCount = headerLine.Count(c => c == ',');
            int semicolonCount = headerLine.Count(c => c == ';');
            return semicolonCount > commaCount ? ';' : ',';
        }

        private List<string> ParseCsvLine(string line, char delimiter = ',')
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == delimiter && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString());
            return result;
        }

        private string CleanCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            value = value.Trim();

            if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length > 1)
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value.Trim();
        }

        public CatalogComponent? FindComponentByPropertyKey(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return null;

            searchTerm = searchTerm.Trim();

            _logger.LogDebug("Searching in {PropertyGroupCount} property groups...", _componentProperties.Count);

            var exactMatch = _componentProperties.Keys.FirstOrDefault(k =>
                k.Equals(searchTerm, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                _logger.LogDebug("Exact property key found: {Key}", exactMatch);
                return GetOrCreateComponent(exactMatch);
            }

            var fuzzyMatch = FindFuzzyPropertyMatch(searchTerm);
            if (fuzzyMatch != null)
            {
                return GetOrCreateComponent(fuzzyMatch);
            }

            return null;
        }

        private string? FindFuzzyPropertyMatch(string searchTerm)
        {
            var searchWords = searchTerm.ToLower().Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 2)
                .ToList();

            if (searchWords.Count == 0)
                return null;

            _logger.LogDebug("Searching with words: [{Words}]", string.Join(", ", searchWords));

            var scores = new List<(string key, int matchedWords, int score, double specificity)>();

            foreach (var propKey in _componentProperties.Keys)
            {
                var matchResult = CalculateMatchScore(propKey, searchWords);
                if (matchResult.matchedWords > 0)
                {
                    scores.Add((propKey, matchResult.matchedWords, matchResult.score, matchResult.specificity));
                }
            }

            if (scores.Count > 0)
            {
                var bestMatches = scores
                    .OrderByDescending(x => x.matchedWords)
                    .ThenByDescending(x => x.score)
                    .ThenByDescending(x => x.specificity)
                    .Take(5)
                    .ToList();

                _logger.LogDebug("Top {Count} property key matches:", bestMatches.Count);
                foreach (var (key, words, sc, spec) in bestMatches)
                {
                    _logger.LogDebug("  [{Score:D3}|W:{MatchedWords}/{TotalWords}|S:{Specificity:F1}] {Key}",
                        sc, words, searchWords.Count, spec, key);
                }

                var best = bestMatches.First();

                if (best.matchedWords >= searchWords.Count / 2)
                {
                    _logger.LogDebug("Best match: {Key} ({MatchedWords}/{TotalWords} words, Score: {Score}, Specificity: {Specificity:F1})",
                        best.key, best.matchedWords, searchWords.Count, best.score, best.specificity);
                    return best.key;
                }
            }

            return null;
        }

        private (int matchedWords, int score, double specificity) CalculateMatchScore(string propKey, List<string> searchWords)
        {
            string propKeyLower = propKey.ToLower();
            var propKeyWords = propKey.ToLower().Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

            int matchedWords = 0;
            int score = 0;
            double specificity = 0.0;

            foreach (var searchWord in searchWords)
            {
                if (propKeyWords.Any(pkw => pkw == searchWord))
                {
                    matchedWords++;
                    score += 100;
                    specificity += searchWord.Length * 10.0;
                    continue;
                }

                if (propKeyWords.Any(pkw => pkw.StartsWith(searchWord)))
                {
                    matchedWords++;
                    score += 50;
                    specificity += searchWord.Length * 5.0;
                    continue;
                }

                if (propKeyLower.Contains(searchWord))
                {
                    matchedWords++;
                    score += 20;
                    specificity += searchWord.Length * 2.0;
                }
            }

            if (matchedWords > 0)
            {
                double lengthPenalty = propKey.Length * 0.5;
                specificity -= lengthPenalty;
            }

            return (matchedWords, score, specificity);
        }

        private CatalogComponent GetOrCreateComponent(string key)
        {
            if (!_catalogComponents.ContainsKey(key))
            {
                _catalogComponents[key] = new CatalogComponent
                {
                    DisplayName = key,
                    InternalName = key,
                    Category = "COMPONENT",
                    FullPath = "/COMPONENT/" + key.Replace(" ", ""),
                    ComponentType = "COMPONENT",
                    RowNumber = -1
                };
                _logger.LogDebug("Virtual component created");
            }

            return _catalogComponents[key];
        }

        public CatalogComponent? FindComponent(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return null;

            // Spezifische Memory-Typen -> Generische Katalog-Kategorien
            searchTerm = MapSpecificMemoryTypesToCatalog(searchTerm);

            // Coil/Inductor-Synonyme -> Katalog-Knoten "Coil" Der Baum kennt nur "Coil" (unter Inductance), das LLM klassifiziert aber typischerweise als "Inductor" / "Inductance" / "Power Choke".
            searchTerm = MapCoilSynonymsToCatalog(searchTerm);

            string normalizedSearch = Normalize(searchTerm);

            return _catalogComponents.Values.FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(c.DisplayName) && Normalize(c.DisplayName).Contains(normalizedSearch)) ||
                (!string.IsNullOrWhiteSpace(c.InternalName) && Normalize(c.InternalName).Contains(normalizedSearch)) ||
                (!string.IsNullOrWhiteSpace(c.FullPath) && Normalize(c.FullPath).Contains(normalizedSearch)) ||
                (!string.IsNullOrWhiteSpace(c.Category) && Normalize(c.Category).Contains(normalizedSearch))
            );
        }

        /// <summary>Mappt Inductor/Inductance/Power Choke etc. auf den Katalog-Knoten "Coil".</summary>
        private string MapCoilSynonymsToCatalog(string searchTerm)
        {
            string lower = searchTerm.ToLowerInvariant();
            var coilKeywords = new[]
            {
                "inductor", "inductance",
                "power choke", "choke",
                "wire wound", "wirewound",
                "multilayer chip inductor",
                "tunable coil", "wideband bias choke",
                "spule", "drossel"
            };
            foreach (var kw in coilKeywords)
            {
                if (lower.Contains(kw))
                {
                    _logger.LogDebug("Mapping '{SearchTerm}' -> 'Coil' (Keyword: {Keyword})", searchTerm, kw);
                    return "Coil";
                }
            }
            return searchTerm;
        }

        /// <summary>Mappt spezifische Memory-Typen (F-RAM, MRAM, etc.) auf generische Katalog-Kategorien</summary>
        private string MapSpecificMemoryTypesToCatalog(string searchTerm)
        {
            // Normalisiere für Case-Insensitive Vergleich
            string lower = searchTerm.ToLowerInvariant();

            // PARALLEL MEMORY TYPES (Async, mit Address/Data Bus)
            var parallelMemoryKeywords = new[]
            {
                "f-ram", "fram",                    // Ferroelectric RAM
                "mram",                             // Magnetoresistive RAM
                "parallel eeprom",
                "parallel flash",
                "prom", "eprom",                    // Programmable ROM
                "parallel memory",
                "async", "asynchronous"
            };

            var serialMemoryKeywords = new[]
            {
                "serial eeprom",
                "serial flash",
                "spi flash", "spi eeprom",
                "i2c eeprom", "i2c flash",
                "serial memory"
            };

            // Prüfe ob Parallel Memory
            foreach (var keyword in parallelMemoryKeywords)
            {
                if (lower.Contains(keyword))
                {
                    _logger.LogDebug("Mapping '{SearchTerm}' -> 'Non-Volatile Memory Parallel' (Keyword: {Keyword})",
                        searchTerm, keyword);
                    return "Non-Volatile Memory Parallel";
                }
            }

            // Prüfe ob Serial Memory
            foreach (var keyword in serialMemoryKeywords)
            {
                if (lower.Contains(keyword))
                {
                    _logger.LogDebug("Mapping '{SearchTerm}' -> 'Non-Volatile Memory Serial' (Keyword: {Keyword})",
                        searchTerm, keyword);
                    return "Non-Volatile Memory Serial";
                }
            }

            // Generische Memory-Erkennung (Fallback)
            if (lower.Contains("nonvolatile") || lower.Contains("non-volatile") || lower.Contains("nv memory"))
            {
                // Default: Parallel (da häufigster Fall)
                _logger.LogDebug("Mapping '{SearchTerm}' -> 'Non-Volatile Memory Parallel' (Generic Memory)",
                    searchTerm);
                return "Non-Volatile Memory Parallel";
            }

            // Keine Änderung
            return searchTerm;
        }

        private string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";
            
            return new string(input
                .ToLowerInvariant()
                .Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-')
                .ToArray());
        }

        public List<ComponentProperty> GetPropertiesForComponent(string componentGroup)
        {
            if (string.IsNullOrWhiteSpace(componentGroup))
                return new List<ComponentProperty>();

            string normalizedGroup = Normalize(componentGroup.Trim());

            var matchingKeys = _componentProperties.Keys
                .Where(k => Normalize(k.Trim()) == normalizedGroup ||
                            Normalize(k.Trim()).Contains(normalizedGroup) ||
                            normalizedGroup.Contains(Normalize(k.Trim())))
                .ToList();

            if (matchingKeys.Count == 0)
            {
                var searchWords = normalizedGroup.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                matchingKeys = _componentProperties.Keys
                    .Where(k => searchWords.All(w => Normalize(k).Contains(w)))
                    .ToList();
            }

            var props = new List<ComponentProperty>();
            foreach (var key in matchingKeys)
                props.AddRange(_componentProperties[key]);

            return props;
        }

        /// <summary>
        /// Liefert fuer eine Kategorie (z.B. "Coil") die CSV-Properties sortiert nach ORDNUM (PropertyNumber).
        /// </summary>
        public List<ComponentProperty> GetOrderedPropertiesForComponent(string componentGroup)
        {
            var props = GetPropertiesForComponent(componentGroup);
            return props
                .Where(p => int.TryParse(p.PropertyNumber, out _))
                .OrderBy(p => int.Parse(p.PropertyNumber))
                .ToList();
        }

        public string GenerateDynamicPrompt(CatalogComponent component)
        {
            var properties = GetPropertiesForComponent(component.ComponentPath);

            if (properties.Count == 0)
            {
                properties = GetPropertiesForComponent(component.InternalName);
            }

            if (properties.Count == 0)
            {
                return "ERROR: No properties found for component " + component.InternalName;
            }

            var propertyDefinitions = LoadPropertyDefinitions();

            var promptBuilder = new StringBuilder();
            
            BuildPromptHeader(promptBuilder, component, properties);
            BuildPropertyList(promptBuilder, properties, propertyDefinitions);
            BuildForbiddenProperties(promptBuilder);
            
            // ? Füge Pitch-Extraction Guidelines hinzu für relevante Komponenten
            if (RequiresPitchGuidelines(component, properties))
            {
                var pitchGuidelines = LoadPitchGuidelinesIfNeeded();
                if (!string.IsNullOrEmpty(pitchGuidelines))
                {
                    promptBuilder.AppendLine();
                    promptBuilder.AppendLine(pitchGuidelines);
                    promptBuilder.AppendLine();
                }
            }
            
            // ? NEU: Füge Memory-Extraction Guidelines hinzu für Memory-Komponenten
            if (RequiresMemoryGuidelines(component, properties))
            {
                var memoryGuidelines = LoadMemoryGuidelinesIfNeeded();
                if (!string.IsNullOrEmpty(memoryGuidelines))
                {
                    promptBuilder.AppendLine();
                    promptBuilder.AppendLine(memoryGuidelines);
                    promptBuilder.AppendLine();
                }
            }
            
            // Füge Coil/Inductor-Extraction Guidelines hinzu (kein Part Number Decoding!)
            if (RequiresCoilGuidelines(component, properties))
            {
                var coilGuidelines = LoadCoilGuidelinesIfNeeded();
                if (!string.IsNullOrEmpty(coilGuidelines))
                {
                    promptBuilder.AppendLine();
                    promptBuilder.AppendLine(coilGuidelines);
                    promptBuilder.AppendLine();
                }
            }

            // Füge EMI-Filter-Extraction Guidelines hinzu (kein Part Number Decoding!)
            if (RequiresEmiFilterGuidelines(component, properties))
            {
                var emiFilterGuidelines = LoadEmiFilterGuidelinesIfNeeded();
                if (!string.IsNullOrEmpty(emiFilterGuidelines))
                {
                    promptBuilder.AppendLine();
                    promptBuilder.AppendLine(emiFilterGuidelines);
                    promptBuilder.AppendLine();
                }
            }

            // Generische Kataloggruppen-Guidelines per Namenskonvention aus Azure Blob.
            var genericGuidelines = LoadGenericGuidelineFor(component);
            if (!string.IsNullOrEmpty(genericGuidelines))
            {
                promptBuilder.AppendLine();
                promptBuilder.AppendLine(genericGuidelines);
                promptBuilder.AppendLine();
            }

            BuildJsonStructure(promptBuilder, component, properties);
            BuildExtractionRules(promptBuilder, properties);
            BuildMinTypMaxRules(promptBuilder);
            BuildFrequencyRangeRules(promptBuilder);

            return promptBuilder.ToString();
        }

        private void BuildPromptHeader(StringBuilder builder, CatalogComponent component, List<ComponentProperty> properties)
        {
            builder.AppendLine("You work at Rohde & Schwarz.");
            builder.AppendLine($"Analyzing technical datasheet: {component.DisplayName}");
            builder.AppendLine();
            builder.AppendLine("???????????????????????????????????????????????????????????????");
            builder.AppendLine($"CRITICAL: Extract EXACTLY {properties.Count} properties - NOT MORE!");
            builder.AppendLine("???????????????????????????????????????????????????????????????");
            builder.AppendLine();
            builder.AppendLine($"DMS Class: {component.InternalName}");
            builder.AppendLine($"DMS Path: {component.FullPath}");
            builder.AppendLine();
            builder.AppendLine($"ALLOWED PROPERTIES ({properties.Count} total):");
        }

        private void BuildPropertyList(StringBuilder builder, List<ComponentProperty> properties, Dictionary<string, string> definitions)
        {
            for (int i = 0; i < properties.Count; i++)
            {
                var prop = properties[i];
                string explanation = GetPropertyExplanation(prop.PropertyInternalName, definitions);

                if (!string.IsNullOrEmpty(explanation))
                {
                    builder.AppendLine($"  [{i + 1:D2}] {prop.PropertyInternalName} - {explanation}");
                }
                else
                {
                    builder.AppendLine($"  [{i + 1:D2}] {prop.PropertyInternalName}");
                }
            }
            builder.AppendLine();
        }

        private void BuildForbiddenProperties(StringBuilder builder)
        {
            builder.AppendLine("FORBIDDEN - DO NOT EXTRACT THESE:");
            builder.AppendLine("  - FrequencyRange, Gain_20to34GHz, Gain_34to44GHz");
            builder.AppendLine("  - GainFlatness, GainVariationOverTemperature");
            builder.AppendLine("  - NoiseFigure_20to34GHz, NoiseFigure_34to44GHz");
            builder.AppendLine("  - InputReturnLoss, OutputReturnLoss");
            builder.AppendLine("  - OutputPower_P1dB, SaturatedOutputPower, OutputIP3");
            builder.AppendLine("  - InputOutputImpedance, DieSize");
            builder.AppendLine("  - SupplyCurrent, DrainBiasVoltage, GateBiasVoltageRange");
            builder.AppendLine("  - ContinuousPowerDissipation, ESD_HBM");
            builder.AppendLine("  - OutlineDimensions, PadConfiguration, Mounting");
            builder.AppendLine("  - ThermalResistance, StorageTemperatureRange");
            builder.AppendLine("  - TechnicalSpecifications, ElectricalCharacteristics");
            builder.AppendLine("  - MechanicalCharacteristics, EnvironmentalData");
            builder.AppendLine("  - ANY property NOT in the ALLOWED list");
            builder.AppendLine();
        }

        private void BuildJsonStructure(StringBuilder builder, CatalogComponent component, List<ComponentProperty> properties)
        {
            builder.AppendLine("JSON STRUCTURE (NO nested objects!):");
            builder.AppendLine("{");
            builder.AppendLine($"  \"ComponentType\": \"{component.InternalName}\",");
            builder.AppendLine("  \"PartNumber\": \"<value>\",");
            builder.AppendLine("  \"Manufacturer\": \"<value>\",");
            builder.AppendLine("  \"Description\": \"<value>\"");

            foreach (var prop in properties)
            {
                builder.AppendLine($"  ,\"{prop.PropertyInternalName}\": \"<value>\"");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        private void BuildExtractionRules(StringBuilder builder, List<ComponentProperty> properties)
        {
            builder.AppendLine("MANDATORY RULES:");
            builder.AppendLine($"1. JSON must have EXACTLY {properties.Count + 4} keys");
            builder.AppendLine($"2. Extract ONLY the {properties.Count} properties from ALLOWED list");
            builder.AppendLine("3. NO nested objects (no TechnicalSpecifications section!)");
            builder.AppendLine("4. If property missing: \"nicht vorhanden\"");
            builder.AppendLine("5. Don't round numbers, keep exact values with units");
            builder.AppendLine("6. Keep original units (Hz, dB, V, A, mm, etc.)");
            builder.AppendLine("7. For ranges use format: \"min to max\" (e.g. \"20 GHz to 44 GHz\")");
            builder.AppendLine("8. Return ONLY JSON (no markdown, no ```json```)");
            builder.AppendLine();
        }

        private void BuildMinTypMaxRules(StringBuilder builder)
        {
            builder.AppendLine("???????????????????????????????????????????????????????????????");
            builder.AppendLine("RULE #0 (MOST IMPORTANT): MIN/TYP/MAX VALUE SELECTION");
            builder.AppendLine("???????????????????????????????????????????????????????????????");
            builder.AppendLine();
            builder.AppendLine("When extracting values from specification tables with Min/Typ/Max columns:");
            builder.AppendLine();
            builder.AppendLine("IMPORTANT: This rule ONLY applies when a Min/Typ/Max table actually exists.");
            builder.AppendLine("If the datasheet lists the value only ONCE (e.g. in a Features bullet list,");
            builder.AppendLine("in a general Specifications section, or as a single stated value like");
            builder.AppendLine("'Gain: 20 dB' or 'Noise figure: 0.65 dB'), then extract THAT value directly.");
            builder.AppendLine("Do NOT return 'nicht klar erkennbar' just because there is no Min/Typ/Max column.");
            builder.AppendLine();
            builder.AppendLine("THE PROPERTY NAME TELLS YOU WHICH COLUMN TO READ (when the table exists):");
            builder.AppendLine();
            builder.AppendLine("  ??????????????????????????????????????????????????????????????????????");
            builder.AppendLine("  ? Property Name       ? Read from Column   ? Example                 ?");
            builder.AppendLine("  ??????????????????????????????????????????????????????????????????????");
            builder.AppendLine("  ? Contains \"Typ\"      ? TYP column ONLY    ? GainTypDb ? Typ = 45    ?");
            builder.AppendLine("  ? Contains \"Min\"      ? MIN column ONLY    ? VccMinV ? Min = 4.5     ?");
            builder.AppendLine("  ? Contains \"Max\"      ? MAX column ONLY    ? IsupMaxA ? Max = 1.2    ?");
            builder.AppendLine("  ? No suffix           ? TYP (default)      ? Frequency ? Typ         ?");
            builder.AppendLine("  ??????????????????????????????????????????????????????????????????????");
            builder.AppendLine();
        }

        private void BuildFrequencyRangeRules(StringBuilder builder)
        {
            builder.AppendLine("???????????????????????????????????????????????????????????????");
            builder.AppendLine("CRITICAL RULE: MULTIPLE FREQUENCY RANGES");
            builder.AppendLine("???????????????????????????????????????????????????????????????");
            builder.AppendLine();
            builder.AppendLine("When table has MULTIPLE frequency ranges (3 or more):");
            builder.AppendLine();
            builder.AppendLine("ALWAYS USE THE MEDIAN (MIDDLE) FREQUENCY RANGE!");
            builder.AppendLine();
            builder.AppendLine("SELECTION ALGORITHM:");
            builder.AppendLine("  IF 2 ranges  ? Use 1st range (lower frequency)");
            builder.AppendLine("  IF 3 ranges  ? Use 2nd range (median)");
            builder.AppendLine("  IF 4 ranges  ? Use 2nd or 3rd range (middle)");
            builder.AppendLine("  IF 5+ ranges ? Use middle range (count/2 + 1)");
            builder.AppendLine();
        }


        /// <summary>Prüft ob Komponente Pitch-Property hat und daher spezielle Guidelines benötigt</summary>
        private bool RequiresPitchGuidelines(CatalogComponent component, List<ComponentProperty> properties)
        {
            // Prüfe ob Pitch in Properties vorhanden
            bool hasPitch = properties.Any(p => 
                p.PropertyInternalName.Equals("Pitch", StringComparison.OrdinalIgnoreCase));
            
            if (!hasPitch)
                return false;
            
            // Komponenten, die typischerweise Pitch benötigen
            var pitchRelevantComponents = new[]
            {
                "Amplifier", "RF Amplifier", "OP Amplifier", "Power Amplifier",
                "Connector", "IC", "Microcontroller", "Processor",
                "Non-Volatile Memory Serial", "Non-Volatile Memory Parallel",
                "NV Memory", "Memory",
                "Interface IC"  // Interface ICs (SOIC/TSSOP/QFN-Gehäuse) benötigen Pitch aus Mechanical Drawings
            };
            
            return pitchRelevantComponents.Any(keyword => 
                component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Lädt Pitch-Extraction Guidelines aus Azure Blob Storage (Cached)
        /// Falls Azure nicht erreichbar, wird Fallback aus Embedded Resource verwendet
        /// </summary>
        private string LoadPitchGuidelinesIfNeeded()
        {
            if (_cachedPitchGuidelines != null)
                return _cachedPitchGuidelines;
            
            try
            {
                _logger.LogDebug("Loading pitch_extraction_guidelines.txt from Azure");
                _cachedPitchGuidelines = _remoteConfigService.LoadBlobContent("pitch_extraction_guidelines.txt");
                _logger.LogInformation("Pitch extraction guidelines loaded from Azure ({Length} characters)", 
                    _cachedPitchGuidelines.Length);
                return _cachedPitchGuidelines;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load from Azure - using embedded fallback");
                _cachedPitchGuidelines = GetEmbeddedPitchGuidelines();
                _logger.LogInformation("Pitch extraction guidelines loaded from embedded resource ({Length} characters)",
                    _cachedPitchGuidelines.Length);
                return _cachedPitchGuidelines;
            }
        }

        /// <summary>
        /// Fallback: Embedded Pitch-Extraction Guidelines (wird verwendet wenn Azure nicht verfügbar)
        /// TODO: Entfernen sobald Azure Blob Storage wieder verfügbar ist!
        /// </summary>
        private string GetEmbeddedPitchGuidelines()
        {
            return @"
???????????????????????????????????????????????????????????????????????
 SPECIAL INSTRUCTIONS FOR PITCH EXTRACTION (Technical Drawings)
???????????????????????????????????????????????????????????????????????

? CRITICAL: Pitch is often ONLY visible in mechanical drawings/package diagrams!
   Do NOT skip the ""Package Information and Dimensions"" section!

???????????????????????????????????????????????????????????????????????
?? WHERE TO FIND PITCH
???????????????????????????????????????????????????????????????????????

1. SECTION TITLES TO CHECK:
   - ""Package Information""
   - ""Package Dimensions""
   - ""Mechanical Outline""
   - ""Mechanical Drawing""
   - ""Package and Pin Dimensions""
   - ""Land Pattern""

2. VISUAL INDICATORS IN DRAWINGS:
   - Package drawings showing pin layout (top view / bottom view)
   - Dimension lines with arrows between pins: ? e ? or ? e
   - Dimension tables near the drawing
   - Symbol 'e' representing pin spacing
   - Maßlinien (measurement lines) with arrows

3. TYPICAL LOCATION IN PDF:
   - Usually in middle/end of datasheet (pages 5-15)
   - Often after ""Electrical Characteristics""
   - Before or after ""Application Information""

???????????????????????????????????????????????????????????????????????
?? WHAT IS PITCH
???????????????????????????????????????????????????????????????????????

DEFINITION:
Pin-to-Pin spacing = Center-to-center distance between TWO ADJACENT pins

COMMON NOTATION:
- Symbol: 'e' (in package drawings)
- Format examples:
  - ""0.50""
  - ""e = 0.5 mm""
  - ""pitch: 1.27mm""
  - ""pin spacing = 0.065"" (inches)

IMPORTANT DISTINCTIONS:
? Pitch = PIN-TO-PIN spacing (e.g., 0.5mm)
? NOT row-to-row spacing (e.g., 7.5mm body width)
? NOT package dimensions (e.g., 3.9mm x 4.9mm)

???????????????????????????????????????????????????????????????????????
? VALIDATION RULES
???????????????????????????????????????????????????????????????????????

MANDATORY CHECKS:
1. Value MUST be between 0.3mm and 5.0mm
   ? If outside this range, it's likely NOT the pitch!

2. If multiple dimensions exist:
   ? Choose the SMALLER value (pin-to-pin, NOT row-to-row)
   ? Example: 0.5mm (pitch) vs 3.9mm (body width) ? Use 0.5mm

3. Common realistic values:
   ? 0.4mm, 0.5mm, 0.65mm, 0.8mm, 1.0mm, 1.27mm, 2.0mm, 2.54mm
   ? 3.9mm, 7.5mm, 10mm ? These are package dimensions, NOT pitch!

4. Unit conversion:
   - If value is in inches, convert to mm
   - 0.1"" = 2.54mm
   - 0.05"" = 1.27mm
   - 0.025"" = 0.635mm

5. If unclear or not found:
   ? Return ""nicht gefunden"" (German!)
   ? NEVER guess or extrapolate!

???????????????????????????????????????????????????????????????????????
?? PACKAGE-SPECIFIC HINTS
???????????????????????????????????????????????????????????????????????

Package  | Typical Pitch | Where to Look               | Symbol
---------|---------------|----------------------------|--------
QFN      | 0.4 or 0.5mm  | Bottom view, dimension 'e' | e
QFP      | 0.5-0.8mm     | Side view, pin-to-pin      | e
SOIC     | 0.65 or 1.27  | Side view, pin spacing     | e
SOP      | 0.65 or 0.8   | Pin spacing on one side    | e
SSOP     | 0.5 or 0.65   | Pin-to-pin (fine pitch)    | e
DIP      | 2.54mm (0.1"") | Through-hole standard      | e

? COMMON MISTAKES TO AVOID:
? SOIC: Confusing 0.65mm (pitch) with 3.9mm (body width)
? QFN: Using package size (3x3mm) instead of pin spacing (0.5mm)
? DIP: Confusing 2.54mm (pitch) with 7.62mm (row spacing)

???????????????????????????????????????????????????????????????????????
?? VISION ANALYSIS STRATEGY (For Image-Based PDFs)
???????????????????????????????????????????????????????????????????????

STEP-BY-STEP APPROACH:

1. LOCATE MECHANICAL DRAWINGS:
   - Scan through all images
   - Focus on pages with package outlines
   - Look for dimensioned technical drawings

2. IDENTIFY DIMENSION LINES:
   - Look for arrows: ? ? or ?
   - Check for measurement lines between pins
   - Find dimension tables near drawings

3. READ DIMENSION VALUES:
   - Focus on small values (0.3-5mm range)
   - Check both horizontal and vertical dimensions
   - Verify unit (mm vs inch)

4. CROSS-REFERENCE:
   - Compare dimension symbol 'e' in drawing with table
   - Verify dimension label (e.g., ""e = pin pitch"")
   - Check legend/notes for symbol definitions

5. VALIDATE:
   - Is value in realistic range (0.3-5mm)?
   - Is it the smallest dimension (likely pin spacing)?
   - Does it match package type expectations?

???????????????????????????????????????????????????????????????????????
?? IF PITCH IS NOT CLEARLY VISIBLE
???????????????????????????????????????????????????????????????????????

ACCEPTABLE RESPONSES:
? ""nicht gefunden"" (when no mechanical drawing exists)
? ""nicht klar erkennbar"" (when drawing is too blurry)
? ""mehrdeutig"" (when multiple values are equally likely)

UNACCEPTABLE RESPONSES:
? Guessing based on package size
? Extrapolating from pin count
? Using body width as pitch
? Inventing a ""typical"" value without evidence

PRINCIPLE:
? ""nicht gefunden"" is BETTER than a WRONG value!
? Precision is more important than recall!

???????????????????????????????????????????????????????????????????????
 END OF PITCH EXTRACTION GUIDELINES
???????????????????????????????????????????????????????????????????????

REMEMBER:
?? Pitch is in TECHNICAL DRAWINGS (Package Dimensions section)
?? Look for dimension 'e' with arrows between pins
? Value must be 0.3-5mm (pin-to-pin spacing)
?? NOT package dimensions (those are much larger!)
?? ""nicht gefunden"" is BETTER than wrong value!
";
        }

        /// <summary>
        /// Prüft ob Komponente Memory-Guidelines benötigt
        /// WICHTIG: Sucht SPEZIFISCHE Memory-Typen (Serial/Parallel), nicht nur "Memory Components"
        /// </summary>
        private bool RequiresMemoryGuidelines(CatalogComponent component, List<ComponentProperty> properties)
        {
            // SPEZIFISCHE Memory-Komponenten (aus get_classes.csv Subkategorien)
            var memoryComponents = new[]
            {
                "Non-Volatile Memory Serial",      // SPI/I2C Memory (z.B. AT25xxx)
                "Non-Volatile Memory Parallel",    // Parallel Memory (z.B. AT28xxx)
                "NV Memory Serial",
                "NV Memory Parallel",
                "EEPROM Serial",
                "EEPROM Parallel",
                "Flash Serial",
                "Flash Parallel",
                "SRAM"
            };
            
            // Prüfe ob DisplayName oder InternalName EXAKT eine spezifische Memory-Kategorie ist
            bool isMemoryComponent = memoryComponents.Any(keyword =>
                component.DisplayName.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            
            if (!isMemoryComponent)
            {
                // Fallback: Prüfe auch FullPath (kann "/Memory Components/Non-Volatile Memory Serial" enthalten)
                isMemoryComponent = memoryComponents.Any(keyword =>
                    component.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }
            
            if (!isMemoryComponent)
                return false;
            
            // Prüfe ob Memory-spezifische Properties vorhanden sind
            bool hasMemoryProperties = properties.Any(p =>
                p.PropertyInternalName.StartsWith("tAA", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.StartsWith("tRC", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Equals("Capacity", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Equals("Size", StringComparison.OrdinalIgnoreCase) ||       // Size = Memory Capacity!
                p.PropertyInternalName.Equals("Organization", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Equals("Interface", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("Width", StringComparison.OrdinalIgnoreCase));
            
            if (isMemoryComponent && hasMemoryProperties)
            {
                _logger.LogDebug("Memory guidelines required for: {ComponentName} (Path: {FullPath})",
                    component.DisplayName, component.FullPath);
            }
            
            return hasMemoryProperties;
        }

        /// <summary>Lädt Memory-Extraction Guidelines aus Azure Blob Storage (Cached)</summary>
        private string? _cachedMemoryGuidelines;
        
        private string LoadMemoryGuidelinesIfNeeded()
        {
            if (_cachedMemoryGuidelines != null)
                return _cachedMemoryGuidelines;
            
            try
            {
                _logger.LogDebug("Loading memory_extraction_guidelines.txt from Azure");
                _cachedMemoryGuidelines = _remoteConfigService.LoadBlobContent("memory_extraction_guidelines.txt");
                _logger.LogInformation("Memory extraction guidelines loaded from Azure ({Length} characters)", 
                    _cachedMemoryGuidelines.Length);
                return _cachedMemoryGuidelines;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load memory_extraction_guidelines.txt - using embedded fallback");
                _cachedMemoryGuidelines = GetEmbeddedMemoryGuidelines();
                _logger.LogInformation("Memory extraction guidelines loaded from embedded resource ({Length} characters)",
                    _cachedMemoryGuidelines.Length);
                return _cachedMemoryGuidelines;
            }
        }

        /// <summary>Embedded Memory-Guidelines (Fallback wenn Azure nicht verfügbar)</summary>
        private string GetEmbeddedMemoryGuidelines()
        {
            return @"
???????????????????????????????????????????????????????????????
 MEMORY COMPONENT EXTRACTION GUIDELINES (Serial vs. Parallel)
???????????????????????????????????????????????????????????????

?? CRITICAL: This datasheet is for a MEMORY COMPONENT!
   The extraction rules are DIFFERENT from standard components!

???????????????????????????????????????????????????????????????
?? STEP 1: DETERMINE MEMORY TYPE (Serial vs. Parallel)
???????????????????????????????????????????????????????????????

Look for these indicators:

SERIAL MEMORY (SPI/I2C/UFS):
  ? Pin count: 6-8 pins (VCC, GND, SCK, MOSI, MISO, CS) OR 153+ pins (UFS, eMMC)
  ? Interface: SPI, I2C, Microwire, UFS, eMMC
  ? Access time: µs range (microseconds) or MHz/GHz clock rates
  ? Example: AT25xxx, 25LCxxx, M95xxx, THGBMDG5D1LBAIL (UFS)
  ? Key features: Serial data transmission, embedded controllers

PARALLEL MEMORY:
  ? Pin count: 20-48+ pins (address bus A0-A15, data bus D0-D7)
  ? Interface: Parallel, Asynchronous
  ? Access time: ns range (nanoseconds)
  ? Example: AT28xxx, 28Cxxx, MT29F2G08

???????????????????????????????????????????????????????????????
?? PROPERTY EXTRACTION RULES
???????????????????????????????????????????????????????????????

0?? FUNKTION = MEMORY TECHNOLOGY TYPE (CLASSIFICATION, NOT EXTRACTION!)
   ?????????????????????????????????????????????????????????????????????????????
   ?? CRITICAL: ""Funktion"" is NOT extracted from PDF text!
      You must CLASSIFY the memory technology based on datasheet content!
   
   AVAILABLE OPTIONS (choose ONE that best matches):
   
   **FOR PARALLEL MEMORY:**
   - ""NAND Flash SLC"" (Single-Level Cell)
   - ""NAND Flash Single-Level Cell""
   - ""NAND Flash MLC"" (Multi-Level Cell)
   - ""NAND Flash Multi-Level Cell""
   - ""NOR Flash""
   - ""NOR Flash Single-Level Cell""
   - ""EEPROM""
   - ""FRAM"" (Ferroelectric RAM)
   - ""Magnetoresistive RAM"" (MRAM)
   - ""Ferroelectric RAM""
   
   **FOR SERIAL MEMORY:**
   - ""Flash"" (Generic Flash Memory)
   - ""EEPROM"" (Electrically Erasable Programmable Read-Only Memory)
   - ""PROM"" (Programmable Read-Only Memory)
   - ""ROM"" (Read-Only Memory)
   - ""Flash"" (NAND Flash in Serial interface, e.g. UFS, eMMC)
   - ""FRAM"" (Ferroelectric RAM with serial interface)
   - ""MRAM"" (Magnetoresistive RAM with serial interface)
   - ""eMMC"" (embedded Multi Media Card Flash IC)
   
   CLASSIFICATION RULES:
   
   1. **NAND Flash SLC** - Indicators:
      - Title mentions ""NAND"" + (""SLC"" or ""Single-Level Cell"")
      - Features: High endurance (100K+ cycles), fast program/erase
      - Organization: Large capacity (>=1Gbit), page-based access
      - NO ""MLC"" or ""TLC"" mentioned
   
   2. **NAND Flash MLC** - Indicators:
      - Title mentions ""NAND"" + (""MLC"" or ""Multi-Level Cell"")
      - Features: Lower cost per bit, higher density
      - Moderate endurance (10K cycles)
   
   3. **NOR Flash** - Indicators:
      - Title mentions ""NOR Flash"" or ""Parallel Flash""
      - Features: Random access, XIP (execute-in-place)
      - Organization: Byte/word addressable
      - Typical sizes: 1-512 Mbit
   
   4. **EEPROM** - Indicators:
      - Title mentions ""EEPROM"" (NOT Flash!)
      - Features: Byte-level erase, high endurance (1M cycles)
      - Organization: Small capacity (<=4Mbit)
      - Serial interface (I2C/SPI) or Parallel
   
   5. **FRAM** - Indicators:
      - Title mentions ""F-RAM"", ""FRAM"", or ""Ferroelectric""
      - Features: Fast write (ns), virtually unlimited endurance
      - NO erase cycles needed
   
   6. **Magnetoresistive RAM (MRAM)** - Indicators:
      - Title mentions ""MRAM"" or ""Magnetoresistive""
      - Features: Non-volatile, fast access, high endurance
   
   DECISION ALGORITHM:
   ```
   // FOR PARALLEL MEMORY:
   IF title contains ""NAND"":
       IF mentions ""SLC"" or ""Single-Level"": return ""NAND Flash SLC""
       ELSE IF mentions ""MLC"" or ""Multi-Level"": return ""NAND Flash MLC""
       ELSE: return ""NAND Flash SLC"" (default for NAND)
   
   ELSE IF title contains ""NOR"":
       return ""NOR Flash""
   
   ELSE IF title contains ""EEPROM"":
       return ""EEPROM""
   
   ELSE IF title contains ""FRAM"" or ""F-RAM"" or ""Ferroelectric"":
       return ""FRAM""
   
   ELSE IF title contains ""MRAM"" or ""Magnetoresistive"":
       return ""Magnetoresistive RAM""
   
   // FOR SERIAL MEMORY (NEW):
   ELSE IF title contains ""UFS"" or ""Universal Flash Storage"":
       return ""Flash"" (UFS is NAND Flash with serial interface)
   
   ELSE IF title contains ""eMMC"" or ""embedded Multi Media Card"":
       return ""eMMC""
   
   ELSE IF title contains ""Serial EEPROM"" or (""EEPROM"" AND ""SPI""):
       return ""EEPROM""
   
   ELSE IF title contains ""Serial Flash"" or (""Flash"" AND ""SPI""):
       return ""Flash""
   
   ELSE IF title contains ""PROM"":
       return ""PROM""
   
   ELSE IF title contains ""ROM"" (and NOT ""EEPROM"", NOT ""PROM""):
       return ""ROM""
   
   ELSE:
       return ""nicht vorhanden"" (unable to classify)
   ```
   
   EXAMPLE CLASSIFICATIONS:
   
   **PARALLEL MEMORY:**
   ? ""2Gb: x8, x16 NAND Flash Memory"" -> ""NAND Flash SLC"" (no MLC mentioned)
   ? ""128-Megabit CMOS 3.0 Volt Flash Memory"" -> ""NOR Flash"" (parallel interface)
   ? ""1-Mbit Parallel EEPROM"" -> ""EEPROM""
   ? ""4-Mbit F-RAM Memory"" -> ""FRAM""
   
   **SERIAL MEMORY:**
   ? ""UFS 2.1 Device with NAND Flash Memory"" -> ""Flash"" (UFS = serial NAND)
   ? ""128GB eMMC 5.1 Flash Memory"" -> ""eMMC""
   ? ""1-Mbit Serial EEPROM"" -> ""EEPROM""
   ? ""64-Mbit SPI Flash Memory"" -> ""Flash""
   ? ""256Kb I2C EEPROM"" -> ""EEPROM""
   
    NEVER:
   ? Return ""Memory"" (too generic!)
   ? Return ""nicht vorhanden"" if classification is possible
   ? Extract this from a table (it's a CLASSIFICATION task!)



1?? SIZE / SIZEBYTE = MEMORY CAPACITY (NOT PHYSICAL DIMENSION!)
???????????????????????????????????????????????????????????????
?? CRITICAL: In Memory Components, ""SizeByte"" means MEMORY SIZE!
   
CORRECT EXAMPLES:
? SizeByte = ""1 Mbit""     (from ""1 Megabit EEPROM"")
? SizeByte = ""128 Kbit""   (from ""128K x 8"")
? SizeByte = ""256 Kbit""   (from ""32K x 8"")
? SizeByte = ""64 GB""      (from ""64GB eMMC"" or ""64GB UFS"")
? SizeByte = ""128 GB""     (from ""128GB Universal Flash Storage"")
   
WRONG EXAMPLES:
? SizeByte = ""3.9 mm""     (that's package width!)
? SizeByte = ""8 mm""       (that's package length!)
? SizeByte = ""0.5 mm""     (that's pin pitch!)
   
WHERE TO FIND:
? First page title: ""64GB UFS 2.1 Device""
? First page title: ""128GB eMMC 5.1""
? Organization section: ""128K x 8""
? Features section: ""256 Kbit of EEPROM""
? DO NOT look in ""Package Dimensions"" section!
   
SERIAL MEMORY SPECIFIC:
? UFS/eMMC: Capacity in GB (e.g., ""64 GB"", ""128 GB"", ""256 GB"")
? SPI Flash: Capacity in Mbit (e.g., ""64 Mbit"", ""128 Mbit"")
? I2C EEPROM: Capacity in Kbit (e.g., ""256 Kbit"", ""1 Mbit"")

2?? WIDTH = DATA BUS WIDTH (NOT PHYSICAL DIMENSION!)
   ???????????????????????????????????????????????????????????????
   Width = Number of data bits per word
   
   CORRECT EXAMPLES:
   ? Width = ""8 bit""      (from Organization: ""8K x 8"")
   ? Width = ""16 bit""     (from Organization: ""16K x 16"")
   ? Width = ""1 bit""      (Serial EEPROM with single data line)
   
   WRONG EXAMPLES:
   ? Width = ""3.9 mm""     (that's package body width!)
   ? Width = ""5.0 mm""     (that's package length!)
   
   WHERE TO FIND:
   ? Organization: ""128K x 8"" ? Second number = Width (8 bit)
   ? If property ""Width_Max"" exists ? Read from MAX column
   ? Serial Memory: Usually 1 bit (single data line)

3?? TIMING PARAMETERS (tAA, tRC, ClockRateHz, etc.)
???????????????????????????????????????????????????????????????
   
**FOR PARALLEL MEMORY:**
tAA = Address Access Time
?? ONLY for PARALLEL Memory! Serial Memory: ""nicht relevant""
   
EXTRACTION RULES:
- tAA_Max -> Read from MAX column (e.g., ""120 ns"")
- tRC_Min -> Read from MIN column (e.g., ""120 ns"")
- Unit MUST be included (ns for Parallel, µs for Serial)
   
IF Serial Memory AND property is ""tAA_Max"":
? Return ""nicht relevant"" (Serial has no address access time!)
   
**FOR SERIAL MEMORY:**
ClockRateHz = Serial Clock Frequency
?? ONLY for SERIAL Memory! Parallel Memory: ""nicht relevant""
   
EXTRACTION RULES:
- Look for ""Clock Frequency"", ""SCK Frequency"", ""Interface Speed""
- Common values:
  - SPI/I2C EEPROM: 100 KHz to 50 MHz
  - UFS: 2.9 GHz (HS-G3), 5.8 GHz (HS-G4)
  - eMMC: 200 MHz (HS200), 400 MHz (HS400)
- Unit MUST be included: ""MHz"", ""GHz"", ""KHz""
   
WHERE TO FIND (Serial Memory):
? ""Interface Specifications"" section
? ""Clock Frequency"" in electrical characteristics
? ""HS-G3 mode: 2.9 GHz"" (for UFS)
? ""HS400 mode: 400 MHz"" (for eMMC)
? ""Max SPI Clock: 50 MHz"" (for SPI Flash)
   
EXAMPLES:
? ClockRateHz = ""2.9 GHz"" (UFS 2.1 HS-G3)
? ClockRateHz = ""400 MHz"" (eMMC 5.1 HS400)
? ClockRateHz = ""50 MHz"" (SPI Flash)
? ClockRateHz = ""1 MHz"" (I2C EEPROM)

4?? CAPACITY / ORGANIZATION
   ???????????????????????????????????????????????????????????????
   
   Extract EXACTLY as written in datasheet!
   
   EXAMPLES:
   ? Capacity = ""1 Mbit""
   ? Organization = ""128K x 8""
   ? Capacity = ""256 Kbit""
   
   DO NOT:
   ? Convert ""1 Mbit"" to ""1048576 bits""
   ? Convert ""128K x 8"" to ""1 Mbit""
   ? Calculate Size from Organization

5?? INTERFACE
   ???????????????????????????????????????????????????????????????
   
   SERIAL MEMORY:
   ? ""SPI"", ""I2C"", ""Microwire"", ""2-Wire""
   
   PARALLEL MEMORY:
   ? ""Parallel"", ""Asynchronous"", ""Address/Data Bus""

???????????????????????????????????????????????????????????????
?? COMMON MISTAKES TO AVOID
???????????????????????????????????????????????????????????????

? MISTAKE #1: Confusing Size with Package Dimensions
   WRONG: Size = ""3.9 mm"" (from Package Drawing body width)
   RIGHT: Size = ""1 Mbit"" (from title ""1-Mbit EEPROM"")

? MISTAKE #2: Confusing Width with Package Width
   WRONG: Width = ""5.0 mm"" (from Package Drawing)
   RIGHT: Width = ""8 bit"" (from Organization ""128K x 8"")

? MISTAKE #3: Extracting tAA for Serial Memory
   WRONG: tAA_Max = ""120 ns"" (for Serial EEPROM)
   RIGHT: tAA_Max = ""nicht relevant"" (Serial has no address bus!)

? MISTAKE #4: Converting Organization to Size
   WRONG: Size = ""1048576 bits"" (calculated from 128K x 8)
   RIGHT: Size = ""1 Mbit"" (extracted from title/description)

???????????????????????????????????????????????????????????????
?? EXTRACTION CHECKLIST
???????????????????????????????????????????????????????????????

1. ? Determine Memory Type: Serial or Parallel?
2. ? Size: Look in TITLE/FEATURES (NOT Package Dimensions!)
3. ? Width: Look in ORGANIZATION (NOT Package Drawing!)
4. ? Timing: Read correct MIN/TYP/MAX column (include unit!)
5. ? Interface: SPI/I2C (Serial) or Parallel?
6. ? Validate: Size/Width should be in bits, NOT mm!

???????????????????????????????????????????????????????????????
 END OF MEMORY EXTRACTION GUIDELINES
???????????????????????????????????????????????????????????????

REMEMBER:
?? Size = MEMORY CAPACITY (Mbit, Kbit) - NOT physical dimension!
?? Width = DATA BUS WIDTH (bit) - NOT package width!
?? tAA only for Parallel Memory (Serial: ""nicht relevant"")
?? Extract values EXACTLY as written (no conversion!)
???????????????????????????????????????????????????????????????";
        }

        /// <summary>
        /// Prüft ob Komponente Coil/Inductor-Guidelines benötigt.
        /// Coil-Datenblätter enthalten genau EINE Komponente -> kein Part Number Decoding.
        /// </summary>
        private bool RequiresCoilGuidelines(CatalogComponent component, List<ComponentProperty> properties)
        {
            var coilKeywords = new[]
            {
                "Coil", "Inductor", "Spule", "Choke", "Drossel"
            };

            bool isCoilComponent = coilKeywords.Any(keyword =>
                component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (!isCoilComponent)
                return false;

            // Plausibilitätscheck: typische Coil-Properties vorhanden?
            bool hasCoilProperties = properties.Any(p =>
                p.PropertyInternalName.Contains("Inductance", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("Induktivit", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("DCR", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("ISat", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("IRms", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Equals("SRF", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Equals("Funktion", StringComparison.OrdinalIgnoreCase));

            if (hasCoilProperties)
            {
                _logger.LogDebug("Coil guidelines required for: {ComponentName} (Path: {FullPath})",
                    component.DisplayName, component.FullPath);
            }

            return hasCoilProperties;
        }

        private string? _cachedCoilGuidelines;

        /// <summary>Lädt Coil-Extraction Guidelines aus Azure Blob Storage (Cached) mit Embedded-Fallback.</summary>
        private string LoadCoilGuidelinesIfNeeded()
        {
            if (_cachedCoilGuidelines != null)
                return _cachedCoilGuidelines;

            try
            {
                _logger.LogDebug("Loading coil_extraction_guidelines.txt from Azure");
                _cachedCoilGuidelines = _remoteConfigService.LoadBlobContent("coil_extraction_guidelines.txt");
                _logger.LogInformation("Coil extraction guidelines loaded from Azure ({Length} characters)",
                    _cachedCoilGuidelines.Length);
                return _cachedCoilGuidelines;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load coil_extraction_guidelines.txt from Azure - using embedded fallback");
                _cachedCoilGuidelines = GetEmbeddedCoilGuidelines();
                _logger.LogInformation("Coil extraction guidelines loaded from embedded resource ({Length} characters)",
                    _cachedCoilGuidelines.Length);
                return _cachedCoilGuidelines;
            }
        }

        /// <summary>
        /// Embedded Fallback der Coil-Guidelines. Funktions-Klassifikation: INDUCTOR, INDUCTOR SHIELDED, MULTILAYER CHIP, POWER CHOKE, POWER CHOKE SHIELDED, TUNABLE, WIDEBAND BIAS CHOKE, WIRE WOUND.
        /// </summary>
        private string GetEmbeddedCoilGuidelines()
        {
            return @"
===============================================================
 COIL / INDUCTOR EXTRACTION GUIDELINES
===============================================================

CRITICAL: This datasheet describes a COIL / INDUCTOR component!
   Coil datasheets describe EXACTLY ONE component variant
   -> NO Part Number Decoding required
   -> Extract ALL parameters DIRECTLY from the datasheet

STEP 1: CLASSIFY THE COIL FUNCTION (Funktion)
--------------------------------------------------------------
'Funktion' is a CLASSIFICATION (not a free-text extraction!)
You MUST select EXACTLY ONE of the following allowed values:

   ENGLISH (DMS value)     | German display (reference)
   ------------------------+-----------------------------
   INDUCTOR                | SPULE
   INDUCTOR SHIELDED       | SPULE GESCHIRMT
   MULTILAYER CHIP         | (Multilayer Chip Inductor)
   POWER CHOKE             | LEISTUNGSDROSSEL
   POWER CHOKE SHIELDED    | LEISTUNGSDROSSEL GESCHIRMT
   TUNABLE                 | ABSTIMMBAR
   WIDEBAND BIAS CHOKE     | BREITBAND DROSSEL
   WIRE WOUND              | DRAHTGEWICKELT

DECISION TREE:
  1. 'Tunable' / 'Variable Inductor'        -> TUNABLE
  2. 'Wideband Bias' / 'Bias Choke' /
     'RF Bias' / 'Conical Inductor'         -> WIDEBAND BIAS CHOKE
  3. 'Multilayer' / 'Chip Inductor' /
     'Thin Film Inductor' (small SMD)       -> MULTILAYER CHIP
  4. 'Wire Wound' / 'Wirewound'
     (NOT Power Choke)                      -> WIRE WOUND
  5. 'Power Inductor' / 'Power Choke' /
     'Leistungsdrossel' / 'DC-DC Inductor'
       a) AND 'Shielded' / 'Magnetic
          Shielding' / 'Geschirmt'          -> POWER CHOKE SHIELDED
       b) Otherwise                         -> POWER CHOKE
  6. 'Shielded Inductor' (without 'Power')  -> INDUCTOR SHIELDED
  7. Default / generic inductor             -> INDUCTOR

STEP 2: PARAMETER EXTRACTION
--------------------------------------------------------------
  Induktivitaet / L [H]   : 'Inductance', 'L', 'Nominal
                            Inductance' (TYP value, e.g. 10 uH)
  Toleranz [%]            : 'Tolerance' (e.g. +/-15%)
  Widerstand / DCR [Ohm]  : 'DCR', 'DC Resistance', 'Rdc'
                            (keep mOhm notation, e.g. 8.76 mOhm)
  Guete / Q               : 'Q', 'Quality Factor' (MIN @ freq)
  Frequenz (Q)            : Frequency at which Q was measured
  f Nom (Typ) [Hz]        : Test frequency for L (100 kHz, 1 MHz)
  I sat / Isat [A]        : 'Saturation Current' (-30% L)
  I rms / Irms / Strom    : 'Rated Current', 'Temp. Rise Current'
  SRF [Hz]                : 'Self Resonant Frequency' (MIN value)
  Gehaeusetyp             : 'SMD' or 'THT' (Surface Mount -> SMD)
  Hoehe [mm]              : MAX height (Z) from package drawing
  Oberflaeche             : Termination/plating (Sn, Sn/Pb, AgPd)

STEP 3: VALUE FORMATTING
--------------------------------------------------------------
  * Keep ORIGINAL units (uH, nH, mOhm, A, MHz)
  * Decimal separator: dot ('.'), NOT comma
  * Do NOT silently convert units
  * Tolerances: keep sign ('+/-15%')
  * If value cannot be determined: 'nicht vorhanden'
  * NEVER guess values!

COMMON MISTAKES TO AVOID
--------------------------------------------------------------
  X Using package length as Hoehe (use Z dimension!)
  X Confusing Isat (saturation) with Irms (thermal/rated)
  X Returning 'Choke' instead of one of the 8 enum values
  X Reporting Q without its measurement frequency
  X Trying to decode a part number (single-component datasheet!)

END OF COIL EXTRACTION GUIDELINES
===============================================================";
        }

        /// <summary>
        /// Prüft ob Komponente EMI-Filter-Guidelines benötigt.
        /// EMI-Filter-Datenblätter enthalten genau EINE Komponente -> kein Part Number Decoding.
        /// </summary>
        private bool RequiresEmiFilterGuidelines(CatalogComponent component, List<ComponentProperty> properties)
        {
            var emiFilterKeywords = new[]
            {
                "EMI Filter", "EMI-Filter", "EMIFilter",
                "Entstörfilter", "Entstoerfilter",
                "Ferrite Bead", "Ferritperle",
                "Common Mode Choke", "Gleichtaktdrossel",
                "Feedthrough Filter", "Durchführungsfilter", "Durchfuehrungsfilter",
                "LC Filter", "PI Filter",
                "Power Line Filter", "Netzfilter",
                "Signal Line Filter", "Signalfilter"
            };

            bool isEmiFilterComponent = emiFilterKeywords.Any(keyword =>
                component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (!isEmiFilterComponent)
                return false;

            // Plausibilitätscheck: typische EMI-Filter-Properties vorhanden?
            bool hasEmiFilterProperties = properties.Any(p =>
                p.PropertyInternalName.Contains("Impedanz", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("Impedance", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("Insertion", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("Einfuegedaempfung", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("Einfügedämpfung", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Contains("DCR", StringComparison.OrdinalIgnoreCase) ||
                p.PropertyInternalName.Equals("Funktion", StringComparison.OrdinalIgnoreCase));

            if (hasEmiFilterProperties)
            {
                _logger.LogDebug("EMI-Filter guidelines required for: {ComponentName} (Path: {FullPath})",
                    component.DisplayName, component.FullPath);
            }

            return hasEmiFilterProperties;
        }

        private string? _cachedEmiFilterGuidelines;

        /// <summary>Lädt EMI-Filter-Extraction Guidelines aus Azure Blob Storage (Cached) mit Embedded-Fallback.</summary>
        private string LoadEmiFilterGuidelinesIfNeeded()
        {
            if (_cachedEmiFilterGuidelines != null)
                return _cachedEmiFilterGuidelines;

            try
            {
                _logger.LogDebug("Loading emi_filter_extraction_guidelines.txt from Azure");
                _cachedEmiFilterGuidelines = _remoteConfigService.LoadBlobContent("emi_filter_extraction_guidelines.txt");
                _logger.LogInformation("EMI-Filter extraction guidelines loaded from Azure ({Length} characters)",
                    _cachedEmiFilterGuidelines.Length);
                return _cachedEmiFilterGuidelines;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load emi_filter_extraction_guidelines.txt from Azure - using embedded fallback");
                _cachedEmiFilterGuidelines = GetEmbeddedEmiFilterGuidelines();
                _logger.LogInformation("EMI-Filter extraction guidelines loaded from embedded resource ({Length} characters)",
                    _cachedEmiFilterGuidelines.Length);
                return _cachedEmiFilterGuidelines;
            }
        }

        /// <summary>
        /// Embedded Fallback der EMI-Filter-Guidelines.
        /// </summary>
        private string GetEmbeddedEmiFilterGuidelines()
        {
            return @"
===============================================================
 EMI FILTER EXTRACTION GUIDELINES
===============================================================

CRITICAL: This datasheet describes an EMI FILTER component!
   EMI-Filter datasheets describe EXACTLY ONE component variant
   -> NO Part Number Decoding required
   -> Extract ALL parameters DIRECTLY from the datasheet

STEP 1: CLASSIFY THE EMI FILTER FUNCTION (Funktion)
--------------------------------------------------------------
'Funktion' is a CLASSIFICATION (not a free-text extraction!)
You MUST select EXACTLY ONE of the following allowed values:

   ENGLISH (DMS value)     | German display (reference)
   ------------------------+-----------------------------
   EMI FILTER              | ENTSTOERFILTER
   FERRITE BEAD            | FERRITPERLE
   COMMON MODE CHOKE       | GLEICHTAKTDROSSEL
   FEEDTHROUGH FILTER      | DURCHFUEHRUNGSFILTER
   LC FILTER               | (LC-Filter)
   PI FILTER               | (PI-Filter)
   POWER LINE FILTER       | NETZFILTER
   SIGNAL LINE FILTER      | SIGNALFILTER

DECISION TREE:
  1. 'Ferrite Bead' / 'Chip Bead' / 'BLM' series  -> FERRITE BEAD
  2. 'Common Mode' / 'CMC' / 'Gleichtakt'         -> COMMON MODE CHOKE
  3. 'Feedthrough' / 'Feed-thru'                  -> FEEDTHROUGH FILTER
  4. 'Pi Filter' / 'PI-Type' / 'T-Filter'         -> PI FILTER
  5. 'LC Filter' / 'LC-Type'                      -> LC FILTER
  6. 'Power Line' / 'AC Line' / 'Netzfilter'      -> POWER LINE FILTER
  7. 'Signal Line' / 'Data Line Filter'           -> SIGNAL LINE FILTER
  8. Default / generic EMI suppression filter     -> EMI FILTER

STEP 2: PARAMETER EXTRACTION
--------------------------------------------------------------
  Impedanz / Z [Ohm]       : 'Impedance', 'Z @ 100MHz'
                             ALWAYS include test frequency!
  Frequenz (Z) [Hz]        : Frequency at which Z was measured
  Einfuegedaempfung [dB]   : 'Insertion Loss', 'Attenuation', 'S21'
  Frequenzbereich [Hz]     : Operating/attenuation band (min to max)
  Widerstand / DCR [Ohm]   : 'DC Resistance', 'Rdc' (mOhm or Ohm)
  Nennstrom / Irms [A]     : 'Rated Current' (mA or A)
  Nennspannung [V]         : 'Rated Voltage' (keep AC/DC suffix)
  Kapazitaet / C [F]       : Only LC/PI/Power Line filters (Cx, Cy)
  Induktivitaet / L [H]    : Only LC/PI/CMC filters
  Gehaeusetyp              : 'SMD' or 'THT' (Surface Mount -> SMD)
  Hoehe [mm]               : MAX height (Z) from package drawing
  Oberflaeche              : Termination/plating (Sn, Sn/Pb, Lead Free)

STEP 3: VALUE FORMATTING
--------------------------------------------------------------
  * Keep ORIGINAL units (Ohm, dB, MHz, mA, V)
  * Decimal separator: dot ('.'), NOT comma
  * Impedance MUST include test frequency ('600 Ohm @ 100 MHz')
  * Ranges: 'min to max' ('150 kHz to 30 MHz')
  * Keep AC/DC suffix on voltages ('250 V AC', '100 V DC')
  * If value cannot be determined: 'nicht vorhanden'
  * NEVER guess values!

COMMON MISTAKES TO AVOID
--------------------------------------------------------------
  X Reporting impedance without measurement frequency
  X Confusing DCR (DC, mOhm) with AC impedance (RF, Ohm)
  X Using package length/width as Hoehe (use Z dimension!)
  X Returning 'Filter' instead of one of the 8 enum values
  X Skipping Cx / Cy distinction on Power Line Filters
  X Trying to decode a part number (single-component datasheet!)

END OF EMI FILTER EXTRACTION GUIDELINES
===============================================================";
        }

        private Dictionary<string, string> LoadPropertyDefinitions()
        {
            var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                _logger.LogDebug("Trying to load property_definitions.txt from Azure");
                string content = _remoteConfigService.LoadBlobContent("property_definitions.txt");

                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length == 2)
                    {
                        string propertyName = parts[0].Trim();
                        string definition = parts[1].Split('|')[0].Trim();
                        definitions[propertyName] = definition;
                    }
                }

                _logger.LogInformation("{DefinitionCount} property definitions loaded", definitions.Count);
            }
            catch
            {
                _logger.LogWarning("property_definitions.txt not found - using without explanations");
            }

            return definitions;
        }

        /// <summary>
        /// Lädt eine kataloggruppen-spezifische Guideline per Namenskonvention aus Azure Blob Storage.
        /// </summary>
        private string? LoadGenericGuidelineFor(CatalogComponent component)
        {
            if (component == null || string.IsNullOrWhiteSpace(component.DisplayName))
                return null;

            string slug = SlugifyForBlobName(component.DisplayName);
            if (string.IsNullOrEmpty(slug))
                return null;

            string blobName = slug + "_extraction_guidelines.txt";

            if (_cachedGenericGuidelines.TryGetValue(blobName, out var cached))
            {
                return cached;
            }

            try
            {
                _logger.LogDebug("Trying to load generic guideline '{BlobName}' from Azure", blobName);
                string content = _remoteConfigService.LoadBlobContent(blobName);

                if (string.IsNullOrWhiteSpace(content))
                {
                    _cachedGenericGuidelines[blobName] = null;
                    _logger.LogDebug("Generic guideline '{BlobName}' is empty - ignored", blobName);
                    return null;
                }

                if (content.Length > MaxGenericGuidelineLength)
                {
                    _logger.LogWarning(
                        "Generic guideline '{BlobName}' is {Length} chars (limit {Limit}) - truncated",
                        blobName, content.Length, MaxGenericGuidelineLength);
                    content = content.Substring(0, MaxGenericGuidelineLength);
                }

                _cachedGenericGuidelines[blobName] = content;
                _logger.LogInformation(
                    "Generic guideline '{BlobName}' loaded from Azure ({Length} characters)",
                    blobName, content.Length);
                return content;
            }
            catch (Exception ex)
            {
                // Blob nicht vorhanden oder anderer Fehler -> negativ cachen und still ignorieren.
                _cachedGenericGuidelines[blobName] = null;
                _logger.LogDebug(ex,
                    "No generic guideline found for '{DisplayName}' (Blob: {BlobName})",
                    component.DisplayName, blobName);
                return null;
            }
        }

        /// <summary>
        /// Wandelt einen Katalog-DisplayName in einen kleingeschriebenen, Blob-tauglichen Slug um.
        /// </summary>
        private static string SlugifyForBlobName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var sb = new StringBuilder(input.Length);
            bool lastWasUnderscore = false;

            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasUnderscore = false;
                }
                else
                {
                    if (!lastWasUnderscore && sb.Length > 0)
                    {
                        sb.Append('_');
                        lastWasUnderscore = true;
                    }
                }
            }

            return sb.ToString().Trim('_');
        }

        private string GetPropertyExplanation(string propertyName, Dictionary<string, string> definitions)
        {
            if (definitions != null && definitions.ContainsKey(propertyName))
            {
                return definitions[propertyName];
            }
            return "";
        }

        public List<string> GetAllCategories()
        {
            return _catalogComponents.Values
                .Select(c => c.Category)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();
        }

        public List<CatalogComponent> GetComponentsByCategory(string category)
        {
            return _catalogComponents.Values
                .Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.DisplayName)
                .ToList();
        }

        public List<CatalogComponent> GetAllComponents()
        {
            return _catalogComponents.Values
                .OrderBy(c => c.Category)
                .ThenBy(c => c.DisplayName)
                .ToList();
        }

        /// <summary>
        /// Prüft ob Komponente ein Series Datasheet ist (Part Number Decoding erforderlich)
        /// Series Datasheets enthalten multiple Component-Varianten mit Part Number Chart
        /// </summary>
        /// <param name="component">Zu prüfende Komponente</param>
        /// <returns>True wenn Part Number Decoding benötigt wird</returns>
        public bool IsSeriesDatasheet(CatalogComponent component)
        {
            if (component == null)
                return false;

            // Komponenten die IMMER Part Number Decoding benötigen
            var seriesComponents = new[]
            {
                "Resistor",                          // Widerstände (Yageo, Vishay, etc.)
                "Non-Volatile Memory Serial",        // Serial Memory (z.B. AT25xxx, M95xxx)
                "Non-Volatile Memory Parallel",      // Parallel Memory (z.B. MT28EW512A, AT28xxx)
                "NV Memory Serial",
                "NV Memory Parallel",
                "EEPROM Serial",
                "EEPROM Parallel",
                "Flash Serial",
                "Flash Parallel",
                "Interface IC"                       // Interface ICs (verschiedene Typen: Controller, Driver, Transceiver, etc.)
            };

            bool isSeriesComponent = seriesComponents.Any(keyword =>
                component.DisplayName.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                component.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (!isSeriesComponent)
            {
                // Fallback: Prüfe auch FullPath
                isSeriesComponent = seriesComponents.Any(keyword =>
                    component.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            if (isSeriesComponent)
            {
                _logger.LogDebug("Component {ComponentName} requires part number decoding (Series Datasheet)",
                    component.DisplayName);
            }

            return isSeriesComponent;
        }
    }

    #region Data Models

    public class CatalogComponent
    {
        public int RowNumber { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public string ComponentType { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ComponentName { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string AdditionalInfo { get; set; } = string.Empty;

        public string ComponentPath => FullPath;

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(ComponentName))
            {
                return $"{DisplayName} - {ComponentName} ({InternalName})";
            }
            return $"{DisplayName} ({InternalName}) - {Category}";
        }
    }

    public class ComponentProperty
    {
        public string ComponentPath { get; set; } = string.Empty;
        public string PropertyGroup { get; set; } = string.Empty;
        public string PropertyNumber { get; set; } = string.Empty;
        public string PropertyInternalName { get; set; } = string.Empty;
        public string PropertyDisplayName { get; set; } = string.Empty;
        public string PropertyType { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{PropertyGroup}.{PropertyDisplayName} ({PropertyInternalName})";
        }
    }

    #endregion
}
