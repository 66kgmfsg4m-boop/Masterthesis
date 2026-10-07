using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// Part Number Decoder Service
    /// 
    /// Decodes manufacturer part numbers automatically and extracts technical parameters.
    /// Supports various manufacturer-specific naming conventions for electronic components.
    /// 
    /// Architecture:
    /// - Pattern-based decoding using configurable rules
    /// - Extensible for new manufacturers and component types
    /// - Fallback to generic pattern matching
    /// 
    /// Currently supported: Resistors (Chip Resistors)
    /// Future extensions: Capacitors, Inductors, ICs, etc.
    /// </summary>
    public class PartNumberDecoderService
    {
        #region Configuration and Pattern Definitions

        // Package size to power rating mapping (standard for chip resistors)
        private static readonly Dictionary<string, double> PackagePowerRatings = new Dictionary<string, double>
        {
            { "0201", 0.05 },   // 1/20 W
            { "0402", 0.063 },  // 1/16 W
            { "0603", 0.1 },    // 1/10 W
            { "0805", 0.125 },  // 1/8 W
            { "1206", 0.25 },   // 1/4 W
            { "1210", 0.5 },    // 1/2 W
            { "2010", 0.75 },   // 3/4 W
            { "2512", 1.0 }     // 1 W
        };

        // IEC 60062 standard tolerance codes
        private static readonly Dictionary<string, double> ToleranceCodes = new Dictionary<string, double>
        {
            { "B", 0.1 },   // ±0.1%
            { "C", 0.25 },  // ±0.25%
            { "D", 0.5 },   // ±0.5%
            { "F", 1.0 },   // ±1%
            { "G", 2.0 },   // ±2%
            { "J", 5.0 },   // ±5%
            { "K", 10.0 },  // ±10%
            { "M", 20.0 }   // ±20%
        };

        // Resistance multipliers
        private static readonly Dictionary<string, double> ResistanceMultipliers = new Dictionary<string, double>
        {
            { "R", 1 },           // Ohm (×1)
            { "K", 1000 },        // k? (×1000)
            { "M", 1000000 }      // M? (×1000000)
        };

        // Part number decoding patterns (ordered by specificity)
        private static readonly List<DecodingPattern> DecodingPatterns = new List<DecodingPattern>
        {
            // Yageo RC-Series: RC0201FR-0775KL
            new DecodingPattern
            {
                Name = "Yageo RC-Series",
                Regex = @"^RC(\d{4})([A-Z])([A-Z]?)-?0?(\d+)([RKM])([A-Z])?$",
                Extractor = ExtractYageoRC,
                Confidence = "High"
            },

            // Vishay CRCW-Series: CRCW0402100KFKED
            new DecodingPattern
            {
                Name = "Vishay CRCW-Series",
                Regex = @"^CRCW(\d{4})(\d+)([RKM])([A-Z])(.*)$",
                Extractor = ExtractVishay,
                Confidence = "High"
            },

            // Panasonic ERJ-Series: ERJ-2RKF1000X
            new DecodingPattern
            {
                Name = "Panasonic ERJ-Series",
                Regex = @"^ERJ-?(\d)([A-Z]+)(\d{3,4})([A-Z])$",
                Extractor = ExtractPanasonic,
                Confidence = "Medium"
            },

            // Generic pattern with package size and value: 0402-100K-F
            new DecodingPattern
            {
                Name = "Generic with Package",
                Regex = @"(0201|0402|0603|0805|1206|1210|2010|2512).*?(\d{1,4})([RKM]).*?([BDFGJK])?",
                Extractor = ExtractGenericWithPackage,
                Confidence = "Low"
            }
        };

        #endregion

        #region Pattern Definition Classes

        private class DecodingPattern
        {
            public string Name { get; set; }
            public string Regex { get; set; }
            public Func<Match, DecodedParameters> Extractor { get; set; }
            public string Confidence { get; set; }
        }

        private class DecodedParameters
        {
            public double Resistance { get; set; }
            public double Tolerance { get; set; }
            public double Power { get; set; }
            public string PackageType { get; set; }
            public double TemperatureCoefficient { get; set; }
            public string ComponentType { get; set; }
            public string Source { get; set; }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Decodes a manufacturer part number and returns extracted parameters.
        /// Uses pattern matching to automatically detect manufacturer format.
        /// </summary>
        /// <param name="partNumber">Part number (e.g., "RC0201FR-0775KL")</param>
        /// <param name="manufacturer">Manufacturer name (optional, used for logging)</param>
        /// <param name="componentCategory">Component category (e.g., "Resistor")</param>
        /// <returns>JObject with decoded parameters or null if decoding fails</returns>
        public JObject DecodePartNumber(string partNumber, string manufacturer, string componentCategory)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
            {
                Console.WriteLine("Part Number ist leer - keine Dekodierung möglich");
                return null;
            }

            Console.WriteLine($"\nSTARTE PART NUMBER DEKODIERUNG");
            Console.WriteLine($"   Part Number: {partNumber}");
            Console.WriteLine($"   Hersteller: {manufacturer ?? "unbekannt"}");
            Console.WriteLine($"   Kategorie: {componentCategory ?? "unbekannt"}");

            // Check if component category is supported
            if (!IsSupportedCategory(componentCategory))
            {
                Console.WriteLine($"Komponenten-Kategorie '{componentCategory}' wird noch nicht unterstützt");
                Console.WriteLine("  Nur Resistoren werden derzeit automatisch dekodiert");
                return null;
            }

            // Try each decoding pattern in order
            foreach (var pattern in DecodingPatterns)
            {
                var match = Regex.Match(partNumber, pattern.Regex, RegexOptions.IgnoreCase);
                
                if (match.Success)
                {
                    Console.WriteLine($"Pattern erkannt: {pattern.Name}");
                    
                    try
                    {
                        var decoded = pattern.Extractor(match);
                        
                        if (decoded != null)
                        {
                            Console.WriteLine("\nDEKODIERUNG ERFOLGREICH:");
                            Console.WriteLine($"  Widerstand: {decoded.Resistance} Ohm");
                            Console.WriteLine($"  Toleranz: ±{decoded.Tolerance}%");
                            Console.WriteLine($"  Leistung: {decoded.Power} W");
                            Console.WriteLine($"  Gehäuse: {decoded.PackageType}");

                            return CreateResultJson(decoded, pattern.Name, pattern.Confidence);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Fehler bei Pattern '{pattern.Name}': {ex.Message}");
                        // Continue to next pattern
                    }
                }
            }

            Console.WriteLine("\nDEKODIERUNG FEHLGESCHLAGEN");
            Console.WriteLine("  Kein passendes Pattern gefunden - manuelle Eingabe erforderlich");
            return null;
        }

        #endregion

        #region Pattern Extractors

        /// <summary>
        /// Extracts parameters from Yageo RC-Series format.
        /// Format: RC[SIZE][TOLERANCE][TEMP]-[VALUE][MULTIPLIER][PACKAGING]
        /// Example: RC0201FR-0775KL
        /// </summary>
        private static DecodedParameters ExtractYageoRC(Match match)
        {
            string sizeCode = match.Groups[1].Value;         // "0201"
            string toleranceCode = match.Groups[2].Value;    // "F"
            string valueStr = match.Groups[4].Value;         // "775"
            string multiplier = match.Groups[5].Value;       // "K"

            return new DecodedParameters
            {
                PackageType = sizeCode,
                Power = GetPowerRating(sizeCode),
                Tolerance = GetTolerance(toleranceCode),
                Resistance = CalculateResistance(valueStr, multiplier),
                TemperatureCoefficient = 100.0, // Standard for RC-series
                ComponentType = "THICK FILM",
                Source = "Yageo RC-Series"
            };
        }

        /// <summary>
        /// Extracts parameters from Vishay CRCW-Series format.
        /// Format: CRCW[SIZE][VALUE][MULTIPLIER][TOLERANCE][FEATURES]
        /// Example: CRCW0402100KFKED
        /// </summary>
        private static DecodedParameters ExtractVishay(Match match)
        {
            string sizeCode = match.Groups[1].Value;         // "0402"
            string valueStr = match.Groups[2].Value;         // "100"
            string multiplier = match.Groups[3].Value;       // "K"
            string toleranceCode = match.Groups[4].Value;    // "F"

            return new DecodedParameters
            {
                PackageType = sizeCode,
                Power = GetPowerRating(sizeCode),
                Tolerance = GetTolerance(toleranceCode),
                Resistance = CalculateResistance(valueStr, multiplier),
                TemperatureCoefficient = 100.0, // Standard for CRCW-series
                ComponentType = "THICK FILM",
                Source = "Vishay CRCW-Series"
            };
        }

        /// <summary>
        /// Extracts parameters from Panasonic ERJ-Series format.
        /// Format: ERJ-[SIZE][SERIES][VALUE][PACKAGING]
        /// Example: ERJ-2RKF1000X
        /// Note: Uses special size encoding (1=0201, 2=0402, etc.)
        /// </summary>
        private static DecodedParameters ExtractPanasonic(Match match)
        {
            string sizeDigit = match.Groups[1].Value;        // "2"
            string valueCode = match.Groups[3].Value;        // "1000"

            string packageType = DecodePanasonicSize(sizeDigit);
            double resistance = DecodePanasonicValue(valueCode);

            return new DecodedParameters
            {
                PackageType = packageType,
                Power = GetPowerRating(packageType),
                Tolerance = 1.0, // Standard for RKF-series
                Resistance = resistance,
                TemperatureCoefficient = 100.0,
                ComponentType = "THICK FILM",
                Source = "Panasonic ERJ-Series"
            };
        }

        /// <summary>
        /// Extracts parameters from generic format with package size.
        /// Fallback pattern for unknown manufacturers.
        /// Example: 0402-100K-F or similar variations
        /// </summary>
        private static DecodedParameters ExtractGenericWithPackage(Match match)
        {
            string packageType = match.Groups[1].Value;      // "0402"
            string valueStr = match.Groups[2].Value;         // "100"
            string multiplier = match.Groups[3].Value;       // "K"
            string toleranceCode = match.Groups[4].Success ? match.Groups[4].Value : "J";

            return new DecodedParameters
            {
                PackageType = packageType,
                Power = GetPowerRating(packageType),
                Tolerance = GetTolerance(toleranceCode),
                Resistance = CalculateResistance(valueStr, multiplier),
                TemperatureCoefficient = 100.0, // Default assumption
                ComponentType = "THICK FILM",
                Source = "Generic Pattern"
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Checks if the component category is supported for decoding.
        /// </summary>
        private bool IsSupportedCategory(string componentCategory)
        {
            if (string.IsNullOrWhiteSpace(componentCategory))
                return false;

            // Support any category containing "Resistor" or "Widerstand"
            return componentCategory.IndexOf("Resistor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   componentCategory.IndexOf("Widerstand", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Calculates resistance value from base value and multiplier.
        /// </summary>
        private static double CalculateResistance(string valueStr, string multiplier)
        {
            double value = double.Parse(valueStr);
            
            if (ResistanceMultipliers.TryGetValue(multiplier.ToUpper(), out double mult))
            {
                return value * mult;
            }

            return value; // Default to Ohms
        }

        /// <summary>
        /// Gets tolerance percentage from IEC 60062 code.
        /// </summary>
        private static double GetTolerance(string code)
        {
            if (ToleranceCodes.TryGetValue(code.ToUpper(), out double tolerance))
            {
                return tolerance;
            }

            return 5.0; // Default ±5%
        }

        /// <summary>
        /// Gets power rating from package size.
        /// </summary>
        private static double GetPowerRating(string packageType)
        {
            if (PackagePowerRatings.TryGetValue(packageType, out double power))
            {
                return power;
            }

            return 0.1; // Default 1/10 W
        }

        /// <summary>
        /// Decodes Panasonic special size encoding.
        /// </summary>
        private static string DecodePanasonicSize(string sizeDigit)
        {
            var sizeMap = new Dictionary<string, string>
            {
                { "1", "0201" },
                { "2", "0402" },
                { "3", "0603" },
                { "6", "0805" },
                { "8", "1206" }
            };

            return sizeMap.TryGetValue(sizeDigit, out string size) ? size : sizeDigit + "xx";
        }

        /// <summary>
        /// Decodes Panasonic resistance value encoding.
        /// Uses 3-4 digit EIA code: base value × 10^multiplier
        /// </summary>
        private static double DecodePanasonicValue(string valueCode)
        {
            if (valueCode.Length == 4)
            {
                string baseValue = valueCode.Substring(0, 3);
                string multiplierDigit = valueCode.Substring(3, 1);

                double baseVal = double.Parse(baseValue);
                int multiplier = int.Parse(multiplierDigit);

                return baseVal * Math.Pow(10, multiplier);
            }
            else if (valueCode.Length == 3)
            {
                string baseValue = valueCode.Substring(0, 2);
                string multiplierDigit = valueCode.Substring(2, 1);

                double baseVal = double.Parse(baseValue);
                int multiplier = int.Parse(multiplierDigit);

                return baseVal * Math.Pow(10, multiplier);
            }

            return 0; // Parsing error
        }

        /// <summary>
        /// Creates JSON result object from decoded parameters.
        /// </summary>
        private JObject CreateResultJson(DecodedParameters decoded, string source, string confidence)
        {
            return new JObject
            {
                ["ResistaOhm"] = decoded.Resistance,
                ["TolerancePct"] = decoded.Tolerance,
                ["PowerW"] = decoded.Power,
                ["TempCoeffPPM"] = decoded.TemperatureCoefficient,
                ["PackageType"] = decoded.PackageType,
                ["resistorConstant"] = decoded.ComponentType,
                ["DecodingSource"] = source,
                ["DecodingConfidence"] = confidence
            };
        }

        #endregion
    }
}
