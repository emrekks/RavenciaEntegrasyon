using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Trendyol's promotion offer exports (Plus, advantage label, flash), read by their headers, and what each offered price
// leaves after the commission and the cost.
public sealed class PromotionOfferTests
{
    private static XlsxTable Table(string[] headers, string[][] rows) =>
        ImportFileReader.ReadXlsxTable(new MemoryStream(CommissionTariffTests.Xlsx(headers, rows)), 10_000_000);

    [Fact]
    public void A_plus_offer_is_read_and_priced_at_its_ceiling_and_offered_rate()
    {
        var parsed = PromotionOfferFileParser.Parse(Table(
            ["Ürün İsmi", "Ürün Id", "Barkod", "Satıcı Stok Kodu", "Beden", "Model Kodu", "Stok", "Güncel TSF", "Komisyona Esas Fiyat", "Güncel Komisyon", "Plus Fiyat Üst Limiti", "Tarih Aralığı (7 Gün)", "Plus Komisyon Teklifi", "Güncel Uygulanan Promosyonlar"],
            [["Bluz", "971576388", "RYP00101", "RYP00101", "M", "RYP001", "11.0", "525.0", "465.92", "20.6", "359.02", "22 Ekim 08.00-29 Ekim 07.59", "14.1", "Sepette %1 İndirim"],
             ["Bluz", "971576388", "RYP00101", "RYP00101", "M", "RYP001", "11.0", "525.0", "465.92", "20.6", "359.02", "22 Ekim 08.00-29 Ekim 07.59", "14.1", ""]]));

        Assert.Equal(PromotionOfferKinds.Plus, parsed.Kind);
        Assert.Equal("22 Ekim 08.00-29 Ekim 07.59", parsed.PeriodText);
        Assert.Equal(1, parsed.SkippedRows);
        var row = Assert.IsType<PlusOfferRow>(Assert.Single(parsed.Rows));
        Assert.Equal((465.92m, 20.6m, 359.02m, 14.1m), (row.BasisPrice!.Value, row.CurrentRate!.Value, row.PlusPriceLimit!.Value, row.PlusRate!.Value));

        // Now 465,92 at %20,6 pays 95,98 and leaves 61,94 over a 308 cost; Plus 359,02 at %14,1 pays 50,62 and leaves 0,40.
        var view = PromotionOfferService.PlusView(row, new(308m, "Siyah", null));
        Assert.Equal((95.98m, 61.94m), (view.Now!.Commission!.Value, view.Now.Remaining!.Value));
        Assert.Equal((50.62m, 0.40m), (view.Plus!.Commission!.Value, view.Plus.Remaining!.Value));
        Assert.Equal(-61.54m, view.Gain);
        // Without a cost what is left is not known.
        Assert.Null(PromotionOfferService.PlusView(row, null).Gain);
    }

