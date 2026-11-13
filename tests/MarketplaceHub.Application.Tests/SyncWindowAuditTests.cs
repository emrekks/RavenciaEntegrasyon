using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The date window, paging and cursor audit of 6 Nov 2026: each case is a read that could loop forever, skip rows or
// call a transient answer final.
public sealed class SyncWindowAuditTests
{
    private static RemoteSettlementDay Day(DateOnly day) => new(day, day.AddDays(1), "ODEME_BEKLENIYOR", 10, 1, 9);

    [Fact]
    public void N11_recent_and_coming_days_never_fill_the_slice_of_older_unread_days()
    {
        var today = new DateOnly(2026, 11, 6);
        var recent = today.AddDays(-7);
        // Twelve recent and coming settlement days (each read before) and three older days never read.
        var days = Enumerable.Range(-6, 12).Select(offset => Day(today.AddDays(offset)))
            .Concat([Day(today.AddDays(-20)), Day(today.AddDays(-21)), Day(today.AddDays(-22))]).ToList();
        var read = days.Select(x => x.SettlementDate!.Value.ToString("yyyyMMdd")).Where(key => string.CompareOrdinal(key, recent.ToString("yyyyMMdd")) >= 0).ToHashSet();

        var plan = N11SettlementSyncJobHandler.PlanDays(days, read, recent, perRun: 10);

        Assert.Equal(12, plan.Recent.Count);
        Assert.Equal([today.AddDays(-20), today.AddDays(-21), today.AddDays(-22)], plan.Older);
        Assert.False(plan.MoreOlder);
    }

    [Fact]
    public void N11_older_unread_days_go_perRun_a_slice_and_a_read_one_is_not_read_again()
    {
        var today = new DateOnly(2026, 11, 6);
        var recent = today.AddDays(-7);
        var days = Enumerable.Range(10, 15).Select(back => Day(today.AddDays(-back))).ToList();
        var read = new HashSet<string> { today.AddDays(-10).ToString("yyyyMMdd") };

        var plan = N11SettlementSyncJobHandler.PlanDays(days, read, recent, perRun: 10);

        Assert.Empty(plan.Recent);
        Assert.Equal(10, plan.Older.Count);
        Assert.DoesNotContain(today.AddDays(-10), plan.Older);
        Assert.Equal(today.AddDays(-11), plan.Older[0]);
        Assert.True(plan.MoreOlder);
    }

    [Fact]
    public void Pazarama_finance_reads_unread_orders_first_then_the_one_refreshed_longest_ago()
    {
        var now = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        var orders = Enumerable.Range(1, 5).Select(n => ($"O{n}", now.AddDays(-n))).ToList();
        // O1 (newest) and O2 refreshed just now, O4 a day ago, O5 a week ago; O3 never read.
        var refreshed = new Dictionary<string, DateTimeOffset> { ["O1"] = now, ["O2"] = now, ["O4"] = now.AddDays(-1), ["O5"] = now.AddDays(-7) };

        var due = PazaramaFinanceSyncJobHandler.Due(orders, refreshed, perRun: 3);

        Assert.Equal(["O3", "O5", "O4"], due.Select(x => x.OrderNumber));
    }

    // Orders Pazarama answers with no movement yet (not delivered) stay unread: they never fill a whole run, and runs
    // move through them block by block, so the stored ones are refreshed and every unread one is asked again.
    [Fact]
    public void Pazarama_finance_unread_orders_never_starve_the_stored_ones_and_rotate()
    {
        var now = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        var unread = Enumerable.Range(1, 10).Select(n => ($"U{n}", now.AddHours(-n))).ToList();
        var stored = Enumerable.Range(1, 3).Select(n => ($"S{n}", now.AddDays(-n))).ToList();
        var refreshed = stored.ToDictionary(x => x.Item1, x => now.AddDays(-1));

        var runs = Enumerable.Range(0, 3).Select(run => PazaramaFinanceSyncJobHandler.Due([.. unread, .. stored], refreshed, perRun: 4, rotation: run).Select(x => x.OrderNumber).ToList()).ToList();

        Assert.All(runs, run => Assert.Equal(2, run.Count(number => number.StartsWith('S'))));
        Assert.Equal(["U1", "U2"], runs[0].Where(number => number.StartsWith('U')));
        Assert.Equal(["U3", "U4"], runs[1].Where(number => number.StartsWith('U')));
        Assert.Equal(["U5", "U6"], runs[2].Where(number => number.StartsWith('U')));
        // Alone, unread orders take the whole run.
        Assert.Equal(4, PazaramaFinanceSyncJobHandler.Due(unread, new Dictionary<string, DateTimeOffset>(), perRun: 4).Count);
    }

