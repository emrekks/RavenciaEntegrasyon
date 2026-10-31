using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;
using Remote = MarketplaceHub.Infrastructure.Adapters.PttAvm.PttAvmProductPayloadRules.RemoteVariant;

namespace MarketplaceHub.Application.Tests;

// "Platformlarda Güncelle" on PTT AVM: only the picked fields go, and every variant PTT AVM holds goes with them.
public sealed class PttAvmProductUpdateTests
{
    private static PttAvmProductPayloadRules.ProductDraft Family() => new("MZDB003", "Kazak", "Yün kazak", "1234", "MZDB003", 499.90m, 20m, 6, 2m,
        ["https://cdn.example/1.jpg", "https://cdn.example/2.jpg"],
        [new("mzdb003lacs", "Lacivert", "S", 2), new("mzdb003lacm", "Lacivert", "M", 2), new("mzdb003yesls", "Yeşil", "S", 2)],
        "Ravencia");

    private static PttAvmProductUpdateRules.UpdateDraft Draft(params string[] fields) =>
        PttAvmProductUpdateComposer.Build(Family(), fields, warrantyMonths: 12, deliveryDays: 2).Draft!;

    [Fact]
    public void Fields_NoListIsEveryFieldPttAvmTakes_AttributesNeverGo()
    {
        Assert.Equal(["TITLE", "DESCRIPTION", "IMAGES", "WARRANTY", "DESI", "VAT_RATE", "DELIVERY"], PttAvmProductUpdateFields.Parse(null).Value);
        Assert.Equal(["TITLE"], PttAvmProductUpdateFields.Parse(["attributes", " title "]).Value);
        Assert.Equal("PRODUCT_UPDATE_FIELDS_UNSUPPORTED", PttAvmProductUpdateFields.Parse(["ATTRIBUTES"]).Error!.Code);
        Assert.Equal("PRODUCT_UPDATE_FIELD_UNKNOWN", PttAvmProductUpdateFields.Parse(["TITLE", "PRICE"]).Error!.Code);
        Assert.Equal("PRODUCT_UPDATE_FIELDS_REQUIRED", PttAvmProductUpdateFields.Parse([]).Error!.Code);
        Assert.Equal("PRODUCT_UPDATE_FIELDS_REQUIRED", PttAvmProductUpdateFields.Parse(["ORIGIN"]).Error!.Code);
    }

    [Fact]
    public void Composer_CarriesOnlyThePickedValues_AndTheWholeFamilyAlways()
    {
        var draft = Draft("TITLE");

        Assert.Equal("MZDB003", draft.Barcode);
        Assert.Equal("Kazak", draft.Name);
        Assert.Null(draft.LongDescription);
        Assert.Null(draft.Images);
        Assert.Null(draft.Desi);
        Assert.Null(draft.VatRate);
        Assert.Null(draft.WarrantyDuration);
        Assert.Null(draft.EstimatedCourierDelivery);
        // Every variant of the family, whatever was picked.
        Assert.Equal(["mzdb003lacs", "mzdb003lacm", "mzdb003yesls"], draft.Variants!.Select(x => x.Barcode));

        var payload = PttAvmProductUpdateRules.Payload(draft);
        var read = PttAvmProductUpdateRules.Read(payload, out var error);
        Assert.Null(error);
        Assert.Equal(3, read!.Variants!.Count);
        Assert.Equal("MZDB003", PttAvmProductUpdateRules.Barcode(payload));
    }

    [Fact]
    public void Composer_TakesWarrantyAndDeliveryFromTheCard_InPttAvmRanges()
    {
        var both = PttAvmProductUpdateComposer.Build(Family(), ["WARRANTY", "DELIVERY"], 24, 30).Draft!;
        Assert.Equal(24, both.WarrantyDuration);
        Assert.Equal(30, both.EstimatedCourierDelivery);
        // No warranty on the card is none (0 months), never left unknown.
        Assert.Equal(0, PttAvmProductUpdateComposer.Build(Family(), ["WARRANTY"], null, null).Draft!.WarrantyDuration);

        Assert.Equal("PTTAVM_WARRANTY_INVALID", PttAvmProductUpdateComposer.Build(Family(), ["WARRANTY"], 36, 2).Error!.Code);
        Assert.Equal("DELIVERY_DAYS_REQUIRED", PttAvmProductUpdateComposer.Build(Family(), ["DELIVERY"], 12, null).Error!.Code);
        Assert.Equal("PTTAVM_DELIVERY_DAYS_INVALID", PttAvmProductUpdateComposer.Build(Family(), ["DELIVERY"], 12, 45).Error!.Code);
        // An unpicked out-of-range value is not read at all.
        Assert.Null(PttAvmProductUpdateComposer.Build(Family(), ["TITLE"], 36, 45).Error);
    }

    [Fact]
    public void Read_RefusesAPickedFieldWithoutAValidValue()
    {
        static string? Error(PttAvmProductUpdateRules.UpdateDraft draft) { PttAvmProductUpdateRules.Read(PttAvmProductUpdateRules.Payload(draft), out var error); return error; }
        Assert.NotNull(Error(new("B1", ["TITLE"])));
        Assert.NotNull(Error(new("B1", ["VAT_RATE"], VatRate: 18)));
        Assert.NotNull(Error(new("B1", ["DESI"], Desi: 301)));
        Assert.NotNull(Error(new("B1", ["IMAGES"], Images: ["http://insecure/1.jpg"])));
        Assert.NotNull(Error(new("B1", ["ATTRIBUTES"])));
        Assert.NotNull(Error(new("B1", [])));
        Assert.Null(Error(new("B1", ["VAT_RATE", "DESI"], VatRate: 10, Desi: 3)));
    }

