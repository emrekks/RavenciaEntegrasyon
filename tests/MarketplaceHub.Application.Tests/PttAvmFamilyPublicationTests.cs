using MarketplaceHub.Infrastructure.Persistence;
using Xunit;
using Row = MarketplaceHub.Infrastructure.Persistence.PttAvmProductPublicationComposer.FamilyVariantRow;

namespace MarketplaceHub.Application.Tests;

// The PTT AVM model family send: every variant of every colour card under one parent barcode, or nothing.
public sealed class PttAvmFamilyPublicationTests
{
    private static Row Variant(string barcode, string color, string size, string modelCode = "MZDB003", decimal? price = 499.90m, int? quantity = 2, bool hasInventory = true, string card = "Lacivert Kazak") =>
        new(Guid.NewGuid(), card, barcode, barcode, modelCode, color, size, 1.2m, price, "TRY", 20m, quantity, hasInventory);

    private static List<Row> TwoColoursThreeSizes() =>
    [
        Variant("mzdb003lacs", "Lacivert", "S"), Variant("mzdb003lacm", "Lacivert", "M"), Variant("mzdb003lacl", "Lacivert", "L"),
        Variant("mzdb003yesls", "Yeşil", "S", card: "Yeşil Kazak"), Variant("mzdb003yeslm", "Yeşil", "M", card: "Yeşil Kazak"), Variant("mzdb003yesll", "Yeşil", "L", card: "Yeşil Kazak")
    ];

    [Fact]
    public void AFamilyIsOneProductUnderItsModelCode()
    {
        var (plan, error) = PttAvmProductPublicationComposer.PlanFamily(TwoColoursThreeSizes(), []);

        Assert.Null(error);
        Assert.Equal("MZDB003", plan!.ParentBarcode);
        Assert.Equal(499.90m, plan.Price);
        Assert.Equal(20m, plan.VatRate);
        Assert.Equal(2m, plan.Desi);
        Assert.Equal(6, plan.Variants.Count);
        Assert.Equal(new("mzdb003yesls", "Yeşil", "S", 2), plan.Variants[3]);
    }

    [Fact]
    public void ThePttAvmCodeOfTheFamilyIsTheParentBarcodeWhenItIsTheModelCode()
    {
        var (plan, error) = PttAvmProductPublicationComposer.PlanFamily(TwoColoursThreeSizes(), ["mzdb003", "MZDB003 "]);

        Assert.Null(error);
        Assert.Equal("mzdb003", plan!.ParentBarcode);
    }

    [Fact]
    public void AParentBarcodeOtherThanTheModelCodeIsRefused()
    {
        Assert.Equal("PTTAVM_PARENT_BARCODE_MISMATCH", PttAvmProductPublicationComposer.PlanFamily(TwoColoursThreeSizes(), ["MZDB004"]).Error!.Code);
        Assert.Equal("PTTAVM_PARENT_BARCODE_MISMATCH", PttAvmProductPublicationComposer.PlanFamily(TwoColoursThreeSizes(), ["MZDB003", "MZDB0031"]).Error!.Code);
    }

    [Fact]
    public void AVariantWithAnotherModelCodeIsRefused()
    {
        var rows = TwoColoursThreeSizes();
        rows[4] = Variant("mzdb004yeslm", "Yeşil", "M", modelCode: "MZDB004");

        var error = PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!;

        Assert.Equal("PTTAVM_MODEL_CODE_MISMATCH", error.Code);
        Assert.Contains("mzdb004yeslm", error.Message);
        rows[4] = Variant("mzdb003yeslm", "Yeşil", "M", modelCode: " ");
        Assert.Equal("PTTAVM_MODEL_CODE_REQUIRED", PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!.Code);
    }

    [Fact]
    public void AVariantWithoutAnOfferStopsTheWholeSend()
    {
        var rows = TwoColoursThreeSizes();
        rows[5] = Variant("mzdb003yesll", "Yeşil", "L", price: null, quantity: null, card: "Yeşil Kazak");

        var error = PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!;

        Assert.Equal("CHANNEL_OFFER_REQUIRED", error.Code);
        Assert.Contains("'mzdb003yesll' (Yeşil Kazak)", error.Message);
    }

    [Fact]
    public void AVariantWithoutStockOrBarcodeOrSizeStopsTheWholeSend()
    {
        var rows = TwoColoursThreeSizes();
        rows[0] = Variant("mzdb003lacs", "Lacivert", "S", quantity: null, hasInventory: false);
        Assert.Equal("INVENTORY_REQUIRED", PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!.Code);
        rows[0] = Variant(" ", "Lacivert", "S");
        Assert.Equal("BARCODE_REQUIRED", PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!.Code);
        rows[0] = Variant("mzdb003lacs", "Lacivert", "");
        Assert.Equal("PTTAVM_VARIANT_OPTIONS_REQUIRED", PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!.Code);
    }

    [Fact]
    public void VariantsMustShareThePriceAndDifferInBarcodeAndOptions()
    {
        var rows = TwoColoursThreeSizes();
        rows[2] = Variant("mzdb003lacl", "Lacivert", "L", price: 549.90m);
        Assert.Equal("PTTAVM_VARIANT_PRICE_DIFFERS", PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!.Code);
        rows[2] = Variant("MZDB003LACM", "Lacivert", "L");
        Assert.Equal("BARCODE_DUPLICATE", PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!.Code);
        rows[2] = Variant("mzdb003lacl", "lacivert", "m");
        Assert.Equal("PTTAVM_VARIANT_OPTIONS_DUPLICATE", PttAvmProductPublicationComposer.PlanFamily(rows, []).Error!.Code);
    }

    [Fact]
    public void AFamilyOfMoreThanAHundredVariantsIsRefused()
    {
        var hundred = Enumerable.Range(1, 100).Select(index => Variant($"mzdb003v{index}", $"Renk{index}", "S", quantity: 0)).ToList();
        Assert.Null(PttAvmProductPublicationComposer.PlanFamily(hundred, []).Error);

        hundred.Add(Variant("mzdb003v101", "Renk101", "S", quantity: 0));
        Assert.Equal("PTTAVM_VARIANT_LIMIT_EXCEEDED", PttAvmProductPublicationComposer.PlanFamily(hundred, []).Error!.Code);
    }
}