    [Theory]
    [InlineData(AdapterErrorClass.NotFound, true)]
    [InlineData(AdapterErrorClass.Validation, true)]
    [InlineData(AdapterErrorClass.ContractViolation, true)]
    [InlineData(AdapterErrorClass.RateLimit, false)]
    [InlineData(AdapterErrorClass.Remote5xx, false)]
    [InlineData(AdapterErrorClass.TransientNetwork, false)]
    [InlineData(AdapterErrorClass.Authentication, false)]
    public void Pazarama_finance_goes_on_past_a_refusal_of_one_order_only(AdapterErrorClass errorClass, bool orderOnly) =>
        Assert.Equal(orderOnly, PazaramaFinanceSyncJobHandler.OrderOnly(new(errorClass, "X", "x", null, null, null)));

    [Fact]
    public void Pazarama_try_later_envelope_is_retried_not_refused()
    {
        using var busy = JsonDocument.Parse("""{"data":null,"success":false,"messageCode":null,"message":"İşleminiz şu anda gerçekleştirilemiyor. Lütfen daha sonra tekrar deneyiniz.","userMessage":"İşleminiz şu anda gerçekleştirilemiyor. Lütfen daha sonra tekrar deneyiniz."}""");

        var error = PazaramaHttpClient.EnvelopeError(busy.RootElement)!;

        Assert.Equal(AdapterErrorClass.Remote5xx, error.Class);
        Assert.Equal("PAZARAMA_REMOTE_BUSY", error.Code);
        Assert.NotNull(error.RetryAfter);
    }

    [Fact]
    public void A_full_catalog_read_leaves_the_watermark_at_its_start_and_a_scoped_one_leaves_it_alone()
    {
        var started = new DateTimeOffset(2026, 10, 31, 21, 30, 22, TimeSpan.Zero);
        var previous = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(started.AddSeconds(-60), ProductImportScanPolicy.CompletedWatermark(started, previous, scoped: false));
        Assert.Equal(previous, ProductImportScanPolicy.CompletedWatermark(started, previous, scoped: true));
        Assert.Null(ProductImportScanPolicy.CompletedWatermark(started, null, scoped: true));
    }

    [Theory]
    [InlineData("0", "s:Accepted=1", "s:Accepted=1")]
    [InlineData("s:Accepted=1,Created=1", "s:Accepted=2", "s:Accepted=2")]
    [InlineData("0", null, "1")]
    [InlineData("4", "", "5")]
    public void Return_reconciliation_follows_the_adapters_bucket_cursor(string current, string? next, string expected) =>
        Assert.Equal(expected, MarketplaceJobProcessor.NextReconcilePage(current, next));

    [Fact]
    public void A_rejected_quiet_storefront_is_read_from_its_last_good_read_but_not_tried_again_at_once()
    {
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var codes = new[] { "TR", "AE" };
        var lastGood = now.AddHours(-2);
        // AE sold 10 days ago (active, quiet) and was last read two hours ago; this read was answered 400.
        var rejected = TrendyolStorefrontPolicy.Rejected("AE", new(now.AddDays(-10), lastGood), now);
        var activity = new Dictionary<string, TrendyolStorefrontActivity> { ["AE"] = rejected };

        Assert.Equal(lastGood, rejected.LastProbedAt);
        Assert.Null(rejected.PausedUntil);
        Assert.Empty(TrendyolStorefrontPolicy.DueQuiet(codes, activity, now.AddMinutes(5)));
        Assert.Equal(["AE"], TrendyolStorefrontPolicy.DueQuiet(codes, activity, now.AddMinutes(31)));
        Assert.Equal(lastGood - TimeSpan.FromMinutes(2), TrendyolStorefrontPolicy.ProbeStart(rejected, now.AddMinutes(31), TimeSpan.FromMinutes(2)));
        // A good read afterwards clears the rejection.
        Assert.Null(TrendyolStorefrontPolicy.Read(rejected, now.AddMinutes(31), receivedOrders: false).LastRejectedAt);
    }

