using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-10-19 W4: the Returns page's cards, filters, search and order, now applied by the server.
public sealed class ReturnListCardTests
{
    private static readonly Guid First = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Second = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Third = Guid.Parse("01900000-0000-7000-8000-000000000003");

    [Fact]
    public void A_claim_with_accepted_and_waiting_lines_is_a_waiting_card_then_an_approved_card()
    {
        var cards = ReturnQueryService.SplitReturnClaim([First, Second, Third],
            new Dictionary<Guid, string> { [First] = "APPROVED", [Second] = "ACTION_REQUIRED" }, ReturnClaimStatus.ActionRequired);

        Assert.Equal([ReturnQueryService.WaitingCard, ReturnQueryService.ApprovedCard], cards.Select(card => card.Kind));
        // A line without a status takes its claim's: this claim waits for the seller.
        Assert.Equal([Second, Third], cards[0].LineIds);
        Assert.Equal([First], cards[1].LineIds);
    }

    // Scan 25 Nov 2026 C-3: a split claim's rejected or still travelling line was on neither card (lost from the page and
    // its search), and a line without a status counted as waiting even on an approved claim.
    [Fact]
    public void A_split_claim_keeps_its_other_lines_on_the_first_card_and_a_blank_line_takes_the_claim_status()
    {
        var cards = ReturnQueryService.SplitReturnClaim([First, Second, Third],
            new Dictionary<Guid, string> { [First] = "APPROVED", [Second] = "ACTION_REQUIRED", [Third] = "REJECTED" }, ReturnClaimStatus.ActionRequired);
        Assert.Equal([Second, Third], cards[0].LineIds);
        Assert.Equal([First], cards[1].LineIds);

        // On an approved claim a blank line is accepted with it.
        var approved = ReturnQueryService.SplitReturnClaim([First, Second, Third],
            new Dictionary<Guid, string> { [Second] = "ACTION_REQUIRED", [Third] = "IN_TRANSIT" }, ReturnClaimStatus.Approved);
        Assert.Equal([Second, Third], approved[0].LineIds);
        Assert.Equal([First], approved[1].LineIds);
        // On an in-transit claim a blank line neither waits nor is accepted: no split.
        Assert.Single(ReturnQueryService.SplitReturnClaim([First, Second], new Dictionary<Guid, string> { [First] = "APPROVED" }, ReturnClaimStatus.InTransit));

        // The split check the list and the card columns read agrees.
        Assert.True(ReturnQueryService.MaySplitReturnClaim([null, "WaitingInAction"], ReturnClaimStatus.Approved));
        Assert.False(ReturnQueryService.MaySplitReturnClaim(["Accepted", null], ReturnClaimStatus.InTransit));
        Assert.True(ReturnQueryService.MaySplitReturnClaim(["Accepted", " "], ReturnClaimStatus.ActionRequired));
    }

    [Fact]
    public void A_claim_stays_one_card_unless_it_mixes_accepted_and_waiting_lines()
    {
        Assert.Single(ReturnQueryService.SplitReturnClaim([First, Second], new Dictionary<Guid, string> { [First] = "APPROVED", [Second] = "COMPLETED" }, ReturnClaimStatus.ActionRequired));
        Assert.Single(ReturnQueryService.SplitReturnClaim([First], new Dictionary<Guid, string> { [First] = "APPROVED" }, ReturnClaimStatus.ActionRequired));
        Assert.Single(ReturnQueryService.SplitReturnClaim([First, Second], new Dictionary<Guid, string> { [First] = "APPROVED", [Second] = "REJECTED" }, ReturnClaimStatus.ActionRequired));
        Assert.Single(ReturnQueryService.SplitReturnClaim([First, Second], null, ReturnClaimStatus.ActionRequired));
        Assert.Equal(ReturnQueryService.WholeCard, ReturnQueryService.SplitReturnClaim([First, Second], new Dictionary<Guid, string>(), ReturnClaimStatus.ActionRequired)[0].Kind);
    }