    [Fact]
    public void Request_HasOnlyThePickedFields_InSchemaOrder_AndNoPriceStockStateOrCategory()
    {
        var draft = Draft("TITLE", "WARRANTY", "DESI");
        var carried = PttAvmProductUpdateRules.CarryVariants([new("mzdb003lacs", "Lacivert", "S", null, 4), new("mzdb003lacm", "Lacivert", "M", null, 0)], draft.Variants!).Variants;

        var xml = PttAvmProductUpdateRules.ItemsXml(draft, carried);

        foreach (var absent in new[] { "<ept:Active>", "<ept:PriceWithVat>", "<ept:CategoryId>", "<ept:NoShippingProduct>", "<ept:LongDescription>", "<ept:Images>", "<ept:VATRate>", "<ept:EstimatedCourierDelivery>", "<ept:ProductCode>" })
            Assert.DoesNotContain(absent, xml);
        // Product-level Quantity never goes; only each variant's.
        Assert.Equal(2, CountOf(xml, "<ept:Quantity>"));
        var order = new[] { "<ept:Barcode>MZDB003", "<ept:Desi>2</ept:Desi>", "<ept:Name>Kazak</ept:Name>", "<ept:Variants>", "<ept:WarrantyDuration>12</ept:WarrantyDuration>" }.Select(part => xml.IndexOf(part, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Contains("<ept:Quantity>4</ept:Quantity><ept:VariantBarcode>mzdb003lacs</ept:VariantBarcode>", xml);
    }

    [Fact]
    public void Request_WithoutVariantsOnPttAvm_HasNoVariantsElement()
    {
        var xml = PttAvmProductUpdateRules.ItemsXml(new("B1", ["DESCRIPTION"], LongDescription: "A & B"), []);
        Assert.DoesNotContain("<ept:Variants>", xml);
        Assert.Contains("<ept:LongDescription>A &amp; B</ept:LongDescription>", xml);
    }

    [Fact]
    public void Carry_SendsEveryVariantPttAvmHolds_AndAddsNone()
    {
        var panel = Draft("TITLE").Variants!;
        // PTT AVM holds two of the panel's three variants plus one the panel does not know.
        Remote[] remote = [new("mzdb003lacs", "Lacivert", "S", null, 3), new(null, null, null, "Lacivert / M", 5), new("mzdb003sias", "Siyah", "S", null, 1)];

        var carry = PttAvmProductUpdateRules.CarryVariants(remote, panel);

        Assert.Empty(carry.Unreadable);
        Assert.Empty(carry.NotCarriable);
        Assert.Equal(["mzdb003lacs", "mzdb003lacm", "mzdb003sias"], carry.Variants.Select(x => x.Barcode));
        Assert.Equal([3, 5, 1], carry.Variants.Select(x => x.Quantity));
        Assert.DoesNotContain(carry.Variants, x => x.Barcode == "mzdb003yesls");
    }

    [Fact]
    public void Carry_AVariantThatCannotBeNamedOrRepeatedStopsTheSend()
    {
        var panel = Draft("TITLE").Variants!;
        // The single-value list PTT AVM gives for a family it opened ("Lacivert", "S"): which pairs exist is unknown.
        Assert.Equal(2, PttAvmProductUpdateRules.CarryVariants([new(null, null, null, "Lacivert", 3), new(null, null, null, "S", 3)], panel).Unreadable.Count);
        // A name no panel variant carries, a stock PTT AVM did not give, the same barcode twice.
        Assert.Single(PttAvmProductUpdateRules.CarryVariants([new(null, null, null, "Kırmızı / S", 3)], panel).Unreadable);
        Assert.Single(PttAvmProductUpdateRules.CarryVariants([new("mzdb003lacs", "Lacivert", "S", null)], panel).Unreadable);
        Assert.Single(PttAvmProductUpdateRules.CarryVariants([new("mzdb003lacs", "Lacivert", "S", null, 1), new(null, null, null, "lacivert / s", 1)], panel).Unreadable);
        // A price difference or a passive variant would be lost by the request.
        var priced = PttAvmProductUpdateRules.CarryVariants([new("mzdb003lacs", "Lacivert", "S", null, 1, Price: 20m), new("mzdb003lacm", "Lacivert", "M", null, 1, Active: false)], panel);
        Assert.Equal(2, priced.NotCarriable.Count);
    }

    [Fact]
    public void CompletedUpdate_KeepsWhatPttAvmHolds()
    {
        Assert.Equal("LIVE", PttAvmProductUpdateRules.CompletedListingStatus("LIVE"));
        Assert.Equal("APPROVAL_PENDING", PttAvmProductUpdateRules.CompletedListingStatus("APPROVAL_PENDING"));
        Assert.Equal("LINKED", PttAvmProductUpdateRules.CompletedListingStatus("UPDATE_REJECTED"));
        Assert.Equal("LIVE", PttAvmProductUpdateRules.CompletedProfileStatus(["LIVE", "LIVE"]));
        Assert.Equal("PARTIAL_LIVE", PttAvmProductUpdateRules.CompletedProfileStatus(["LIVE", "LINKED"]));
        Assert.Equal("LINKED", PttAvmProductUpdateRules.CompletedProfileStatus(["APPROVAL_PENDING", "LINKED"]));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