    [Fact]
    public void An_advantage_offer_prices_each_star_with_the_commission_tariff_and_tells_the_stars_earned()
    {
        var parsed = PromotionOfferFileParser.Parse(Table(
            ["ÜRÜN İSMİ", "BARKOD", "BEDEN", "MODEL KODU", "STOK", "KOMİSYON TARİFESİ", "1 YILDIZ ÜST FİYAT", "1 YILDIZ ALT FİYAT", "2 YILDIZ ÜST FİYAT", "2 YILDIZ ALT FİYAT", "3 YILDIZ ÜST FİYAT", "MÜŞTERİNİN GÖRDÜĞÜ FİYAT", "TRENDYOL SATIŞ FİYATI"],
            [["Bluz", "RYP00101", "M", "RYP001", "11.0", "Var", "470,63", "435,98", "435,97", "388,64", "388,63", "465,92", "525,00"]]));

        Assert.Equal(PromotionOfferKinds.Advantage, parsed.Kind);
        var row = Assert.IsType<AdvantageOfferRow>(Assert.Single(parsed.Rows));
        Assert.Equal((true, 470.63m, 388.63m, 465.92m), (row.InTariff!.Value, row.Star1Max!.Value, row.Star3Max!.Value, row.CustomerPrice!.Value));

        var tariff = new CommissionTariffRow { Barcode = "RYP00101", Tier1Min = 470.64m, Tier2Max = 470.63m, Tier2Min = 435.98m, Tier3Max = 435.97m, Tier3Min = 388.64m, Tier4Max = 388.63m, Rate1 = 21.5m, Rate2 = 20.6m, Rate3 = 19.4m, Rate4 = 17.5m };
        var view = PromotionOfferService.AdvantageView(row, new(160m, null, null), tariff);
        Assert.Equal(1, view.CurrentStars);
        Assert.Equal([20.6m, 19.4m, 17.5m], view.Stars.Select(x => x.Price.Rate!.Value));
        // 3 stars: 388,63 at %17,5 pays 68,01 and leaves 160,62 over a 160 cost.
        Assert.Equal((68.01m, 160.62m), (view.Stars[2].Price.Commission!.Value, view.Stars[2].Price.Remaining!.Value));
        // Without a commission tariff the rate at a star price is not known, never guessed.
        Assert.Null(PromotionOfferService.AdvantageView(row, new(160m, null, null), null).Stars[0].Price.Rate);
        // A product outside the tariff ("Yok") pays its one current rate at every star price; one inside it does not.
        var outside = row with { InTariff = false };
        Assert.Equal([17.5m, 17.5m, 17.5m], PromotionOfferService.AdvantageView(outside, new(160m, null, null), null, 17.5m).Stars.Select(x => x.Price.Rate!.Value));
        Assert.Null(PromotionOfferService.AdvantageView(row, new(160m, null, null), null, 17.5m).Stars[0].Price.Rate);
    }

    [Fact]
    public void A_flash_offer_keeps_a_models_barcodes_and_its_slot_times()
    {
        var parsed = PromotionOfferFileParser.Parse(Table(
            ["Model Kodu", "Barkod", "Ürün Adı", "Stok", "Mevcut Fiyat", "Müşterinin Gördüğü Fiyat", "Plus Müşterinin Gördüğü Fiyat", "Mevcut Komisyon", "Güncellenecek Fiyat", "24 Saat Fiyat", "3 Saat Fiyat", "24 Saat Flaş Başlangıç Tarihi", "24 Saat Flaş Bitiş Tarihi", "Ürün Komisyon Tarife Seçeneği", "Kampanyalı Ürün"],
            [["RYP001", "RYP00105, RYP00104, RYP00106", "Bluz", "20.0", "525.0", "472.16", "-", "20.7", "", "426.46", "", "26/10/2026 00:00", "26/10/2026 23:59", "Var", "Kampanyalı Ürün"]]));

        Assert.Equal(PromotionOfferKinds.Flash, parsed.Kind);
        var row = Assert.IsType<FlashOfferRow>(Assert.Single(parsed.Rows));
        Assert.Equal(["RYP00105", "RYP00104", "RYP00106"], row.Barcodes);
        Assert.Equal(426.46m, row.Price24h);
        Assert.Null(row.Price3h);
        Assert.True(row.InTariff);
        Assert.True(row.Campaign);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.Zero), PromotionOfferService.ParseLocal(row.Start24h));
    }

    [Fact]
    public void A_file_of_no_known_offer_is_refused_in_turkish()
    {
        var error = Assert.Throws<InvalidDataException>(() => PromotionOfferFileParser.Parse(Table(["Barkod", "Fiyat"], [["a", "1"]])));
        Assert.Contains("Plus komisyon tarifesi", error.Message, StringComparison.Ordinal);
    }

    // A model's variants of different costs leave a range: the dearest leaves the least.
    [Fact]
    public void Variants_of_different_costs_leave_a_range()
    {
        var price = PromotionOfferService.Ranged(426.46m, 21.5m, 300m, 330m);
        Assert.Equal((91.69m, 4.77m, 34.77m), (price.Commission!.Value, price.Remaining!.Value, price.RemainingHigh!.Value));
    }
}
