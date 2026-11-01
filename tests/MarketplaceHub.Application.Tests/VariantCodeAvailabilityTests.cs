using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class VariantCodeAvailabilityTests
{
    private static readonly Guid Indigo = Guid.Parse("01a178e0-9ecb-790a-bbf7-30da96781869");
    private static readonly Guid IndigoVariant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public void Match_NamesTheCardHoldingEachCode_PerField()
    {
        var rows = new List<VariantCodeAvailability.Row>
        {
            new(IndigoVariant, Indigo, "Kadın Çiçek Desenli İndigo Bluz", "MZ021Y-CD-01", "MZ021Y-CD-01"),
            new(Guid.NewGuid(), Guid.NewGuid(), "Kadın Siyah Bluz", "MZ021Y-CD10", "MZ021Y-CD10"),
        };

        var taken = VariantCodeAvailability.Match(["mz021y-cd-01", "MZ021Y-CD-02"], ["MZ021Y-CD-01", "MZ021Y-CD-03"], rows);

        Assert.Equal(2, taken.Count);
        Assert.Equal(new VariantCodeAvailability.TakenVariantCode("mz021y-cd-01", "sku", IndigoVariant, Indigo, "Kadın Çiçek Desenli İndigo Bluz"), taken[0]);
        Assert.Equal("barcode", taken[1].Field);
        Assert.Equal("MZ021Y-CD-01", taken[1].Code);
    }

    [Fact]
    public void Describe_ListsCodesAndOwners_ForTheEditor()
    {
        var (message, fields) = VariantCodeAvailability.Describe("Stok kodu başka bir üründe kullanılıyor", [new("MZ021Y-CD-01", "sku", IndigoVariant, Indigo, "İndigo Bluz")]);

        Assert.Equal("Stok kodu başka bir üründe kullanılıyor: MZ021Y-CD-01 ('İndigo Bluz' ürününde).", message);
        Assert.Equal(["sku\tMZ021Y-CD-01\tİndigo Bluz"], fields["variantCodeConflicts"]);
    }

    [Fact]
    public void RequestedCodes_TrimsDropsBlanksAndCapsTheList()
    {
        Assert.Equal(["A-01", "A-02"], VariantCodeAvailability.RequestedCodes([" A-01 ", "", null, "A-02", "A-01"]));
        Assert.Throws<ArgumentException>(() => VariantCodeAvailability.RequestedCodes(Enumerable.Range(1, VariantCodeAvailability.MaxCodesPerRequest + 1).Select(index => $"A-{index}")));
    }
}
