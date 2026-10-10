using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolEFaturamProblemDetailsTests
{
    [Fact]
    public void OperatorSummaryExplainsValidationFieldAndCode()
    {
        var summary = TrendyolEFaturamProblemDetails.ToOperatorSummary("validation:recipientInfo.name:required");

        Assert.Equal("E-Faturam doğrulaması: alan recipientInfo.name, kod required.", summary);
    }

    [Fact]
    public void OperatorSummaryMasksLongNumbersInProviderDetail()
    {
        var summary = TrendyolEFaturamProblemDetails.ToOperatorSummary("provider-detail:Recipient tax id 11111111111 rejected");

        Assert.Equal("E-Faturam ayrıntısı: Recipient tax id [sayı gizlendi] rejected", summary);
        Assert.DoesNotContain("11111111111", summary);
    }

    [Theory]
    [InlineData("provider-detail:Contact test@example.com is invalid")]
    [InlineData("provider-detail:See https://example.invalid for details")]
    [InlineData("request-1234567890")]
    public void OperatorSummaryHidesUntrustedOrUnclassifiedReferences(string reference)
    {
        Assert.Null(TrendyolEFaturamProblemDetails.ToOperatorSummary(reference));
    }
}
