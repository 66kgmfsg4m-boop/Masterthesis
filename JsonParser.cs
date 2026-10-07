using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CheckOrderConfirmationFromSupplier.Util
{
    /// <summary>
    /// JSON Parser Utility
    /// 
    /// Converts JSON structures into formatted display items for UI presentation.
    /// Provides hierarchical formatting with indentation and special handling for arrays.
    /// 
    /// Features:
    /// - Recursive JSON tree traversal
    /// - Indentation-based hierarchy visualization
    /// - Special formatting for object arrays (with summaries)
    /// - Separator lines between array elements
    /// - Empty array detection
    /// 
    /// Use Cases:
    /// - Displaying extracted datasheet properties
    /// - Showing order confirmation data
    /// - Pretty-printing JSON for user review
    /// 
    /// Architecture:
    /// - JsonDisplayItem: Represents a single line in the output
    /// - ParseJsonToDisplayItems: Public entry point
    /// - ParseNode: Recursive parser for all JSON node types
    /// - GetObjectSummary: Creates inline summaries for array objects
    /// 
    /// Example Output:
    /// DocumentType: COMPONENT
    /// ComponentCategory: Resistor
    /// Properties: {
    ///   ResistaOhm: 10000
    ///   TolerancePct: 1
    /// }
    /// </summary>
    public class JsonParser
    {
        #region Configuration

        // Maximum number of fields to show in object summary
        private const int MAX_SUMMARY_FIELDS = 5;

        // Priority fields for object summaries (in order of importance)
        private static readonly string[] PriorityFields = 
        {
            "PartNumber",
            "ResistorOhm", "Resistance",
            "Tolerance", "TolerancePct",
            "PowerConsumed", "PowerRating", "PowerW",
            "PackageSize", "PackageType",
            "OrderCode",
            "Description",
            "Name"
        };

        #endregion

        #region Data Classes

        /// <summary>
        /// Represents a single formatted line in the JSON display output.
        /// Contains key-value pair with indentation level for hierarchical display.
        /// </summary>
        public class JsonDisplayItem
        {
            public string Key { get; set; }
            public string Value { get; set; }
            public int Level { get; set; }

            public JsonDisplayItem(string key, string value, int level)
            {
                Key = key;
                Value = value;
                Level = level;
            }

            /// <summary>
            /// Formats the item with proper indentation for display.
            /// Format: "  Key: Value" or "  Value" (if no key)
            /// </summary>
            public override string ToString()
            {
                string indent = new string(' ', Level * 2);
                
                if (!string.IsNullOrEmpty(Key))
                {
                    return $"{indent}{Key}: {Value}";
                }
                else
                {
                    return $"{indent}{Value}";
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Parses a JSON string into a list of formatted display items.
        /// Handles objects, arrays, and primitive values recursively.
        /// </summary>
        /// <param name="jsonString">JSON string to parse</param>
        /// <returns>List of formatted display items with hierarchy</returns>
        public List<JsonDisplayItem> ParseJsonToDisplayItems(string jsonString)
        {
            var items = new List<JsonDisplayItem>();

            try
            {
                var rootNode = JToken.Parse(jsonString);
                ParseNode(rootNode, "", 0, items);
            }
            catch (Exception e)
            {
                items.Add(new JsonDisplayItem("Error", $"Invalid JSON: {e.Message}", 0));
            }

            return items;
        }

        #endregion

        #region JSON Parsing Logic

        /// <summary>
        /// Recursively parses a JSON node and converts it to display items.
        /// Handles three node types:
        /// 1. Objects: Shown with { } brackets
        /// 2. Arrays: Special formatting with summaries for object arrays
        /// 3. Primitives: Direct key-value display
        /// </summary>
        /// <param name="node">JSON node to parse</param>
        /// <param name="key">Property name (empty for root)</param>
        /// <param name="level">Indentation level</param>
        /// <param name="items">Output list to append items to</param>
        private void ParseNode(JToken node, string key, int level, List<JsonDisplayItem> items)
        {
            if (node.Type == JTokenType.Object)
            {
                ParseObject(node, key, level, items);
            }
            else if (node.Type == JTokenType.Array)
            {
                ParseArray(node, key, level, items);
            }
            else
            {
                ParsePrimitive(node, key, level, items);
            }
        }

        /// <summary>
        /// Parses a JSON object and displays its properties.
        /// Format:
        /// ObjectName: {
        ///   Property1: Value1
        ///   Property2: Value2
        /// }
        /// </summary>
        private void ParseObject(JToken node, string key, int level, List<JsonDisplayItem> items)
        {
            // Show opening bracket with key (if present)
            if (!string.IsNullOrEmpty(key))
            {
                items.Add(new JsonDisplayItem(key, "{", level));
            }

            // Parse all properties
            var obj = (JObject)node;
            foreach (var property in obj.Properties())
            {
                ParseNode(property.Value, property.Name, level + 1, items);
            }

            // Show closing bracket
            if (!string.IsNullOrEmpty(key))
            {
                items.Add(new JsonDisplayItem("", "}", level));
            }
        }

        /// <summary>
        /// Parses a JSON array with special formatting.
        /// Two display modes:
        /// 1. Object arrays: Show count + summaries + details with separators
        /// 2. Primitive arrays: Standard bracket notation
        /// </summary>
        private void ParseArray(JToken node, string key, int level, List<JsonDisplayItem> items)
        {
            var array = (JArray)node;

            if (array.Count == 0)
            {
                // Empty array: show placeholder
                items.Add(new JsonDisplayItem(key, "[ leer ]", level));
                return;
            }

            bool containsObjects = array[0].Type == JTokenType.Object;

            if (containsObjects)
            {
                ParseObjectArray(array, key, level, items);
            }
            else
            {
                ParsePrimitiveArray(array, key, level, items);
            }
        }

        /// <summary>
        /// Parses an array of objects with enhanced formatting.
        /// Shows:
        /// - Array count
        /// - Inline summary for each object
        /// - Detailed properties
        /// - Separator lines between elements
        /// 
        /// Example:
        /// ResistanceVariants: [ 3 Einträge ]
        ///   [0]: RC0805-10R, 10 Ohm, 0.125W
        ///     PartNumber: RC0805-10R
        ///     ResistaOhm: 10
        ///     PowerW: 0.125
        ///   ---
        ///   [1]: RC0805-100R, 100 Ohm, 0.125W
        ///     ...
        /// </summary>
        private void ParseObjectArray(JArray array, string key, int level, List<JsonDisplayItem> items)
        {
            // Show array header with count
            items.Add(new JsonDisplayItem(key, $"[ {array.Count} Einträge ]", level));

            for (int i = 0; i < array.Count; i++)
            {
                // Show index with inline summary
                string summary = GetObjectSummary(array[i] as JObject);
                items.Add(new JsonDisplayItem($"  [{i}]", summary, level + 1));

                // Show detailed properties
                var obj = array[i] as JObject;
                foreach (var property in obj.Properties())
                {
                    ParseNode(property.Value, property.Name, level + 2, items);
                }

                // Add separator between elements (except after last)
                if (i < array.Count - 1)
                {
                    items.Add(new JsonDisplayItem("", "---", level + 1));
                }
            }
        }

        /// <summary>
        /// Parses an array of primitive values (strings, numbers, etc.)
        /// Standard bracket notation:
        /// ArrayName: [
        ///   [0]: Value1
        ///   [1]: Value2
        /// ]
        /// </summary>
        private void ParsePrimitiveArray(JArray array, string key, int level, List<JsonDisplayItem> items)
        {
            items.Add(new JsonDisplayItem(key, "[", level));

            for (int i = 0; i < array.Count; i++)
            {
                ParseNode(array[i], $"[{i}]", level + 1, items);
            }

            items.Add(new JsonDisplayItem("", "]", level));
        }

        /// <summary>
        /// Parses a primitive value (string, number, boolean, null).
        /// Simple key-value display.
        /// </summary>
        private void ParsePrimitive(JToken node, string key, int level, List<JsonDisplayItem> items)
        {
            string value = node.ToString();
            items.Add(new JsonDisplayItem(key, value, level));
        }

        #endregion

        #region Object Summary Generation

        /// <summary>
        /// Creates a one-line summary of a JSON object for inline display.
        /// Prioritizes important fields (PartNumber, Resistance, etc.) and limits to 5 fields.
        /// 
        /// Examples:
        /// - "RC0805-10R, 10 Ohm, ±1%, 0.125W"
        /// - "Yageo, RC Series, 0402 Package"
        /// - "100k, ±5%, 0.25W"
        /// 
        /// Algorithm:
        /// 1. Check priority fields in order
        /// 2. Add first 5 non-empty values
        /// 3. Fallback: Show first 3 properties
        /// </summary>
        /// <param name="obj">JSON object to summarize</param>
        /// <returns>Comma-separated summary string</returns>
        private string GetObjectSummary(JObject obj)
        {
            if (obj == null) 
                return "";

            var summaryParts = new List<string>();

            // Add priority fields (up to MAX_SUMMARY_FIELDS)
            foreach (var field in PriorityFields)
            {
                if (summaryParts.Count >= MAX_SUMMARY_FIELDS)
                    break;

                var value = obj[field]?.ToString();
                
                if (IsValidSummaryValue(value))
                {
                    summaryParts.Add(value);
                }
            }

            // Fallback: If no priority fields found, show first 3 properties
            if (summaryParts.Count == 0)
            {
                summaryParts = obj.Properties()
                    .Take(3)
                    .Select(p => $"{p.Name}={p.Value}")
                    .ToList();
            }

            return string.Join(", ", summaryParts);
        }

        /// <summary>
        /// Checks if a value is valid for inclusion in summary.
        /// Excludes null, empty, and placeholder values.
        /// </summary>
        private bool IsValidSummaryValue(string value)
        {
            return !string.IsNullOrEmpty(value) && 
                   value != "nicht vorhanden" &&
                   value != "null";
        }

        #endregion
    }
}
