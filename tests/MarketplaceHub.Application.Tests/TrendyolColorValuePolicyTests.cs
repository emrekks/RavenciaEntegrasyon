using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolColorValuePolicyTests
{
    [Theory]
    [InlineData("Renk", true)]
    [InlineData("COLOR", true)]
    [InlineData("[TDG] Renk", true)]
    [InlineData("Web Color", false)]
    [InlineData("[A-TDG] Renk", true)]
    public void UsesCustomPanelColorValue_OnlyMatchesPlainColorFields(string attributeName, bool expected)
    {
        Assert.Equal(expected, TrendyolColorValuePolicy.UsesCustomPanelColorValue(attributeName));
    }

    [Theory]
    [InlineData("BORDO", "Bordo")]
    [InlineData("AÇIK KREM", "Açık Krem")]
    [InlineData("kİREMİT rENGİ", "Kiremit Rengi")]
    [InlineData("  çok renkli  ", "Çok Renkli")]
    public void FormatPanelColorValue_TitleCasesTurkishPanelValues(string value, string expected)
    {
        Assert.Equal(expected, TrendyolColorValuePolicy.FormatPanelColorValue(value));
    }

    [Theory]
    [InlineData("Renk", "AÇIK KREM", "Açık Krem")]
    [InlineData("[TDG] Renk", "BORDO", "Bordo")]
    public void TrySetCustomPanelColorValue_WritesCustomAttributePayload(string attributeName, string panelValue, string expected)
    {
        var payload = new Dictionary<string, object?>();

        Assert.True(TrendyolColorValuePolicy.TrySetCustomPanelColorValue(payload, attributeName, panelValue));
        Assert.Equal(expected, payload["customAttributeValue"]);
    }

    [Fact]
    public void TrySetCustomPanelColorValue_DoesNotTreatWebColorAsPlainColor()
    {
        var payload = new Dictionary<string, object?>();

        Assert.False(TrendyolColorValuePolicy.TrySetCustomPanelColorValue(payload, "Web Color", "BORDO"));
        Assert.Empty(payload);
    }
}
