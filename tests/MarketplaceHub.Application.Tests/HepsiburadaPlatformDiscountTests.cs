using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Hepsiburada's own campaign discount a unit, which Hepsiburada pays the seller (its CampaignDiscount records), is the
// line's platform discount: part of the sale, as Trendyol's own discount is.
public sealed class HepsiburadaPlatformDiscountTests
{
    [Theory]
    // Live line of order 4091991027 (Hepsiburada credited 212.57: 194.85 grossed up from 10 % to 20 % VAT).
    [InlineData("""{"hbDiscount":{"unitPrice":{"amount":194.85,"currency":"TRY"},"totalPrice":{"amount":194.85,"currency":"TRY"}},"merchantDiscount":{"unitPrice":{"amount":10,"currency":"TRY"}}}""", 194.85)]
    [InlineData("""{"hbDiscount":{"unitPrice":{"amount":"93,75"}}}""", 93.75)]
    public void The_line_takes_hepsiburadas_own_discount_a_unit(string line, double expected)
    {
        using var json = JsonDocument.Parse(line);
        Assert.Equal((decimal)expected, HepsiburadaJsonMapper.PlatformDiscount(json.RootElement));
    }

    [Theory]
    [InlineData("""{"hbDiscount":{"unitPrice":{"amount":0}}}""")]
    [InlineData("""{"hbDiscount":null}""")]
    [InlineData("""{"merchantDiscount":{"unitPrice":{"amount":10}}}""")]
    [InlineData("""{"discountInfo":[]}""")]
    public void A_line_without_one_has_none_and_the_sellers_own_discount_is_not_one(string line)
    {
        using var json = JsonDocument.Parse(line);
        Assert.Null(HepsiburadaJsonMapper.PlatformDiscount(json.RootElement));
    }
}
