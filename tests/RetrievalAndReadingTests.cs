using DatasheetAnalyzer.Blazor.Services;
using DatasheetAnalyzer.Core.Services;
using Xunit;

public class PassageRetrievalTests
{
    private static Dictionary<int, string> SixPages() => new()
    {
        [1] = "Electrical characteristics gain 20 dB supply voltage frequency response table",
        [2] = "Absolute maximum ratings voltage current power dissipation frequency",
        [3] = "Pin configuration pin assignment gain bandwidth",
        [4] = "Application information gain voltage frequency typical performance",
        [5] = "Package drawing mechanical dimensions pitch land pattern",
        [6] = "Ordering information. Device TRF-37A75. Tape and reel."
    };

    [Fact]
    public void OrderingPageIsKeptWhenThePartNumberIsConfirmed()
    {
        var pages = SixPages();
        const string query = "gain voltage frequency pin electrical characteristics";

        string without = PassageRetrieval.Select(pages, query, null);
        Assert.DoesNotContain("[Page 6]", without);

        string selected = PassageRetrieval.Select(pages, query, "TRF-37A75");
        Assert.Contains("[Page 6]", selected);
        Assert.DoesNotContain("[Page 5]", selected);

        var order = System.Text.RegularExpressions.Regex.Matches(selected, @"\[Page (\d+)\]")
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, order);
    }

    [Fact]
    public void ShortFileIsReturnedInPageOrder()
    {
        var pages = new Dictionary<int, string>
        {
            [2] = "second page gain",
            [1] = "first page voltage"
        };
        string selected = PassageRetrieval.Select(pages, "gain voltage", "TRF37A75");
        Assert.StartsWith("[Page 1]", selected);
        Assert.Contains("[Page 2]", selected);
    }

    [Fact]
    public void PartNumberPrintedOnEveryPageDoesNotOverrideTheTermRank()
    {
        var pages = SixPages().ToDictionary(
            pair => pair.Key,
            pair => pair.Value + " TRF37A75");
        string selected = PassageRetrieval.Select(
            pages,
            "gain voltage frequency pin electrical characteristics",
            "TRF37A75");
        Assert.Contains("[Page 1]", selected);
        Assert.DoesNotContain("[Page 6]", selected);
    }
}

public class SubstituteAlignmentTests
{
    [Fact]
    public void JointReadingRequiresFunctionLimitsAndPinsOnBothSides()
    {
        var left = new Dictionary<string, string?>
        {
            ["Function"] = "amplifier",
            ["Gain"] = "18 dB",
            ["PinAssignment"] = "pin 2 RFIN"
        };
        var right = new Dictionary<string, string?>
        {
            ["Function"] = "amplifier",
            ["SupplyVoltage"] = "5 V",
            ["PinCount"] = "8"
        };

        var report = SubstituteAlignment.Evaluate(left, right);
        Assert.True(report.JointReadingPossible);
        Assert.Equal("", report.MissingGroups);
    }

    [Fact]
    public void AbsenceMarkerAndUnrelatedKeysDoNotFillAGroup()
    {
        var left = new Dictionary<string, string?>
        {
            ["Function"] = "nicht vorhanden",
            ["Description"] = "gain block",
            ["Mapping"] = "see figure"
        };
        var right = new Dictionary<string, string?>
        {
            ["Function"] = "amplifier",
            ["Gain"] = "18 dB",
            ["PinAssignment"] = "pin 2"
        };

        var report = SubstituteAlignment.Evaluate(left, right);
        Assert.False(report.FunctionOnBoth);
        Assert.False(report.LimitsOnBoth);
        Assert.False(report.PinsOnBoth);
        Assert.Contains("Funktion", report.MissingGroups);
    }
}

public class FieldAgreementTests
{
    [Fact]
    public void WhitespaceAndCaseDoNotSplitAMatch()
    {
        Assert.True(FieldAgreement.StringsAgree("5 V", "5  v"));
        Assert.True(FieldAgreement.StringsAgree("5\u00A0V", "5 V"));
    }

