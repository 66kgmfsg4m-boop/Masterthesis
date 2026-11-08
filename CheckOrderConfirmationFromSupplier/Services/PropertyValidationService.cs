using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// Property Validation Service
    /// 
    /// Validates extracted property values from datasheets against objective rules.
    /// Checks plausibility, units, value ranges, and component-specific constraints.
    /// 
    /// Features:
    /// - Unit validation (e.g., dB, mm, Hz)
    /// - Range validation (min/max values)
    /// - Common value suggestions
    /// - Component-type specific hints
    /// - Enumeration validation (allowed values)
    /// - Integer constraints
    /// 
    /// Architecture:
    /// - Rule-based validation system
    /// - Extensible property rules
    /// - Detailed warning and error reporting
    /// - Unit conversion support
    /// 
    /// Usage:
    /// var validator = new PropertyValidationService();
    /// var result = validator.ValidateExtractedProperties(jsonData, "RF Amplifier");
    /// </summary>
    public class PropertyValidationService
    {
        #region Data Classes

        /// <summary>
        /// Container for validation results with warnings and errors.
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public List<ValidationWarning> Warnings { get; set; }
            public List<ValidationError> Errors { get; set; }

            public ValidationResult()
            {
                Warnings = new List<ValidationWarning>();
                Errors = new List<ValidationError>();
                IsValid = true;
            }

            public bool HasIssues => Warnings.Count > 0 || Errors.Count > 0;
        }

        /// <summary>
        /// Validation warning for suspicious but not necessarily incorrect values.
        /// </summary>
        public class ValidationWarning
        {
            public string PropertyName { get; set; }
            public string Message { get; set; }
            public string Suggestion { get; set; }
            public string CurrentValue { get; set; }
        }

        /// <summary>
        /// Validation error for clearly incorrect values.
        /// </summary>
        public class ValidationError
        {
            public string PropertyName { get; set; }
            public string Message { get; set; }
            public string CurrentValue { get; set; }
        }

        /// <summary>
        /// Definition of a validation rule for a specific property.
        /// </summary>
        public class PropertyRule
        {
            public string PropertyName { get; set; }
            public string RequiredUnit { get; set; }
            public double MinValue { get; set; }
            public double MaxValue { get; set; }
            public double[] CommonValues { get; set; }
            public bool MustBeInteger { get; set; }
            public string ValidationMessage { get; set; }
            public Dictionary<string, double> UnitConversions { get; set; }
            public Dictionary<string, string> ComponentTypeHints { get; set; }
            public Dictionary<string, string[]> AllowedValues { get; set; }
        }

        #endregion

        #region Fields and Configuration

        private readonly Dictionary<string, PropertyRule> _validationRules;

        // Regex pattern for extracting numeric values from strings
        private static readonly Regex NumericPattern = new Regex(@"(\d+\.?\d*)");

        #endregion

        #region Constructor

        public PropertyValidationService()
        {
            _validationRules = InitializeValidationRules();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Validates all extracted properties against defined rules.
        /// Skips metadata fields and "nicht vorhanden" values.
        /// </summary>
        /// <param name="extractedData">JSON object containing extracted properties</param>
        /// <param name="componentType">Component type for type-specific validation</param>
        /// <returns>Validation result with warnings and errors</returns>
        public ValidationResult ValidateExtractedProperties(JObject extractedData, string componentType)
        {
            var result = new ValidationResult();

            Console.WriteLine($"\n=== VALIDIERE EXTRAHIERTE PROPERTIES ({componentType}) ===");

            foreach (var property in extractedData.Properties())
            {
                string propertyName = property.Name;
                string propertyValue = property.Value?.ToString() ?? "";

                // Skip metadata fields
                if (IsMetadataField(propertyName))
                    continue;

                // Skip "nicht vorhanden" values
                if (propertyValue == "nicht vorhanden")
                    continue;

                // Validate if rule exists
                if (_validationRules.ContainsKey(propertyName))
                {
                    var rule = _validationRules[propertyName];
                    ValidateProperty(propertyValue, rule, componentType, result);
                }
            }

            result.IsValid = result.Errors.Count == 0;

            Console.WriteLine($"Validierung abgeschlossen: {result.Warnings.Count} Warnungen, {result.Errors.Count} Fehler");

            return result;
        }

        #endregion

        #region Validation Logic

        /// <summary>
        /// Validates a single property value against its rule.
        /// Performs multiple checks: allowed values, units, ranges, integers, common values.
        /// </summary>
        private void ValidateProperty(string value, PropertyRule rule, string componentType, ValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "nicht vorhanden")
                return;

            // Check 1: Allowed values (for enumerations like Function)
            if (rule.AllowedValues != null && rule.AllowedValues.ContainsKey(componentType))
            {
                ValidateAllowedValues(value, rule, componentType, result);
            }

            // Check 2: Unit validation
            if (!string.IsNullOrEmpty(rule.RequiredUnit))
            {
                ValidateUnit(value, rule, result);
            }

            // Check 3-6: Numeric validations
            double numericValue;
            if (TryExtractNumericValue(value, rule, out numericValue))
            {
                ValidateRange(numericValue, rule, result, value);
                ValidateCommonValues(numericValue, rule, result, value);
                ValidateInteger(numericValue, rule, result, value);
            }

            // Check 7: Component-type specific hints
            AppendComponentTypeHints(rule, componentType, result);
        }

        /// <summary>
        /// Validates that value is in the list of allowed values.
        /// </summary>
        private void ValidateAllowedValues(string value, PropertyRule rule, string componentType, ValidationResult result)
        {
            string[] allowedValues = rule.AllowedValues[componentType];
            bool isAllowed = allowedValues.Any(av =>
                value.Equals(av, StringComparison.OrdinalIgnoreCase) ||
                value.IndexOf(av, StringComparison.OrdinalIgnoreCase) >= 0);

            if (!isAllowed)
            {
                result.Errors.Add(new ValidationError
                {
                    PropertyName = rule.PropertyName,
                    CurrentValue = value,
                    Message = $"Invalid value for {componentType}. Allowed: {string.Join(", ", allowedValues)}"
                });
            }
        }

        /// <summary>
        /// Validates that value contains the required unit.
        /// </summary>
        private void ValidateUnit(string value, PropertyRule rule, ValidationResult result)
        {
            if (!value.Contains(rule.RequiredUnit) && !ContainsUnitVariant(value, rule))
            {
                result.Warnings.Add(new ValidationWarning
                {
                    PropertyName = rule.PropertyName,
                    CurrentValue = value,
                    Message = $"Unit missing or incorrect. Expected: {rule.RequiredUnit}",
                    Suggestion = $"Should contain '{rule.RequiredUnit}'."
                });
            }
        }

        /// <summary>
        /// Validates that numeric value is within expected range.
        /// </summary>
        private void ValidateRange(double numericValue, PropertyRule rule, ValidationResult result, string originalValue)
        {
            if (numericValue < rule.MinValue || numericValue > rule.MaxValue)
            {
                result.Warnings.Add(new ValidationWarning
                {
                    PropertyName = rule.PropertyName,
                    CurrentValue = originalValue,
                    Message = $"Value outside expected range ({rule.MinValue} - {rule.MaxValue}).",
                    Suggestion = rule.ValidationMessage
                });
            }
        }

        /// <summary>
        /// Suggests common values if current value is unusual but close to a standard value.
        /// </summary>
        private void ValidateCommonValues(double numericValue, PropertyRule rule, ValidationResult result, string originalValue)
        {
            if (rule.CommonValues != null && rule.CommonValues.Length > 0)
            {
                double closestCommon = rule.CommonValues.OrderBy(v => Math.Abs(v - numericValue)).First();
                double difference = Math.Abs(closestCommon - numericValue);

                // Warn if value is unusual but within 20% of a common value
                if (difference > 0.01 && difference < numericValue * 0.2)
                {
                    result.Warnings.Add(new ValidationWarning
                    {
                        PropertyName = rule.PropertyName,
                        CurrentValue = originalValue,
                        Message = $"Unusual value. Common values: {string.Join(", ", rule.CommonValues)}",
                        Suggestion = $"Nearest standard value: {closestCommon}{rule.RequiredUnit}"
                    });
                }
            }
        }

        /// <summary>
        /// Validates that value is an integer if required.
        /// </summary>
        private void ValidateInteger(double numericValue, PropertyRule rule, ValidationResult result, string originalValue)
        {
            if (rule.MustBeInteger && numericValue != Math.Floor(numericValue))
            {
                result.Errors.Add(new ValidationError
                {
                    PropertyName = rule.PropertyName,
                    CurrentValue = originalValue,
                    Message = "Value must be an integer."
                });
            }
        }

        /// <summary>
        /// Appends component-type specific hints to the last warning.
        /// </summary>
        private void AppendComponentTypeHints(PropertyRule rule, string componentType, ValidationResult result)
        {
            if (rule.ComponentTypeHints != null && rule.ComponentTypeHints.ContainsKey(componentType))
            {
                if (result.Warnings.Any(w => w.PropertyName == rule.PropertyName))
                {
                    var lastWarning = result.Warnings.Last(w => w.PropertyName == rule.PropertyName);
                    lastWarning.Suggestion += $" | {rule.ComponentTypeHints[componentType]}";
                }
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Checks if a field is metadata and should be skipped.
        /// </summary>
        private bool IsMetadataField(string propertyName)
        {
            return propertyName == "ComponentType" ||
                   propertyName == "PartNumber" ||
                   propertyName == "Manufacturer" ||
                   propertyName == "Description";
        }

        /// <summary>
        /// Extracts numeric value from string and applies unit conversions.
        /// </summary>
        private bool TryExtractNumericValue(string value, PropertyRule rule, out double numericValue)
        {
            numericValue = 0;

            var match = NumericPattern.Match(value);
            if (!match.Success)
                return false;

            if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out numericValue))
                return false;

            // Apply unit conversion if applicable
            if (rule.UnitConversions != null)
            {
                foreach (var conversion in rule.UnitConversions)
                {
                    if (value.Contains(conversion.Key))
                    {
                        numericValue *= conversion.Value;
                        break;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Checks if value contains any unit variant defined in the rule.
        /// </summary>
        private bool ContainsUnitVariant(string value, PropertyRule rule)
        {
            if (rule.UnitConversions == null)
                return false;

            return rule.UnitConversions.Keys.Any(unit => value.Contains(unit));
        }

        #endregion

        #region Rule Initialization

        /// <summary>
        /// Initializes all validation rules for supported properties.
        /// Rules are organized by category: General, Mechanical, RF, Power, Passive.
        /// </summary>
        private Dictionary<string, PropertyRule> InitializeValidationRules()
        {
            var rules = new Dictionary<string, PropertyRule>();

            // General Properties
            rules.Add("Function", CreateFunctionRule());
            
            // Mechanical Properties
            rules.Add("Pitch", CreatePitchRule());
            rules.Add("NumberOfPin", CreateNumberOfPinRule());
            
            // RF Properties
            rules.Add("GainTypDb", CreateGainRule());
            rules.Add("Op1DbDbm", CreateOp1DbRule());
            rules.Add("Oip3TypDbm", CreateOip3Rule());
            rules.Add("NfTypDb", CreateNfRule());
            rules.Add("FMinHz", CreateFMinRule());
            rules.Add("FMaxHz", CreateFMaxRule());
            
            // Power Properties
            rules.Add("VccVddTypV", CreateVccRule());
            rules.Add("IsupCdMaxA", CreateIsupRule());
            
            // Passive Component Properties
            rules.Add("ResistaOhm", CreateResistanceRule());
            rules.Add("TolerancePct", CreateToleranceRule());

            return rules;
        }

        // Rule creation methods (organized by category)

        private PropertyRule CreateFunctionRule()
        {
            return new PropertyRule
            {
                PropertyName = "Function",
                ValidationMessage = "Function must contain a valid value.",
                ComponentTypeHints = new Dictionary<string, string>
                {
                    {"RF Amplifier", "EXACTLY 4 allowed values: (1) RF Amplifier, (2) Radio Frequency Amplifier, (3) Broadband Active Balun, (4) RF PA Linearizer"}
                },
                AllowedValues = new Dictionary<string, string[]>
                {
                    {"RF Amplifier", new[] {
                        "RF Amplifier",
                        "Radio Frequency Amplifier",
                        "Broadband Active Balun",
                        "RF PA Linearizer"
                    }}
                }
            };
        }

        private PropertyRule CreatePitchRule()
        {
            return new PropertyRule
            {
                PropertyName = "Pitch",
                RequiredUnit = "mm",
                MinValue = 0.3,
                MaxValue = 5.0,
                CommonValues = new[] { 0.4, 0.5, 0.65, 0.8, 1.0, 1.27, 2.0, 2.54 },
                ValidationMessage = "Pitch should be between 0.3mm and 5mm.",
                ComponentTypeHints = new Dictionary<string, string>
                {
                    {"QFN", "Typical 0.5mm or 0.4mm (pin-to-pin, dimension 'e')"},
                    {"QFP", "Typical 0.5mm, 0.65mm or 0.8mm (pin-to-pin, dimension 'e')"},
                    {"SO", "Typical 0.65mm (pin-to-pin) or 1.27mm (pin-to-pin). NOT row-to-row!"},
                    {"SOIC", "Typical 0.65mm or 1.27mm (pin-to-pin = 'e', NOT row spacing!)"},
                    {"DIP", "Typical 2.54mm (pin-to-pin)"}
                }
            };
        }

        private PropertyRule CreateNumberOfPinRule()
        {
            return new PropertyRule
            {
                PropertyName = "NumberOfPin",
                MinValue = 2,
                MaxValue = 1000,
                MustBeInteger = true,
                ValidationMessage = "NumberOfPin should be between 2 and 1000."
            };
        }

        private PropertyRule CreateGainRule()
        {
            return new PropertyRule
            {
                PropertyName = "GainTypDb",
                RequiredUnit = "dB",
                MinValue = -10,
                MaxValue = 50,
                ValidationMessage = "Gain should be between -10 dB and +50 dB.",
                ComponentTypeHints = new Dictionary<string, string>
                {
                    {"RF Amplifier", "Typical 10-30 dB at maximum frequency (FMaxHz)"},
                    {"OP Amplifier", "Typical 60-140 dB (Open Loop)"},
                    {"LNA", "Typical 15-25 dB at maximum frequency (FMaxHz)"}
                }
            };
        }

        private PropertyRule CreateOp1DbRule()
        {
            return new PropertyRule
            {
                PropertyName = "Op1DbDbm",
                RequiredUnit = "dBm",
                MinValue = -20,
                MaxValue = 40,
                ValidationMessage = "Output P1dB should be between -20 dBm and +40 dBm.",
                ComponentTypeHints = new Dictionary<string, string>
                {
                    {"RF Amplifier", "IMPORTANT: Read value at maximum frequency (FMaxHz)!"}
                }
            };
        }

        private PropertyRule CreateOip3Rule()
        {
            return new PropertyRule
            {
                PropertyName = "Oip3TypDbm",
                RequiredUnit = "dBm",
                MinValue = -10,
                MaxValue = 50,
                ValidationMessage = "OIP3 should be between -10 dBm and +50 dBm.",
                ComponentTypeHints = new Dictionary<string, string>
                {
                    {"RF Amplifier", "IMPORTANT: Read value at maximum frequency (FMaxHz)! Typical: OIP3 ? OP1dB + 10dB"}
                }
            };
        }

        private PropertyRule CreateNfRule()
        {
            return new PropertyRule
            {
                PropertyName = "NfTypDb",
                RequiredUnit = "dB",
                MinValue = 0.1,
                MaxValue = 10,
                ValidationMessage = "Noise figure should be between 0.1 dB and 10 dB.",
                ComponentTypeHints = new Dictionary<string, string>
                {
                    {"LNA", "Typical 0.5-2dB at maximum frequency"},
                    {"Mixer", "Typical 8-15dB"}
                }
            };
        }

        private PropertyRule CreateFMinRule()
        {
            return new PropertyRule
            {
                PropertyName = "FMinHz",
                RequiredUnit = "Hz",
                MinValue = 0,
                MaxValue = 1e12,
                ValidationMessage = "Minimum frequency should be between 0 Hz and 1 THz.",
                UnitConversions = new Dictionary<string, double>
                {
                    {"Hz", 1},
                    {"KHz", 1e3},
                    {"kHz", 1e3},
                    {"MHz", 1e6},
                    {"GHz", 1e9}
                }
            };
        }

        private PropertyRule CreateFMaxRule()
        {
            return new PropertyRule
            {
                PropertyName = "FMaxHz",
                RequiredUnit = "Hz",
                MinValue = 0,
                MaxValue = 1e12,
                ValidationMessage = "Maximum frequency should be between 0 Hz and 1 THz.",
                UnitConversions = new Dictionary<string, double>
                {
                    {"Hz", 1},
                    {"KHz", 1e3},
                    {"kHz", 1e3},
                    {"MHz", 1e6},
                    {"GHz", 1e9}
                }
            };
        }

        private PropertyRule CreateVccRule()
        {
            return new PropertyRule
            {
                PropertyName = "VccVddTypV",
                RequiredUnit = "V",
                MinValue = 0.5,
                MaxValue = 50,
                CommonValues = new[] { 1.8, 2.5, 3.0, 3.3, 5.0, 12.0, 15.0, 24.0 },
                ValidationMessage = "Supply voltage should be between 0.5V and 50V."
            };
        }

        private PropertyRule CreateIsupRule()
        {
            return new PropertyRule
            {
                PropertyName = "IsupCdMaxA",
                RequiredUnit = "A",
                MinValue = 0.001,
                MaxValue = 10,
                ValidationMessage = "Supply current should be between 1mA and 10A.",
                UnitConversions = new Dictionary<string, double>
                {
                    {"A", 1},
                    {"mA", 0.001},
                    {"µA", 0.000001},
                    {"uA", 0.000001}
                }
            };
        }

        private PropertyRule CreateResistanceRule()
        {
            return new PropertyRule
            {
                PropertyName = "ResistaOhm",
                RequiredUnit = "Ohm",
                MinValue = 0.01,
                MaxValue = 10e6,
                ValidationMessage = "Resistance should be between 0.01 Ohm and 10 MOhm.",
                UnitConversions = new Dictionary<string, double>
                {
                    {"Ohm", 1},
                    {"kOhm", 1e3},
                    {"MOhm", 1e6}
                }
            };
        }

        private PropertyRule CreateToleranceRule()
        {
            return new PropertyRule
            {
                PropertyName = "TolerancePct",
                RequiredUnit = "",
                MinValue = 0.01,
                MaxValue = 20,
                CommonValues = new[] { 0.1, 0.5, 1.0, 5.0, 10.0, 20.0 },
                ValidationMessage = "Tolerance should be between 0.01% and 20%."
            };
        }

        #endregion
    }
}
