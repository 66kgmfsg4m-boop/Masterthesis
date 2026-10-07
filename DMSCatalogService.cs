using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// Service zum Laden und Verarbeiten der DMS-Katalogdaten aus CSV-Dateien.
    /// Lädt Komponenten-Definitionen und Properties aus Azure Blob Storage.
    /// Generiert dynamische Prompts für KI-basierte Extraktion basierend auf Katalog-Schema.
    /// </summary>
    public class DMSCatalogService
    {
        private readonly RemoteConfigService _remoteConfigService;
        private Dictionary<string, CatalogComponent> _catalogComponents;
        private Dictionary<string, List<ComponentProperty>> _componentProperties;
        private Dictionary<string, Dictionary<string, string>> _valueLists;

        public DMSCatalogService()
        {
            _remoteConfigService = new RemoteConfigService("DMSCatalog-" + Guid.NewGuid());
            _catalogComponents = new Dictionary<string, CatalogComponent>();
            _componentProperties = new Dictionary<string, List<ComponentProperty>>();
            _valueLists = new Dictionary<string, Dictionary<string, string>>();
        }

        public void LoadCatalogData()
        {
            Console.WriteLine("\n=== LADE DMS KATALOGDATEN ===");
            try
            {
                LoadClasses();
                Console.WriteLine($"Katalog geladen: {_catalogComponents.Count} Komponenten, {_componentProperties.Count} Property-Gruppen");
                Console.WriteLine("=== KATALOG BEREIT ===\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler beim Laden der Katalogdaten: {ex.Message}");
                throw;
            }
        }

        private void LoadClasses()
        {
            try
            {
                Console.WriteLine("Lade get_classes.csv aus Azure...");
                string csvContent = _remoteConfigService.LoadBlobContent("get_classes.csv");
                var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (lines.Length < 2)
                {
                    Console.WriteLine("get_classes.csv ist leer oder hat nur Header");
                    return;
                }
                
                char delimiter = DetectDelimiter(lines[0]);
                Console.WriteLine($"  Erkanntes Trennzeichen: '{delimiter}'");
                
                for (int i = 1; i < lines.Length; i++)
                {
                    ParseClassLine(lines[i], delimiter, i);
                }
                
                Console.WriteLine($"{_catalogComponents.Count} Katalog-Komponenten geladen");
                Console.WriteLine($"Properties für {_componentProperties.Count} Komponenten geladen");
                
                foreach (var kvp in _componentProperties.Take(5))
                {
                    Console.WriteLine($"  - {kvp.Key}: {kvp.Value.Count} Properties");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fehler beim Laden von get_classes.csv: {e.Message}");
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
                Console.WriteLine($"  [Zeile {lineIndex + 1}] Komponente: D={displayName}, E={componentName}, Internal={internalName}");
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
                Console.WriteLine($"  [Zeile {lineIndex + 1}] Property: {componentPath}.{propertyName} = {propertyNumber}");
            }
        }

        private void LoadProperties()
        {
            try
            {
                Console.WriteLine("Lade get_properties.csv aus Azure...");
                string csvContent = _remoteConfigService.LoadBlobContent("get_properties.csv");

                var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length < 2)
                {
                    Console.WriteLine("get_properties.csv ist leer oder hat nur Header - überspringe");
                    return;
                }

                char delimiter = DetectDelimiter(lines[0]);
                Console.WriteLine($"  Erkanntes Trennzeichen: '{delimiter}'");

                int addedProperties = 0;

                for (int i = 1; i < lines.Length; i++)
                {
                    var columns = ParseCsvLine(lines[i], delimiter);

                    if (columns.Count >= 5)
                    {
                        var property = new ComponentProperty
                        {
                            ComponentPath = CleanCsvValue(columns[0]),
                            PropertyGroup = CleanCsvValue(columns[1]),
                            PropertyNumber = columns.Count > 2 ? CleanCsvValue(columns[2]) : "",
                            PropertyInternalName = CleanCsvValue(columns[3]),
                            PropertyDisplayName = CleanCsvValue(columns[4]),
                            PropertyType = columns.Count > 5 ? CleanCsvValue(columns[5]) : ""
                        };

                        string key = property.ComponentPath;
                        if (!_componentProperties.ContainsKey(key))
                        {
                            _componentProperties[key] = new List<ComponentProperty>();
                        }
                        _componentProperties[key].Add(property);
                        addedProperties++;
                    }
                }

                if (addedProperties > 0)
                {
                    Console.WriteLine($"{addedProperties} zusätzliche Properties aus get_properties.csv geladen");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"get_properties.csv nicht gefunden oder fehlerhaft - überspringe");
                Console.WriteLine($"   Fehler: {e.Message}");
            }
        }

        private void LoadValueLists()
        {
            try
            {
                Console.WriteLine("Lade get_valueliste.csv aus Azure...");
                string csvContent = _remoteConfigService.LoadBlobContent("get_valueliste.csv");

                var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length < 2)
                {
                    Console.WriteLine("get_valueliste.csv ist leer oder hat nur Header");
                    return;
                }

                var headers = ParseCsvLine(lines[0]);
                Console.WriteLine($"  get_valueliste.csv hat {headers.Count} Spalten");

                for (int i = 1; i < lines.Length; i++)
                {
                    var columns = ParseCsvLine(lines[i]);

                    if (columns.Count >= 2)
                    {
                        string key = CleanCsvValue(columns[0]);

                        if (!_valueLists.ContainsKey(key))
                        {
                            _valueLists[key] = new Dictionary<string, string>();
                        }

                        for (int j = 0; j < Math.Min(columns.Count, headers.Count); j++)
                        {
                            string headerName = CleanCsvValue(headers[j]);
                            string value = CleanCsvValue(columns[j]);

                            if (!string.IsNullOrEmpty(headerName))
                            {
                                _valueLists[key][headerName] = value;
                            }
                        }
                    }
                }

                Console.WriteLine($"{_valueLists.Count} Wertelisten-Einträge geladen");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fehler beim Laden von get_valueliste.csv: {e.Message}");
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

        public CatalogComponent FindComponentByPropertyKey(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return null;

            searchTerm = searchTerm.Trim();

            Console.WriteLine($"  Suche in {_componentProperties.Count} Property-Gruppen...");

            var exactMatch = _componentProperties.Keys.FirstOrDefault(k =>
                k.Equals(searchTerm, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                Console.WriteLine($"  Exakter Property-Key gefunden: {exactMatch}");
                return GetOrCreateComponent(exactMatch);
            }

            var fuzzyMatch = FindFuzzyPropertyMatch(searchTerm);
            if (fuzzyMatch != null)
            {
                return GetOrCreateComponent(fuzzyMatch);
            }

            return null;
        }

        private string FindFuzzyPropertyMatch(string searchTerm)
        {
            var searchWords = searchTerm.ToLower().Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 2)
                .ToList();

            if (searchWords.Count == 0)
                return null;

            Console.WriteLine($"  Suche mit Wörtern: [{string.Join(", ", searchWords)}]");

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

                Console.WriteLine($"  Top {bestMatches.Count} Property-Key-Matches:");
                foreach (var (key, words, sc, spec) in bestMatches)
                {
                    Console.WriteLine($"    [{sc:D3}|W:{words}/{searchWords.Count}|S:{spec:F1}] {key}");
                }

                var best = bestMatches.First();

                if (best.matchedWords >= searchWords.Count / 2)
                {
                    Console.WriteLine($"  Beste Übereinstimmung: {best.key} ({best.matchedWords}/{searchWords.Count} Wörter, Score: {best.score}, Specificity: {best.specificity:F1})");
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
                Console.WriteLine($"  → Virtuelle Komponente erstellt");
            }

            return _catalogComponents[key];
        }

        public CatalogComponent FindComponent(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return null;

            string normalizedSearch = Normalize(searchTerm);

            return _catalogComponents.Values.FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(c.DisplayName) && Normalize(c.DisplayName).Contains(normalizedSearch)) ||
                (!string.IsNullOrWhiteSpace(c.InternalName) && Normalize(c.InternalName).Contains(normalizedSearch)) ||
                (!string.IsNullOrWhiteSpace(c.FullPath) && Normalize(c.FullPath).Contains(normalizedSearch)) ||
                (!string.IsNullOrWhiteSpace(c.Category) && Normalize(c.Category).Contains(normalizedSearch))
            );
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
            builder.AppendLine("═══════════════════════════════════════════════════════════════");
            builder.AppendLine($"CRITICAL: Extract EXACTLY {properties.Count} properties - NOT MORE!");
            builder.AppendLine("═══════════════════════════════════════════════════════════════");
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
            builder.AppendLine("  • FrequencyRange, Gain_20to34GHz, Gain_34to44GHz");
            builder.AppendLine("  • GainFlatness, GainVariationOverTemperature");
            builder.AppendLine("  • NoiseFigure_20to34GHz, NoiseFigure_34to44GHz");
            builder.AppendLine("  • InputReturnLoss, OutputReturnLoss");
            builder.AppendLine("  • OutputPower_P1dB, SaturatedOutputPower, OutputIP3");
            builder.AppendLine("  • InputOutputImpedance, DieSize");
            builder.AppendLine("  • SupplyCurrent, DrainBiasVoltage, GateBiasVoltageRange");
            builder.AppendLine("  • ContinuousPowerDissipation, ESD_HBM");
            builder.AppendLine("  • OutlineDimensions, PadConfiguration, Mounting");
            builder.AppendLine("  • ThermalResistance, StorageTemperatureRange");
            builder.AppendLine("  • TechnicalSpecifications, ElectricalCharacteristics");
            builder.AppendLine("  • MechanicalCharacteristics, EnvironmentalData");
            builder.AppendLine("  • ANY property NOT in the ALLOWED list");
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
            builder.AppendLine("═══════════════════════════════════════════════════════════════");
            builder.AppendLine("RULE #0 (MOST IMPORTANT): MIN/TYP/MAX VALUE SELECTION");
            builder.AppendLine("═══════════════════════════════════════════════════════════════");
            builder.AppendLine();
            builder.AppendLine("When extracting values from specification tables with Min/Typ/Max columns:");
            builder.AppendLine();
            builder.AppendLine("THE PROPERTY NAME TELLS YOU WHICH COLUMN TO READ:");
            builder.AppendLine();
            builder.AppendLine("  ┌─────────────────────┬────────────────────┬─────────────────────────┐");
            builder.AppendLine("  │ Property Name       │ Read from Column   │ Example                 │");
            builder.AppendLine("  ├─────────────────────┼────────────────────┼─────────────────────────┤");
            builder.AppendLine("  │ Contains \"Typ\"      │ TYP column ONLY    │ GainTypDb → Typ = 45    │");
            builder.AppendLine("  │ Contains \"Min\"      │ MIN column ONLY    │ VccMinV → Min = 4.5     │");
            builder.AppendLine("  │ Contains \"Max\"      │ MAX column ONLY    │ IsupMaxA → Max = 1.2    │");
            builder.AppendLine("  │ No suffix           │ TYP (default)      │ Frequency → Typ         │");
            builder.AppendLine("  └─────────────────────┴────────────────────┴─────────────────────────┘");
            builder.AppendLine();
        }

        private void BuildFrequencyRangeRules(StringBuilder builder)
        {
            builder.AppendLine("═══════════════════════════════════════════════════════════════");
            builder.AppendLine("CRITICAL RULE: MULTIPLE FREQUENCY RANGES");
            builder.AppendLine("═══════════════════════════════════════════════════════════════");
            builder.AppendLine();
            builder.AppendLine("When table has MULTIPLE frequency ranges (3 or more):");
            builder.AppendLine();
            builder.AppendLine("ALWAYS USE THE MEDIAN (MIDDLE) FREQUENCY RANGE!");
            builder.AppendLine();
            builder.AppendLine("SELECTION ALGORITHM:");
            builder.AppendLine("  IF 2 ranges  → Use 1st range (lower frequency)");
            builder.AppendLine("  IF 3 ranges  → Use 2nd range (median)");
            builder.AppendLine("  IF 4 ranges  → Use 2nd or 3rd range (middle)");
            builder.AppendLine("  IF 5+ ranges → Use middle range (count/2 + 1)");
            builder.AppendLine();
        }

        private Dictionary<string, string> LoadPropertyDefinitions()
        {
            var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Console.WriteLine("  Versuche property_definitions.txt aus Azure zu laden...");
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

                Console.WriteLine($"  {definitions.Count} Property-Definitionen geladen");
            }
            catch
            {
                Console.WriteLine("  property_definitions.txt nicht gefunden - verwende ohne Erklärungen");
            }

            return definitions;
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
    }

    #region Data Models

    public class CatalogComponent
    {
        public int RowNumber { get; set; }
        public string FullPath { get; set; }
        public string ComponentType { get; set; }
        public string Category { get; set; }
        public string DisplayName { get; set; }
        public string ComponentName { get; set; }
        public string InternalName { get; set; }
        public string Description { get; set; }
        public string AdditionalInfo { get; set; }

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
        public string ComponentPath { get; set; }
        public string PropertyGroup { get; set; }
        public string PropertyNumber { get; set; }
        public string PropertyInternalName { get; set; }
        public string PropertyDisplayName { get; set; }
        public string PropertyType { get; set; }

        public override string ToString()
        {
            return $"{PropertyGroup}.{PropertyDisplayName} ({PropertyInternalName})";
        }
    }

    #endregion
}