    [Fact]
    public void UnitAndDecimalMarkStayDistinct()
    {
        Assert.False(FieldAgreement.StringsAgree("4.3 A", "4,3 A"));
        Assert.False(FieldAgreement.StringsAgree("15 MHz", "15000000"));
        Assert.False(FieldAgreement.StringsAgree("0.001 A", "1 mA"));
    }

    [Fact]
    public void ShareUsesFilledDmsFieldsOnly()
    {
        var score = FieldAgreement.Evaluate(new (string?, string?)[]
        {
            ("5 V", "5 V"),
            ("15 MHz", "15000000"),
            (null, "80 mA"),
            ("12 dB", null),
            ("  ", "  ")
        });

        Assert.Equal(1, score.Matches);
        Assert.Equal(3, score.ReferenceFields);
        Assert.Equal(1, score.Unsupported);
        Assert.Equal(1.0 / 3.0, score.Ratio);
    }
}

public class FieldAgreementLogTests
{
    [Fact]
    public void SummaryKeepsOneRowPerMaterialNumber()
    {
        var directory = Path.Combine(Path.GetTempPath(), "field-agreement-" + Guid.NewGuid().ToString("N"));
        var log = new FieldAgreementLog(directory);
        try
        {
            log.Append(Sample("1001", new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), 1, 2));
            log.Append(Sample("1001", new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc), 2, 2));
            log.Append(Sample("2002", new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc), 0, 4));

            var summary = log.SummarizeByPart();

            Assert.Equal(2, summary.Count);
            var first = summary.Single(s => s.MaterialNumber == "1001");
            Assert.Equal(2, first.Runs);
            Assert.Equal(2, first.LatestMatches);
            Assert.Equal(1.0, first.LatestRatio);
            Assert.Equal(0.75, first.MeanRatio);
            Assert.Equal("RF Amplifier", first.Category);
            var second = summary.Single(s => s.MaterialNumber == "2002");
            Assert.Equal(0, second.LatestMatches);
            Assert.Equal(4, second.LatestReferenceFields);
            Assert.True(File.Exists(log.SummaryPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static FieldAgreementRecord Sample(string materialNumber, DateTime at, int matches, int referenceFields)
    {
        return new FieldAgreementRecord
        {
            RecordedAtUtc = at,
            MaterialNumber = materialNumber,
            Category = "RF Amplifier",
            PartNumber = "TRF37A75",
            Matches = matches,
            ReferenceFields = referenceFields,
            Unsupported = 0,
            Ratio = referenceFields == 0 ? null : (double)matches / referenceFields,
            Fields = new List<FieldAgreementField>
            {
                new() { Name = "GainTypDb", Ai = "12 dB", Dms = "12 dB", Outcome = "match" }
            }
        };
    }
}

public class ReadingContextTests
{
    [Fact]
    public void ValueKeepsUnitConditionVariantAndPage()
    {
        var json = Newtonsoft.Json.Linq.JObject.Parse("""
        {
          "GainTypDb": {
            "value": "17",
            "unit": "dB",
            "condition": "Vd = 5 V",
            "variant": "",
            "page": 9
          },
          "PartNumber": "HMC1040"
        }
        """);
        var pages = new Dictionary<int, string>
        {
            [1] = "Ordering information HMC1040 and ADL5611",
            [2] = "Gain 17 dB at Vd = 5 V for HMC1040. ADL5611 is the other code."
        };

        ReadingContext.TakeDetails(json);
        ReadingContext.Attach(json, pages, "HMC1040");
        var row = ReadingContext.Rows(json).Single(item => item.Designation == "GainTypDb");

        Assert.Equal("17", row.Value);
        Assert.Equal("dB", row.Unit);
        Assert.Equal("Vd = 5 V", row.Condition);
        Assert.Equal("HMC1040", row.Variant);
        Assert.Equal(2, row.Page);
        Assert.Equal("typ", row.Statement);
        Assert.Equal("17 dB", ReadingContext.Plain(json["GainTypDb"]));
        Assert.Equal("HMC1040", json["PartNumber"]?.ToString());
    }

    [Fact]
    public void AbsenceStaysAbsentAndIsNotZero()
    {
        var json = Newtonsoft.Json.Linq.JObject.Parse("""
        { "NoiseTypDb": "nicht vorhanden", "SupplyVoltage": "0 V" }
        """);
        var pages = new Dictionary<int, string> { [1] = "Supply 0 V" };

        ReadingContext.Attach(json, pages, null);
        var absent = ReadingContext.Rows(json).Single(item => item.Designation == "NoiseTypDb");
        var zero = ReadingContext.Rows(json).Single(item => item.Designation == "SupplyVoltage");

        Assert.True(absent.Absent);
        Assert.Equal("nicht vorhanden", absent.Value);
        Assert.Null(absent.Page);
        Assert.False(zero.Absent);
        Assert.Equal("0", zero.Value);
        Assert.Equal("V", zero.Unit);
        Assert.Equal(1, zero.Page);
    }

    [Fact]
    public void MinimumTypicalAndMaximumStayOnSeparateKeys()
    {
        var fields = new Dictionary<string, string?>
        {
            ["GainMinDb"] = "10 / 17 / 20 dB",
            ["GainTypDb"] = "nicht vorhanden",
            ["GainMaxDb"] = "nicht vorhanden"
        };

        ReadingContext.SeparateStatements(fields);

        Assert.Equal("10 dB", fields["GainMinDb"]);
        Assert.Equal("17 dB", fields["GainTypDb"]);
        Assert.Equal("20 dB", fields["GainMaxDb"]);
    }

    [Fact]
    public void PageComesFromThePassageThatContainsTheValue()
    {
        var pages = new Dictionary<int, string>
        {
            [3] = "Absolute maximum ratings",
            [4] = "Typical gain 14.5 dB"
        };

        int? page = ReadingContext.LocatePage(pages, "14.5", "dB", hinted: 3);

        Assert.Equal(4, page);
    }
}

public class SimilarPartSearchTests
{
    [Fact]
    public void DmsIndexKeepsNearAmplifiersAndSkipsAMixer()
    {
        var json = Newtonsoft.Json.Linq.JObject.Parse("""
        {
          "_reading": [
            {"designation":"Function","value":"amplifier","unit":"","condition":"","variant":"","statement":"","page":1,"absent":false},
            {"designation":"GainTypDb","value":"17","unit":"dB","condition":"","variant":"","statement":"typ","page":2,"absent":false}
          ]
        }
        """);

        var query = SimilarPartSearch.QueryFrom(json);
        var matches = SimilarPartSearch.Rank(query, SimilarPartSearch.Parse(SimilarPartSearch.SampleJson, "test"));

        Assert.Contains(matches, match => match.Candidate.MaterialNumber == "1001001");
        Assert.Contains(matches, match => match.Candidate.MaterialNumber == "1001002");
        Assert.DoesNotContain(matches, match => match.Candidate.PartNumber == "MIX-DEMO-7");
    }

    [Fact]
    public void DistributorLinksCarryTheSameParameters()
    {
        var links = DistributorSearch.Links("amplifier 17 dB");

        Assert.Contains(links, link => link.Name == "Mouser" && link.Url.Contains("mouser.de", StringComparison.Ordinal));
        Assert.Contains(links, link => link.Name == "Digi-Key");
        Assert.Contains(links, link => link.Name == "Farnell");
        Assert.Contains(links, link => link.Name == "LCSC");
        Assert.Contains("amplifier", links[0].Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MouserPayloadBecomesHits()
    {
        const string payload = """
        {"SearchResults":{"Parts":[{"Manufacturer":"Analog Devices","ManufacturerPartNumber":"HMC1040","Description":"RF amplifier"}]}}
        """;

        var hits = DistributorSearch.ParseMouser(payload);

        Assert.Single(hits);
        Assert.Equal("HMC1040", hits[0].PartNumber);
    }
}

public class FieldAcceptanceTests
{
    [Fact]
    public void MarksStayUntilTheSetIsCleared()
    {
        var acceptance = new FieldAcceptance();
        acceptance.Set("Gain", true);
        acceptance.Set("Function", true);
        acceptance.Set("Gain", false);

        Assert.False(acceptance.IsAccepted("Gain"));
        Assert.True(acceptance.IsAccepted("function"));
        Assert.Equal(1, acceptance.Count);

        acceptance.Clear();
        Assert.Equal(0, acceptance.Count);
        Assert.False(acceptance.IsAccepted("Function"));
    }
}
