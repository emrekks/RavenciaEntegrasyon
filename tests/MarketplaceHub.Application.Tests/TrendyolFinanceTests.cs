using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolFinanceTests
{
    private const string Settlements = TrendyolFinanceRequestContract.Settlements;
    private const string Others = TrendyolFinanceRequestContract.OtherFinancials;

    [Theory]
    [InlineData(Settlements, "Sale", null, "Satış", null, MarketplaceFinanceCategories.Sale)]
    [InlineData(Settlements, "Return", null, "İade", null, MarketplaceFinanceCategories.Return)]
    [InlineData(Settlements, "ManuelRefund", null, null, null, MarketplaceFinanceCategories.Return)]
    [InlineData(Settlements, "TYDiscountCancel", null, null, null, MarketplaceFinanceCategories.Discount)]
    [InlineData(Settlements, "CouponCancel", null, null, null, MarketplaceFinanceCategories.Coupon)]
    [InlineData(Settlements, "CommissionNegativeCancel", null, null, null, MarketplaceFinanceCategories.Commission)]
    [InlineData(Settlements, "ProvisionPositive", null, null, null, MarketplaceFinanceCategories.Other)]
    [InlineData(Settlements, "SellerRevenuePositive", null, null, null, MarketplaceFinanceCategories.Other)]
    [InlineData(Others, "Stoppage", null, "E-Ticaret Stopajı", null, MarketplaceFinanceCategories.Stoppage)]
    [InlineData(Others, "CommissionAgreementInvoice", null, null, null, MarketplaceFinanceCategories.Commission)]
    [InlineData(Others, "DeductionInvoices", null, "Kargo Faturası", null, MarketplaceFinanceCategories.Cargo)]
    [InlineData(Others, "DeductionInvoices", null, "KARGO FATURA", null, MarketplaceFinanceCategories.Cargo)]
    [InlineData(Others, "DeductionInvoices", "PlatformServiceFee", "Platform Hizmet Bedeli", null, MarketplaceFinanceCategories.ServiceFee)]
    [InlineData(Others, "DeductionInvoices", null, "Platform Hizmet Bedeli", null, MarketplaceFinanceCategories.ServiceFee)]
    [InlineData(Others, "DeductionInvoices", null, "Erken Ödeme Hizmet Bedeli", null, MarketplaceFinanceCategories.EarlyPayment)]
    [InlineData(Others, "DeductionInvoices", null, "Reklam Hizmet Bedeli", null, MarketplaceFinanceCategories.Advertising)]
    [InlineData(Others, "DeductionInvoices", null, "Fatura", "Geç teslimat CEZASI", MarketplaceFinanceCategories.Penalty)]
    // A deduction invoice no keyword names is still a charge to the seller (live: "Yurtdışı Operasyon Iade Bedeli").
    [InlineData(Others, "DeductionInvoices", null, "Yurtdışı Operasyon Iade Bedeli", null, MarketplaceFinanceCategories.OtherDeduction)]
    [InlineData(Others, "FinancialItem", null, "Düzeltme", null, MarketplaceFinanceCategories.Other)]
    [InlineData(Others, "SomethingNew", null, null, null, MarketplaceFinanceCategories.Other)]
    public void A_record_is_sorted_by_its_type_or_its_words_and_an_unknown_one_is_other(string source, string type, string? subType, string? raw, string? description, string expected) =>
        Assert.Equal(expected, TrendyolFinanceCategorizer.Categorize(source, type, subType, raw, description));

    [Theory]
    [InlineData(Others, "DeductionInvoices", "OTHER", 379.32, 0, "OTHER_DEDUCTION")]
    [InlineData(Others, "FinancialItem", "OTHER", 10, 0, "OTHER_DEDUCTION")]
    [InlineData(Others, "FinancialItem", "OTHER", 0, 3, "OTHER")]
    [InlineData(Others, "FinancialItem", "OTHER", 5, 5, "OTHER")]
    [InlineData(Others, "ReturnInvoice", "OTHER", 0, 664.07, "OTHER")]
    [InlineData(Settlements, "ProvisionPositive", "OTHER", 7, 0, "OTHER")]
    [InlineData(Others, "DeductionInvoices", "CARGO", 34.24, 0, "CARGO")]
    public void An_unrecognised_charge_counts_as_another_deduction_and_other_stays_for_the_rest(string source, string type, string stored, double debt, double credit, string expected) =>
        Assert.Equal(expected, TrendyolFinanceCategorizer.Charged(source, type, stored, (decimal)debt, (decimal)credit));

    [Fact]
    public void The_cargo_estimate_is_the_tariff_most_recent_parcels_were_billed_at_not_their_mean()
    {
        // 23 Oct 2026: most parcels at 98.34, a few heavier ones at 106.76 and one order with two parcels.
        var sample = Assert.IsType<ReportCargoSample>(ReportCargoSample.Tariff(
            [(98.34m, "A"), (98.34m, "B"), (98.34m, "C"), (106.76m, "D"), (106.76m, "E"), (98.34m, "E")]));

        Assert.Equal(98.34m, sample.PerOrder);
        Assert.Equal(5, sample.Orders);
        Assert.Equal(106.76m, ReportCargoSample.Tariff([(98.34m, "A"), (106.76m, "B")])!.PerOrder);
        Assert.Null(ReportCargoSample.Tariff([]));
    }

    [Fact]
    public void Another_deduction_is_a_cost_and_a_row_stored_as_other_moves_to_it_when_read_again()
    {
        Assert.Contains(MarketplaceFinanceCategories.OtherDeduction, MarketplaceFinanceCategories.Costs);
        Assert.Contains(MarketplaceFinanceCategories.OtherDeduction, MarketplaceFinanceCategories.All);
        Assert.DoesNotContain(MarketplaceFinanceCategories.Other, MarketplaceFinanceCategories.Costs);

        var invoice = Record("910", Others, "DeductionInvoices", type: "Yurtdışı Operasyon Iade Bedeli", debt: 379.32m);
        var tenant = Guid.NewGuid();
        var (added, _) = TrendyolFinanceSyncPlan.Merge(new Dictionary<(string, string), MarketplaceFinanceTransaction>(), [invoice], tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(MarketplaceFinanceCategories.OtherDeduction, Assert.Single(added).Category);

        // A row stored as OTHER before the rule is put right by the next overlapping read.
        var stored = added.ToDictionary(row => (row.Source, row.ExternalId));
        stored[(Others, "910")].Category = MarketplaceFinanceCategories.Other;
        var (_, updated) = TrendyolFinanceSyncPlan.Merge(stored, [invoice], tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(1, updated);
        Assert.Equal(MarketplaceFinanceCategories.OtherDeduction, stored[(Others, "910")].Category);
    }

    [Fact]
    public void The_report_counts_a_stored_other_deduction_invoice_as_a_cost_and_leaves_other_out()
    {
        var (input, _, _, store) = ReportFixture();
        var finance = new ReportFinance(
            [
                Row(store, Others, "1", TrendyolFinanceCategorizer.DeductionInvoices, MarketplaceFinanceCategories.ServiceFee, null, null, debt: 9.99m),
                // Stored as OTHER before the rule: an unrecognised deduction invoice and a financial item that takes money.
                Row(store, Others, "2", TrendyolFinanceCategorizer.DeductionInvoices, MarketplaceFinanceCategories.Other, null, null, debt: 379.32m),
                Row(store, Others, "3", TrendyolFinanceCategorizer.FinancialItem, MarketplaceFinanceCategories.Other, null, null, debt: 10m),
                // Stored as it is now.
                Row(store, Others, "4", TrendyolFinanceCategorizer.DeductionInvoices, MarketplaceFinanceCategories.OtherDeduction, null, null, debt: 0.68m),
                // A financial item that gives money back and a provision stay OTHER, out of the total.
                Row(store, Others, "5", TrendyolFinanceCategorizer.FinancialItem, MarketplaceFinanceCategories.Other, null, null, credit: 3m),
                Row(store, Settlements, "6", "ProvisionPositive", MarketplaceFinanceCategories.Other, null, null, debt: 7m)
            ],
            [],
            new HashSet<(Guid, string)>(),
            new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = new() { TenantId = Guid.NewGuid(), ConnectionId = store, LastSyncedAt = input.Period.End } });
        var costs = ReportBuilder.Build(input with { Finance = finance }, Empty(input), new Dictionary<Guid, decimal>()).Costs;

        var deduction = costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.OtherDeduction);
        Assert.Equal("Diğer kesinti", deduction.Label);
        Assert.True(deduction.IsCost);
        Assert.Equal(390m, deduction.Amount);
        Assert.Equal(3, deduction.Records);
        var other = costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.Other);
        Assert.False(other.IsCost);
        Assert.Equal(4m, other.Amount);
        // Neither order is settled yet: each adds the estimated platform fee and 1 % withholding tax on its sale.
        Assert.Equal(9.99m + 390m + 2 * 13.19m + 1.5m, costs.RealCostTotal);
        var channel = Assert.Single(costs.Channels);
        Assert.Equal(390m, channel.Amounts.Single(amount => amount.Category == MarketplaceFinanceCategories.OtherDeduction).Amount);
    }

    [Theory]
    [InlineData("Sale", 12.5, 0, 0, 12.5)]
    [InlineData("Sale", -12.5, 0, 0, 12.5)]
    [InlineData("Return", 12.5, 0, 0, -12.5)]
    [InlineData("Discount", 3, 0, 0, -3)]
    [InlineData("CouponCancel", 2, 0, 0, 2)]
    [InlineData("CommissionPositive", 99, 10, 0, 10)]
    [InlineData("CommissionNegative", 99, 0, 4, -4)]
    [InlineData("SellerRevenuePositive", 99, 0, 0, 0)]
    public void The_commission_of_a_record_follows_the_documented_sign(string type, double amount, double debt, double credit, double expected) =>
        Assert.Equal((decimal)expected, TrendyolFinanceCategorizer.CommissionCost(type, (decimal)amount, (decimal)debt, (decimal)credit));

    [Fact]
    public void Turkish_words_fold_to_plain_lower_case()
    {
        Assert.Equal("kargo faturasi", TrendyolFinanceCategorizer.Fold(" Kargo  Faturası "));
        Assert.Equal("erken odeme", TrendyolFinanceCategorizer.Fold("ERKEN ÖDEME"));
        Assert.Equal("", TrendyolFinanceCategorizer.Fold(null));
    }

    [Fact]
    public void A_range_is_cut_into_windows_of_at_most_fifteen_days()
    {
        var start = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
        var windows = TrendyolFinanceSyncPlan.Windows(start, start.AddDays(90), TimeSpan.FromDays(15));
        Assert.Equal(6, windows.Count);
        Assert.All(windows, window => Assert.True(window.End - window.Start <= TimeSpan.FromDays(15)));
        Assert.Equal(start, windows[0].Start);
        Assert.Equal(start.AddDays(90), windows[^1].End);
        for (var index = 1; index < windows.Count; index++) Assert.Equal(windows[index - 1].End, windows[index].Start);

        var uneven = TrendyolFinanceSyncPlan.Windows(start, start.AddDays(16).AddHours(3), TimeSpan.FromDays(15));
        Assert.Equal(2, uneven.Count);
        Assert.Equal(TimeSpan.FromDays(1).Add(TimeSpan.FromHours(3)), uneven[1].End - uneven[1].Start);
        Assert.Empty(TrendyolFinanceSyncPlan.Windows(start, start, TimeSpan.FromDays(15)));
    }

    [Fact]
    public void A_first_run_goes_back_ninety_days_a_new_run_overlaps_and_a_run_in_progress_carries_on()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal((now.AddDays(-90), now), TrendyolFinanceSyncPlan.Range(null, null, null, now, 90, 3));

        // The last run finished at what it covered: the next one reads three days of it again.
        var finished = now.AddDays(-1);
        Assert.Equal((finished.AddDays(-3), now), TrendyolFinanceSyncPlan.Range(now.AddDays(-90), finished, finished, now, 90, 3));
        // A run in progress (coverage past its last finished run) goes on from its last window.
        var through = now.AddDays(-60);
        Assert.Equal((through, now), TrendyolFinanceSyncPlan.Range(now.AddDays(-90), through, null, now, 90, 3));
        // The overlap never reaches before what was ever covered.
        var from = now.AddDays(-2);
        Assert.Equal((from, now), TrendyolFinanceSyncPlan.Range(from, now.AddDays(-1), now.AddDays(-1), now, 90, 3));
        Assert.Null(TrendyolFinanceSyncPlan.Range(now.AddDays(-90), now, null, now, 90, 3));
    }

    [Fact]
    public void A_record_read_again_updates_the_stored_one_and_an_unchanged_one_changes_nothing()
    {
        var tenant = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var sale = Record("725041340", Settlements, "Sale", orderNumber: "501915861", credit: 59.99m, commission: 7.5m);
        var fee = Record("900", Others, "DeductionInvoices", type: "Platform Hizmet Bedeli", debt: 12m);

        var (added, updated) = TrendyolFinanceSyncPlan.Merge(new Dictionary<(string, string), MarketplaceFinanceTransaction>(), [sale, fee], tenant, connection, now);
        Assert.Equal(2, added.Count);
        Assert.Equal(0, updated);
        var stored = added.ToDictionary(row => (row.Source, row.ExternalId));
        Assert.Equal(MarketplaceFinanceCategories.Sale, stored[(Settlements, "725041340")].Category);
        Assert.Equal(MarketplaceFinanceCategories.ServiceFee, stored[(Others, "900")].Category);

        // The same answer again: nothing added, nothing changed.
        var again = TrendyolFinanceSyncPlan.Merge(stored, [sale, fee], tenant, connection, now.AddDays(1));
        Assert.Empty(again.Added);
        Assert.Equal(0, again.Updated);
        Assert.Equal(now, stored[(Settlements, "725041340")].UpdatedAt);

        // The payment order filled in later updates the stored record in place.
        var paid = sale with { PaymentOrderId = "112360", RawJson = """{"id":"725041340","paymentOrderId":112360}""" };
        var later = TrendyolFinanceSyncPlan.Merge(stored, [paid], tenant, connection, now.AddDays(2));
        Assert.Empty(later.Added);
        Assert.Equal(1, later.Updated);
        Assert.Equal("112360", stored[(Settlements, "725041340")].PaymentOrderId);
    }

    [Fact]
    public void The_service_fee_mark_of_a_deduction_invoice_is_kept_when_it_is_read_again_without_the_filter()
    {
        var plain = Record("901", Others, "DeductionInvoices", type: "Hizmet Faturası", debt: 5m);
        var marked = plain with { TransactionSubType = TrendyolFinanceCategorizer.PlatformServiceFee };
        var collected = TrendyolFinanceSyncPlan.Collect([plain, marked, plain]);
        Assert.Single(collected);
        Assert.Equal(TrendyolFinanceCategorizer.PlatformServiceFee, collected[(Others, "901")].TransactionSubType);

        var tenant = Guid.NewGuid();
        var (added, _) = TrendyolFinanceSyncPlan.Merge(new Dictionary<(string, string), MarketplaceFinanceTransaction>(), [marked], tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var stored = added.ToDictionary(row => (row.Source, row.ExternalId));
        var (_, updated) = TrendyolFinanceSyncPlan.Merge(stored, [plain], tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(0, updated);
        Assert.Equal(MarketplaceFinanceCategories.ServiceFee, stored[(Others, "901")].Category);
    }

    [Fact]
    public void A_statement_page_is_read_with_string_or_number_ids_and_a_record_without_an_id_is_a_contract_error()
    {
        const string json = """
            {"page":0,"size":500,"totalPages":2,"totalElements":501,"content":[
              {"id":"725041340","transactionDate":1613397671561,"barcode":"8681385952874","transactionType":"Satış","description":"Satış","debt":0.0,"credit":59.99,
               "commissionRate":12.5,"commissionAmount":7.4988,"sellerRevenue":52.4912,"orderNumber":"501915861","paymentOrderId":112360,"paymentDate":1615989671561,"shipmentPackageId":1111111111},
              {"id":1639160,"transactionDate":1613062815995,"transactionType":"Kargo Faturası","debt":34.24,"credit":0.0,"orderNumber":null}
            ]}
            """;
        var page = TrendyolFinanceMapper.Page(json, Settlements, "Sale", null);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(2, page.Items.Count);
        var sale = page.Items[0];
        Assert.Equal("725041340", sale.ExternalId);
        Assert.Equal("501915861", sale.OrderNumber);
        Assert.Equal("1111111111", sale.ShipmentPackageId);
        Assert.Equal("112360", sale.PaymentOrderId);
        Assert.Equal(59.99m, sale.Credit);
        Assert.Equal(7.4988m, sale.CommissionAmount);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1613397671561), sale.TransactionDate);
        Assert.Equal("1639160", page.Items[1].ExternalId);
        Assert.Null(page.Items[1].OrderNumber);
        Assert.Contains("\"barcode\"", sale.RawJson);

        Assert.ThrowsAny<JsonException>(() => TrendyolFinanceMapper.Page("""{"content":[{"transactionDate":1613397671561}]}""", Settlements, "Sale", null));
        Assert.ThrowsAny<JsonException>(() => TrendyolFinanceMapper.Page("""{"content":[{"id":"1"}]}""", Settlements, "Sale", null));
        Assert.Empty(TrendyolFinanceMapper.Page("""{"page":0,"totalPages":0,"content":[]}""", Settlements, "Sale", null).Items);
    }

    [Fact]
    public void Cargo_invoice_lines_and_the_request_paths_follow_the_documentation()
    {
        var page = TrendyolFinanceMapper.CargoPage("""
            {"page":0,"size":500,"totalPages":1,"totalElements":2,"content":[
              {"shipmentPackageType":"Gönderi Kargo Bedeli","parcelUniqueId":7260001151141191,"orderNumber":"2111681160","amount":34.24,"desi":1},
              {"shipmentPackageType":"İade Kargo Bedeli","parcelUniqueId":7265609146531138,"orderNumber":"2111161312","amount":34.24,"desi":1}]}
            """);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("7260001151141191", page.Items[0].ParcelUniqueId);
        Assert.Equal(34.24m, page.Items[1].Amount);
        Assert.Equal(1, page.Items[1].Desi);

        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        var path = TrendyolFinanceRequestContract.Path(Others, "123456", "DeductionInvoices", "PlatformServiceFee", start, start.AddDays(15), 2);
        Assert.Equal("finance/che/sellers/123456/otherfinancials?transactionType=DeductionInvoices&transactionSubType=PlatformServiceFee&startDate=1760000000000&endDate=1761296000000&page=2&size=500&supplierId=123456", path);
        Assert.StartsWith("finance/che/sellers/123456/settlements?transactionType=Sale&startDate=", TrendyolFinanceRequestContract.Path(Settlements, "123456", "Sale", null, start, start.AddDays(1), 0));
        Assert.Equal("finance/che/sellers/123456/cargo-invoice/DDF2026000001/items?page=0&size=500", TrendyolFinanceRequestContract.CargoInvoiceItemsPath("123456", "DDF2026000001", 0));
    }

    [Fact]
    public void The_finance_sync_runs_alone_in_its_own_lane_and_coalesces_scheduled_duplicates()
    {
        Assert.Contains(MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.ProcessorJobTypes);
        Assert.Equal("finance", MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.TrendyolFinanceSync));
        Assert.True(MarketplaceSyncExecutionLock.IsCoalescableRead(MarketplaceJobTypes.TrendyolFinanceSync));
        Assert.Equal(JobPriorityPolicy.Reconciliation, JobPriorityPolicy.For(MarketplaceJobTypes.TrendyolFinanceSync, userInitiated: false));
        var defaults = ScheduledJobProducer.DefaultPolicies(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var finance = Assert.Single(defaults, policy => policy.ResourceType == "FINANCE");
        Assert.Equal(86_400, finance.IntervalSeconds);
        Assert.False(ScheduledJobProducer.SupportsHepsiburadaScheduledPolicy("FINANCE"));
        Assert.False(ScheduledJobProducer.SupportsN11ScheduledPolicy("FINANCE"));
        Assert.False(ScheduledJobProducer.SupportsShopifyPolicy("FINANCE"));
    }

    [Fact]
    public void The_report_takes_real_costs_by_order_or_by_date_and_prefers_the_settled_commission()
    {
        var (input, orderA, orderB, store) = ReportFixture();
        var finance = new ReportFinance(
            [
                // Order A was settled: its sale line carries the real commission (estimate 21.5% of 100 = 21.50).
                Row(store, Settlements, "1", "Sale", MarketplaceFinanceCategories.Sale, "A-1", "BAR-1", credit: 100m, commission: 18m),
                Row(store, Settlements, "2", "Discount", MarketplaceFinanceCategories.Discount, "A-1", "BAR-1", debt: 5m, commission: 1m),
                // A service fee and withholding tax tied to no order, counted by date.
                Row(store, Others, "3", TrendyolFinanceCategorizer.DeductionInvoices, MarketplaceFinanceCategories.ServiceFee, null, null, debt: 9.99m),
                Row(store, Others, "4", "Stoppage", MarketplaceFinanceCategories.Stoppage, null, null, debt: 1m),
                // A cargo invoice whose lines were read counts through its lines; one without lines counts whole.
                Row(store, Others, "INV-1", TrendyolFinanceCategorizer.DeductionInvoices, MarketplaceFinanceCategories.Cargo, null, null, debt: 500m),
                Row(store, Others, "INV-2", TrendyolFinanceCategorizer.DeductionInvoices, MarketplaceFinanceCategories.Cargo, null, null, debt: 40m),
                Row(store, Others, "5", "FinancialItem", MarketplaceFinanceCategories.Other, null, null, credit: 3m),
                // Trendyol's commission invoice bills the settled commission above; it is not added again.
                Row(store, Others, "6", TrendyolFinanceCategorizer.DeductionInvoices, MarketplaceFinanceCategories.Commission, null, null, debt: 17m)
            ],
            [
                new ReportCargoItemRow { ConnectionId = store, InvoiceSerialNumber = "INV-1", OrderNumber = "A-1", ShipmentPackageType = "Gönderi Kargo Bedeli", Amount = 34.24m },
                // A return's cargo is real cost but does not mean order B's shipment was billed.
                new ReportCargoItemRow { ConnectionId = store, InvoiceSerialNumber = "INV-1", OrderNumber = "B-1", ShipmentPackageType = "İade Kargo Bedeli", Amount = 10m }
            ],
            new HashSet<(Guid, string)> { (store, "INV-1") },
            new Dictionary<Guid, MarketplaceFinanceSyncState?>
            {
                [store] = new() { TenantId = Guid.NewGuid(), ConnectionId = store, CoveredFrom = input.Period.Start.AddDays(-90), CoveredThrough = input.Period.End.AddDays(1), LastSyncedAt = input.Period.End.AddDays(1) }
            },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(196.68m, 2) });
        var report = ReportBuilder.Build(input with { Finance = finance }, Empty(input), new Dictionary<Guid, decimal>());
        var costs = report.Costs;

        Assert.Equal(ReportCosts.FinanceSource, costs.Source);
        Assert.Equal(2, costs.FinanceOrders);
        Assert.Equal(1, costs.SettledOrders);
        Assert.Equal(0.5m, costs.Coverage);
        Amount(costs, MarketplaceFinanceCategories.Commission, 17m);
        Amount(costs, MarketplaceFinanceCategories.Cargo, 84.24m);
        // Order B is not settled yet: its platform fee and withholding tax are estimated on top of the dated records.
        Amount(costs, MarketplaceFinanceCategories.ServiceFee, 9.99m + 13.19m);
        Amount(costs, MarketplaceFinanceCategories.Stoppage, 1m + 0.5m);
        Amount(costs, MarketplaceFinanceCategories.Sale, -100m);
        Amount(costs, MarketplaceFinanceCategories.Discount, 5m);
        Amount(costs, MarketplaceFinanceCategories.Other, -3m);
        // Commission, cargo, service fee and withholding tax; OTHER is shown but not added.
        Assert.Equal(17m + 84.24m + 9.99m + 13.19m + 1m + 0.5m, costs.RealCostTotal);
        var cargo = costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.Cargo);
        Assert.Equal(44.24m, cargo.OrderLinked);
        Assert.Equal(40m, cargo.Unlinked);

        // Order A's shipment was billed; order B's not yet: it is estimated at the store's recent cargo per order.
        var cargoCost = Assert.IsType<ReportCargoCostView>(costs.Cargo);
        Assert.Equal(84.24m, cargoCost.Real);
        Assert.Equal(1, cargoCost.BilledOrders);
        Assert.Equal(1, cargoCost.UnbilledOrders);
        Assert.Equal(98.34m, cargoCost.AveragePerOrder);
        Assert.Equal(2, cargoCost.SampleOrders);
        Assert.Equal(98.34m, cargoCost.Estimated);
        Assert.Equal(84.24m + 98.34m, cargoCost.Combined);

        var commission = Assert.IsType<ReportRealCommissionView>(costs.Commission);
        Assert.Equal(17m, commission.Real);
        Assert.Equal(1, commission.RealOrders);
        Assert.Equal(21.5m, commission.EstimatedForSettled);
        Assert.Equal(10.75m, commission.EstimatedForUnsettled);
        Assert.Equal(27.75m, commission.Combined);
        // The estimate keeps its meaning.
        Assert.Equal(32.25m, report.Commission.EstimatedCommission);

        var channel = Assert.Single(costs.Channels);
        Assert.Equal(ReportCosts.FinanceSource, channel.Source);
        Assert.Equal(17m, channel.RealCommission);
        Assert.Equal(10.75m, channel.EstimatedCommission);
        Assert.Equal(98.34m, channel.EstimatedCargo);
        Assert.Equal(17m + 10.75m + 84.24m + 9.99m + 13.19m + 1m + 0.5m + 98.34m, channel.Total);

        var product = Assert.Single(costs.Products);
        Assert.Equal("BAR-1", product.Barcode);
        Assert.Equal(17m, product.Commission);
        Assert.Equal(5m, product.Discount);
        Assert.Equal(2, product.Records);

        Assert.Contains(report.DataNotes, note => note.Contains("cari hesap ekstresinden", StringComparison.Ordinal));
        Assert.Contains(report.DataNotes, note => note.Contains("tahmin yerine gerçek tutarla", StringComparison.Ordinal));
        Assert.Contains(report.DataNotes, note => note.Contains("Sınıflandırılamayan 1", StringComparison.Ordinal));
        Assert.Contains(report.DataNotes, note => note.Contains("ikinci kez eklenmez", StringComparison.Ordinal));
        Assert.Contains(report.DataNotes, note => note.Contains("faturası henüz gelmeyen 1 siparişin kargosu", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_recent_cargo_sample_the_unbilled_cargo_is_reported_missing_not_zero()
    {
        var (input, _, _, store) = ReportFixture();
        var finance = new ReportFinance(
            [Row(store, Settlements, "1", "Sale", MarketplaceFinanceCategories.Sale, "A-1", "BAR-1", credit: 100m, commission: 18m)], [],
            new HashSet<(Guid, string)>(),
            new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = new() { TenantId = Guid.NewGuid(), ConnectionId = store, LastSyncedAt = input.Period.End } });
        var report = ReportBuilder.Build(input with { Finance = finance }, Empty(input), new Dictionary<Guid, decimal>());

        var cargo = Assert.IsType<ReportCargoCostView>(report.Costs.Cargo);
        Assert.Equal(0m, cargo.Real);
        Assert.Equal(2, cargo.UnbilledOrders);
        Assert.Null(cargo.AveragePerOrder);
        Assert.Null(cargo.Estimated);
        Assert.Contains(report.DataNotes, note => note.Contains("tahmin edilemedi", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_real_records_the_costs_are_marked_as_estimates_and_hold_no_zeros()
    {
        var (input, _, _, store) = ReportFixture();
        var never = ReportBuilder.Build(input with { Finance = new ReportFinance([], [], new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = null }) },
            Empty(input), new Dictionary<Guid, decimal>());
        Assert.Equal(ReportCosts.EstimateSource, never.Costs.Source);
        Assert.Null(never.Costs.RealCostTotal);
        Assert.Null(never.Costs.Commission);
        Assert.Null(never.Costs.Cargo);
        Assert.Null(never.Costs.Coverage);
        Assert.Null(never.Costs.LastSyncedAt);
        Assert.Empty(never.Costs.Lines);
        Assert.Empty(never.Costs.Channels);
        Assert.Empty(never.Costs.Products);
        Assert.Contains(never.DataNotes, note => note.Contains("henüz okunmadı", StringComparison.Ordinal));

        var noStore = ReportBuilder.Build(input, Empty(input), new Dictionary<Guid, decimal>());
        Assert.Equal(ReportCosts.EstimateSource, noStore.Costs.Source);
        Assert.Contains(noStore.DataNotes, note => note.Contains("canlı Trendyol mağazası yok", StringComparison.Ordinal));
    }

    // Yesterday's orders, before Trendyol wrote any record of them (24 Oct 2026: the owner's reference for 23 Oct had
    // 15 orders, 3,040.19 commission, 1,475.10 cargo at 98.34 an order, 197.85 service fee at 13.19 an order, 34.22
    // international fee on a 570.34 Greek order, 123.85 withholding tax and -53.17 net VAT): everything is estimated
    // from the store's own history instead of being left out.
    [Fact]
    public void A_read_store_without_records_of_the_period_yet_has_its_trendyol_costs_estimated()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 23);
        var start = new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (lineA, lineB, lineC) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol Ravencia", false) },
            [
                new() { Id = a, ConnectionId = store, OrderNumber = "A", DerivedStatus = "SHIPPED", Amount = 1000m, LineValue = 1000m, RealAt = at, OrderedAt = at },
                new() { Id = b, ConnectionId = store, OrderNumber = "B", DerivedStatus = "SHIPPED", Amount = 500m, LineValue = 500m, RealAt = at, OrderedAt = at },
                new() { Id = c, ConnectionId = store, OrderNumber = "C", DerivedStatus = "SHIPPED", Amount = 570.34m, LineValue = 570.34m, RealAt = at, OrderedAt = at, StoreFrontCode = "GR" }
            ],
            new Dictionary<Guid, ReportOrderExtra>(),
            [
                new() { Id = lineA, OrderId = a, Title = "Ceket", Sku = "A", UnitPrice = 1000m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 400m },
                new() { Id = lineB, OrderId = b, Title = "Bluz", Sku = "B", UnitPrice = 500m, OrderedQuantity = 1m, VatRate = 10m },
                new() { Id = lineC, OrderId = c, Title = "Kaban", Sku = "C", UnitPrice = 570.34m, OrderedQuantity = 1m, VatRate = 10m }
            ],
            new Dictionary<Guid, ReportLineExtra>
            {
                [lineA] = new() { Id = lineA, CommissionType = "number", Commission = "21.5" },
                [lineC] = new() { Id = lineC, CommissionType = "number", Commission = "21.5" }
            }, [], [], new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var finance = new ReportFinance([], [], new HashSet<(Guid, string)>(),
            new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = new() { TenantId = Guid.NewGuid(), ConnectionId = store, LastSyncedAt = start, CoveredFrom = start.AddDays(-90), CoveredThrough = start } },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(983.4m, 10) });

        var costs = ReportBuilder.Build(input with { Finance = finance }, Empty(input), new Dictionary<Guid, decimal>()).Costs;

        Assert.Equal(ReportCosts.FinanceSource, costs.Source);
        // B has no commission: it is taken at the store's rate over A and C (21.5 %).
        Assert.Equal(215m + 122.62m + 107.5m, costs.Commission!.Combined);
        Assert.Equal(295.02m, costs.Cargo!.Combined);
        // 3 × 13.19 + 6 % of the Greek sale; 1 % of the VAT-free domestic sales.
        Amount(costs, MarketplaceFinanceCategories.ServiceFee, 73.79m);
        Amount(costs, MarketplaceFinanceCategories.Stoppage, 13.64m);
        Assert.Equal(73.79m, costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.ServiceFee).Estimated);
        // VAT in the domestic sales (1,500 at 10 %), less the VAT in A's cost (400) and in commission, cargo and the
        // platform fee (20 %; the international fee carries none).
        var vat = Assert.IsType<ReportNetVatView>(costs.NetVat);
        Assert.Equal((136.36m, 36.36m, 129.95m, -29.95m), (vat.SalesVat, vat.ProductCostVat, vat.ExpenseVat, vat.Net));
    }

    private static void Amount(ReportCostsView costs, string category, decimal expected) =>
        Assert.Equal(expected, costs.Lines.Single(line => line.Category == category).Amount);

    // Two delivered Trendyol orders of 15 Oct: A (one line of 100, barcode BAR-1) and B (one line of 50), both with a
    // 21.5% commission on the line.
    private static (ReportInput Input, Guid OrderA, Guid OrderB, Guid Store) ReportFixture()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 15);
        var start = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);
        var period = new ReportPeriod(day, day, start, start.AddDays(1));
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var lineA = Guid.NewGuid();
        var lineB = Guid.NewGuid();
        var at = start.AddHours(12);
        var channels = new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol Ravencia", false) };
        var orders = new List<ReportOrderRow>
        {
            new() { Id = orderA, ConnectionId = store, OrderNumber = "A-1", DerivedStatus = "DELIVERED", Amount = 100m, LineValue = 100m, RealAt = at, OrderedAt = at },
            new() { Id = orderB, ConnectionId = store, OrderNumber = "B-1", DerivedStatus = "DELIVERED", Amount = 50m, LineValue = 50m, RealAt = at, OrderedAt = at }
        };
        var lines = new List<ReportLineRow>
        {
            new() { Id = lineA, OrderId = orderA, Title = "Kılıf", Sku = "KLF-1", Barcode = "BAR-1", UnitPrice = 100m, OrderedQuantity = 1m },
            new() { Id = lineB, OrderId = orderB, Title = "Örtü", Sku = "ORT-1", Barcode = "BAR-2", UnitPrice = 50m, OrderedQuantity = 1m }
        };
        var extras = new Dictionary<Guid, ReportLineExtra>
        {
            [lineA] = new() { Id = lineA, CommissionType = "number", Commission = "21.5" },
            [lineB] = new() { Id = lineB, CommissionType = "number", Commission = "21.5" }
        };
        var timezone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var input = new ReportInput(period, ReportBuckets.Day, timezone, channels, orders, new Dictionary<Guid, ReportOrderExtra>(), lines, extras, [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        return (input, orderA, orderB, store);
    }

    private static ReportInput Empty(ReportInput input) =>
        input with { Period = new ReportPeriod(input.Period.From.AddDays(-1), input.Period.To.AddDays(-1), input.Period.Start.AddDays(-1), input.Period.Start), Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>(), Finance = null };

    private static ReportFinanceRow Row(Guid store, string source, string id, string type, string category, string? orderNumber, string? barcode,
        decimal debt = 0m, decimal credit = 0m, decimal? commission = null) => new()
        {
            ConnectionId = store,
            Source = source,
            ExternalId = id,
            RequestedType = type,
            Category = category,
            TransactionDate = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero),
            OrderNumber = orderNumber,
            Barcode = barcode,
            Debt = debt,
            Credit = credit,
            CommissionAmount = commission
        };

    private static RemoteStatementRecord Record(string id, string source, string requestedType, string? type = null, string? orderNumber = null,
        decimal debt = 0m, decimal credit = 0m, decimal? commission = null) =>
        new(id, source, requestedType, null, type, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), null, orderNumber, null, null, debt, credit, null, commission, null, null, null,
            JsonSerializer.Serialize(new { id, requestedType, debt, credit }));
}
