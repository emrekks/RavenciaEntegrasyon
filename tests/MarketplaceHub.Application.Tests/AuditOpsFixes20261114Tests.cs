using System.Net;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit of 14 Nov 2026 (operations): a PttAVM order with a cancelled line refused at its import, refused new orders
// re-read on every marketplace, Pazarama's empty brand page logged as a warning.
public sealed class AuditOpsFixes20261114Tests
{
    private static IReadOnlyList<RemoteOrder> PttOrders()
    {
        var (result, error) = PttAvmHttpClient.SoapResponse(HttpStatusCode.OK, PttAvmAdapterTests.OrderListAnswer, PttAvmHttpClient.Orders);
        Assert.Null(error);
        return PttAvmOrderMapper.Page(result!).Items;
    }

    // The window read merges each order's copies (MarketplaceJobProcessor.UpsertOrders → MergeOrderPackages). It took a
    // line's ordered quantity from its packages' active units only, so PttAVM's cancelled line (0 active, 1 cancelled)
    // became 0 ordered with 1 cancelled and the package check refused the whole order: PACKAGE_QUANTITY_INVARIANT_REJECTED
    // (live: PTT-1C2OSLF2R-120926). The documented sample's partly and fully cancelled orders show it.
    [Theory]
    [InlineData("PTT-A1712HP2E-071024")]
    [InlineData("PTT-L6LI1VK2D-211024")]
    [InlineData("PTT-OPEN12345-211026")]
    public void PttAvmOrdersWithACancelledLinePassThePackageCheckAfterTheMerge(string orderNumber)
    {
        var read = PttOrders().Single(x => x.ExternalOrderId == orderNumber);
        var merged = TrendyolJsonMapper.MergeOrderPackages([read], orderNumber)!;

        Assert.Equal(read.Lines.Select(x => (x.ExternalLineId, x.Quantity)), merged.Lines.Select(x => (x.ExternalLineId, x.Quantity)));
        Assert.True(PackageIngestionSafety.TryGetOrderedQuantities(merged.Lines, out var ordered));
        Assert.True(PackageIngestionSafety.TryNormalizeOrder(ordered, merged.Packages, out _));
    }

    [Fact]
    public void TrendyolPackagesStillSumTheirActiveUnitsPerLine()
    {
        RemoteOrder Package(string id, decimal quantity) => new("o-1", "o-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "TRY", 10, 0, 10, "{}", "{}", "{}",
            [new RemoteOrderLine("line-1", "S", null, "P", quantity, 10, 20, "Created")],
            [new RemotePackage(id, null, "Created", DateTimeOffset.UnixEpoch, null, null, [new RemotePackageAllocation("line-1", quantity, 0, 0, 0, 0)])], "{}");

        var merged = TrendyolJsonMapper.MergeOrderPackages([Package("p-1", 1), Package("p-2", 2)], "o-1")!;

        Assert.Equal(3, Assert.Single(merged.Lines).Quantity);
    }

    [Fact]
    public void ARefusalNoticeNamesTheOrderToReadAgain()
    {
        var connection = Guid.Parse("01a15fda-b767-714d-ab0c-d40636b79aec");
        // The live notice of the order that was lost.
        Assert.Equal("PTT-1C2OSLF2R-120926", MarketplaceJobProcessor.RefusedOrderIdOf($"package-quantity:{connection}:order:PTT-1C2OSLF2R-120926:1789229541000", connection));
        Assert.Equal("4001", MarketplaceJobProcessor.RefusedOrderIdOf($"order-coverage:{connection}:4001:1789229541000", connection));
        Assert.Equal("4001", MarketplaceJobProcessor.RefusedOrderIdOf($"order-lines:{connection}:4001:1", connection));
        Assert.Equal("PTT-X-010126", MarketplaceJobProcessor.RefusedOrderIdOf(MarketplaceJobProcessor.OrderNotImportedKey(connection, "PTT-X-010126"), connection));
        // A Trendyol package id is no order number; an adapter's own contract key ends with a code, not a time.
        Assert.Null(MarketplaceJobProcessor.RefusedOrderIdOf($"package-quantity:{connection}:3456789:1789229541000", connection));
        Assert.Null(MarketplaceJobProcessor.RefusedOrderIdOf($"order-contract:{connection}:4001:ORDER_DATE_MISSING", connection));
        // Another connection's notice is not this one's.
        Assert.Null(MarketplaceJobProcessor.RefusedOrderIdOf($"order-coverage:{Guid.NewGuid()}:4001:1", connection));
    }

    [Fact]
    public void SeededOrdersAreDueAtOnceAndKeepAnEntryAlreadyListed()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var listed = RejectedOrderRereadPolicy.Note([], ["A"], now);

        var entries = RejectedOrderRereadPolicy.Seed(listed, ["A", "B", " "], now);

        Assert.Equal(["A", "B"], entries.Select(x => x.OrderId));
        Assert.Equal(now + RejectedOrderRereadPolicy.Delays[0], entries[0].NextAt);
        Assert.Equal(now, entries[1].NextAt);
        Assert.Equal(["B"], RejectedOrderRereadPolicy.Due(entries, now).Select(x => x.OrderId));
        var full = RejectedOrderRereadPolicy.Note([], Enumerable.Range(0, RejectedOrderRereadPolicy.MaxTracked).Select(i => $"F{i}"), now);
        Assert.Equal(RejectedOrderRereadPolicy.MaxTracked, RejectedOrderRereadPolicy.Seed(full, ["C"], now).Count);
    }

    [Fact]
    public void PazaramasEmptyBrandPageIsLoggedAtDebug()
    {
        var empty = new AdapterError(AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED", "Pazarama isteği reddetti: Listelenecek veri bulunamadı!", 200, null, null);
        var refused = new AdapterError(AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED", "Pazarama isteği reddetti: Yetkisiz işlem", 200, null, null);

        Assert.Equal(LogLevel.Debug, PazaramaHttpClient.RefusalLogLevel(empty));
        Assert.Equal(LogLevel.Warning, PazaramaHttpClient.RefusalLogLevel(refused));
    }

    [Fact]
    public void TheResetKeepsOnlyOpenPendingWebhookRetries()
    {
        var keep = PanelFactoryResetService.PendingWebhookRetryKeep;
        Assert.Contains("'webhook-pause:%'", keep);
        Assert.Contains("'webhook-resume:%'", keep);
        Assert.Contains("\"Status\" <> 'Resolved'", keep);
        Assert.NotEqual(PanelFactoryResetService.RequestLockKey(Guid.NewGuid()), PanelFactoryResetService.RequestLockKey(Guid.NewGuid()));
    }
}
