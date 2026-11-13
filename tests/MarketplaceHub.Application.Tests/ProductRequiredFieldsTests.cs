using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The editor's eleven required fields held by the server (owner, 10–11 Nov 2026), the one desi limit, and the brand rule
// the imports share (owner decision 12 Nov 2026).
public sealed class ProductRequiredFieldsTests
{
    private static readonly Guid BrandId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();

    private static ProductRequiredFields.Row CompleteRow(string sku = "A-1") => new(sku, $"869{sku}", "MOD-1", 1m, 120m, 100m);

    private static ProductRequiredFields.Card CompleteCard(params ProductRequiredFields.Row[] rows) =>
        new("Bluz", BrandId, CategoryId, WarrantyGiven: true, DeliveryDaysGiven: true, 120m, 100m, rows.Length == 0 ? [CompleteRow()] : rows);

    [Fact]
    public void ACompleteCard_MissesNothing() => Assert.Empty(ProductRequiredFields.Missing(CompleteCard()));

    [Fact]
    public void EveryEmptyField_IsNamedInTheEditorsOrder_WithTheEditorsMessages()
    {
        var card = new ProductRequiredFields.Card(" ", null, Guid.Empty, WarrantyGiven: false, DeliveryDaysGiven: false, 0m, null,
            [new ProductRequiredFields.Row("", null, " ", null, null, null)]);

        var gaps = ProductRequiredFields.Missing(card);

        Assert.Equal(["title", "brandId", "categoryId", "modelCode", "baseSku", "barcode", "desi", "warrantyMonths", "deliveryDays", "listPrice", "salePrice"], gaps.Select(gap => gap.Field));
        Assert.Equal("Zorunlu alanları doldurun: Ürün Adı, Marka, Panel Kategorisi, Model Kodu, Stok Kodu, Barkod, Desi, Garanti Süresi, Teslimat Süresi, Liste Fiyatı, Satış Fiyatı.", ProductRequiredFields.Message(gaps));
        var errors = ProductRequiredFields.FieldErrors(gaps);
        Assert.Equal(["Marka zorunludur."], errors["brandId"]);
        // The card's list price is 0: there, but not above 0.
        Assert.Equal(["Liste Fiyatı 0'dan büyük olmalı."], errors["listPrice"]);
        Assert.Equal(["Satış Fiyatı zorunludur."], errors["salePrice"]);
    }

    [Fact]
    public void ARowsOwnPrice_ElseTheCards_MustBeAboveZero_OnEveryRow()
    {
        // The second row has no price of its own and takes the card's.
        Assert.Empty(ProductRequiredFields.Missing(CompleteCard(CompleteRow(), CompleteRow("A-2") with { ListPrice = null, SalePrice = null })));
        var gaps = ProductRequiredFields.Missing(CompleteCard(CompleteRow(), CompleteRow("A-2") with { SalePrice = 0m }));
        Assert.Equal("Satış Fiyatı 0'dan büyük olmalı.", Assert.Single(gaps).Message);
        // Stock code, barcode and desi are asked of every row.
        Assert.Equal(["barcode", "desi"], ProductRequiredFields.Missing(CompleteCard(CompleteRow(), CompleteRow("A-2") with { Barcode = null, Desi = 0m })).Select(gap => gap.Field));
    }

    [Fact]
    public void Warranty_IsGivenWhenTheSaveSendsIt_ZeroIncluded()
    {
        Assert.Empty(ProductRequiredFields.Missing(CompleteCard() with { WarrantyGiven = true }));
        Assert.Equal("Garanti Süresi zorunludur.", Assert.Single(ProductRequiredFields.Missing(CompleteCard() with { WarrantyGiven = false })).Message);
    }

    [Fact]
    public void TheDesiLimit_IsOneNumberOnEveryPath()
    {
        Assert.Equal(9999m, ProductDesi.Maximum);
        Assert.Equal(ProductDesi.Maximum, BulkProductFields.MaxDesi);
        Assert.Equal(ProductDesi.Maximum, ProductTransferFileParser.MaximumDesi);
        Assert.Equal(ProductDesi.Maximum, ImportedDesi.Maximum);
        Assert.NotNull(BulkProductFields.Parse(BulkProductFields.Desi, "9999").Value);
        Assert.Null(BulkProductFields.Parse(BulkProductFields.Desi, "9999,5").Value);
        Assert.Equal(ProductTransferValueKind.Set, ProductTransferFileParser.ParseDesi("9999", fromCsv: true).Kind);
        Assert.Contains("9.999", ProductTransferFileParser.ParseDesi("10000", fromCsv: true).Error);
        Assert.Null(ImportedDesi.From(10000m));
    }

