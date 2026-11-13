using Xunit;

namespace MarketplaceHub.Application.Tests;

// Toplu Değer Ata: the value checks are the editor's; a wrong value is refused before anything is read.
public sealed class BulkProductFieldsTests
{
    [Theory]
    [InlineData("vatRate", "10")]
    [InlineData("vatRate", "0")]
    [InlineData("vatInclusion", "excluded")]
    [InlineData("desi", "0.5")]
    [InlineData("desi", "0,5")]
    [InlineData("desi", "12.25")]
    [InlineData("warrantyMonths", "0")]
    [InlineData("warrantyMonths", "240")]
    [InlineData("deliveryDays", "1")]
    [InlineData("deliveryDays", "30")]
    [InlineData("safetyStock", "0")]
    [InlineData("safetyStock", "5")]
    [InlineData("status", "active")]
    [InlineData("status", "ARCHIVED")]
    [InlineData("brandId", "0199a0a0-0000-7000-8000-000000000001")]
    [InlineData("categoryId", "0199a0a0-0000-7000-8000-000000000002")]
    [InlineData("description", "<p>Pamuklu bluz</p>")]
    [InlineData("DESI", "1")]
    public void AcceptsTheEditorsValues(string field, string value)
    {
        var parsed = BulkProductFields.Parse(field, value);
        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Value);
    }

    [Theory]
    [InlineData("title", "Yeni ad", "field")]
    [InlineData(null, "1", "field")]
    [InlineData("vatRate", "18", "value")]
    [InlineData("vatRate", "", "value")]
    [InlineData("vatInclusion", "SOMETIMES", "value")]
    [InlineData("desi", "0", "value")]
    [InlineData("desi", "-1", "value")]
    [InlineData("desi", "0.555", "value")]
    [InlineData("desi", "abc", "value")]
    [InlineData("warrantyMonths", "-1", "value")]
    [InlineData("warrantyMonths", "241", "value")]
    [InlineData("warrantyMonths", "1.5", "value")]
    [InlineData("deliveryDays", "0", "value")]
    [InlineData("deliveryDays", "31", "value")]
    [InlineData("safetyStock", "-1", "value")]
    [InlineData("safetyStock", "2.5", "value")]
    [InlineData("status", "DRAFT", "value")]
    [InlineData("brandId", "not-a-guid", "value")]
    [InlineData("categoryId", "00000000-0000-0000-0000-000000000000", "value")]
    [InlineData("description", "  ", "value")]
    [InlineData("description", "<p> </p>", "value")]
    public void RefusesAWrongValue(string? field, string value, string errorField)
    {
        var parsed = BulkProductFields.Parse(field, value);
        Assert.Null(parsed.Value);
        Assert.Equal(errorField, parsed.ErrorField);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
    }

    [Fact]
    public void ReadsADecimalCommaDesiAndNamesTheValue()
    {
        var parsed = BulkProductFields.Parse("desi", "0,5").Value!;
        Assert.Equal(0.5m, parsed.Number);
        Assert.Equal("0,5", parsed.Display);
        Assert.Equal("%10", BulkProductFields.Parse("vatRate", "10").Value!.Display);
        Assert.Equal("Satışa Kapalı", BulkProductFields.Parse("status", "archived").Value!.Display);
        Assert.Equal("Garanti Süresi (Ay)", BulkProductFields.Label(BulkProductFields.WarrantyMonths));
    }

    [Fact]
    public void RefusesADescriptionLongerThanTheLimit()
    {
        var parsed = BulkProductFields.Parse("description", new string('a', BulkProductFields.MaxDescriptionLength + 1));
        Assert.Equal("value", parsed.ErrorField);
    }
}
