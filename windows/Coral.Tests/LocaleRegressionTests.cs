using System.Globalization;
using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Regression coverage for a real bug found testing on a Windows
/// machine set to a non-English locale (Spanish): `$"{value:F2}"`-style
/// interpolation uses `CultureInfo.CurrentCulture`, so `$0.83` rendered as
/// `$0,83` — wrong output, and it broke every test/consumer that expects a
/// literal decimal point (`.mcp.json`-adjacent text, markdown reports, the
/// manual smoke test's diagnostic output). Neither this sandbox nor
/// `windows-latest` normally runs under a non-English culture, so this bug
/// shipped past both — these tests pin `CurrentCulture` to es-ES for the
/// duration of each case specifically to catch a regression here without
/// needing a non-English CI runner.</summary>
public class LocaleRegressionTests
{
    private static void UnderSpanishCulture(Action body)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES"); // comma decimal separator
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void MissionReportHeadlineUsesADecimalPointUnderAnyLocale()
    {
        UnderSpanishCulture(() =>
        {
            Assert.Equal("A 4-agent team finished for $0.83.", MissionReport.Headline(4, 0.83, 1000));
        });
    }

    [Fact]
    public void MissionReportMarkdownUsesADecimalPointUnderAnyLocale()
    {
        UnderSpanishCulture(() =>
        {
            var md = MissionReport.Markdown("T", "S", new List<MissionReport.AgentLine>(), 1_500_000, 0.83, "", "");
            Assert.Contains("$0.83", md);
            Assert.Contains("1.5M", md); // FormatTokens, not just the cost figure
        });
    }

    [Fact]
    public void StrategyCostUsdAndTokensShortUseADecimalPointUnderAnyLocale()
    {
        UnderSpanishCulture(() =>
        {
            var cost = new StrategyCost(0.83, 1_500_000, new Dictionary<string, double>());
            Assert.Equal("$0.83", cost.UsdShort);
            Assert.Equal("1.5M", cost.TokensShort);
            Assert.Equal("~1.5M ($0.83)", cost.Headline);
        });
    }
}
