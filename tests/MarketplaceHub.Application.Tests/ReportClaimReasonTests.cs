using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReportClaimReasonTests
{
    [Theory]
    // Trendyol keeps the customer's note in the text; the reason is the option behind the code.
    [InlineData("TRENDYOL", "BIGSIZE", "3xl sipariş etmiştim 2xl geldi", "Bedeni/Ebatı Büyük Geldi")]
    [InlineData("TRENDYOL", "dislike", "Beğenmedim", "Beğenmedim")]
    [InlineData("TRENDYOL", "NEWREASON", "Alakasız bir ürün gönderildi", "NEWREASON")]
    // Hepsiburada and n11 send the option's own name as the code.
    [InlineData("HEPSIBURADA", "Bedeni / boyutu uymadı", "Kısa geldi", "Bedeni / boyutu uymadı")]
    [InlineData("N11", "Vazgeçtim", null, "Vazgeçtim")]
    // No code: the text is all there is.
    [InlineData("PTTAVM", null, "Hasarlı geldi", "Hasarlı geldi")]
    [InlineData("TRENDYOL", null, null, ReportBuilder.Unknown)]
    public void A_return_reason_is_the_marketplaces_option_not_the_customers_note(string platform, string? code, string? text, string expected) =>
        Assert.Equal(expected, ReportBuilder.ClaimReason(platform, code, text));
}