    [Fact]
    public void TheClassicImport_CreatesAProductMissingARequiredField_AsADraft()
    {
        Dictionary<string, string> Row(string sku, string? desi = "2", string? barcode = null) => new()
        {
            ["title"] = "Bluz",
            ["sku"] = sku,
            ["barcode"] = barcode ?? $"{sku}B",
            ["modelCode"] = "MOD-1",
            ["desi"] = desi ?? "",
            ["listPrice"] = "120",
            ["salePrice"] = "100",
            ["vatRate"] = "10"
        };
        IReadOnlyList<IReadOnlyDictionary<string, string>> complete = [Row("A-1"), Row("A-2")];
        Assert.False(ImportJobProcessor.CreatesDraft(complete, brandUsable: true, Guid.CreateVersion7()));
        // No usable brand (none named, or the named one switched off), no leaf category, or one row without a desi.
        Assert.True(ImportJobProcessor.CreatesDraft(complete, brandUsable: false, Guid.CreateVersion7()));
        Assert.True(ImportJobProcessor.CreatesDraft(complete, brandUsable: true, null));
        Assert.True(ImportJobProcessor.CreatesDraft([Row("A-1"), Row("A-2", desi: "")], brandUsable: true, Guid.CreateVersion7()));
        Assert.True(ImportJobProcessor.CreatesDraft([new Dictionary<string, string>(Row("A-1")) { ["salePrice"] = "0" }], brandUsable: true, Guid.CreateVersion7()));
        // A model whose rows name no KDV is a draft; one row naming it is enough (the card takes it from that row).
        Assert.True(ImportJobProcessor.CreatesDraft([new Dictionary<string, string>(Row("A-1")) { ["vatRate"] = "" }, new Dictionary<string, string>(Row("A-2")) { ["vatRate"] = "" }], brandUsable: true, Guid.CreateVersion7()));
        Assert.False(ImportJobProcessor.CreatesDraft([new Dictionary<string, string>(Row("A-1")) { ["vatRate"] = "" }, Row("A-2")], brandUsable: true, Guid.CreateVersion7()));
    }

    [Fact]
    public void TheExcelImport_RefusesToEmptyTheDesiOrModelCodeOfACardThatIsNotADraft()
    {
        ProductTransferCandidate Candidate(string sku, bool draft) =>
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, sku, null, "Bluz", null, null, 2m, ModelCode: "MOD-1", ProductDraft: draft);
        var onSale = Candidate("S-1", draft: false);
        var draft = Candidate("D-1", draft: true);
        ProductTransferFileRow Clear(int number, string sku) => new(number, sku, null, ProductTransferValue.Empty, ProductTransferValue.Clear, ModelCode: ProductTransferValue.Clear);

        var preview = ProductTransferImportPlanner.Plan("f.xlsx", [Clear(2, "S-1"), Clear(3, "D-1")], [onSale, draft], [ProductTransferFields.Desi, ProductTransferFields.ModelCode]);

        var refused = preview.Rows.Single(row => row.RowNumber == 2);
        Assert.Equal(("ERROR", "Desi zorunlu alandır; taslak olmayan üründe silinemez."), (refused.Outcome, refused.Message));
        var change = Assert.Single(preview.Changes);
        Assert.Equal((draft.VariantId, true, (decimal?)null, true, (string?)null), (change.VariantId, change.SetDesi, change.Desi, change.SetModelCode, change.ModelCode));

