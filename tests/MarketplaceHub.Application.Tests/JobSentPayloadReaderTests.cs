using System.Text.Json;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobSentPayloadReaderTests
{
    [Fact]
    public void Price_stock_lists_the_request_fields_barcodes_trigger_and_marketplace_id()
    {
        var payload = JsonSerializer.Serialize(new
        {
            Phase = "POLL",
            PayloadJson = """{"items":[{"barcode":"A-1","quantity":4,"salePrice":10},{"barcode":"A-2","quantity":0}]}""",
            ExternalOperationId = "LISTING_INVENTORY:upload-9",
            Trigger = "ORDER"
        });
        var sent = JobSentPayloadReader.Read(MarketplaceJobTypes.PriceInventorySync, payload)!;
        var part = Assert.Single(sent.Parts);
        Assert.Equal("PRICE_STOCK", part.Key);
        Assert.Equal(2, part.ItemCount);
        Assert.Equal(["barcode", "quantity", "salePrice"], part.Fields);
        Assert.Equal(["A-1", "A-2"], part.Barcodes);
        Assert.Equal("ORDER", sent.Trigger);
        Assert.Equal([new JobWriteRequestView("PRICE_STOCK", "upload-9")], sent.Requests);
    }

    [Fact]
    public void Pazarama_price_and_stock_are_two_requests_and_an_empty_one_is_not_sent()
    {
        var payload = JsonSerializer.Serialize(new { PayloadJson = """{"price":[{"code":"B-1","listPrice":3,"salePrice":3}],"stock":[]}""", ExternalOperationId = (string?)null });
        var sent = JobSentPayloadReader.Read(MarketplaceJobTypes.PriceInventorySync, payload)!;
        Assert.Equal(["PRICE", "STOCK"], sent.Parts.Select(x => x.Key));
        Assert.Equal(["B-1"], sent.Parts[0].Barcodes);
        Assert.Equal(0, sent.Parts[1].ItemCount);
        Assert.Empty(sent.Requests);
    }

    [Fact]
    public void Shopify_instant_answer_is_a_note_not_a_request_id()
    {
        var payload = JsonSerializer.Serialize(new { PayloadJson = """{"items":[]}""", ExternalOperationId = "shopify-completed:eyJ4Ijox" });
        var sent = JobSentPayloadReader.Read(MarketplaceJobTypes.PriceInventorySync, payload)!;
        Assert.Empty(sent.Requests);
        Assert.Contains("Shopify", sent.Note);
    }

    [Fact]
    public void Shipment_action_lists_field_names_and_line_count_never_values()
    {
        var payload = JsonSerializer.Serialize(new { Action = "picking", PayloadJson = """{"lines":[{"lineId":1,"quantity":1},{"lineId":2,"quantity":2}],"params":{},"status":"Picking"}""" });
        var sent = JobSentPayloadReader.Read(MarketplaceJobTypes.ShipmentAction, payload)!;
        var part = Assert.Single(sent.Parts);
        Assert.Equal("SHIPMENT_PICKING", part.Key);
        Assert.Equal(2, part.ItemCount);
        Assert.Equal(["lines", "status"], part.Fields);
        Assert.DoesNotContain("Picking", JsonSerializer.Serialize(sent));
    }

    [Fact]
    public void Pazarama_sale_close_reads_its_barcodes_and_other_archives_are_left_to_the_product_reader()
    {
        var payload = JsonSerializer.Serialize(new { JobId = Guid.NewGuid(), SaleStatusBarcodes = new[] { "C-1", "C-2" }, Close = true });
        var sent = JobSentPayloadReader.Read(MarketplaceJobTypes.ProductArchive, payload)!;
        var part = Assert.Single(sent.Parts);
        Assert.Equal("SALE_CLOSE", part.Key);
        Assert.Equal(["C-1", "C-2"], part.Barcodes);
        Assert.Null(JobSentPayloadReader.Read(MarketplaceJobTypes.ProductArchive, JsonSerializer.Serialize(new { PayloadJson = "{}" })));
        Assert.Null(JobSentPayloadReader.Read(MarketplaceJobTypes.ProductUpdate, "{}"));
    }

    [Fact]
    public void Dispatch_and_invoice_jobs_say_what_goes_without_inventing_ids()
    {
        var dispatch = JobSentPayloadReader.Read(MarketplaceJobTypes.StockProjectionDispatch, """{"variantId":"x"}""")!;
        Assert.Empty(dispatch.Parts);
        Assert.NotNull(dispatch.Note);
        var submit = JobSentPayloadReader.Read(InvoicingJobTypes.InvoiceSubmit, """{"invoiceId":"x"}""")!;
        Assert.Equal("INVOICE_SUBMIT", Assert.Single(submit.Parts).Key);
        Assert.Empty(submit.Requests);
    }

    [Fact]
    public void Request_ids_and_values_are_read_plainly()
    {
        Assert.Equal("abc-1", JobSentRequestId.Displayable(" abc-1 "));
        Assert.Null(JobSentRequestId.Displayable("""{"title":"Bad Request","detail":"x"}"""));
        Assert.Null(JobSentRequestId.Displayable(null));
        Assert.Equal("Stok 4 · Satış 1.299,50 TRY", JobSentRequestId.PriceStockValues(4, 1299.5m, null, "TRY"));
        Assert.Null(JobSentRequestId.PriceStockValues(null, null, null, "TRY"));
        Assert.Equal("VERIFIED", JobSentRequestId.EffectStatus("APPLIED", DateTimeOffset.UtcNow));
        Assert.Equal("ACCEPTED", JobSentRequestId.EffectStatus(null, DateTimeOffset.UtcNow));
        Assert.Equal("OPEN", JobSentRequestId.EffectStatus(null, null));
        Assert.Equal("Ret", JobSentRequestId.DecisionLabel("REJECT"));
    }
}
