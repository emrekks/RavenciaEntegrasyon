using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaCatalogTests
{
    private const string Root = "11111111-1111-1111-1111-111111111111";
    private const string Child = "22222222-2222-2222-2222-222222222222";
    private const string Leaf = "33333333-3333-3333-3333-333333333333";
    private const string Zero = "00000000-0000-0000-0000-000000000000";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Tree(string rows) => $$"""{"data":[{{rows}}],"success":true}""";

    private static JsonElement Data(string json) => Parse(json).GetProperty("data");

    // ---- categories ----

    [Fact]
    public void Category_tree_becomes_a_rooted_list_with_paths_and_depth()
    {
        var data = Data(Tree($$"""
            {"id":"{{Leaf}}","parentId":"{{Child}}","name":"Bebek Arabası","leaf":true,"parentCategories":[]},
            {"id":"{{Root}}","parentId":null,"name":"Anne Bebek","leaf":false},
            {"id":"{{Child}}","parentId":"{{Root}}","name":"Gezdirme","leaf":false}
            """));

        var items = PazaramaReferenceMapper.Categories(data);

        Assert.Equal([Root, Child, Leaf], items.Select(x => x.ExternalId));
        Assert.Null(items[0].ParentExternalId);
        Assert.Equal(Root, items[1].ParentExternalId);
        Assert.Equal("Anne Bebek / Gezdirme / Bebek Arabası", items[2].Path);
        Assert.Equal([0, 1, 2], items.Select(x => x.Depth));
        Assert.True(items[2].IsLeaf);
        Assert.False(items[0].IsLeaf);
    }

    [Fact]
    public void Category_with_empty_or_unknown_parent_becomes_a_root()
    {
        var data = Data(Tree($$"""
            {"id":"{{Root}}","parentId":"{{Zero}}","name":"A","leaf":true},
            {"id":"{{Child}}","parentId":"99999999-9999-9999-9999-999999999999","name":"B","leaf":true},
            {"id":"{{Leaf}}","parentId":"{{Leaf}}","name":"C","leaf":true}
            """));

        var items = PazaramaReferenceMapper.Categories(data);

        Assert.All(items, item => Assert.Null(item.ParentExternalId));
        Assert.All(items, item => Assert.Equal(0, item.Depth));
    }

    [Fact]
    public void Category_cycle_does_not_loop_forever()
    {
        var data = Data(Tree($$"""
            {"id":"{{Root}}","parentId":"{{Child}}","name":"A","leaf":false},
            {"id":"{{Child}}","parentId":"{{Root}}","name":"B","leaf":false}
            """));

        var items = PazaramaReferenceMapper.Categories(data);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Empty_category_tree_is_a_contract_error()
    {
        Assert.Throws<JsonException>(() => PazaramaReferenceMapper.Categories(Data(Tree(""))));
    }

    // ---- attributes and values ----

    private const string Category = $$"""
        {"data":{"id":"{{Leaf}}","name":"Bebek Arabası","attributes":[
          {"id":"a1","name":"Renk","isVariantable":true,"isRequired":true,"attributeValues":[{"id":"v1","value":"Siyah"},{"id":"v2","value":"Beyaz"},{"id":"v1","value":"Siyah"}]},
          {"id":"a2","name":"Materyal","isVariantable":false,"isRequired":false,"attributeValues":[]}
        ]},"success":true}
        """;

    [Fact]
    public void Attributes_carry_required_flag_and_never_allow_custom_values()
    {
        var items = PazaramaReferenceMapper.Attributes(Data(Category), Leaf);

        Assert.Equal(["a1", "a2"], items.Select(x => x.ExternalId));
        Assert.All(items, item => Assert.Equal(Leaf, item.ParentExternalId));
        Assert.True(items[0].IsRequired);
        Assert.False(items[1].IsRequired);
        Assert.All(items, item => Assert.False(item.AllowsCustomValue));
        // The value list stays out of the hashed attribute payload.
        Assert.DoesNotContain("attributeValues", items[0].RawJson);
    }

    [Fact]
    public void Attribute_values_are_scoped_to_category_and_attribute_and_deduplicated()
    {
        var values = PazaramaReferenceMapper.Values(Data(Category), Leaf, "a1");

        Assert.Equal(["v1", "v2"], values.Select(x => x.ExternalId));
        Assert.All(values, value => Assert.Equal($"{Leaf}/a1", value.ParentExternalId));
        Assert.Equal(["Siyah", "Beyaz"], values.Select(x => x.Name));
        Assert.Empty(PazaramaReferenceMapper.Values(Data(Category), Leaf, "a2"));
        Assert.Empty(PazaramaReferenceMapper.Values(Data(Category), Leaf, "unknown"));
    }

    // ---- brands ----

    [Fact]
    public void Brand_cursor_starts_at_page_one_and_reads_back_what_it_wrote()
    {
        Assert.Equal((1, null), PazaramaReferenceMapper.BrandCursor(null));
        Assert.Equal((3, "b1"), PazaramaReferenceMapper.BrandCursor("3:b1"));
        Assert.Equal((1, null), PazaramaReferenceMapper.BrandCursor("garbage"));
    }

    [Fact]
    public void Brand_page_continues_while_pages_carry_brands()
    {
        var data = Data("""{"data":[{"id":"b1","name":"Marka 1"},{"id":"b2","name":"Marka 2"}]}""");

        var (items, next) = PazaramaReferenceMapper.Brands(data, 1, null, 2);
        Assert.Equal(["b1", "b2"], items.Select(x => x.ExternalId));
        Assert.Equal("2:b1", next);

        var (_, shortPage) = PazaramaReferenceMapper.Brands(data, 2, "other", 5);
        Assert.Equal("3:other", shortPage);
    }

    [Fact]
    public void A_no_data_refusal_is_an_empty_brand_answer()
    {
        Assert.True(PazaramaReferenceMapper.IsNoDataRefusal(PazaramaHttpClient.EnvelopeError(Parse("""{"success":false,"userMessage":"Listelenecek veri bulunamadı!"}"""))!));
        Assert.False(PazaramaReferenceMapper.IsNoDataRefusal(PazaramaHttpClient.EnvelopeError(Parse("""{"success":false,"userMessage":"Yetkisiz istek"}"""))!));
    }

    [Fact]
    public void Brand_scan_stops_when_the_server_repeats_page_one()
    {
        var data = Data("""{"data":[{"id":"b1","name":"Marka 1"},{"id":"b2","name":"Marka 2"}]}""");

        var (items, next) = PazaramaReferenceMapper.Brands(data, 2, "b1", 2);

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public void Empty_brand_page_ends_the_scan()
    {
        var (items, next) = PazaramaReferenceMapper.Brands(Data("""{"data":[]}"""), 4, "b1", 500);

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public void Brand_name_lookup_asks_the_filtered_list_with_an_escaped_name()
    {
        Assert.Equal("brand/getBrands?name=Ravencia&Page=1&Size=100", PazaramaHttpClient.BrandSearch(" Ravencia ", 100));
        Assert.Equal("brand/getBrands?name=Moda%20Zeyn%20%26%20Co&Page=1&Size=100", PazaramaHttpClient.BrandSearch("Moda Zeyn & Co", 100));
        Assert.Equal("brand/getBrands?name=AKMAV%C4%B0&Page=1&Size=100", PazaramaHttpClient.BrandSearch("AKMAVİ", 100));
    }

    [Fact]
    public void Brand_name_lookup_rows_are_brand_rows_deduplicated_by_id()
    {
        var rows = PazaramaReferenceMapper.BrandMatches(Data("""{"data":[{"id":"r1","name":"Ravencia"},{"id":"R1","name":"Ravencia"},{"id":"r2","name":"Ravencia Home"},{"id":"x","name":null},{"id":"00000000-0000-0000-0000-000000000000","name":"Ravencia"}]}"""));

        Assert.Equal(["r1", "r2"], rows.Select(x => x.ExternalId));
        Assert.All(rows, row => { Assert.Equal("BRANDS", row.ResourceType); Assert.Null(row.ParentExternalId); });
        Assert.Empty(PazaramaReferenceMapper.BrandMatches(Data("""{"data":[]}""")));
    }

    [Fact]
    public void Brand_name_lookup_counts_only_when_every_row_carries_the_name()
    {
        RemoteReferenceItem Row(string name) => new("BRANDS", name, null, name, name, 0, true, true, "{}");

        Assert.True(PazaramaReferenceMapper.BrandSearchHonoured("Ravencia", []));
        Assert.True(PazaramaReferenceMapper.BrandSearchHonoured("akmavi", [Row("AKMAVİ"), Row("Akmavi Kids")]));
        // A server that ignored the filter answers page one of the whole list: that proves nothing.
        Assert.False(PazaramaReferenceMapper.BrandSearchHonoured("Ravencia", [Row("Ravencia"), Row("Başka Marka")]));
    }

    [Theory]
    [InlineData("AKMAVİ", "Akmavi", true)]
    [InlineData("AKMAVI", "akmavi", true)]
    [InlineData("Moda  Zeyn", "MODA ZEYN", true)]
    [InlineData("Nacar Moda", "Nacar", false)]
    public void Brand_names_compare_the_turkish_way(string left, string right, bool same) =>
        Assert.Equal(same, PazaramaReferenceMapper.SameBrandName(left, right));

    // ---- products ----

    private const string ApprovedPage = $$"""
        {"data":{"sellerProducts":[
          {"name":"Bebek Arabası","displayName":"Bebek Arabası Siyah","description":"Açıklama","brandName":"Marka","code":"869001","groupCode":"g-1","stockCount":7,"stockCode":"BA-S","listPrice":1200,"salePrice":999.9,"vatRate":10,"categoryName":"Bebek Arabası","categoryId":"{{Leaf}}","state":1,"productStatus":1,
           "attributes":[{"attributeName":"Renk","attributeValue":"Siyah"}],"images":[{"imageUrl":"https://i/2.jpg","sortOrder":2},{"imageUrl":"https://i/1.jpg","sortOrder":1}]},
          {"name":"Bebek Arabası","code":"869002","groupCode":"g-1","stockCount":0,"stockCode":"BA-B","listPrice":1200,"salePrice":999.9,"vatRate":10,"productStatus":10,"attributes":[{"attributeName":"Renk","attributeValue":"Beyaz"}]},
          {"name":"Tek","code":"869003","stockCount":3,"salePrice":50,"vatRate":20,"categoryId":"{{Zero}}","productStatus":1},
          {"name":"Kodsuz","stockCode":"X"}
        ],"nextCursor":"abc=="},"success":true}
        """;

    [Fact]
    public void Approved_item_becomes_a_variant_keyed_by_barcode()
    {
        var page = PazaramaProductMapper.Page(Parse(ApprovedPage), pending: false);
        var product = page.Items[0];
        var variant = product.Variants.Single();

        Assert.Equal("group:g-1", product.ExternalProductId);
        Assert.Equal("g-1", product.ProductMainId);
        Assert.Equal("Bebek Arabası Siyah", product.Title);
        Assert.Equal("Marka", product.BrandName);
        Assert.Equal(Leaf, product.CategoryExternalId);
        Assert.False(product.IsPendingApproval);
        Assert.Equal("869001", variant.ExternalVariantId);
        Assert.Equal("869001", variant.Barcode);
        Assert.Equal("BA-S", variant.Sku);
        // The group code is only a grouping key, never a model code.
        Assert.Null(variant.ModelCode);
        Assert.Equal(999.9m, variant.SalePrice);
        Assert.Equal(1200m, variant.ListPrice);
        Assert.Equal(10m, variant.VatRate);
        Assert.Equal(7m, variant.StockQuantity);
        Assert.Equal("TRY", variant.Currency);
        Assert.Equal("Siyah", variant.Options["Renk"]);
        Assert.Equal(["https://i/1.jpg", "https://i/2.jpg"], variant.ImageUrls);
        Assert.False(variant.Archived);
    }

    [Fact]
    public void Closed_for_sale_item_is_archived_and_groups_with_its_sibling()
    {
        var page = PazaramaProductMapper.Page(Parse(ApprovedPage), pending: false);

        Assert.True(page.Items[1].Variants.Single().Archived);
        Assert.Equal(page.Items[0].ExternalProductId, page.Items[1].ExternalProductId);
    }

    [Fact]
    public void Item_without_group_code_is_its_own_product_and_zero_category_is_dropped()
    {
        var single = PazaramaProductMapper.Page(Parse(ApprovedPage), pending: false).Items[2];

        Assert.Equal("code:869003", single.ExternalProductId);
        Assert.Null(single.CategoryExternalId);
        Assert.Equal("869003", single.Variants.Single().Sku);
    }

    [Fact]
    public void Item_without_barcode_is_reported_not_imported()
    {
        var page = PazaramaProductMapper.Page(Parse(ApprovedPage), pending: false);

        Assert.Equal(3, page.Items.Count);
        var issue = Assert.Single(page.Issues);
        Assert.Equal("PRODUCT_INVALID", issue.Code);
        Assert.Equal("abc==", page.NextCursor);
        Assert.Equal("869001", page.FirstCode);
    }

    [Fact]
    public void Unapproved_items_are_marked_pending_and_the_array_shape_is_read()
    {
        var page = PazaramaProductMapper.Page(Parse("""
            {"data":[{"name":"Bekleyen","code":"B1","groupCode":"g","stockCount":1,"stockCode":"S1","salePrice":10,"listPrice":12,"vatRate":10,"state":0,"attributes":null,"images":null}],
             "pageIndex":0,"pageSize":100,"totalPages":2,"totalCount":150,"hasNextPage":true,"hasPreviousPage":false,"success":true}
            """), pending: true);

        Assert.True(page.Items.Single().IsPendingApproval);
        Assert.Equal(true, page.HasNextPage);
        Assert.Equal(false, page.HasPreviousPage);
        Assert.Equal(150, page.TotalCount);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void Null_data_is_an_empty_page()
    {
        var page = PazaramaProductMapper.Page(Parse("""{"data":null,"success":true}"""), pending: false);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void Missing_data_is_a_contract_error()
    {
        Assert.Throws<JsonException>(() => PazaramaProductMapper.Page(Parse("""{"success":true}"""), pending: false));
    }

    // ---- scan position ----

    [Theory]
    [InlineData(null, 'A', null, 0, null)]
    [InlineData("A", 'A', null, 0, null)]
    [InlineData("A:abc==", 'A', "abc==", 0, null)]
    [InlineData("C:cur", 'C', "cur", 0, null)]
    [InlineData("U:1:", 'U', null, 1, null)]
    [InlineData("U:3:869001", 'U', null, 3, "869001")]
    [InlineData("nonsense", 'A', null, 0, null)]
    public void Scan_position_reads_every_cursor_form(string? cursor, char phase, string? token, int index, string? firstCode)
    {
        var position = PazaramaProductMapper.Position(cursor);

        Assert.Equal(phase, position.Phase);
        Assert.Equal(token, position.Cursor);
        Assert.Equal(index, position.PageIndex);
        Assert.Equal(firstCode, position.FirstCode);
    }

    private static PazaramaProductMapper.ProductPage UnapprovedPage(int count, bool? hasNext = null, string? firstCode = "B1", string? nextCursor = null)
    {
        var items = Enumerable.Range(0, count)
            .Select(i => PazaramaProductMapper.Product(Parse($$"""{"name":"n{{i}}","code":"C{{i}}","salePrice":1}"""), pending: true)!)
            .ToList();
        return new(items, [], nextCursor, hasNext, null, null, firstCode);
    }

    [Fact]
    public void Unapproved_scan_advances_by_index_while_the_server_reports_another_page()
    {
        Assert.Equal("U:2:B1", PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(100, hasNext: true), false, 1, null, 100));
        Assert.Null(PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(100, hasNext: false), false, 1, null, 100));
    }

    [Fact]
    public void Unapproved_scan_falls_back_to_page_fullness_without_a_next_flag()
    {
        Assert.Equal("U:2:B1", PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(100), false, 1, null, 100));
        Assert.Null(PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(40), false, 1, null, 100));
    }

    [Fact]
    public void Unapproved_scan_stops_on_an_empty_page_and_on_a_repeated_first_code()
    {
        Assert.Null(PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(0, hasNext: true), false, 4, "B1", 100));
        Assert.Null(PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(100, hasNext: true, firstCode: "B1"), false, 2, "B1", 100));
    }

    [Fact]
    public void Unapproved_scan_follows_a_cursor_when_the_server_hands_one_out()
    {
        Assert.Equal("C:next", PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(100, nextCursor: "next"), false, 1, null, 100));
        Assert.Equal("C:more", PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(100, nextCursor: "more"), true, 0, null, 100));
        Assert.Null(PazaramaProductMapper.NextUnapprovedCursor(UnapprovedPage(100), true, 0, null, 100));
    }

    // ---- request paths ----

    [Fact]
    public void Request_paths_use_the_documented_parameters()
    {
        var filter = new ProductReadFilter(null);
        Assert.Equal("product/products/approved?Size=100", PazaramaHttpClient.ApprovedPage(null, 100, filter));
        Assert.Equal("product/products/approved?Size=100&Cursor=ab%3D%3D", PazaramaHttpClient.ApprovedPage("ab==", 100, filter));
        Assert.Equal("product/products/approved?Size=1&Code=869001", PazaramaHttpClient.ApprovedPage(null, 1, new ProductReadFilter(null, Barcode: "869001")));
        // A lookup typed in the import screen arrives as the product-main-id filter and is read as a barcode.
        Assert.Equal("product/products/approved?Size=1&Code=869001", PazaramaHttpClient.ApprovedPage(null, 1, new ProductReadFilter(null, ProductMainId: " 869001 ")));
        Assert.Equal("product/products/unapproved?pageIndex=2&pageSize=100&Size=100", PazaramaHttpClient.UnapprovedPage(2, null, 100, filter));
        Assert.Equal("product/products/unapproved?Size=100&Cursor=c1", PazaramaHttpClient.UnapprovedPage(0, "c1", 100, filter));
        Assert.Equal("category/getCategoryWithAttributes?Id=" + Leaf, PazaramaHttpClient.CategoryAttributes(Leaf));
        Assert.Equal("brand/getBrands?Page=2&Size=500", PazaramaHttpClient.BrandPage(2, 500));
    }
}
