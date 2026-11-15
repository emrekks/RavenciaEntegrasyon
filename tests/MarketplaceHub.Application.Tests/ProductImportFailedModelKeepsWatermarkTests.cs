using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-1 (15 Nov 2026): a catalog run with a failed model leaves the PRODUCTS watermark where it was, so the next
// incremental read asks for the failed products again; a run fails as a whole only when no model finished, the products
// it filtered out (drafts, archived) not counted. The PostgreSQL run is in TrendyolImportRegressionPostgreSqlTests.
public sealed class ProductImportFailedModelKeepsWatermarkTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Previous = new(2026, 11, 15, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_full_read_with_a_failed_model_keeps_the_watermark()
    {
        Assert.Equal(Previous, ProductImportScanPolicy.CompletedWatermark(Started, Previous, scoped: false, failedModels: 1));
        Assert.Null(ProductImportScanPolicy.CompletedWatermark(Started, null, scoped: false, failedModels: 3));
    }

    [Fact]
    public void A_full_read_without_a_failed_model_moves_the_watermark_to_its_start()
    {
        Assert.Equal(Started.AddSeconds(-60), ProductImportScanPolicy.CompletedWatermark(Started, Previous, scoped: false, failedModels: 0));
    }

    [Theory]
    [InlineData(70, 0, true)]   // 100 received, 30 filtered out, 70 failed: nothing finished
    [InlineData(1, 0, true)]
    [InlineData(1, 39, false)]  // one model failed, the rest finished (changed or unchanged)
    [InlineData(0, 0, false)]   // an empty or wholly filtered page failed nothing
    [InlineData(0, 5, false)]
    public void A_run_fails_as_a_whole_only_when_no_model_finished(int failedProducts, int completedModels, bool expected)
    {
        Assert.Equal(expected, ProductImportScanPolicy.AllFailed(failedProducts, completedModels));
    }
}
