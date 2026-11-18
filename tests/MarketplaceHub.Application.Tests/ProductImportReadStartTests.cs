using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Fiyat ve Stokları Güncelle" (Mevcut Ürünleri Güncelle) reads the whole catalog (19 Nov 2026): live job 01a1f0bc took
// the PRODUCTS watermark and read 13 of Trendyol's 1,013 contents, so MZ040RCY kept panel stock 44 against Trendyol's 50.
// Only the plain incremental read starts from the watermark. The PostgreSQL run is in TrendyolImportRegressionPostgreSqlTests.
public sealed class ProductImportReadStartTests
{
    private static readonly DateTimeOffset Watermark = new(2026, 11, 17, 17, 0, 41, TimeSpan.Zero);

    [Fact]
    public void An_existing_only_run_reads_the_whole_catalog_despite_a_watermark()
    {
        Assert.Null(ProductImportScanPolicy.IncrementalReadStart(fullScan: false, existingOnly: true, singleLookup: false, hasSnapshots: true, Watermark));
    }

    [Fact]
    public void The_plain_incremental_read_starts_two_minutes_before_the_watermark()
    {
        Assert.Equal(Watermark.AddMinutes(-2), ProductImportScanPolicy.IncrementalReadStart(fullScan: false, existingOnly: false, singleLookup: false, hasSnapshots: true, Watermark));
    }

    [Theory]
    [InlineData(true, false, true, true)]   // full scan
    [InlineData(false, true, true, true)]   // single lookup
    [InlineData(false, false, false, false)] // nothing imported yet
    public void Other_reads_take_no_window(bool fullScan, bool singleLookup, bool hasSnapshots, bool withWatermark)
    {
        Assert.Null(ProductImportScanPolicy.IncrementalReadStart(fullScan, existingOnly: false, singleLookup, hasSnapshots, withWatermark ? Watermark : null));
    }
}
