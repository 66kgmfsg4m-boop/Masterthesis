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