    [Fact]
    public void A_never_read_listed_storefront_that_refuses_waits_its_probe_interval()
    {
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var activity = new Dictionary<string, TrendyolStorefrontActivity> { ["DE"] = TrendyolStorefrontPolicy.Rejected("DE", null, now) };

        Assert.Null(TrendyolStorefrontPolicy.NextProbe(["TR", "DE"], activity, now.AddHours(1)));
        Assert.Equal("DE", TrendyolStorefrontPolicy.NextProbe(["TR", "DE"], activity, now.AddHours(7)));
    }

    [Fact]
    public void A_Trendyol_product_page_that_counts_products_but_sends_no_list_is_a_broken_contract()
    {
        Assert.Throws<JsonException>(() => MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.CatalogProducts("""{"totalElements":120,"totalPages":2,"page":1,"size":100}"""));
        Assert.Throws<JsonException>(() => MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.CatalogProducts("""{"content":{},"totalElements":0}"""));
        Assert.Empty(MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.CatalogProducts("""{"totalElements":0,"totalPages":0,"page":0,"size":100}""").Items);
        Assert.Empty(MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.CatalogProducts("""{"content":null,"page":0,"size":100}""").Items);
    }

    [Fact]
    public void A_Trendyol_finance_page_that_counts_records_but_sends_no_list_is_a_broken_contract()
    {
        Assert.Throws<JsonException>(() => MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolFinanceMapper.Page("""{"page":0,"totalPages":1,"totalElements":3}""", "SETTLEMENTS", "Sale", null));
        Assert.Empty(MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolFinanceMapper.Page("""{"page":0,"totalPages":0,"totalElements":0}""", "SETTLEMENTS", "Sale", null).Items);
        Assert.Empty(MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolFinanceMapper.Page("""{"content":null}""", "SETTLEMENTS", "Sale", null).Items);
    }

    [Fact]
    public void Pazarama_recovery_reads_a_week_back_and_the_hot_read_two_days()
    {
        var now = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        var watermark = now.AddMinutes(-15);

        Assert.Equal(new DateTime(2026, 10, 30), PazaramaOrderHistoryPolicy.StartDay(false, watermark, now, PazaramaOrderHistoryPolicy.LookbackDays("ORDERS_RECOVERY")));
        Assert.Equal(new DateTime(2026, 11, 4), PazaramaOrderHistoryPolicy.StartDay(false, watermark, now, PazaramaOrderHistoryPolicy.LookbackDays("ORDERS_HOT")));
    }

    private static AdapterPageResult<RemoteOrder> PaidPage(int? total, string? next) => new([], next, next is not null, total);

    [Fact]
    public void Hepsiburada_paid_order_window_is_read_again_when_its_list_moves_between_pages()
    {
        var start = new MarketplaceJobProcessor.HepsiburadaOrderSyncState("v", DateTimeOffset.UnixEpoch.AddDays(2), DateTimeOffset.UnixEpoch, 0, 0);

        // 101 lines: page one, then one line is packaged and page two (offset 100) counts 100.
        var second = MarketplaceJobProcessor.NextHepsiburadaOrderPage(start, PaidPage(101, "100"));
        Assert.Equal((100, 101), (second.Offset, second.PageTotal));
        var reread = MarketplaceJobProcessor.NextHepsiburadaOrderPage(second, PaidPage(100, null));
        Assert.Equal((0, 0, 1, false), (reread.WindowIndex, reread.Offset, reread.Rereads, reread.Moved));

        // A steady list closes the window at once.
        var steady = MarketplaceJobProcessor.NextHepsiburadaOrderPage(MarketplaceJobProcessor.NextHepsiburadaOrderPage(start, PaidPage(101, "100")), PaidPage(101, null));
        Assert.Equal((1, 0, 0), (steady.WindowIndex, steady.Offset, steady.Rereads));

        // After two re-reads the window closes even if the list keeps moving.
        var tired = start with { Offset = 100, PageTotal = 101, Rereads = MarketplaceJobProcessor.HepsiburadaOrderWindowRereads };
        Assert.Equal(1, MarketplaceJobProcessor.NextHepsiburadaOrderPage(tired, PaidPage(99, null)).WindowIndex);
    }

    [Fact]
    public void Hepsiburada_open_package_list_that_ignores_the_offset_is_told_apart()
    {
        Assert.False(MarketplaceJobProcessor.HepsiburadaPackageOffsetIgnored(0, null, "P1,P2"));
        Assert.True(MarketplaceJobProcessor.HepsiburadaPackageOffsetIgnored(10, "P1,P2", "P1,P2"));
        Assert.False(MarketplaceJobProcessor.HepsiburadaPackageOffsetIgnored(10, "P1,P2", "P3,P4"));
        Assert.False(MarketplaceJobProcessor.HepsiburadaPackageOffsetIgnored(10, "P1,P2", null));
        Assert.Null(MarketplaceJobProcessor.HepsiburadaPackagePageKey([]));
    }

