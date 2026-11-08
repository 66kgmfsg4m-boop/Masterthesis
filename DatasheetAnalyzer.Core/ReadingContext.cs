using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Keeps unit, condition, variant and page next to a value. A missing value stays "nicht vorhanden".
/// </summary>
public static class ReadingContext
{
    public const string Carried = "_carried";
    public const string Reading = "_reading";

    public const string PromptRule = @"

================================================================
 PFLICHT: STRUKTURIERTER WERT UND SEITE
================================================================
Jedes Fachfeld ist ein Objekt, keine nackte Zeichenkette.

""GainTypDb"": {""value"":""17"",""unit"":""dB"",""condition"":""Vd = 5 V, f = 20 GHz"",""variant"":"""",""page"":2}

Regeln:
  - value enthaelt die Zahl oder den Text, ohne Einheit.
  - unit enthaelt die gedruckte Einheit, wenn eine dasteht.
  - condition enthaelt die Pruefbedingung (Spannung, Strom, Frequenz, Temperatur).
  - variant bleibt leer, solange das Blatt keine zweite Bestellvariante unterscheidet.
    Steht der Wert in einer Spalte einer bestimmten Bestellnummer, setze diese Nummer.
  - page ist die Seitennummer aus der Markierung [[page:N]] der Stelle, an der der Wert steht.
  - Minimum, Typical und Maximum bleiben in den jeweils eigenen Feldern.
    Schreibe sie nicht als einen Text mit Schraegstrichen in ein Feld.
  - Fehlt die Angabe im Dokument, setze value auf 'nicht vorhanden'.
    Schreibe dafuer nicht 0 und nicht eine leere Zeichenkette.
  - page ist bei 'nicht vorhanden' null.
================================================================
";

    private static readonly string[] AbsenceMarkers =
    {
        "nicht vorhanden", "nicht gefunden", "nicht klar erkennbar",
        "mehrdeutig", "nicht relevant", "n/a", "-"
    };

    private static readonly HashSet<string> Meta = new(StringComparer.OrdinalIgnoreCase)
    {
        "ComponentType", "PartNumber", "Manufacturer", "ManufacturerInfo",
        "Description", "DocumentType", "ComponentCategory", "ComponentName",
        "WasCorrected", "OriginalCategory", "CorrectionTimestamp",
        "IsSeriesDatasheet", "CategoryWasChanged",
        "_PropertySchemaUsed", "_RequestedCategory", Carried, Reading
    };

    private static readonly Regex UnitTail = new(
        @"^(?<v>-?\d+(?:[.,]\d+)?)\s*(?<u>dBm|dBc|dB|µV|uV|mV|kV|V|µA|uA|mA|A|µW|uW|mW|kW|W|GHz|MHz|kHz|Hz|kOhm|MOhm|Ohm|kΩ|MΩ|Ω|°C|mm|µm|um|mil|ps|ns|µs|us|ms|s|%|pF|nF|µF|uF|nH|µH|uH)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StatementMark = new(
        @"(Min|Max|Typ)(?=[A-Z]|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record Row(
        string Designation,
        string Value,
        string Unit,
        string Condition,
        string Variant,
        string Statement,
        int? Page,
        bool Absent)
    {
        public string PageLabel =>
            Absent ? "im Datenblatt nicht angegeben" :
            Page is int page ? "Seite " + page.ToString(CultureInfo.InvariantCulture) :
            "Seite nicht zugeordnet";

        public string Display
        {
            get
            {
                if (Absent || string.IsNullOrWhiteSpace(Unit))
                    return Value;
                if (Value.Contains(Unit, StringComparison.OrdinalIgnoreCase))
                    return Value;
                return Value + " " + Unit;
            }
        }
    }

    public static bool IsAbsence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        return AbsenceMarkers.Any(marker =>
            string.Equals(value.Trim(), marker, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsEmpty(JToken? token) => IsAbsence(Plain(token));

    public static string? Plain(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
            return null;
        if (token is JObject obj)
        {
            string? value = obj["value"]?.Type == JTokenType.Null ? null : obj["value"]?.ToString();
            string? unit = obj["unit"]?.Type == JTokenType.Null ? null : obj["unit"]?.ToString();
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (IsAbsence(value) || string.IsNullOrWhiteSpace(unit))
                return value.Trim();
            if (value.Contains(unit, StringComparison.OrdinalIgnoreCase))
                return value.Trim();
            return value.Trim() + " " + unit.Trim();
        }
        return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
    }

    /// <summary>
    /// Remembers unit, condition, variant and page from an object the model returned,
    /// then leaves a plain string so the catalogue filter can keep working on text.
    /// </summary>
    public static void TakeDetails(JObject json)
    {
        var carried = json[Carried] as JObject ?? new JObject();
        foreach (var prop in json.Properties().ToList())
        {
            if (prop.Name.StartsWith("_", StringComparison.Ordinal))
                continue;
            if (prop.Value is not JObject obj)
                continue;
            carried[prop.Name] = obj.DeepClone();
            json[prop.Name] = Plain(obj) ?? "";
        }
        if (carried.HasValues)
            json[Carried] = carried;
    }

    public static void Attach(
        JObject json,
        IReadOnlyDictionary<int, string>? pages,
        string? confirmedPartNumber)
    {
        var carried = json[Carried] as JObject;
        var names = json.Properties()
            .Select(prop => prop.Name)
            .Where(name => !Meta.Contains(name) && !name.StartsWith("_", StringComparison.Ordinal))
            .ToList();

        var plain = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            plain[name] = Plain(json[name]);

        SeparateStatements(plain);

        var reading = new JArray();
        foreach (var name in names)
        {
            var row = Build(name, plain[name], carried?[name] as JObject, pages, confirmedPartNumber);
            json[name] = ToToken(row);
            reading.Add(ToToken(row));
        }

        json[Reading] = reading;
        json.Remove(Carried);
    }

    public static IReadOnlyList<Row> Rows(JObject json)
    {
        if (json[Reading] is JArray array && array.Count > 0)
            return array.OfType<JObject>().Select(FromToken).ToList();

        return json.Properties()
            .Where(prop => !Meta.Contains(prop.Name) && !prop.Name.StartsWith("_", StringComparison.Ordinal))
            .Select(prop => Build(prop.Name, Plain(prop.Value), prop.Value as JObject, null, null))
            .ToList();
    }

    public static string FormatLog(JObject json)
    {
        var builder = new StringBuilder();
        foreach (var row in Rows(json))
        {
            builder.Append(row.Designation);
            builder.Append(" = ");
            builder.Append(row.Display);
            if (!string.IsNullOrWhiteSpace(row.Condition))
                builder.Append(" | Bedingung: ").Append(row.Condition);
            if (!string.IsNullOrWhiteSpace(row.Variant))
                builder.Append(" | Variante: ").Append(row.Variant);
            if (!string.IsNullOrWhiteSpace(row.Statement))
                builder.Append(" | ").Append(row.Statement);
            builder.Append(" | Quelle: ").Append(row.PageLabel);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static (string Value, string? Unit, string? Condition) Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ("", null, null);
        string text = raw.Trim();
        if (IsAbsence(text))
            return (text, null, null);

        string? condition = null;
        var paren = Regex.Match(text, @"^(?<v>.*?)\s*\((?<c>[^)]+)\)\s*$");
        if (paren.Success && Regex.IsMatch(paren.Groups["c"].Value, @"[A-Za-z]"))
        {
            condition = paren.Groups["c"].Value.Trim();
            text = paren.Groups["v"].Value.Trim();
        }
        else
        {
            var at = Regex.Match(text, @"^(?<v>.*?)\s+(?:@|bei|at)\s+(?<c>.+)$", RegexOptions.IgnoreCase);
            if (at.Success)
            {
                condition = at.Groups["c"].Value.Trim();
                text = at.Groups["v"].Value.Trim();
            }
        }

        var unit = UnitTail.Match(text);
        if (!unit.Success)
            return (text, null, condition);
        return (unit.Groups["v"].Value, unit.Groups["u"].Value, condition);
    }

    public static string StatementOf(string designation)
    {
        var match = StatementMark.Match(designation);
        if (!match.Success)
            return "";
        return match.Value.ToLowerInvariant();
    }

    public static void SeparateStatements(IDictionary<string, string?> fields)
    {
        var grouped = fields.Keys
            .Select(name => (Name: name, Kind: StatementOf(name), Base: StatementMark.Replace(name, "")))
            .Where(item => item.Kind.Length > 0)
            .GroupBy(item => item.Base, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var members = group.ToList();
            var kinds = members.Select(item => item.Kind).ToHashSet(StringComparer.Ordinal);
            bool threeKinds = kinds.Contains("min") && kinds.Contains("typ") && kinds.Contains("max");
            if (members.Count < 2)
                continue;

            foreach (var holder in members)
            {
                string? raw = fields[holder.Name];
                if (IsAbsence(raw) || string.IsNullOrWhiteSpace(raw))
                    continue;
                if (Regex.Matches(raw, @"-?\d+(?:[.,]\d+)?").Count < 2)
                    continue;

                var bound = BindStatements(raw, threeKinds);
                if (bound.Count == 0)
                    continue;

                string? unit = TrailingUnit(raw) ?? Split(raw).Unit;
                foreach (var member in members)
                {
                    if (!bound.TryGetValue(member.Kind, out var number))
                        continue;
                    bool filled = !IsAbsence(fields[member.Name]);
                    bool sameHolder = string.Equals(member.Name, holder.Name, StringComparison.OrdinalIgnoreCase);
                    if (filled && !sameHolder)
                        continue;
                    fields[member.Name] = string.IsNullOrWhiteSpace(unit)
                        ? number
                        : number + " " + unit;
                }
                break;
            }
        }
    }

    public static int? LocatePage(
        IReadOnlyDictionary<int, string>? pages,
        string? value,
        string? unit,
        int? hinted)
    {
        if (pages == null || pages.Count == 0 || IsAbsence(value) || string.IsNullOrWhiteSpace(value))
            return null;

        int? bestPage = null;
        int bestScore = 0;
        foreach (var page in pages.OrderBy(pair => pair.Key))
        {
            if (string.IsNullOrWhiteSpace(page.Value))
                continue;
            int score = 0;
            if (Hits(page.Value, value))
                score += 4;
            if (!string.IsNullOrWhiteSpace(unit) &&
                page.Value.Contains(unit, StringComparison.OrdinalIgnoreCase))
                score += 2;
            if (hinted == page.Key)
                score += 1;
            if (score > bestScore)
            {
                bestScore = score;
                bestPage = page.Key;
            }
        }

        if (bestScore >= 4)
            return bestPage;
        return null;
    }

    private static Row Build(
        string name,
        string? plain,
        JObject? carried,
        IReadOnlyDictionary<int, string>? pages,
        string? confirmedPartNumber)
    {
        string? carriedPlain = Plain(carried);
        bool useCarried = carried != null &&
            string.Equals((plain ?? "").Trim(), (carriedPlain ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        if (!useCarried)
            carried = null;

        var split = Split(plain);
        string? unit = FirstText(carried?["unit"]) ?? split.Unit;
        string? condition = FirstText(carried?["condition"]) ?? split.Condition;
        string? variant = FirstText(carried?["variant"]);
        int? hinted = ReadPage(carried?["page"]);
        string value = split.Value;
        if (string.IsNullOrWhiteSpace(value))
            value = FirstText(carried?["value"]) ?? "";

        bool absent = IsAbsence(value);
        int? page = absent ? null : LocatePage(pages, value, unit, hinted);
        if (!absent && string.IsNullOrWhiteSpace(variant) && page is int found &&
            pages != null && pages.TryGetValue(found, out var pageText))
        {
            variant = VariantOnPage(pageText, confirmedPartNumber);
        }

        string stored = !absent
            ? value
            : string.IsNullOrWhiteSpace(value) ? "nicht vorhanden" : value.Trim();

        return new Row(
            name,
            stored,
            absent ? "" : unit ?? "",
            absent ? "" : condition ?? "",
            absent ? "" : variant ?? "",
            StatementOf(name),
            page,
            absent);
    }

    private static string? TrailingUnit(string raw)
    {
        var match = Regex.Match(
            raw,
            @"\b(?<u>dBm|dBc|dB|GHz|MHz|kHz|Hz|mV|kV|V|mA|A|mW|W|Ohm|Ω|°C|mm|%)\s*$",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["u"].Value : null;
    }

    private static Dictionary<string, string> BindStatements(string raw, bool threeKinds)
    {
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            raw,
            @"(?<n>-?\d+(?:[.,]\d+)?)\s*(?<k>min|typ|max)\b|(?<k2>min|typ|max)\s*(?<n2>-?\d+(?:[.,]\d+)?)",
            RegexOptions.IgnoreCase))
        {
            string kind = match.Groups["k"].Success ? match.Groups["k"].Value : match.Groups["k2"].Value;
            string number = match.Groups["n"].Success ? match.Groups["n"].Value : match.Groups["n2"].Value;
            bound[kind.ToLowerInvariant()] = number;
        }
        if (bound.Count > 0)
            return bound;

        if (!threeKinds || raw.IndexOf('/') < 0)
            return bound;

        var numbers = Regex.Matches(raw, @"-?\d+(?:[.,]\d+)?");
        if (numbers.Count != 3)
            return bound;
        string[] kinds = { "min", "typ", "max" };
        for (int i = 0; i < 3; i++)
            bound[kinds[i]] = numbers[i].Value;
        return bound;
    }

    private static string? VariantOnPage(string? pageText, string? confirmed)
    {
        if (string.IsNullOrWhiteSpace(pageText) || string.IsNullOrWhiteSpace(confirmed))
            return null;
        string wanted = Compact(confirmed);
        if (wanted.Length < 5)
            return null;

        var codes = Regex.Matches(pageText, @"\b[A-Za-z][A-Za-z0-9]{4,}\b")
            .Select(match => match.Value)
            .Where(token => token.Any(char.IsDigit))
            .Select(Compact)
            .Where(token => token.Length >= 5)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count < 2)
            return null;
        return codes.Any(code => code.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ? confirmed.Trim()
            : null;
    }

    private static bool Hits(string text, string value)
    {
        if (Regex.IsMatch(value, @"^-?\d+(?:[.,]\d+)?$"))
        {
            string alt = value.Contains('.') ? value.Replace('.', ',') : value.Replace(',', '.');
            return Boundary(text, value) || (alt != value && Boundary(text, alt));
        }
        return text.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Boundary(string text, string token)
    {
        return Regex.IsMatch(
            text,
            @"(?<![0-9])" + Regex.Escape(token) + @"(?![0-9])",
            RegexOptions.IgnoreCase);
    }

    private static string Compact(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToUpperInvariant(ch));
        }
        return builder.ToString();
    }

    private static string? FirstText(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
            return null;
        string text = token.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static int? ReadPage(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
            return null;
        if (token.Type == JTokenType.Integer)
            return token.Value<int>();
        return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) && page > 0
            ? page
            : null;
    }

    private static JObject ToToken(Row row)
    {
        return new JObject
        {
            ["designation"] = row.Designation,
            ["value"] = row.Value,
            ["unit"] = row.Unit,
            ["condition"] = row.Condition,
            ["variant"] = row.Variant,
            ["statement"] = row.Statement,
            ["page"] = row.Page.HasValue ? new JValue(row.Page.Value) : JValue.CreateNull(),
            ["absent"] = row.Absent
        };
    }

    private static Row FromToken(JObject obj)
    {
        return new Row(
            obj["designation"]?.ToString() ?? "",
            obj["value"]?.ToString() ?? "",
            obj["unit"]?.ToString() ?? "",
            obj["condition"]?.ToString() ?? "",
            obj["variant"]?.ToString() ?? "",
            obj["statement"]?.ToString() ?? "",
            ReadPage(obj["page"]),
            obj["absent"]?.Value<bool>() ?? IsAbsence(obj["value"]?.ToString()));
    }
}
