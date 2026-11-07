using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductImportOverwriteTests
{
    [Theory]
    [InlineData(true, true, false, false, true)]   // one product by its code, update on
    [InlineData(true, false, true, false, true)]   // existing-products update, update on
    [InlineData(false, true, false, false, false)] // update off keeps the panel
    [InlineData(true, false, false, false, false)] // full / incremental import keeps panel edits
    [InlineData(true, true, false, true, false)]   // read-only observation never writes content
    public void Overwrites_panel_edits_only_when_the_user_asked_for_existing_products(bool update, bool single, bool existingOnly, bool observeOnly, bool expected) =>
        Assert.Equal(expected, ProductImportMergePolicy.OverwriteLocalChanges(update, single, existingOnly, observeOnly));

    [Fact]
    public void Lower_case_code_is_asked_again_in_upper_case_once()
    {
        var retry = ProductImportMergePolicy.UpperCaseLookupRetry(new ProductReadFilter(null, ProductMainId: "Ryp003t"));
        Assert.Equal("RYP003T", retry?.ProductMainId);
        Assert.Null(ProductImportMergePolicy.UpperCaseLookupRetry(retry!));
    }

    [Fact]
    public void A_link_or_content_lookup_is_not_upper_cased()
    {
        Assert.Null(ProductImportMergePolicy.UpperCaseLookupRetry(new ProductReadFilter(null, ProductMainId: "https://x/abc", ProductUrl: "https://x/abc")));
        Assert.Null(ProductImportMergePolicy.UpperCaseLookupRetry(new ProductReadFilter(null, ContentId: "123", ProductUrl: "https://x/a-p-123")));
        Assert.Null(ProductImportMergePolicy.UpperCaseLookupRetry(new ProductReadFilter(null)));
    }
}
