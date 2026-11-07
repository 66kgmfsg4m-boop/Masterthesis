using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DatasheetAnalyzer.Core.Services;

/// <summary>
/// Opens the same parameter query on distributor pages.
/// A configured Mouser key calls the keyword search. Without that key the
/// pages themselves are the search. Nothing here orders a part.
/// </summary>
public static class DistributorSearch
{
    public sealed record Link(string Name, string Url);

    public sealed record Hit(string Manufacturer, string PartNumber, string Description);

    public static IReadOnlyList<Link> Links(string keyword)
    {
        string query = Uri.EscapeDataString(keyword ?? "");
        return new Link[]
        {
            new("Mouser", "https://www.mouser.de/c/?q=" + query),
            new("Digi-Key", "https://www.digikey.de/de/products/result?keywords=" + query),
            new("Farnell", "https://de.farnell.com/search?st=" + query),
            new("LCSC", "https://www.lcsc.com/search?q=" + query)
        };
    }

    public static bool HasMouserKey() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MOUSER_API_KEY"));

    public static async Task<IReadOnlyList<Hit>> SearchMouserAsync(
        string keyword,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        string? key = Environment.GetEnvironmentVariable("MOUSER_API_KEY");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(keyword))
            return Array.Empty<Hit>();

        bool ownClient = client == null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            string url = "https://api.mouser.com/api/v1/search/keyword?apiKey=" + Uri.EscapeDataString(key);
            var body = new JObject
            {
                ["SearchByKeywordRequest"] = new JObject
                {
                    ["keyword"] = keyword,
                    ["records"] = 8,
                    ["startingRecord"] = 0,
                    ["searchOptions"] = "None",
                    ["searchWithYourSignUpLanguage"] = "false"
                }
            };
            using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content, cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Mouser-Suche fehlgeschlagen (" + (int)response.StatusCode + ").");
            return ParseMouser(payload);
        }
        finally
        {
            if (ownClient)
                client.Dispose();
        }
    }

    public static IReadOnlyList<Hit> ParseMouser(string json)
    {
        var root = JObject.Parse(json);
        var parts = root["SearchResults"]?["Parts"] as JArray;
        if (parts == null)
            return Array.Empty<Hit>();

        return parts.OfType<JObject>()
            .Select(part => new Hit(
                part["Manufacturer"]?.ToString() ?? "",
                part["ManufacturerPartNumber"]?.ToString() ?? "",
                part["Description"]?.ToString() ?? ""))
            .Where(hit => hit.PartNumber.Length > 0)
            .ToList();
    }
}