    [Theory]
    [InlineData("REQUESTED", "REQUESTED")]
    [InlineData("AWAITING_SHIPMENT", "SHIPPING")]
    [InlineData("IN_TRANSIT", "SHIPPING")]
    [InlineData("ACTION_REQUIRED", "ACTION_REQUIRED")]
    [InlineData("APPROVED", "APPROVED")]
    [InlineData("COMPLETED", "APPROVED")]
    [InlineData("REJECTED", "REJECTED")]
    [InlineData("DISPUTED", "DISPUTED")]
    [InlineData("SOMETHING_NEW", "REVIEW")]
    public void Cards_count_under_the_tab_that_lists_them(string status, string tab)
    {
        Assert.Equal(tab, ReturnQueryService.ReturnTabGroup(status));
    }

    [Fact]
    public void Platform_cargo_and_reason_filters_match_as_the_page_matched_them()
    {
        Assert.True(ReturnQueryService.MatchesReturnRefinements("TRENDYOL", " Aras Kargo ", "  ", "Beden uymadı", new ReturnListQuery(Platforms: ["TRENDYOL", "N11"], Cargo: "Aras Kargo", ReasonKey: "Beden uymadı")));
        Assert.False(ReturnQueryService.MatchesReturnRefinements("HEPSIBURADA", null, null, null, new ReturnListQuery(Platforms: ["TRENDYOL"])));
        // "Tümünü Kaldır" sends a value no platform has: nothing is listed.
        Assert.False(ReturnQueryService.MatchesReturnRefinements("TRENDYOL", null, null, null, new ReturnListQuery(Platforms: ["__NONE__"])));
        Assert.False(ReturnQueryService.MatchesReturnRefinements("TRENDYOL", "Yurtiçi", null, null, new ReturnListQuery(Cargo: "Aras Kargo")));
        // The reason key is the code when there is one, otherwise the text.
        Assert.False(ReturnQueryService.MatchesReturnRefinements("TRENDYOL", null, "SIZE", "Beden uymadı", new ReturnListQuery(ReasonKey: "Beden uymadı")));
        Assert.True(ReturnQueryService.MatchesReturnRefinements("TRENDYOL", null, "SIZE", "Beden uymadı", new ReturnListQuery(ReasonKey: "SIZE", Cargo: "ALL")));
    }

