using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// Post-validation service for component identification.
    /// Validates AI results against objective rules to ensure quality.
    /// </summary>
    public class IdentificationValidationService
    {
        private readonly DMSCatalogService _catalogService;

        // Validation thresholds (extracted as constants for maintainability)
        private const double CONFIDENCE_THRESHOLD = 0.7;
        private const double ERROR_PENALTY = 0.3;
        private const double WARNING_PENALTY = 0.1;
        private const int MIN_PART_NUMBER_LENGTH = 3;
        private const int MIN_KEYWORD_LENGTH = 4;

        // Generic categories that are usually incorrect
        private static readonly string[] GenericCategories = new[]
        {
            "Amplifier",
            "Connector",
            "IC",
            "Transistor",
            "Component",
            "Electronic Component",
            "Circuit"
        };

        // Suspicious words in part numbers
        private static readonly string[] SuspiciousPartNumberWords = new[]
        {
            "not", "none", "n/a", "unknown", "siehe", "see", "page"
        };

        // Suspicious words in manufacturer info
        private static readonly string[] SuspiciousManufacturerWords = new[]
        {
            "unknown", "not specified", "n/a", "siehe", "see datasheet"
        };

        public enum ValidationSeverity
        {
            Info,
            Warning,
            Error
        }

        public class ValidationIssue
        {
            public ValidationSeverity Severity { get; set; }
            public string Field { get; set; }
            public string Message { get; set; }
            public string Suggestion { get; set; }
            public string CurrentValue { get; set; }

            public override string ToString()
            {
                string icon = Severity == ValidationSeverity.Error ? "X" :
                             Severity == ValidationSeverity.Warning ? "!" : "i";
                return $"[{icon}] {Field}: {Message}\n   Aktuell: {CurrentValue}\n   {Suggestion}";
            }
        }

        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public bool RequiresUserReview { get; set; }
            public List<ValidationIssue> Issues { get; set; }
            public double ConfidenceScore { get; set; }

            public ValidationResult()
            {
                Issues = new List<ValidationIssue>();
                IsValid = true;
                RequiresUserReview = false;
                ConfidenceScore = 1.0;
            }

            public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
            public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);

            public override string ToString()
            {
                int errors = Issues.Count(i => i.Severity == ValidationSeverity.Error);
                int warnings = Issues.Count(i => i.Severity == ValidationSeverity.Warning);
                string status = IsValid ? "BESTANDEN" : "FEHLER";
                return $"Validierung: {status} (Fehler: {errors}, Warnungen: {warnings}, Konfidenz: {ConfidenceScore:P0})";
            }
        }

        public IdentificationValidationService(DMSCatalogService catalogService)
        {
            _catalogService = catalogService;
        }

        /// <summary>
        /// Main validation method: Checks all aspects of identification.
        /// Supports: Technical Data Sheets (COMPONENT) and Safety Data Sheets (MIXTURE).
        /// </summary>
        public ValidationResult ValidateIdentification(JObject identification)
        {
            var result = new ValidationResult();

            try
            {
                string docType = identification["DocumentType"]?.Value<string>() ?? "";

                // Skip validation for MIXTURE (Safety Data Sheets are too complex)
                if (docType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("DocumentType = MIXTURE (Sicherheitsdatenblatt)");
                    Console.WriteLine("Sicherheitsdatenblätter werden NICHT validiert (zu komplex)");
                    return result; // Already initialized with IsValid=true
                }

                // Validation for COMPONENT (Technical Data Sheets)
                Console.WriteLine($"DocumentType = {docType} (Technisches Datenblatt)");
                Console.WriteLine("Starte Validierung...");

                // Apply validation rules
                ValidateMandatoryFields(identification, result);
                ValidateCategorySpecificity(identification, result);
                ValidateCategoryInCatalog(identification, result);
                ValidatePartNumberFormat(identification, result);
                ValidateCategoryNameConsistency(identification, result);
                DetectCommonMistakes(identification, result);

                // Calculate final confidence score
                CalculateConfidenceScore(result);

                Console.WriteLine($"Validierung abgeschlossen: Konfidenz = {result.ConfidenceScore:P0}, Issues = {result.Issues.Count}");

                // Determine if user review is required
                result.RequiresUserReview = result.HasErrors ||
                                           result.HasWarnings ||
                                           result.ConfidenceScore < CONFIDENCE_THRESHOLD;

                result.IsValid = !result.HasErrors;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler bei der Validierung: {ex.Message}");
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Field = "Validierung",
                    Message = "Validierung fehlgeschlagen",
                    CurrentValue = ex.Message,
                    Suggestion = "Manuelle Überprüfung erforderlich"
                });
                result.IsValid = false;
                result.RequiresUserReview = true;
                result.ConfidenceScore = 0.5;
            }

            return result;
        }

        #region Validation Rules (COMPONENT only)

        /// <summary>
        /// RULE 1: All mandatory fields must be present.
        /// </summary>
        private void ValidateMandatoryFields(JObject identification, ValidationResult result)
        {
            var mandatoryFields = new Dictionary<string, string>
            {
                { "DocumentType", "Dokumenttyp" },
                { "ComponentCategory", "Komponenten-Kategorie" },
                { "ComponentName", "Komponenten-Name" },
                { "ManufacturerInfo", "Hersteller" }
            };

            foreach (var field in mandatoryFields)
            {
                var value = identification[field.Key]?.Value<string>();
                if (string.IsNullOrWhiteSpace(value))
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        Field = field.Value,
                        Message = "Pflichtfeld fehlt",
                        CurrentValue = "leer",
                        Suggestion = "Dieses Feld muss ausgefüllt sein"
                    });
                }
            }

            // Part Number: Either PartNumber OR PartNumbers must be present
            bool hasPartNumber = !string.IsNullOrWhiteSpace(identification["PartNumber"]?.Value<string>());
            bool hasPartNumbers = identification["PartNumbers"]?.HasValues == true;

            if (!hasPartNumber && !hasPartNumbers)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "Herstellernummer",
                    Message = "Keine Part Number gefunden",
                    CurrentValue = "nicht vorhanden",
                    Suggestion = "Überprüfen Sie ob eine Herstellernummer im Dokument steht"
                });
            }
        }

        /// <summary>
        /// RULE 2: Category must be SPECIFIC (most important rule!).
        /// Generic categories like "Amplifier" are usually incorrect.
        /// </summary>
        private void ValidateCategorySpecificity(JObject identification, ValidationResult result)
        {
            string category = identification["ComponentCategory"]?.Value<string>() ?? "";
            string name = identification["ComponentName"]?.Value<string>() ?? "";

            // Check for generic categories
            if (GenericCategories.Any(g => category.Equals(g, StringComparison.OrdinalIgnoreCase)))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Field = "Komponenten-Kategorie",
                    Message = "Kategorie ist zu generisch und wahrscheinlich falsch",
                    CurrentValue = category,
                    Suggestion = $"Prüfen Sie ob '{category}' spezifischer sein könnte " +
                                "(z.B. 'OP Amplifier' statt 'Amplifier', 'RF Connector' statt 'Connector')"
                });
            }

            // Check for keyword mismatches
            ValidateOperationalAmplifierKeywords(category, name, result);
            ValidateRFKeywords(category, name, result);
        }

        /// <summary>
        /// Helper: Validates operational amplifier keywords.
        /// </summary>
        private void ValidateOperationalAmplifierKeywords(string category, string name, ValidationResult result)
        {
            var nameKeywords = name.ToLower();

            if ((nameKeywords.Contains("operational") || nameKeywords.Contains("opamp") || nameKeywords.Contains("op-amp")) &&
                !category.ToLower().Contains("op") && !category.ToLower().Contains("operational"))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "Komponenten-Kategorie",
                    Message = "Name enthält 'Operational Amplifier' aber Kategorie nicht",
                    CurrentValue = category,
                    Suggestion = "Sollte die Kategorie 'OP Amplifier' sein?"
                });
            }
        }

        /// <summary>
        /// Helper: Validates RF/microwave keywords.
        /// </summary>
        private void ValidateRFKeywords(string category, string name, ValidationResult result)
        {
            var nameKeywords = name.ToLower();

            if ((nameKeywords.Contains("rf ") || nameKeywords.Contains("microwave")) &&
                !category.ToLower().Contains("rf"))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "Komponenten-Kategorie",
                    Message = "Name enthält 'RF/Microwave' aber Kategorie nicht",
                    CurrentValue = category,
                    Suggestion = "Sollte die Kategorie 'RF Amplifier' oder 'RF Component' sein?"
                });
            }
        }

        /// <summary>
        /// RULE 3: Check if category exists in DMS catalog.
        /// </summary>
        private void ValidateCategoryInCatalog(JObject identification, ValidationResult result)
        {
            string category = identification["ComponentCategory"]?.Value<string>()?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(category))
                return;

            try
            {
                // Search category in catalog
                var component = _catalogService.FindComponent(category);
                var properties = _catalogService.GetPropertiesForComponent(category);

                // Check if category exists as property group
                if (component == null && properties.Count > 0)
                {
                    Console.WriteLine($"[DMSCatalog] Kategorie '{category}' existiert als Property-Gruppe mit {properties.Count} Properties.");
                    return;
                }

                if (component == null)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Field = "Komponenten-Kategorie",
                        Message = "Kategorie nicht im DMS-Katalog gefunden",
                        CurrentValue = category,
                        Suggestion = "Prüfen Sie die Schreibweise oder ob die Kategorie existiert.\n" +
                                    "   Die technischen Parameter werden generisch oder mit ähnlichem Schema extrahiert."
                    });
                    return;
                }

                // Found in catalog - search for properties using multiple strategies
                properties = TryGetPropertiesMultipleWays(component, category);

                Console.WriteLine($"[DMSCatalog] Suche Properties für Kategorie: '{category}'");
                Console.WriteLine($"  Gefunden: {properties.Count} Properties");

                if (properties.Count == 0)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Field = "Komponenten-Kategorie",
                        Message = "Kategorie im Katalog, aber keine Properties definiert",
                        CurrentValue = category,
                        Suggestion = "Detail-Extraktion wird generisch erfolgen"
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler bei Katalog-Validierung: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper: Tries multiple strategies to get properties from catalog.
        /// </summary>
        private List<ComponentProperty> TryGetPropertiesMultipleWays(CatalogComponent component, string category)
        {
            var strategies = new[]
            {
                component.DisplayName?.Trim(),
                component.InternalName?.Trim(),
                component.FullPath?.Trim(),
                category
            };

            foreach (var strategy in strategies)
            {
                if (string.IsNullOrWhiteSpace(strategy))
                    continue;

                var properties = _catalogService.GetPropertiesForComponent(strategy);
                if (properties.Count > 0)
                    return properties;
            }

            return new List<ComponentProperty>();
        }

        /// <summary>
        /// RULE 4: Part number format validation.
        /// </summary>
        private void ValidatePartNumberFormat(JObject identification, ValidationResult result)
        {
            string partNumber = identification["PartNumber"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(partNumber))
                return;

            // Check if part number is suspiciously short
            if (partNumber.Length < MIN_PART_NUMBER_LENGTH)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "Herstellernummer",
                    Message = "Part Number ist sehr kurz",
                    CurrentValue = partNumber,
                    Suggestion = "Überprüfen Sie ob die komplette Part Number extrahiert wurde"
                });
            }

            // Check for suspicious words
            if (SuspiciousPartNumberWords.Any(w => partNumber.ToLower().Contains(w)))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Field = "Herstellernummer",
                    Message = "Part Number enthält verdächtigen Text",
                    CurrentValue = partNumber,
                    Suggestion = "Dies ist wahrscheinlich keine gültige Part Number"
                });
            }
        }

        /// <summary>
        /// RULE 5: Consistency between category and name.
        /// </summary>
        private void ValidateCategoryNameConsistency(JObject identification, ValidationResult result)
        {
            string category = identification["ComponentCategory"]?.Value<string>() ?? "";
            string name = identification["ComponentName"]?.Value<string>() ?? "";

            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(name))
                return;

            // Extract main word from category (e.g., "Amplifier" from "OP Amplifier")
            string[] categoryWords = category.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
            string mainCategoryWord = categoryWords.LastOrDefault() ?? "";

            // Check if name contains the main word
            if (!string.IsNullOrWhiteSpace(mainCategoryWord) &&
                mainCategoryWord.Length > MIN_KEYWORD_LENGTH &&
                !name.ToLower().Contains(mainCategoryWord.ToLower()))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "Konsistenz",
                    Message = $"Kategorie enthält '{mainCategoryWord}' aber Name nicht",
                    CurrentValue = $"Kategorie: {category}, Name: {name}",
                    Suggestion = "Prüfen Sie ob Kategorie und Name zusammenpassen"
                });
            }
        }

        /// <summary>
        /// RULE 6: Detect common AI mistakes.
        /// </summary>
        private void DetectCommonMistakes(JObject identification, ValidationResult result)
        {
            string category = identification["ComponentCategory"]?.Value<string>() ?? "";
            string docType = identification["DocumentType"]?.Value<string>() ?? "";
            string manufacturer = identification["ManufacturerInfo"]?.Value<string>() ?? "";

            // Mistake 1: MIXTURE but component category specified
            if (docType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(category))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Field = "Dokumenttyp",
                    Message = "DocumentType ist MIXTURE aber Component-Kategorie angegeben",
                    CurrentValue = $"Type: {docType}, Category: {category}",
                    Suggestion = "Bei MIXTURE (Safety Data Sheet) sollte keine Component-Kategorie angegeben sein"
                });
            }

            // Mistake 2: COMPONENT but no category
            if (docType.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(category))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Field = "Komponenten-Kategorie",
                    Message = "DocumentType ist COMPONENT aber keine Kategorie angegeben",
                    CurrentValue = "leer",
                    Suggestion = "Bei Technical Data Sheet muss eine Component-Kategorie angegeben sein"
                });
            }

            // Mistake 3: Manufacturer contains generic words
            if (SuspiciousManufacturerWords.Any(g => manufacturer.ToLower().Contains(g)))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Field = "Hersteller",
                    Message = "Hersteller-Information enthält generische Wörter",
                    CurrentValue = manufacturer,
                    Suggestion = "Überprüfen Sie die Hersteller-Information auf Genauigkeit"
                });
            }
        }

        /// <summary>
        /// Calculates confidence score based on validation issues.
        /// </summary>
        private void CalculateConfidenceScore(ValidationResult result)
        {
            double errorPenalty = result.HasErrors ? ERROR_PENALTY : 0;
            double warningPenalty = result.HasWarnings ? WARNING_PENALTY : 0;
            double baseScore = 1.0 - errorPenalty - warningPenalty;

            result.ConfidenceScore = Math.Max(0.0, Math.Min(1.0, baseScore));
        }

        #endregion
    }
}