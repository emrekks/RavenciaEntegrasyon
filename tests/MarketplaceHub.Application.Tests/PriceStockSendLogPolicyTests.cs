using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The price/stock send log: what a row says of a line, and why the line was sent.
public sealed class PriceStockSendLogPolicyTests
{
    private const string Barcode = "8680000000001";
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 18, 0, 0, TimeSpan.FromHours(3));

    private static PriceInventoryPushLine Line(decimal quantity = 4, decimal sale = 749m, decimal list = 899m, bool quantityOmitted = false, bool priceOmitted = false, string barcode = Barcode) =>
        new(Guid.NewGuid(), Guid.NewGuid(), barcode, quantity, list, sale, "try", 2, 2, "hash", MerchantSku: "SKU-1", QuantityOmitted: quantityOmitted, PriceOmitted: priceOmitted);

    private static PriceStockSendLog Row(PriceInventoryPushLine line, PriceStockConfirmedValues? confirmed, string result = PriceStockSendResults.Submitted, string? code = null, string? message = null) =>
        PriceStockSendLogPolicy.Row(Guid.NewGuid(), Guid.NewGuid(), "trendyol", Guid.NewGuid(), line, confirmed, PriceStockSendTriggers.Order, "batch-1", Now, result, code, message);

    [Fact]
    public void Row_ShowsTheConfirmedValuesOfTheSameCodeAgainstTheSentOnes()
    {
        var row = Row(Line(), new(Barcode, 5m, 799m, 899m));

        Assert.Equal((5m, 4m), (row.PreviousQuantity, row.NewQuantity));
        Assert.Equal((799m, 749m), (row.PreviousSalePrice, row.NewSalePrice));
        Assert.Equal((899m, 899m), (row.PreviousListPrice, row.NewListPrice));
        Assert.Equal(("TRENDYOL", "TRY", "SKU-1", "batch-1"), (row.PlatformCode, row.Currency, row.StockCode, row.ExternalRequestId));
        Assert.Equal(PriceStockSendResults.Submitted, row.Result);
        Assert.Null(row.CompletedAt);
        Assert.Equal(TimeSpan.Zero, row.CreatedAt.Offset);
    }

    // Another code's confirmed values say nothing of this one; none confirmed is unknown, not zero.
    [Theory]
    [InlineData("OTHER-BARCODE")]
    [InlineData(null)]
    public void Row_KnowsNoPreviousValueForAnotherOrNoCode(string? confirmedCode)
    {
        var row = Row(Line(), new(confirmedCode, 5m, 799m, 899m));

        Assert.Null(row.PreviousQuantity);
        Assert.Null(row.PreviousSalePrice);
        Assert.Null(row.PreviousListPrice);
        Assert.Null(Row(Line(), null).PreviousQuantity);
    }

    [Fact]
    public void Row_HasNoNewValueForAFieldLeftOutOfTheRequest()
    {
        var stockOnly = Row(Line(priceOmitted: true), new(Barcode, 5m, 799m, 899m));
        var priceOnly = Row(Line(quantityOmitted: true), new(Barcode, 5m, 799m, 899m));

        Assert.Equal((4m, (decimal?)null, (decimal?)null), (stockOnly.NewQuantity, stockOnly.NewSalePrice, stockOnly.NewListPrice));
        Assert.Equal(799m, stockOnly.PreviousSalePrice);
        Assert.Equal(((decimal?)null, 749m), (priceOnly.NewQuantity, priceOnly.NewSalePrice));
    }

    [Fact]
    public void Row_WithAnOutcomeIsSettledAndCutToItsColumns()
    {
        var row = Row(Line(), null, PriceStockSendResults.Failed, new string('C', 300), new string('m', 900));

        Assert.Equal(PriceStockSendResults.Failed, row.Result);
        Assert.Equal(PriceStockSendLogPolicy.MaxCodeLength, row.ErrorCode!.Length);
        Assert.Equal(PriceStockSendLogPolicy.MaxMessageLength, row.ErrorMessage!.Length);
        Assert.Equal(Now.ToUniversalTime(), row.CompletedAt);
    }

    // A settled row keeps its outcome: a later answer of the same request does not overwrite it.
    [Fact]
    public void Settle_LeavesASettledRowAlone()
    {
        var row = Row(Line(), null);
        PriceStockSendLogPolicy.Settle(row, PriceStockSendResults.Rejected, "OUT_OF_RANGE", "Fiyat aralık dışında", Now);
        PriceStockSendLogPolicy.Settle(row, PriceStockSendResults.Applied, null, null, Now.AddMinutes(1));

        Assert.Equal((PriceStockSendResults.Rejected, "OUT_OF_RANGE"), (row.Result, row.ErrorCode));
    }

    [Theory]
    [InlineData("manual:adjust-1", PriceStockSendTriggers.ManualStock)]
    [InlineData("price:0123456789abcdef0123456789abcdef:p4", PriceStockSendTriggers.PriceEdit)]
    [InlineData("catalog-stock:0123456789abcdef0123456789abcdef:0123456789abcdef0123456789abcdef:v3", PriceStockSendTriggers.StockImport)]
    [InlineData("0123456789abcdef0123456789abcdef:1761840000000:committed:1:0", PriceStockSendTriggers.Order)]
    [InlineData("0123456789abcdef0123456789abcdef:1761840000000:2", PriceStockSendTriggers.Order)]
    [InlineData("return-claim-key:stock:0123456789abcdef0123456789abcdef", PriceStockSendTriggers.StockChange)]
    [InlineData("", PriceStockSendTriggers.StockChange)]
    [InlineData(null, PriceStockSendTriggers.StockChange)]
    public void Trigger_IsReadFromTheSourceEventOfTheDispatch(string? sourceEventId, string expected) =>
        Assert.Equal(expected, PriceStockSendTriggers.FromSourceEvent(sourceEventId));

    [Theory]
    [InlineData(PriceStockSendTriggers.Order, false, true, false, true, PriceStockSendTriggers.Order)]
    [InlineData("SOMETHING_ELSE", false, false, true, true, PriceStockSendTriggers.PanelSend)]
    [InlineData(null, true, false, false, true, PriceStockSendTriggers.PanelSend)]
    [InlineData(null, false, true, false, true, PriceStockSendTriggers.Automatic)]
    [InlineData(null, false, true, false, false, PriceStockSendTriggers.Reconcile)]
    public void Trigger_OfAJobWithoutOneInItsPayloadComesFromTheJob(string? payloadTrigger, bool manual, bool automatic, bool userTriggered, bool singleVariant, string expected) =>
        Assert.Equal(expected, PriceStockSendTriggers.ForJob(payloadTrigger, manual, automatic, userTriggered, singleVariant));

    [Fact]
    public void Query_PagesAtMostAHundredRows()
    {
        Assert.Equal(50, new PriceStockSendLogQuery().EffectiveLimit);
        Assert.Equal(100, new PriceStockSendLogQuery(Limit: 5_000).EffectiveLimit);
        Assert.Equal(1, new PriceStockSendLogQuery(Limit: 0).EffectiveLimit);
    }
}