    [Fact]
    public void Search_ignores_case_the_Turkish_way_and_reads_only_the_cards_own_lines()
    {
        var view = View(lines: [Line(First, "SKU-A", "Kırmızı İpek Şal"), Line(Second, "SKU-B", "Mavi Gömlek")]);

        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [First, Second], "ipek"));
        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [First], "AYŞE"));
        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [First], "claim-77"));
        Assert.False(ReturnQueryService.MatchesReturnSearch(view, [First], "gömlek"));
        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [Second], "GÖMLEK"));
        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [First], "  "));
        // The i letters fold like the database pre-filter: "kirmizi" and "KIRMIZI" find "Kırmızı" (O3).
        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [First], "kirmizi"));
        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [First], "KIRMIZI"));
        Assert.True(ReturnQueryService.MatchesReturnSearch(view, [First], "İPEK"));
    }

    [Fact]
    public void The_approved_tab_sorts_by_date_with_empty_dates_last_then_by_claim_and_card()
    {
        var early = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var late = early.AddDays(2);
        var items = new (DateTimeOffset? Date, Guid ClaimId, int Kind)[] { (null, First, 0), (early, Third, 0), (late, Second, 2), (late, Second, 1), (early, First, 0) };

        var descending = items.OrderBy(x => x, Comparer<(DateTimeOffset?, Guid, int)>.Create((left, right) => ReturnQueryService.CompareReturnCards(left, right, "APPROVED_DESC"))).ToList();
        var byClaim = items.OrderBy(x => x, Comparer<(DateTimeOffset?, Guid, int)>.Create((left, right) => ReturnQueryService.CompareReturnCards(left, right, "ID"))).ToList();

        Assert.Equal([(late, Second, 1), (late, Second, 2), (early, First, 0), (early, Third, 0), (null, First, 0)], descending);
        Assert.Equal([First, First, Second, Second, Third], byClaim.Select(x => x.ClaimId));
        Assert.True(ReturnQueryService.CompareReturnCards((early, First, 0), (late, First, 0), "ORDERED_ASC") < 0);
    }

    [Fact]
    public void A_split_card_shows_only_its_lines_and_their_refund()
    {
        var view = View(lines: [Line(First, "SKU-A", "A", 80m), Line(Second, "SKU-B", "B", 40m)]) with { ReturnAmounts = new(null, null, 120m, null) };
        var card = new ReturnQueryService.ReturnCard(view.Id, ReturnQueryService.ApprovedCard, "APPROVED", "TRENDYOL", "Trendyol", null, null, null, [Second]);

        var shown = ReturnQueryService.ReturnCardView(view, card);

        Assert.Equal([Second], shown.Lines!.Select(line => line.Id));
        Assert.Equal(1, shown.ProductCount);
        Assert.Equal("APPROVED", shown.Status);
        Assert.Equal("Accepted", shown.RawStatus);
        Assert.Equal(40m, shown.ReturnAmounts!.InvoicedAmount);
        Assert.Equal($"{view.Id:D}:approved", shown.RowKey);
        Assert.Same(view, ReturnQueryService.ReturnCardView(view, card with { Kind = ReturnQueryService.WholeCard }));
    }

    [Theory]
    [InlineData(MarketplaceInvoiceStatus.NotInvoiced, null)]
    [InlineData(MarketplaceInvoiceStatus.Invoiced, null)]
    [InlineData(MarketplaceInvoiceStatus.Received, null)]
    [InlineData(MarketplaceInvoiceStatus.Rejected, null)]
    [InlineData(MarketplaceInvoiceStatus.Unknown, "Invoiced")]
    public void The_invoice_label_reads_the_customer_snapshot_only_when_said_so(MarketplaceInvoiceStatus marketplaceStatus, string? rawStatus)
    {
        Assert.False(ReturnQueryService.ReturnInvoiceLabelReadsSnapshot(null, marketplaceStatus, rawStatus));
        Assert.Equal(
            InvoiceLabelPolicy.InvoiceLabel(null, null, marketplaceStatus, "{}", [rawStatus]),
            InvoiceLabelPolicy.InvoiceLabel(null, null, marketplaceStatus, """{"invoiceStatus":"REJECTED"}""", [rawStatus]));
        foreach (var invoiceStatus in Enum.GetValues<InvoiceStatus>())
        {
            Assert.False(ReturnQueryService.ReturnInvoiceLabelReadsSnapshot(invoiceStatus, MarketplaceInvoiceStatus.Unknown, null));
            Assert.Equal(
                InvoiceLabelPolicy.InvoiceLabel(invoiceStatus, "INV-1", MarketplaceInvoiceStatus.Unknown, "{}", [null]),
                InvoiceLabelPolicy.InvoiceLabel(invoiceStatus, "INV-1", MarketplaceInvoiceStatus.Unknown, """{"invoiceStatus":"REJECTED"}""", [null]));
        }
        Assert.True(ReturnQueryService.ReturnInvoiceLabelReadsSnapshot(null, MarketplaceInvoiceStatus.Unknown, "Delivered"));
    }

    [Fact]
    public void A_return_cursor_keeps_its_sort_date_claim_and_card()
    {
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        var date = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);

        Assert.True(codec.TryDecodeReturn(codec.EncodeReturn("APPROVED_DESC", date, Second, 2), out var sort, out var decodedDate, out var claimId, out var card));
        Assert.Equal(("APPROVED_DESC", date, Second, 2), (sort, decodedDate, claimId, card));
        Assert.True(codec.TryDecodeReturn(codec.EncodeReturn("ID", null, First, 0), out _, out var noDate, out _, out _));
        Assert.Null(noDate);
        Assert.False(codec.TryDecodeReturn(codec.Encode(First), out _, out _, out _, out _));
    }

    private static OrderLineView Line(Guid id, string sku, string title, decimal? refund = null) =>
        new(id, sku, null, title, 1, 0, 1, 1, 0, 100m, 20m, "Delivered", ReturnAmounts: refund is null ? null : new(null, null, refund, null));

    private static ReturnListView View(IReadOnlyList<OrderLineView> lines) =>
        new(Guid.Parse("01900000-0000-7000-8000-0000000000aa"), "claim-77", "1001", "ACTION_REQUIRED", "WaitingInAction", "Beden küçük", null, 1,
            CustomerName: "Ayşe Yılmaz", Lines: lines, ProductCount: lines.Count, ReasonCode: "SIZE");
}