        var modelOnly = ProductTransferImportPlanner.Plan("f.xlsx", [new ProductTransferFileRow(2, "S-1", null, ProductTransferValue.Empty, ProductTransferValue.Empty, ModelCode: ProductTransferValue.Clear)], [onSale], [ProductTransferFields.ModelCode]);
        Assert.Equal("Model Kodu zorunlu alandır; taslak olmayan üründe silinemez.", Assert.Single(modelOnly.Rows).Message);
    }

    [Fact]
    public void TheExcelBrandColumn_IsReadAsTheModelsBrand()
    {
        var csv = "Stok Kodu;Marka\r\nA-1; Ravencia \r\nA-2;sil\r\nA-3;\r\n";
        var file = ProductTransferFileParser.ParseCsv(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)));

        Assert.Equal([ProductTransferFields.Brand], file.AvailableFields);
        Assert.Equal(("Ravencia", ProductTransferValueKind.Set), (file.Rows[0].Brand.Text, file.Rows[0].Brand.Kind));
        // A brand cannot be removed; an empty cell changes nothing.
        Assert.Equal("Marka silinemez; hücreyi boş bırakırsanız değişmez.", file.Rows[1].Brand.Error);
        Assert.Equal(ProductTransferValueKind.Empty, file.Rows[2].Brand.Kind);
    }

    [Fact]
    public void TheExcelBrand_ChangesByName_RefusesTwoBrandsInOneModel_AndASwitchedOffBrand()
    {
        ProductTransferCandidate Card(string sku, string modelCode, string? brand) =>
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, sku, null, $"Ürün {sku}", null, null, 1m, ProductVersion: 4, ModelCode: modelCode, BrandName: brand);
        var siyah = Card("S-1", "MOD-1", "Eski Marka");
        var bordo = Card("B-1", "mod 1", "Eski Marka");
        var same = Card("X-1", "MOD-2", "Gümüş Moda");
        var other = Card("O-1", "MOD-3", null);
        var mixedA = Card("M-1", "MOD-4", null);
        var mixedB = Card("M-2", "MOD-4", null);
        var closed = Card("C-1", "MOD-5", null);
        ProductTransferFileRow Row(int number, string sku, string brand) => new(number, sku, null, ProductTransferValue.Empty, ProductTransferValue.Empty, Brand: ProductTransferValue.SetText(brand));

        var preview = ProductTransferImportPlanner.Plan("f.xlsx",
            [Row(2, "S-1", "Ravencia"), Row(3, "B-1", "RAVENCİA"), Row(4, "X-1", "gumus moda"), Row(5, "O-1", "Yeni"), Row(6, "M-1", "Bir"), Row(7, "M-2", "İki"), Row(8, "C-1", "Kapalı Marka")],
            [siyah, bordo, same, other, mixedA, mixedB, closed], [ProductTransferFields.Brand], switchedOffBrandKeys: [PanelBrandByName.Key("kapali marka")]);

        // Two colours of one model naming one brand (the same key) both change.
        Assert.Equal(new[] { (siyah.ProductId, "Ravencia"), (bordo.ProductId, "RAVENCİA"), (other.ProductId, "Yeni") }.OrderBy(item => item.Item1),
            preview.ProductChanges!.Select(change => (change.ProductId, change.Brand!)).OrderBy(item => item.Item1));
        Assert.Equal(4L, preview.ProductChanges!.First().Version);
        Assert.Equal(3, preview.BrandUpdates);
        Assert.Equal(("UNCHANGED", null), (preview.Rows.Single(row => row.RowNumber == 4).Outcome, preview.Rows.Single(row => row.RowNumber == 4).BrandChange));
        Assert.Equal(("Eski Marka", "Ravencia", "SET"), (preview.Rows.Single(row => row.RowNumber == 2).OldBrand, preview.Rows.Single(row => row.RowNumber == 2).NewBrand, preview.Rows.Single(row => row.RowNumber == 2).BrandChange));
        Assert.All(preview.Rows.Where(row => row.RowNumber is 6 or 7), row => Assert.Contains("Aynı modelin satırlarında farklı marka var", row.Message));
        Assert.Contains("markası panelde kapalı", preview.Rows.Single(row => row.RowNumber == 8).Message);
        Assert.Empty(preview.Changes);
    }

    [Fact]
    public void TheSharedBrandRule_KeysNamesAlike_AndPicksActive_ThenTheCurrentKey_ThenTheOldestId()
    {
        Assert.Equal(PanelBrandByName.Key("Gümüş Moda"), PanelBrandByName.Key(" GUMUS-MODA "));
        Assert.Equal(PanelBrandByName.Key("RAVENCİA"), PanelBrandByName.Key("ravencia"));
        Assert.Equal("", PanelBrandByName.Key("  "));
        Assert.Equal(160, PanelBrandByName.PanelName(new string('a', 200)).Length);

        var key = PanelBrandByName.Key("Kırmızı Ev");
        var older = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var newer = Guid.Parse("00000000-0000-0000-0000-000000000002");
        (Guid Id, string NormalizedName, bool IsActive)[] stored =
        [
            (newer, key, true),
            (older, CatalogKeyNormalizer.Legacy("Kırmızı Ev"), true),
            (Guid.Parse("00000000-0000-0000-0000-000000000000"), key, false),
            (Guid.CreateVersion7(), PanelBrandByName.Key("Başka"), true)
        ];
        (Guid, string, bool)? Pick(IEnumerable<(Guid Id, string NormalizedName, bool IsActive)> brands) =>
            PanelBrandByName.Pick(brands.Select(brand => new Tuple<Guid, string, bool>(brand.Id, brand.NormalizedName, brand.IsActive)), key, brand => brand.Item2, brand => brand.Item3, brand => brand.Item1) is { } picked ? (picked.Item1, picked.Item2, picked.Item3) : null;

        // Active before switched off, the current key form before a legacy one.
        Assert.Equal(newer, Pick(stored)!.Value.Item1);
        // Between two active brands of the same key form, the oldest id.
        Assert.Equal(older, Pick([(newer, key, true), (older, key, true)])!.Value.Item1);
        // Only a switched-off brand: it is the one named (and the caller does not revive it).
        Assert.False(Pick([stored[2]])!.Value.Item3);
        Assert.Null(Pick([stored[3]]));
    }
}