    private static RemoteCatalogProduct PttModel(bool active, params string[] names)
    {
        System.Xml.Linq.XNamespace a = "http://schemas.datacontract.org/2004/07/ePttAVMService";
        System.Xml.Linq.XNamespace n = "http://schemas.datacontract.org/2004/07/ePttAVMService.New";
        var row = new System.Xml.Linq.XElement(a + "StokKontrolDetay", new System.Xml.Linq.XElement(a + "Aktif", active ? "true" : "false"),
            new System.Xml.Linq.XElement(a + "Barkod", "FK4530"), new System.Xml.Linq.XElement(a + "KDVli", "500"), new System.Xml.Linq.XElement(a + "UrunAdi", "Etek"),
            new System.Xml.Linq.XElement(a + "UrunId", "900001"));
        var entries = names.Select((name, index) => new System.Xml.Linq.XElement(n + "Variant", new System.Xml.Linq.XElement(n + "Quantity", "1"),
            new System.Xml.Linq.XElement(n + "VariantId", (7100 + index).ToString()), new System.Xml.Linq.XElement(n + "VariantName", name))).ToList();
        return MarketplaceHub.Infrastructure.Adapters.PttAvm.PttAvmProductMapper.Product(row, [], new Dictionary<string, IReadOnlyList<System.Xml.Linq.XElement>> { ["FK4530"] = entries })!;
    }

    [Fact]
    public void Ptt_model_variants_count_as_seen_when_the_model_lists_their_colour_and_size()
    {
        var product = PttModel(true, "Bordo", "S", "M");
        var listed = Guid.NewGuid();
        var sizeMissing = Guid.NewGuid();
        var otherModel = Guid.NewGuid();
        var panel = new (Guid, string?, string?)[]
        {
            (listed, "FK4530", "Renk: Bordo | Beden: S"),
            (sizeMissing, "FK4530", "Renk: Bordo | Beden: XL"),
            (otherModel, "FK9999", "Renk: Bordo | Beden: S")
        };

        Assert.Equal([listed], MarketplaceJobProcessor.PttAvmModelVariantsSeen(product, panel));
        Assert.Equal(MarketplaceHub.Domain.ListingSaleState.OnSale, MarketplaceJobProcessor.PttAvmModelSaleState(product).State);
        Assert.Equal(MarketplaceHub.Domain.ListingSaleState.OffSale, MarketplaceJobProcessor.PttAvmModelSaleState(PttModel(false, "Bordo", "S")).State);
        // A model that lists no values says nothing about its variants.
        Assert.Empty(MarketplaceJobProcessor.PttAvmModelVariantsSeen(product with { Variants = [] }, panel));
    }

    [Fact]
    public void Pazarama_approved_page_productStatus_values_are_counted()
    {
        RemoteCatalogVariant Variant(string raw) => new("v", "s", "b", null, new Dictionary<string, string>(), false, null, null, null, null, "TRY", raw);
        var product = new RemoteCatalogProduct("p", null, "t", "", null, null, null, null, [], [Variant("""{"productStatus":0}"""), Variant("""{"productStatus":0}"""), Variant("""{"code":"x"}"""), Variant("""{"productStatus":10}""")], "{}");

        Assert.Equal("0:2, none:1, 10:1", PazaramaProductMapper.ProductStatusSummary([product]));
    }

    [Fact]
    public void Hepsiburada_missing_invoice_pass_stops_when_the_list_moves_between_pages()
    {
        int? total = null;
        Assert.True(HepsiburadaMissingInvoiceStatusPolicy.SameTotal(ref total, 60, 0));
        Assert.True(HepsiburadaMissingInvoiceStatusPolicy.SameTotal(ref total, 60, 1));
        // An invoice uploaded while the list was read: 59 left, so one package moved up and was never seen.
        Assert.False(HepsiburadaMissingInvoiceStatusPolicy.SameTotal(ref total, 59, 2));

        int? none = null;
        Assert.True(HepsiburadaMissingInvoiceStatusPolicy.SameTotal(ref none, null, 0));
        Assert.True(HepsiburadaMissingInvoiceStatusPolicy.SameTotal(ref none, null, 1));
        Assert.False(HepsiburadaMissingInvoiceStatusPolicy.SameTotal(ref none, 40, 2));
    }
}
