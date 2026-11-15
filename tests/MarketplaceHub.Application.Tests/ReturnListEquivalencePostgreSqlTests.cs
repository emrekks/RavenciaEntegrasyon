using System.Data.Common;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// Audit SIPARIS-STOK-9: the Returns page no longer reads every claim and line of the tenant for each page and counter
// request. The old reads (LegacyReturnListQueries) and the new ones run on the same richly seeded tenant (split claims,
// partly approved ones, blank and unknown line statuses, repeated order lines, equal and empty dates, approvals without
// a date) and must give the same rows in the same order, the same counts and cursors that point at the same card.
[Collection("postgres-integration")]
public sealed class ReturnListEquivalencePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string?[] Sorts = [null, "REQUESTED_ASC", "ORDERED_ASC", "ORDERED_DESC", "APPROVED_ASC", "APPROVED_DESC", "ID"];
    private static readonly string?[] Tabs = [null, "ALL", "REQUESTED", "SHIPPING", "ACTION_REQUIRED", "APPROVED", "REJECTED", "REVIEW", "DISPUTED", "SUSPENDED", "UNKNOWN_TAB"];

    [PostgreSqlFact]
    public async Task Every_page_of_every_tab_and_sort_equals_the_in_memory_pages()
    {
        var tenantId = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var cursors = NewCursors();
            var service = NewService(db, cursors);
            // The seed's saves filled the card columns: the pages below are read through them.
            Assert.True((await service.ReturnColumnStateAsync(tenantId, CancellationToken.None)).Fresh);
            var compared = 0;
            foreach (var sort in Sorts)
                foreach (var tab in Tabs)
                    foreach (var limit in new[] { 3, 17 })
                        compared += await ComparePagesAsync(service, db, cursors, tenantId, limit, new ReturnListQuery(Status: tab, Sort: sort));
            // Database filters other than the tab stay on the database path.
            var istanbul = TimeSpan.FromHours(3);
            foreach (var sort in new[] { "REQUESTED_DESC", "APPROVED_ASC" })
            {
                compared += await ComparePagesAsync(service, db, cursors, tenantId, 4, new ReturnListQuery(Sort: sort, DateFrom: new DateTimeOffset(2026, 11, 1, 0, 0, 0, istanbul), DateTo: new DateTimeOffset(2026, 11, 30, 23, 59, 59, istanbul)));
                compared += await ComparePagesAsync(service, db, cursors, tenantId, 4, new ReturnListQuery(Status: "APPROVED", Sort: sort, Barcode: "EQ-SKU"));
            }
            Assert.True(compared > 300, $"only {compared} pages compared");
            output.WriteLine($"{compared} pages compared");
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    [PostgreSqlFact]
    public async Task Pages_filtered_card_by_card_equal_the_in_memory_pages()
    {
        var tenantId = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var cursors = NewCursors();
            var service = NewService(db, cursors);
            ReturnListQuery[] filters =
            [
                new(Search: "a"), new(Search: "EQ-SKU-1"), new(Search: "ayşe"), new(Search: "BEĞENMEDİM", Status: "APPROVED"),
                new(Platforms: ["TRENDYOL"]), new(Platforms: ["N11", "HEPSIBURADA"], Status: "APPROVED"), new(Cargo: "Aras Kargo"), new(Cargo: "Yurtiçi", Status: "ACTION_REQUIRED"),
                new(ReasonKey: "SIZE"), new(ReasonKey: "Beğenmedim", Sort: "ORDERED_ASC"), new(InvoiceStatus: "FATURA_BILINMIYOR"), new(InvoiceStatus: "FATURA_KONTROLDE", Sort: "ID"),
                new(Search: "eq", Platforms: ["TRENDYOL"], Cargo: "Aras Kargo", Sort: "APPROVED_DESC")
            ];
            var compared = 0;
            foreach (var filter in filters)
                foreach (var limit in new[] { 2, 9 })
                    compared += await ComparePagesAsync(service, db, cursors, tenantId, limit, filter);
            output.WriteLine($"{compared} pages compared");
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    [PostgreSqlFact]
    public async Task The_summary_equals_the_in_memory_summary()
    {
        var tenantId = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var cursors = NewCursors();
            var service = NewService(db, cursors);
            List<ReturnListQuery> options = [.. Tabs.Select(tab => new ReturnListQuery(Status: tab))];
            options.AddRange([
                new(Search: "a"), new(Search: "EQ-SKU-2", Status: "APPROVED"), new(Platforms: ["TRENDYOL"]), new(Platforms: ["N11"], Status: "APPROVED"),
                new(Cargo: "Aras Kargo"), new(ReasonKey: "SIZE", Status: "REJECTED"), new(InvoiceStatus: "FATURA_BILINMIYOR"), new(InvoiceStatus: "FATURA_KONTROLDE", Status: "SHIPPING"),
                new(Barcode: "EQ-SKU")
            ]);
            foreach (var option in options)
            {
                var expected = await LegacyReturnListQueries.BuildReturnSummaryAsync(service, db, tenantId, option, CancellationToken.None);
                var actual = await service.BuildReturnSummaryAsync(tenantId, option, MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal), CancellationToken.None);
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            }
            var all = await service.BuildReturnSummaryAsync(tenantId, new ReturnListQuery(), MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal), CancellationToken.None);
            // The seed has split claims, so the cards outnumber the claims.
            Assert.True(all.TabCounts["ALL"] > await service.ReturnBaseQuery(tenantId).CountAsync());
            Assert.True(all.TabCounts["APPROVED"] > 0 && all.TabCounts["ACTION_REQUIRED"] > 0 && all.TabCounts["REJECTED"] > 0);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // SIPARIS-STOK-9: the platform, cargo and reason choices are decided by the database alone now, so its filter must keep
    // exactly the claims the card check keeps, judged on every claim of the tenant (not only on the ones it kept).
    [PostgreSqlFact]
    public async Task The_database_platform_cargo_and_reason_filter_keeps_exactly_the_claims_the_card_check_keeps()
    {
        var tenantId = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db, NewCursors());
            var connections = await db.PlatformConnections.Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id, x => x.PlatformCode);
            var all = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync();
            ReturnListQuery[] filters =
            [
                new(Platforms: ["TRENDYOL"]), new(Platforms: [" N11 ", "HEPSIBURADA"]), new(Cargo: "Aras Kargo"), new(Cargo: " Yurtiçi "), new(Cargo: "ALL"),
                // White space inside a value, after a line break, stays: a multi-line pattern would trim it too.
                new(Cargo: "Aras\n Kargo"),
                new(ReasonKey: "SIZE"), new(ReasonKey: "Beğenmedim"), new(ReasonKey: "DEFECT"),
                new(Platforms: ["TRENDYOL"], Cargo: "Aras Kargo", ReasonKey: "SIZE")
            ];
            foreach (var filter in filters)
            {
                var query = db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId);
                service.ApplyReturnRefinements(ref query, tenantId, filter);
                var kept = (await query.Select(x => x.Id).ToListAsync()).ToHashSet();
                var expected = all.Where(x => MarketplaceSalesService.MatchesReturnRefinements(connections[x.ConnectionId], x.CargoProviderName, x.ReasonCode, x.ReasonText, filter)).Select(x => x.Id).ToHashSet();
                Assert.True(expected.SetEquals(kept), $"{JsonSerializer.Serialize(filter)}: {expected.Count} expected, {kept.Count} kept");
                Assert.True(expected.Count > 0, $"{JsonSerializer.Serialize(filter)} keeps no claim");
            }
            // The seed carries values that only a full white space trim matches.
            Assert.Contains(all, x => x.CargoProviderName?.StartsWith(' ') == true);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // SIPARIS-STOK-9 card columns: rows never filled (the claims the old code wrote), some filled and some not (a deploy's
    // backfill half way), and filled rows made stale behind the save hook's back must never change a page or a summary: the
    // staleness check sends the tenant back to the computation from claims and lines, and the repair pass brings the
    // columns back.
    [PostgreSqlFact]
    public async Task Pages_and_summaries_stay_the_same_while_the_card_columns_are_unfilled_or_stale()
    {
        var tenantId = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var cursors = NewCursors();
            var service = NewService(db, cursors);
            var claims = db.ReturnClaims.Where(x => x.TenantId == tenantId);
            var ids = await claims.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
            var splitIds = await claims.Where(x => x.CardSplit == true).Select(x => x.Id).ToListAsync();
            var approvedIds = await claims.Where(x => x.ApprovedAt != null).Select(x => x.Id).ToListAsync();
            Assert.NotEmpty(splitIds);
            Assert.NotEmpty(approvedIds);
            var everyThird = ids.Where((_, index) => index % 3 == 0).ToList();
            var states = new (string Name, Func<Task> Break)[]
            {
                ("unfilled", () => claims.ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, (string?)null).SetProperty(x => x.CardSplit, (bool?)null).SetProperty(x => x.ApprovedAt, (DateTimeOffset?)null))),
                ("a third unfilled", () => claims.Where(x => everyThird.Contains(x.Id)).ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, (string?)null).SetProperty(x => x.CardSplit, (bool?)null).SetProperty(x => x.ApprovedAt, (DateTimeOffset?)null))),
                ("tab stale", () => claims.Where(x => x.TabGroup == "REJECTED").ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, "APPROVED"))),
                ("split stale", () => claims.Where(x => splitIds.Contains(x.Id)).ExecuteUpdateAsync(set => set.SetProperty(x => x.CardSplit, false))),
                ("split stale the other way", () => claims.Where(x => x.CardSplit == false).ExecuteUpdateAsync(set => set.SetProperty(x => x.CardSplit, true))),
                ("approval date stale", () => claims.Where(x => approvedIds.Contains(x.Id)).ExecuteUpdateAsync(set => set.SetProperty(x => x.ApprovedAt, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)))),
                // A line changed behind the save hook: the stored split no longer follows the lines.
                ("lines changed", () => db.ReturnLines.Where(x => x.TenantId == tenantId && splitIds.Contains(x.ClaimId) && x.RawStatus == "WaitingInAction").ExecuteUpdateAsync(set => set.SetProperty(x => x.RawStatus, "Rejected"))),
            };
            List<ReturnListQuery> summaries = [.. Tabs.Select(tab => new ReturnListQuery(Status: tab)), new(Search: "a", Status: "APPROVED"), new(Platforms: ["TRENDYOL"], Status: "REJECTED")];
            var compared = 0;
            foreach (var (name, breakIt) in states)
            {
                await breakIt();
                Assert.False((await service.ReturnColumnStateAsync(tenantId, CancellationToken.None)).Fresh, name);
                compared += await CompareAllAsync();
                await ReturnCardColumns.RepairAsync(db, CancellationToken.None);
                Assert.True((await service.ReturnColumnStateAsync(tenantId, CancellationToken.None)).Fresh, $"{name} repaired");
                compared += await CompareAllAsync();
            }
            output.WriteLine($"{compared} pages and summaries compared");

            async Task<int> CompareAllAsync()
            {
                var pages = 0;
                foreach (var sort in new[] { null, "APPROVED_DESC", "APPROVED_ASC", "ORDERED_ASC" })
                    foreach (var tab in new[] { null, "APPROVED", "ACTION_REQUIRED", "REJECTED", "SHIPPING" })
                        pages += await ComparePagesAsync(service, db, cursors, tenantId, 7, new ReturnListQuery(Status: tab, Sort: sort));
                pages += await ComparePagesAsync(service, db, cursors, tenantId, 5, new ReturnListQuery(Search: "eq", Status: "APPROVED"));
                pages += await ComparePagesAsync(service, db, cursors, tenantId, 5, new ReturnListQuery(Cargo: "Aras Kargo", Sort: "APPROVED_DESC"));
                foreach (var option in summaries)
                {
                    var expected = await LegacyReturnListQueries.BuildReturnSummaryAsync(service, db, tenantId, option, CancellationToken.None);
                    var actual = await service.BuildReturnSummaryAsync(tenantId, option, MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal), CancellationToken.None);
                    Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
                    pages++;
                }
                return pages;
            }
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // What one request reads, old and new, on the seeded tenant: queries sent and rows the database returned.
    [PostgreSqlFact]
    public async Task The_unfiltered_page_reads_a_page_of_rows_instead_of_every_claim_and_line()
    {
        var tenantId = await SeedAsync(600, liveLike: true);
        try
        {
            var cursors = NewCursors();
            async Task<(int Queries, long Rows)> MeasureAsync(Func<MarketplaceSalesService, AppDbContext, Task> read)
            {
                var recorder = new CommandRecorder();
                await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")).AddInterceptors(recorder).Options))
                    await read(NewService(db, cursors), db);
                return (recorder.Commands.Count, await recorder.RowsAsync());
            }
            var options = new ReturnListQuery();
            var oldPage = await MeasureAsync((service, db) => LegacyReturnListQueries.ReturnCardPageAsync(service, db, cursors, tenantId, 20, 1, null, options, CancellationToken.None));
            var newPage = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options, CancellationToken.None));
            var oldTab = await MeasureAsync((service, db) => LegacyReturnListQueries.ReturnCardPageAsync(service, db, cursors, tenantId, 20, 1, null, options with { Status = "APPROVED", Sort = "APPROVED_DESC" }, CancellationToken.None));
            var newTab = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options with { Status = "APPROVED", Sort = "APPROVED_DESC" }, CancellationToken.None));
            var oldSearch = await MeasureAsync((service, db) => LegacyReturnListQueries.ReturnCardPageAsync(service, db, cursors, tenantId, 20, 1, null, options with { Search = "eq" }, CancellationToken.None));
            var newSearch = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options with { Search = "eq" }, CancellationToken.None));
            var oldSummary = await MeasureAsync((service, db) => LegacyReturnListQueries.BuildReturnSummaryAsync(service, db, tenantId, options with { Status = "APPROVED" }, CancellationToken.None));
            var newSummary = await MeasureAsync((service, _) => service.BuildReturnSummaryAsync(tenantId, options with { Status = "APPROVED" }, MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal), CancellationToken.None));
            var newCargo = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options with { Cargo = "Aras Kargo" }, CancellationToken.None));
            var newAllSummary = await MeasureAsync((service, _) => service.BuildReturnSummaryAsync(tenantId, options, MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal), CancellationToken.None));
            // The same requests with the card columns not filled (the claims the old code wrote): the computation of
            // 15 Nov 2026 wave 3 plus the staleness check.
            await using (var clear = fixture.CreateContext())
                await clear.ReturnClaims.Where(x => x.TenantId == tenantId).ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, (string?)null));
            var bareSummary = await MeasureAsync((service, _) => service.BuildReturnSummaryAsync(tenantId, options with { Status = "APPROVED" }, MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal), CancellationToken.None));
            var bareAllSummary = await MeasureAsync((service, _) => service.BuildReturnSummaryAsync(tenantId, options, MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal), CancellationToken.None));
            var barePage = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options, CancellationToken.None));
            var bareTab = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options with { Status = "APPROVED", Sort = "APPROVED_DESC" }, CancellationToken.None));
            var bareSearch = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options with { Search = "eq" }, CancellationToken.None));
            var bareCargo = await MeasureAsync((service, _) => service.ReturnCardPageAsync(tenantId, 20, 1, null, options with { Cargo = "Aras Kargo" }, CancellationToken.None));
            await using (var count = fixture.CreateContext())
                output.WriteLine($"seed: {await count.ReturnClaims.CountAsync(x => x.TenantId == tenantId)} claims, {await count.ReturnLines.CountAsync(x => x.TenantId == tenantId)} lines");
            output.WriteLine($"page (all tabs, newest request): old {oldPage.Queries} queries / {oldPage.Rows} rows, columns unfilled {barePage.Queries} / {barePage.Rows}, filled {newPage.Queries} / {newPage.Rows}");
            output.WriteLine($"page (approved tab, approval date): old {oldTab.Queries} queries / {oldTab.Rows} rows, columns unfilled {bareTab.Queries} / {bareTab.Rows}, filled {newTab.Queries} / {newTab.Rows}");
            output.WriteLine($"page (search): old {oldSearch.Queries} queries / {oldSearch.Rows} rows, columns unfilled {bareSearch.Queries} / {bareSearch.Rows}, filled {newSearch.Queries} / {newSearch.Rows}");
            output.WriteLine($"page (cargo filter): columns unfilled {bareCargo.Queries} queries / {bareCargo.Rows} rows, filled {newCargo.Queries} / {newCargo.Rows}");
            output.WriteLine($"summary (approved tab): old {oldSummary.Queries} queries / {oldSummary.Rows} rows, columns unfilled {bareSummary.Queries} / {bareSummary.Rows}, filled {newSummary.Queries} / {newSummary.Rows}");
            output.WriteLine($"summary (all tabs): columns unfilled {bareAllSummary.Queries} queries / {bareAllSummary.Rows} rows, filled {newAllSummary.Queries} / {newAllSummary.Rows}");
            Assert.True(newSummary.Rows < bareSummary.Rows, $"summary rows {newSummary.Rows} vs {bareSummary.Rows} unfilled");
            Assert.True(newPage.Rows * 2 < oldPage.Rows, $"page rows {newPage.Rows} vs {oldPage.Rows}");
            Assert.True(newTab.Rows < oldTab.Rows, $"tab rows {newTab.Rows} vs {oldTab.Rows}");
            Assert.True(newSearch.Rows <= oldSearch.Rows, $"search rows {newSearch.Rows} vs {oldSearch.Rows}");
            Assert.True(newSummary.Rows < oldSummary.Rows, $"summary rows {newSummary.Rows} vs {oldSummary.Rows}");
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // Walks every page by cursor and by page number, old and new side by side; returns the pages compared.
    private static async Task<int> ComparePagesAsync(MarketplaceSalesService service, AppDbContext db, CursorCodec cursors, Guid tenantId, int limit, ReturnListQuery options)
    {
        var compared = 0;
        string? after = null;
        for (var guard = 0; guard < 200; guard++)
        {
            var expected = await LegacyReturnListQueries.ReturnCardPageAsync(service, db, cursors, tenantId, limit, 1, after, options, CancellationToken.None);
            var actual = await service.ReturnCardPageAsync(tenantId, limit, 1, after, options, CancellationToken.None);
            AssertSamePage(cursors, expected, actual, $"{JsonSerializer.Serialize(options)} limit {limit} after page {compared}");
            compared++;
            if (actual.NextCursor is null) break;
            after = actual.NextCursor;
        }
        var pages = (int)Math.Ceiling((await service.ReturnCardPageAsync(tenantId, limit, 1, null, options, CancellationToken.None)).TotalCount!.Value / (double)limit);
        for (var page = 2; page <= pages + 1; page++)
        {
            var expected = await LegacyReturnListQueries.ReturnCardPageAsync(service, db, cursors, tenantId, limit, page, null, options, CancellationToken.None);
            var actual = await service.ReturnCardPageAsync(tenantId, limit, page, null, options, CancellationToken.None);
            AssertSamePage(cursors, expected, actual, $"{JsonSerializer.Serialize(options)} limit {limit} page {page}");
            compared++;
        }
        return compared;
    }

    private static void AssertSamePage(CursorCodec cursors, PageResult<ReturnListView> expected, PageResult<ReturnListView> actual, string what)
    {
        static string Rows(PageResult<ReturnListView> result) => string.Join(", ", result.Items.Select(item => item.RowKey ?? item.Id.ToString("D")));
        // A view lists its claim's return lines in id order (RETURN-ORDER), so lines and their statuses compare as they are.
        static string Json(PageResult<ReturnListView> result) => JsonSerializer.Serialize(result.Items);
        var (expectedJson, actualJson) = (Json(expected), Json(actual));
        var at = Enumerable.Range(0, Math.Min(expectedJson.Length, actualJson.Length)).FirstOrDefault(index => expectedJson[index] != actualJson[index]);
        Assert.True(expectedJson == actualJson, $"items differ: {what}: [{Rows(expected)}] vs [{Rows(actual)}] at {at}: {expectedJson[Math.Max(0, at - 300)..Math.Min(expectedJson.Length, at + 200)]} ||| {actualJson[Math.Max(0, at - 300)..Math.Min(actualJson.Length, at + 200)]}");
        Assert.True((expected.HasMore, expected.TotalCount) == (actual.HasMore, actual.TotalCount), $"counts differ: {what} ({expected.HasMore}, {expected.TotalCount}) vs ({actual.HasMore}, {actual.TotalCount})");
        // A cursor is sealed with a fresh nonce each time: compare the card it names.
        Assert.True(Decode(cursors, expected.NextCursor) == Decode(cursors, actual.NextCursor), $"cursors differ: {what}");
    }

    private static (string Sort, DateTimeOffset? Date, Guid ClaimId, int Kind)? Decode(CursorCodec cursors, string? cursor) =>
        cursor is not null && cursors.TryDecodeReturn(cursor, out var sort, out var date, out var claimId, out var kind) ? (sort, date, claimId, kind) : null;

    private static CursorCodec NewCursors() => new(new EphemeralDataProtectionProvider(), TimeProvider.System);

    private static MarketplaceSalesService NewService(AppDbContext db, CursorCodec cursors) =>
        new(db, cursors, new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    // The rich seed mixes every line status; the live-like one (measuring) gives each claim one line status that follows the
    // claim, with one claim in forty split, as live has almost none (0 of 252 claims on 15 Nov 2026).
    private async Task<Guid> SeedAsync(int count = 72, bool liveLike = false)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"return-eq-{Guid.NewGuid():N}", DisplayName = "return-eq", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        PlatformConnection Connection(string platform, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = name, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        PlatformConnection[] connections = [Connection("TRENDYOL", "Trendyol Mağaza"), Connection("HEPSIBURADA", "Hepsiburada Mağaza"), Connection("N11", "N11 Mağaza")];
        string[] customers = ["""{"customerFirstName":"Ayşe","customerLastName":"Yılmaz"}""", """{"customerFirstName":"Mehmet"}""", """{"customerFirstName":"Işıl","customerLastName":"Eq"}""", "{}"];
        ReturnClaimStatus[] statuses = Enum.GetValues<ReturnClaimStatus>();
        string?[] lineStatuses = ["Accepted", "WaitingInAction", "Rejected", null, " ", "Created", "InTransit", "Approved", "Completed", "Weird"];
        // Spaces and other white space (tab, line feed, no-break, em and ideographic space) around the values: .NET Trim strips
        // them all, so the database filter must too.
        (string? Code, string? Text)[] reasons = [("SIZE", "Beden küçük"), (null, "Beğenmedim"), ("DEFECT", null), (null, null), (" SIZE", "Büyük"), ("SIZE ", "x"), ("　", "Beğenmedim\n")];
        string?[] cargo = [" Aras Kargo", "Aras Kargo", "Yurtiçi", null, "Yurtiçi ", " Aras Kargo\t", "\u0085Yurtiçi\r\n", "Aras\n Kargo"];
        var requestedBase = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var orders = new List<Order>();
        var orderLines = new List<OrderLine>();
        var claims = new List<ReturnClaim>();
        var returnLines = new List<ReturnLine>();
        var decisions = new List<ReturnDecision>();
        var packages = new List<ShipmentPackage>();

        for (var i = 0; i < count; i++)
        {
            var connection = connections[i % connections.Length];
            var orderedAt = now.AddDays(-(i % 6)).AddHours(-(i % 4));
            var order = new Order
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                ExternalOrderId = $"EQ-{i}",
                OrderNumber = $"EQ-{1000 + i}",
                Currency = "TRY",
                GrossAmount = 100m + i,
                DiscountAmount = 0m,
                NetAmount = 100m + i,
                OrderedAt = new DateTimeOffset(orderedAt.Year, orderedAt.Month, orderedAt.Day, orderedAt.Hour, 0, 0, TimeSpan.Zero),
                LastRemoteModifiedAt = orderedAt,
                CustomerSnapshotJson = customers[i % customers.Length],
                ShipmentAddressSnapshotJson = "{}",
                InvoiceAddressSnapshotJson = "{}",
                DerivedStatus = "DELIVERED",
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            orders.Add(order);
            var lines = Enumerable.Range(0, 1 + i % 3).Select(j => new OrderLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                OrderId = order.Id,
                ExternalLineId = $"EQ-SKU-{i}-{j}",
                Sku = $"EQ-SKU-{i}-{j}",
                Barcode = $"868{i:D6}",
                TitleSnapshot = $"Ürün {i} {j}",
                OrderedQuantity = 2,
                DeliveredQuantity = 2,
                ShippedQuantity = 2,
                UnitPrice = 50m,
                VatRate = 20m,
                RawStatus = "Delivered"
            }).ToList();
            orderLines.AddRange(lines);
            var status = statuses[(i * 7) % statuses.Length];
            string?[] raws = liveLike
                ? [.. lines.Select(_ => status switch { ReturnClaimStatus.Approved or ReturnClaimStatus.Completed => "Accepted", ReturnClaimStatus.Rejected => "Rejected", ReturnClaimStatus.ActionRequired => "WaitingInAction", _ => null })]
                : [.. lines.Select((_, j) => lineStatuses[(i + 3 * j) % lineStatuses.Length])];
            var split = liveLike ? i % 40 : i % 6;
            // Claims that split (accepted beside waiting), claims approved in part (accepted beside rejected).
            if (split == 1 && lines.Count > 1) { status = ReturnClaimStatus.ActionRequired; raws[0] = "Accepted"; raws[1] = "WaitingInAction"; }
            if (split == 2 && lines.Count > 1) { status = ReturnClaimStatus.Approved; raws[0] = "Approved"; raws[1] = null; }
            if (split == 3 && lines.Count > 1) { status = ReturnClaimStatus.Completed; raws[0] = "Accepted"; raws[1] = "Rejected"; }
            if (split == 4 && lines.Count > 2) { status = ReturnClaimStatus.InTransit; raws[0] = "Completed"; raws[2] = " "; }
            var reason = reasons[i % reasons.Length];
            var decided = i % 4 == 0 ? now.AddMinutes(-5) : i % 4 == 1 ? now.AddDays(-2) : (DateTimeOffset?)null;
            var claim = new ReturnClaim
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                OrderId = order.Id,
                ExternalClaimId = $"eq-claim-{i}",
                Status = status,
                RawStatus = status.ToString(),
                CargoProviderName = cargo[i % cargo.Length],
                ReasonCode = reason.Code,
                ReasonText = reason.Text,
                LastRemoteModifiedAt = i % 11 == 0 ? null : now.AddMinutes(-(i % 8)),
                RequestedAt = i % 7 == 0 ? null : requestedBase.AddHours(-(i % 5)),
                DecidedAt = decided,
                RejectedPackageState = status == ReturnClaimStatus.Rejected ? (i % 3 == 0 ? ReturnRejectedPackageStates.AwaitingCargo : i % 3 == 1 ? null : ReturnRejectedPackageStates.Shipped) : null,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            claims.Add(claim);
            for (var j = 0; j < lines.Count; j++)
                returnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = lines[j].Id, ExternalLineId = $"r-{i}-{j}", Quantity = 1m, RawStatus = raws[j] });
            // A second unit of the same order line, with the same status (Trendyol sends one claim item per unit).
            if (i % 5 == 0)
                returnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = lines[0].Id, ExternalLineId = $"r-{i}-0b", Quantity = 1m, RawStatus = raws[0] });
            if (status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed or ReturnClaimStatus.Rejected && i % 2 == 0)
            {
                var action = status == ReturnClaimStatus.Rejected ? "REJECT" : "APPROVE";
                decisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, Action = action, IdempotencyKey = $"eq-{tenant.Id:N}-{i}-a", Status = "SUCCEEDED", CreatedAt = now, CompletedAt = i % 4 == 0 ? null : now.AddHours(-(i % 3)) });
                if (i % 3 == 0)
                    decisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, Action = action, IdempotencyKey = $"eq-{tenant.Id:N}-{i}-b", Status = "SUCCEEDED", CreatedAt = now, CompletedAt = now.AddMinutes(-(i % 7)) });
            }
            if (i % 3 != 2)
                packages.Add(new ShipmentPackage
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    ConnectionId = connection.Id,
                    OrderId = order.Id,
                    ExternalPackageId = $"eq-pkg-{i}",
                    GrossAmount = 100m,
                    NetAmount = 100m,
                    Status = ShipmentPackageStatus.Delivered,
                    RawStatus = i % 4 == 0 ? "Invoiced" : "Delivered",
                    StatusOccurredAt = now.AddDays(-1),
                    MarketplaceInvoiceStatus = (MarketplaceInvoiceStatus)(i % 5),
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1
                });
        }

        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(connections);
        db.Orders.AddRange(orders);
        db.OrderLines.AddRange(orderLines);
        db.ShipmentPackages.AddRange(packages);
        db.ReturnClaims.AddRange(claims);
        db.ReturnLines.AddRange(returnLines);
        db.ReturnDecisions.AddRange(decisions);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.ReturnDecisions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // Keeps every query a read sends; RowsAsync counts the rows each returns by running it again under count(*).
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(parameter => parameter.Clone()).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public async Task<long> RowsAsync()
        {
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION"));
            await connection.OpenAsync();
            long rows = 0;
            foreach (var (sql, parameters) in Commands)
            {
                await using var command = new NpgsqlCommand($"SELECT count(*) FROM ({sql}) AS measured", connection);
                command.Parameters.AddRange(parameters.Select(parameter => parameter.Clone()).ToArray());
                rows += (long)(await command.ExecuteScalarAsync())!;
            }
            return rows;
        }
    }
}
