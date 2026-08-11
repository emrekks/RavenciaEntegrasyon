using MarketplaceHub.Infrastructure.Adapters.Trendyol.Contracts;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F3TrendyolContractTests
{
    [Fact]
    public void Anonymous_order_fixture_maps_cursor_package_and_line_without_PII()
    {
        var json = Fixture("order-success.json"); var page = TrendyolJsonMapper.Orders(json); var order = Assert.Single(page.Items); var package = Assert.Single(order.Packages); var line = Assert.Single(order.Lines);
        Assert.False(page.HasMore); Assert.Equal("ORDER-ANON-001", order.ExternalOrderId); Assert.Equal("900001", package.ExternalPackageId); Assert.Equal(125.50m, package.NetAmount); Assert.Equal("SKU-ANON-001", line.Sku);
        Assert.Equal(125.50m, line.UnitPrice); Assert.Equal(20m, line.VatRate);
        Assert.Contains("\"productImageUrl\"", line.SourceSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("https://cdn.example.test/products/anon-001.jpg", line.SourceSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"productSize\"", line.SourceSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"micro\":true", order.CustomerSnapshotJson, StringComparison.Ordinal); Assert.Contains("\"3pByTrendyol\":true", order.CustomerSnapshotJson, StringComparison.Ordinal); Assert.Contains("\"agreedDeliveryDate\":1762253500000", order.CustomerSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("\"invoiceStatus\":\"Invoiced\"", order.CustomerSnapshotJson, StringComparison.Ordinal); Assert.Contains("\"invoiceLink\":\"https://invoices.example.test/ORDER-ANON-001.pdf\"", order.CustomerSnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal); Assert.DoesNotContain("+90", json, StringComparison.Ordinal); Assert.True(TrendyolContractGuard.HasContentArray(json));
    }

    [Fact]
    public void Legacy_order_fields_remain_readable_during_Trendyol_transition()
    {
        const string json = """
            {"content":[{"shipmentPackageId":1,"orderNumber":"O-1","currencyCode":"TRY","packageTotalPrice":120,
            "lines":[{"id":2,"merchantSku":"OLD-SKU","productName":"Legacy","quantity":1,"price":120,"vatBaseAmount":20}]}]}
            """;
        var line = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(json).Items).Lines);
        Assert.Equal("2", line.ExternalLineId); Assert.Equal("OLD-SKU", line.Sku); Assert.Equal(120m, line.UnitPrice); Assert.Equal(20m, line.VatRate);
    }

    [Fact]
    public void Partial_batch_preserves_each_line_result()
    {
        var json = Fixture("batch-partial.json"); var batch = TrendyolJsonMapper.Batch(json, "fallback");
        Assert.Equal("BATCH-ANON-001", batch.ExternalOperationId); Assert.Equal("COMPLETED", batch.Status); Assert.Equal(2, batch.Lines.Count);
        Assert.Equal("BARCODE-ANON-001", Assert.Single(batch.Lines, x => x.Succeeded).ExternalKey);
        var failed = Assert.Single(batch.Lines, x => !x.Succeeded); Assert.Equal("BARCODE-ANON-002", failed.ExternalKey); Assert.Equal("ANONYMIZED_REMOTE_VALIDATION", failed.ErrorCode); Assert.False(failed.Retryable);
        Assert.True(TrendyolContractGuard.HasBatchRequestId(json));
    }

    [Fact]
    public void Product_and_return_fixtures_map_only_documented_identifiers()
    {
        var product = Assert.Single(TrendyolJsonMapper.Products(Fixture("product-approved.json")).Items); Assert.Contains("https://cdn.example.test/products/anon-001.jpg", product.RawJson, StringComparison.Ordinal); var claim = Assert.Single(TrendyolJsonMapper.Returns(Fixture("return-success.json")).Items); Assert.Equal("CLAIM-ANON-001", claim.ExternalClaimId); Assert.Equal("Created", claim.RawStatus); var line = Assert.Single(claim.Lines); Assert.Equal(1, line.Quantity); Assert.Equal("ANON", claim.ReasonCode);
    }

    [Fact]
    public void Approved_product_direct_variant_shape_preserves_images_for_order_enrichment()
    {
        const string json = """
            {"page":0,"size":1,"totalPages":1,"content":[{"id":810001,"productMainId":"PRODUCT-1","barcode":"8681358387092v6","stockCode":"merchantSku","images":[{"url":"https://cdn.example.test/products/bag.jpg"}]}]}
            """;

        var product = Assert.Single(TrendyolJsonMapper.Products(json).Items);
        Assert.Equal("8681358387092v6", product.Barcode);
        Assert.Equal("merchantSku", product.Sku);
        Assert.Contains("https://cdn.example.test/products/bag.jpg", product.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Product_approval_readback_distinguishes_approved_pending_and_rejected_rows()
    {
        var approved = TrendyolJsonMapper.ApprovedPublicationStatus(Fixture("product-approved.json"), "BARCODE-ANON-001");
        Assert.NotNull(approved);
        Assert.Equal("APPROVED", approved!.Status);
        Assert.Equal("800001", approved.ExternalProductId);
        Assert.Equal("810001", approved.ExternalVariantId);

        var rejected = TrendyolJsonMapper.UnapprovedPublicationStatus(Fixture("product-unapproved.json"), "BARCODE-ANON-002");
        Assert.NotNull(rejected);
        Assert.Equal("REJECTED", rejected!.Status);
        Assert.Equal("ANONYMIZED_CONTENT_REJECTION", rejected.RejectionCode);

        const string pendingJson = """
            {"content":[{"productMainId":"PRODUCT-ANON-003","status":"pendingApproval","barcode":"BARCODE-ANON-003","rejectReasonDetails":[]}]}
            """;
        var pending = TrendyolJsonMapper.UnapprovedPublicationStatus(pendingJson, "BARCODE-ANON-003");
        Assert.NotNull(pending);
        Assert.Equal("PENDING_APPROVAL", pending!.Status);
        Assert.Null(pending.RejectionCode);
        Assert.Null(TrendyolJsonMapper.ApprovedPublicationStatus(Fixture("product-approved.json"), "UNKNOWN-BARCODE"));
    }


    [Fact]
    public void Order_v2_documented_fields_map_without_legacy_aliases()
    {
        const string json = """
            {"hasMore":true,"nextCursor":"CURSOR-2","content":[{"shipmentPackageId":991,"orderNumber":"O-V2","currencyCode":"TRY","packageGrossAmount":150,"packageSellerDiscount":10,"packageTyDiscount":5,"packageTotalPrice":135,"shipmentPackageStatus":"Created","cargoSenderNumber":"TRACK-V2","orderDate":1762253333685,"lastModifiedDate":1762253393685,"lines":[{"lineId":771,"stockCode":"SKU-V2","barcode":"BC-V2","productName":"V2 Product","quantity":2,"lineGrossAmount":150,"lineUnitPrice":75,"vatRate":20,"orderLineItemStatusName":"Created"}]}]}
            """;
        var page = TrendyolJsonMapper.Orders(json);
        Assert.True(page.HasMore); Assert.Equal("CURSOR-2", page.NextCursor);
        var order = Assert.Single(page.Items); var line = Assert.Single(order.Lines); var package = Assert.Single(order.Packages);
        Assert.Equal("991", package.ExternalPackageId); Assert.Equal("TRACK-V2", package.CargoTrackingNumber); Assert.Equal("771", line.ExternalLineId); Assert.Equal("SKU-V2", line.Sku);
        Assert.Equal(75m, line.UnitPrice); Assert.Equal(20m, line.VatRate); Assert.Equal(150m, package.GrossAmount); Assert.Equal(15m, package.DiscountAmount); Assert.Equal(135m, package.NetAmount);
    }

    [Fact]
    public void Approved_product_pagination_uses_page_then_next_page_token_at_the_ten_thousand_boundary()
    {
        const string offsetJson = """{"page":0,"size":100,"totalPages":2,"content":[],"nextPageToken":null}""";
        var offset = TrendyolJsonMapper.Products(offsetJson);
        Assert.True(offset.HasMore); Assert.Equal("p:1", offset.NextCursor);

        const string tokenJson = """{"page":99,"size":100,"totalPages":101,"content":[],"nextPageToken":"TOKEN-10000"}""";
        var token = TrendyolJsonMapper.Products(tokenJson);
        Assert.True(token.HasMore); Assert.Equal("t:TOKEN-10000", token.NextCursor);
    }

    [Fact]
    public void Batch_lines_accept_content_and_stock_keys_for_update_and_price_inventory_operations()
    {
        const string json = """
            {"batchRequestId":"B-2","status":"COMPLETED","items":[
              {"requestItem":{"contentId":800001},"status":"SUCCESS","failureReasons":[]},
              {"requestItem":{"stockCode":"SKU-2"},"status":"FAILED","failureReasons":["INVALID_PRICE"]}
            ]}
            """;
        var batch = TrendyolJsonMapper.Batch(json, "fallback");
        Assert.Equal("800001", batch.Lines[0].ExternalKey);
        Assert.Equal("SKU-2", batch.Lines[1].ExternalKey);
        Assert.False(batch.Lines[1].Succeeded);
    }

    [Fact]
    public void Current_return_contract_prefers_claimId_and_preserves_line_ids()
    {
        const string json = """
            {"page":0,"totalPages":1,"content":[{"claimId":"CLAIM-V2","id":"LEGACY-ID","orderNumber":"O-1","lastModifiedDate":1762253993685,"items":[{"id":"CLAIM-LINE-V2","orderLineItemId":"ORDER-LINE-V2","quantity":1,"claimItemStatus":{"name":"Created"},"customerClaimItemReason":{"name":"Reason","code":"R1"}}]}]}
            """;
        var claim = Assert.Single(TrendyolJsonMapper.Returns(json).Items);
        Assert.Equal("CLAIM-V2", claim.ExternalClaimId);
        var line = Assert.Single(claim.Lines); Assert.Equal("CLAIM-LINE-V2", line.ExternalLineId); Assert.Equal("ORDER-LINE-V2", line.ExternalOrderLineId);
    }

    [Fact]
    public void Source_contract_uses_storefront_header_v2_orders_core_channel_and_tracking_details_write()
    {
        var root = FindRoot();
        var http = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "TrendyolHttpClient.cs"));
        var auth = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "TrendyolAuthenticationHandler.cs"));
        var options = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "TrendyolOptions.cs"));
        var composer = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Persistence", "ProductPublicationComposer.cs"));
        Assert.Contains("storeFrontCode", auth, StringComparison.Ordinal); Assert.Contains("TR", auth, StringComparison.Ordinal);
        Assert.Contains("includeStoreFrontCode", auth, StringComparison.Ordinal);
        Assert.Contains("PollStageClaimsByStatusAsync", http, StringComparison.Ordinal);
        Assert.Contains("claimItemStatus=", http, StringComparison.Ordinal);
        Assert.Contains("WaitingInAction", http, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll", http, StringComparison.Ordinal);
        Assert.Contains("/v2/orders", options, StringComparison.Ordinal); Assert.Contains("OrderStream", http, StringComparison.Ordinal);
        Assert.Contains("[\"channels\"] = new[] { \"CORE\" }", composer, StringComparison.Ordinal);
        Assert.Contains("TRACKING_NUMBER", http, StringComparison.Ordinal);
        Assert.Contains("/tracking-details", options, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(page.Limit, 1, 100)", http, StringComparison.Ordinal);
    }

    [Fact]
    public void Documented_brand_response_maps_to_canonical_BRANDS_resource()
    {
        const string json = "{\"brands\":[{\"id\":1584892,\"name\":\"brand-1\"}]}";
        var brand = Assert.Single(TrendyolJsonMapper.References("BRANDS", json, null));
        Assert.Equal("BRANDS", brand.ResourceType);
        Assert.Equal("1584892", brand.ExternalId);
        Assert.Equal("brand-1", brand.Name);
        Assert.True(brand.IsActive);
    }

    [Fact]
    public void Documented_category_attribute_and_value_responses_preserve_parent_scope()
    {
        const string attributesJson = """
            {"id":14609,"categoryAttributes":[{"allowCustom":false,"attribute":{"id":293,"name":"Beden"},"categoryId":14609,"required":true,"varianter":true,"slicer":false,"allowMultipleAttributeValues":false}]}
            """;
        var attribute = Assert.Single(TrendyolJsonMapper.References("CATEGORY_ATTRIBUTES", attributesJson, "14609"));
        Assert.Equal("293", attribute.ExternalId);
        Assert.Equal("14609", attribute.ParentExternalId);
        Assert.Equal("Beden", attribute.Name);
        Assert.True(attribute.IsRequired);
        Assert.False(attribute.AllowsCustomValue);
        Assert.False(attribute.AllowsMultipleValues);

        const string valuesJson = """
            {"totalElements":2,"totalPages":1,"page":0,"size":10,"content":[{"attributeValueId":4872,"attributeValue":"Tek Ebat"},{"attributeValueId":4873,"attributeValue":"S"}]}
            """;
        var values = TrendyolJsonMapper.References("ATTRIBUTE_VALUES", valuesJson, "14609/293");
        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.Equal("14609/293", value.ParentExternalId));
        Assert.Equal(["4872", "4873"], values.Select(value => value.ExternalId));
    }

    [Fact]
    public void Capability_discovery_probes_documented_reads_and_contains_no_write_request()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "TrendyolHttpClient.cs"));
        var discovery = source[source.IndexOf("DiscoverCapabilitiesAsync", StringComparison.Ordinal)..source.IndexOf("public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>>", StringComparison.Ordinal)];

        Assert.Contains("F3Capabilities.ProductRead", discovery, StringComparison.Ordinal);
        Assert.Contains("F3Capabilities.OrderRead", discovery, StringComparison.Ordinal);
        Assert.Contains("F3Capabilities.ReturnRead", discovery, StringComparison.Ordinal);
        Assert.Contains("F3Capabilities.ReferenceRead", discovery, StringComparison.Ordinal);
        Assert.Contains("new(\"CATEGORIES\", null)", discovery, StringComparison.Ordinal);
        Assert.Contains("new(null, 1)", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Post", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Put", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Delete", discovery, StringComparison.Ordinal);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "Fixtures", name));
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
