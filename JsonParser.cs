using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace DatasheetAnalyzer.Core.Util;

/// <summary>
/// Converts JSON structures into formatted display items for UI presentation.
/// </summary>
public class JsonParser
{
    private const int MAX_SUMMARY_FIELDS = 5;
    private static readonly string[] PriorityFields =
    {
        "PartNumber", "ResistorOhm", "Resistance", "Tolerance", "TolerancePct",
        "PowerConsumed", "PowerRating", "PowerW", "PackageSize", "PackageType",
        "OrderCode", "Description", "Name"
    };

    public class JsonDisplayItem
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public int Level { get; set; }
        public JsonDisplayItem(string key, string value, int level) { Key = key; Value = value; Level = level; }
        public override string ToString()
        {
            var indent = new string(' ', Level * 2);
            return string.IsNullOrEmpty(Key) ? $"{indent}{Value}" : $"{indent}{Key}: {Value}";
        }
    }

    public List<JsonDisplayItem> ParseJsonToDisplayItems(string jsonString)
    {
        var items = new List<JsonDisplayItem>();
        try
        {
            var rootNode = JToken.Parse(jsonString);
            ParseNode(rootNode, "", 0, items);
        }
        catch (System.Exception e)
        {
            items.Add(new JsonDisplayItem("Error", $"Invalid JSON: {e.Message}", 0));
        }
        return items;
    }

    private void ParseNode(JToken node, string key, int level, List<JsonDisplayItem> items)
    {
        if (node.Type == JTokenType.Object) ParseObject(node, key, level, items);
        else if (node.Type == JTokenType.Array) ParseArray(node, key, level, items);
        else ParsePrimitive(node, key, level, items);
    }

    private void ParseObject(JToken node, string key, int level, List<JsonDisplayItem> items)
    {
        if (!string.IsNullOrEmpty(key)) items.Add(new JsonDisplayItem(key, "{", level));
        var obj = (JObject)node;
        foreach (var property in obj.Properties())
            ParseNode(property.Value, property.Name, level + 1, items);
        if (!string.IsNullOrEmpty(key)) items.Add(new JsonDisplayItem("", "}", level));
    }

    private void ParseArray(JToken node, string key, int level, List<JsonDisplayItem> items)
    {
        var array = (JArray)node;
        if (array.Count == 0) { items.Add(new JsonDisplayItem(key, "[ leer ]", level)); return; }
        bool containsObjects = array[0].Type == JTokenType.Object;
        if (containsObjects) ParseObjectArray(array, key, level, items);
        else ParsePrimitiveArray(array, key, level, items);
    }

    private void ParseObjectArray(JArray array, string key, int level, List<JsonDisplayItem> items)
    {
        items.Add(new JsonDisplayItem(key, $"[ {array.Count} Einträge ]", level));
        for (int i = 0; i < array.Count; i++)
        {
            string summary = GetObjectSummary(array[i] as JObject);
            items.Add(new JsonDisplayItem($"  [{i}]", summary, level + 1));
            var obj = array[i] as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties())
                    ParseNode(property.Value, property.Name, level + 2, items);
            }
            if (i < array.Count - 1) items.Add(new JsonDisplayItem("", "---", level + 1));
        }
    }

    private void ParsePrimitiveArray(JArray array, string key, int level, List<JsonDisplayItem> items)
    {
        items.Add(new JsonDisplayItem(key, "[", level));
        for (int i = 0; i < array.Count; i++)
            ParseNode(array[i], $"[{i}]", level + 1, items);
        items.Add(new JsonDisplayItem("", "]", level));
    }

    private void ParsePrimitive(JToken node, string key, int level, List<JsonDisplayItem> items)
    {
        items.Add(new JsonDisplayItem(key, node.ToString(), level));
    }

    private static string GetObjectSummary(JObject? obj)
    {
        if (obj == null) return "";
        var summaryParts = new List<string>();
        foreach (var field in PriorityFields)
        {
            if (summaryParts.Count >= MAX_SUMMARY_FIELDS) break;
            var value = obj[field]?.ToString();
            if (!string.IsNullOrEmpty(value) && value != "nicht vorhanden" && value != "null")
                summaryParts.Add(value);
        }
        if (summaryParts.Count == 0)
            summaryParts = obj.Properties().Take(3).Select(p => $"{p.Name}={p.Value}").ToList();
        return string.Join(", ", summaryParts);
    }
}
