using System.Text.Json;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobWritePayloadReaderTests
{
    [Fact]
    public void Approved_update_lists_the_three_requests_and_not_the_whole_product_payload()
    {
        var payload = JsonSerializer.Serialize(new
        {
            Mode = "APPROVED",
            Phase = "SUBMIT_DELIVERY",
            ProductOperationId = "batch-1",
            ExternalOperationId = (string?)null,
            UnapprovedPayloadJson = """{"items":[{"barcode":"A-1","title":"T","salePrice":10}]}""",
            ApprovedContentPayloadJson = """{"items":[{"contentId":12,"title":"T","description":"D","images":[{"url":"u"}],"attributes":[{"attributeId":338,"attributeValueId":1},{"attributeId":47,"customAttributeValue":"Haki"}]},{"contentId":13,"title":"T","attributes":[{"attributeId":338,"attributeValueId":2}]}]}""",
            ApprovedVariantPayloadJson = """{"items":[]}""",
            ApprovedDeliveryPayloadJson = """{"items":[]}"""
        });

        var write = JobWritePayloadReader.Read(MarketplaceJobTypes.ProductUpdate, payload)!;

        Assert.Equal("APPROVED", write.Mode);
        Assert.Equal(["CONTENT", "VARIANTS", "DELIVERY"], write.Parts.Select(x => x.Key));
        var content = write.Parts[0];
        Assert.Equal(2, content.ItemCount);
        Assert.Equal(2, content.ContentCount);
        Assert.Equal(["contentId", "title", "description", "images", "attributes"], content.Fields);
        Assert.Equal(["338", "47"], content.Attributes.Select(x => x.Id));
        Assert.Empty(content.Barcodes);
        Assert.Equal(0, write.Parts[1].ItemCount);
        Assert.Equal([new JobWriteRequestView("PRODUCT", "batch-1")], write.Requests);
    }

    [Fact]
    public void Unapproved_update_and_create_read_barcodes_and_fields()
    {
        var update = JsonSerializer.Serialize(new
        {
            Mode = "UNAPPROVED",
            Phase = "POLL_UNAPPROVED",
            ExternalOperationId = "batch-2",
            UnapprovedPayloadJson = """{"items":[{"barcode":"A-1","title":"T","salePrice":10,"origin":null},{"barcode":"A-2","title":"T","quantity":3}]}""",
            ApprovedContentPayloadJson = "",
            ApprovedVariantPayloadJson = "",
            ApprovedDeliveryPayloadJson = ""
        });
        var write = JobWritePayloadReader.Read(MarketplaceJobTypes.ProductUpdate, update)!;
        var part = Assert.Single(write.Parts);
        Assert.Equal("UNAPPROVED", part.Key);
        Assert.Equal(["A-1", "A-2"], part.Barcodes);
        Assert.Equal(["barcode", "title", "salePrice", "quantity"], part.Fields);
        Assert.Equal([new JobWriteRequestView("POLL_UNAPPROVED", "batch-2")], write.Requests);

        var create = JsonSerializer.Serialize(new { Phase = "POLL", ExternalOperationId = "batch-3", PayloadJson = """{"items":[{"barcode":"B-1","title":"X"}]}""" });
        var created = JobWritePayloadReader.Read(MarketplaceJobTypes.ProductCreate, create)!;
        Assert.Equal("CREATE", Assert.Single(created.Parts).Key);
        Assert.Equal([new JobWriteRequestView("PRODUCT", "batch-3")], created.Requests);
    }

    [Fact]
    public void Other_jobs_and_broken_payloads_give_nothing()
    {
        Assert.Null(JobWritePayloadReader.Read("ORDER_STATUS_SYNC", "{}"));
        Assert.Null(JobWritePayloadReader.Read(MarketplaceJobTypes.ProductUpdate, "not json"));
        Assert.Null(JobWritePayloadReader.Part("CONTENT", ""));
    }
}
