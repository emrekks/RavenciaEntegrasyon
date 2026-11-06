using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// O7/D6: the codes a save leaves on the family's rows, checked together before anything is written.
public sealed class VariantCodeSaveTests
{
    [Fact]
    public void A_code_on_an_updated_and_a_new_row_is_named_with_both_rows()
    {
        var duplicates = VariantCodeAvailability.DuplicatesInSave([
            new("variant:a", "MZ071-M", "8690000000011"),
            new("variant:b", "MZ071-L", null),
            new("new:0", " mz071-m ", "8690000000011")
        ]);

        Assert.Equal(2, duplicates.Count);
        Assert.Equal(("sku", "MZ071-M"), (duplicates[0].Field, duplicates[0].Code));
        Assert.Equal(["variant:a", "new:0"], duplicates[0].RowRefs);
        Assert.Equal("barcode", duplicates[1].Field);

        var (message, fields) = VariantCodeAvailability.DescribeDuplicates(duplicates);
        Assert.Contains("MZ071-M iki varyantta kullanılıyor", message);
        Assert.Contains("hiçbir değişiklik kaydedilmedi", message);
        Assert.Equal("sku\tMZ071-M\t\tSAME_SAVE\tvariant:a,new:0", fields["variantCodeConflicts"][0]);
    }

    [Fact]
    public void Blank_codes_and_distinct_codes_are_no_clash()
    {
        Assert.Empty(VariantCodeAvailability.DuplicatesInSave([
            new("variant:a", "A-1", null),
            new("variant:b", "A-2", ""),
            new("new:0", "A-3", "  ")
        ]));
    }
}
