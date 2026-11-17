using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-9: the Returns page's invoice label is decided by the database (ReturnInvoiceLabelRows), the customer
// snapshot read in memory only where the database finds an invoice word in its raw text. The label of every claim must
// equal the in-memory rule (LegacyReturnListQueries.ReturnInvoiceLabelsAsync) and the label its view shows, for every
// label value, every invoice status, marketplace invoice status and package raw status, snapshots that hide the words
// in escapes, nested objects or non-text values, ties of date, and after each of the label's inputs changes.
[Collection("postgres-integration")]
public sealed class ReturnInvoiceLabelPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // Package raw statuses: the INVOICED word with white space, dotless i, a dotted capital I and the Kelvin sign.
    private static readonly string[] RawStatuses = ["Invoiced", " invoiced\t", "ınvoıced", "INVOICED　", "Invoiced x", "Delivered", "Inİvoiced", "Knvoiced", ""];

    // A JSON \uXXXX escape of every character, built so no tool reads it as a C# escape.
    private static string Escape(string text) => string.Concat(text.Select(ch => "\\" + "u" + ((int)ch).ToString("X4")));

    private static readonly string[] Snapshots =
    [
        "{}", "", "  ", "not json", "[1,2]", """{"customerFirstName":"Ayşe"}""",
        """{"invoiceStatus":"Invoiced"}""", """{"marketplaceInvoiceStatus":" received "}""", """{"a":{"InvoiceStatus":"REJECTED"}}""",
        "{\"invoiceStatus\":\"" + Escape("I") + "nvoiced\"}", """{"invoiceStatus":"NOT_INVOICED"}""", "{\"INVOICESTATUS\":\"notınvoıced\"}",
        """{"invoiceStatus":5,"x":{"invoiceStatus":"Invoiced"}}""", """{"invoiceStatus":"--","x":[{"invoiceStatus":"Rejected"}]}""",
        """{"invoiceStatus":"Invoiced","invoiceStatus":"Rejected"}""", """{"other":"Invoiced"}""", "{\"invoiceStatus\":\"" + Escape("REJECTED") + "\"}",
        "{\"invoice" + Escape("S") + "tatus\":\"Received\"}", "{\"invoiceStatus\":\"" + Escape("ı") + "nvo" + Escape("ı") + "ced\"}",
        """{"invoiceStatus":"invoiced later"}""", """{"invoiceStatus":true}""", """{"invoiceStatus":null,"y":{"marketplaceInvoiceStatus":"NotInvoiced"}}""",
        "{\"invoiceStatus\":\"Receıved\"}", "{\"invoiceStatus\":\"ſent\"}"
    ];

    [PostgreSqlFact]
    public async Task Every_claim_label_equals_the_in_memory_label_and_its_view_through_input_changes()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var compared = await CompareAsync(db, service, seed.TenantId, "seeded");
            var labels = await LabelsAsync(service, db, seed.TenantId);
            // Every label but BEKLIYOR without an order (a claim always has its order: a foreign key) turns up.
            foreach (var label in AllLabels)
                Assert.Contains(label, labels.Values);

            // The label's inputs change after the claims were written: nothing is stored, so the next read follows.
            var tenantId = seed.TenantId;
            await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Unknown)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.MarketplaceInvoiceStatus, MarketplaceInvoiceStatus.Received));
            compared += await CompareAsync(db, service, tenantId, "package marketplace status changed");
            await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.RawStatus == "Delivered").ExecuteUpdateAsync(set => set.SetProperty(x => x.RawStatus, "invoiced"));
            compared += await CompareAsync(db, service, tenantId, "package raw status changed");
            await db.Invoices.Where(x => x.TenantId == tenantId && x.Status == InvoiceStatus.Draft).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, InvoiceStatus.Completed));
            compared += await CompareAsync(db, service, tenantId, "invoice status changed");
            await db.PlatformConnections.Where(x => x.Id == seed.ProviderId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "DISABLED"));
            compared += await CompareAsync(db, service, tenantId, "provider connection disabled");
            await db.PlatformConnections.Where(x => x.Id == seed.ProviderId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "VERIFIED"));
            await db.Orders.Where(x => x.TenantId == tenantId).ExecuteUpdateAsync(set => set.SetProperty(x => x.CustomerSnapshotJson, """{"x":{"invoiceStatus":"Rejected"}}"""));
            compared += await CompareAsync(db, service, tenantId, "snapshots changed");
            // A newer package lands on every order: the latest package decides.
            var orderIds = await db.Orders.Where(x => x.TenantId == tenantId).Select(x => x.Id).ToListAsync();
            db.ShipmentPackages.AddRange(orderIds.Select((orderId, index) => Package(tenantId, seed.ConnectionId, orderId, $"late-{index}", DateTimeOffset.UtcNow.AddDays(1), (MarketplaceInvoiceStatus)(index % 5), index % 2 == 0 ? "Invoiced" : "Shipped")));
            await db.SaveChangesAsync();
            compared += await CompareAsync(db, service, tenantId, "newer packages");
            output.WriteLine($"{compared} claim labels compared");
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // The invoice filter keeps exactly the claims whose label it names.
    [PostgreSqlFact]
    public async Task The_invoice_filter_keeps_exactly_the_claims_with_that_label()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var expected = await LabelsAsync(service, db, seed.TenantId);
            foreach (var label in expected.Values.Distinct().Append("FATURA_YOK"))
            {
                var filtered = (await service.ReturnQueries.ApplyReturnInvoiceFilterAsync(db.ReturnClaims.Where(x => x.TenantId == seed.TenantId), seed.TenantId, label, CancellationToken.None)).Select(x => x.Id);

                var kept = await filtered.ToListAsync();
                Assert.True(expected.Where(x => x.Value == label).Select(x => x.Key).ToHashSet().SetEquals(kept), label);
            }
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // The database's text rules equal .NET's on values built to break them: every white space .NET trims, every letter
    // equal to an invoice word's letter ignoring case, escapes.
    [PostgreSqlFact]
    public async Task The_database_text_rules_equal_the_dotnet_rules()
    {
        var whiteSpace = Enumerable.Range(0, char.MaxValue + 1).Select(code => (char)code).Where(char.IsWhiteSpace).Select(ch => ch.ToString()).ToList();
        var lookAlike = Enumerable.Range(1, char.MaxValue).Where(code => code is < 0xD800 or > 0xDFFF)
            .Select(code => ((char)code).ToString()).Where(ch => "invoced".Contains(char.ToLowerInvariant(ch[0])) || char.ToUpperInvariant(ch[0]) is 'I' or 'N' or 'V' or 'O' or 'C' or 'E' or 'D' or 'R' or 'J' or 'T')
            .ToList();
        List<string> values = ["INVOICED", "invoiced", "Invoiced ", "INVOICE D", "INVOICEDD", "", " ", "x"];
        values.AddRange(whiteSpace.Select(ws => ws + "Invoiced" + ws));
        values.AddRange(whiteSpace.Select(ws => "Invo" + ws + "iced"));
        for (var at = 0; at < 8; at++)
            values.AddRange(lookAlike.Select(ch => "INVOICED"[..at] + ch + "INVOICED"[(at + 1)..]));
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION"));
        await connection.OpenAsync();
        var invoiced = ReturnSearchText.TrimmedIgnoreCasePattern("INVOICED");
        var blank = ReturnSearchText.BlankPattern;
        var words = ReturnSearchText.JsonWordsPattern("INVOICED", "RECEIVED", "REJECTED");
        var checkedValues = 0;
        foreach (var value in values.Distinct())
        {
            await using var command = new NpgsqlCommand("SELECT @v ~ ('(?p)' || @p1), @v ~ ('(?p)' || @p2), @j ~ ('(?p)' || @p3)", connection);
            command.Parameters.AddWithValue("v", value);
            command.Parameters.AddWithValue("j", "{\"invoiceStatus\":" + System.Text.Json.JsonSerializer.Serialize(value) + "}");
            command.Parameters.AddWithValue("p1", invoiced);
            command.Parameters.AddWithValue("p2", blank);
            command.Parameters.AddWithValue("p3", words);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            Assert.True(string.Equals(value.Trim(), "INVOICED", StringComparison.OrdinalIgnoreCase) == reader.GetBoolean(0), $"invoiced: U+{string.Join(" U+", value.Select(ch => ((int)ch).ToString("X4")))}");
            Assert.True(string.IsNullOrWhiteSpace(value) == reader.GetBoolean(1), $"blank: U+{string.Join(" U+", value.Select(ch => ((int)ch).ToString("X4")))}");
            // A superset: a value whose upper case holds a word is always found.
            var upper = value.ToUpperInvariant();
            if (upper.Contains("INVOICED", StringComparison.Ordinal) || upper.Contains("RECEIVED", StringComparison.Ordinal) || upper.Contains("REJECTED", StringComparison.Ordinal))
                Assert.True(reader.GetBoolean(2), $"words: U+{string.Join(" U+", value.Select(ch => ((int)ch).ToString("X4")))}");
            checkedValues++;
        }
        output.WriteLine($"{checkedValues} values checked");
    }

    private async Task<int> CompareAsync(AppDbContext db, MarketplaceSalesService service, Guid tenantId, string what)
    {
        db.ChangeTracker.Clear();
        var rows = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id)
            .Select(x => new ReturnQueryService.ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt, x.RejectedPackageState, x.DecidedAt, x.RequestedAt, x.CardSplit))
            .ToListAsync();
        var expected = await LegacyReturnListQueries.ReturnInvoiceLabelsAsync(db, tenantId, rows, CancellationToken.None);
        var actual = await service.ReturnQueries.ReturnInvoiceLabelsAsync(tenantId, rows, CancellationToken.None);
        var views = await service.ReturnQueries.ReturnViewsByIdAsync(tenantId, [.. rows.Select(x => x.Id)], CancellationToken.None);
        foreach (var row in rows)
        {
            Assert.True(expected[row.Id] == actual[row.Id], $"{what}: claim {row.ExternalClaimId}: {expected[row.Id]} expected, {actual[row.Id]} read");
            Assert.True(expected[row.Id] == views[row.Id].InvoiceStatus, $"{what}: claim {row.ExternalClaimId}: {expected[row.Id]} expected, the view shows {views[row.Id].InvoiceStatus}");
        }
        // The pages and the summary filtered on each label, with their counters, equal the card-by-card ones.
        var pages = 0;
        foreach (var label in AllLabels.Append("ALL"))
        {
            pages += await ComparePagesAsync(service, db, Cursors, tenantId, 80, new ReturnListQuery(InvoiceStatus: label, Sort: "ID"));
            var options = new ReturnListQuery(InvoiceStatus: label);
            var expectedSummary = await LegacyReturnListQueries.BuildReturnSummaryAsync(service, db, tenantId, options, CancellationToken.None);
            var actualSummary = await service.ReturnQueries.BuildReturnSummaryAsync(tenantId, options, ReturnQueryService.ReturnTabs.ToDictionary(x => x, _ => 0, StringComparer.Ordinal), CancellationToken.None);
            Assert.True(System.Text.Json.JsonSerializer.Serialize(expectedSummary) == System.Text.Json.JsonSerializer.Serialize(actualSummary), $"{what}: summary of {label}");
        }
        Assert.True(pages > AllLabels.Length, $"{what}: {pages} pages");
        return rows.Count;
    }

    private static readonly string[] AllLabels = ["FATURA_BEKLIYOR", "FATURA_BELIRSIZ", "FATURA_REDDEDILDI", "FATURA_PLATFORMA_AKTARILMADI", "FATURA_IPTAL", "FATURA_ISLENIYOR", "FATURA_KONTROLDE", "FATURA_BILINMIYOR"];

    private static readonly CursorCodec Cursors = new(new EphemeralDataProtectionProvider(), TimeProvider.System);

    private static async Task<Dictionary<Guid, string>> LabelsAsync(MarketplaceSalesService service, AppDbContext db, Guid tenantId)
    {
        var rows = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId)
            .Select(x => new ReturnQueryService.ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt, x.RejectedPackageState, x.DecidedAt, x.RequestedAt, x.CardSplit))
            .ToListAsync();
        return await LegacyReturnListQueries.ReturnInvoiceLabelsAsync(db, tenantId, rows, CancellationToken.None);
    }

    // Walks every page by cursor, the database page and the card-by-card reference side by side; returns the pages compared.
    internal static async Task<int> ComparePagesAsync(MarketplaceSalesService service, AppDbContext db, CursorCodec cursors, Guid tenantId, int limit, ReturnListQuery options)
    {
        var compared = 0;
        string? after = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var expected = await LegacyReturnListQueries.ReturnCardPageAsync(service, db, cursors, tenantId, limit, 1, after, options, CancellationToken.None);
            var actual = await service.ReturnQueries.ReturnCardPageAsync(tenantId, limit, 1, after, options, CancellationToken.None);
            Assert.True(System.Text.Json.JsonSerializer.Serialize(expected.Items) == System.Text.Json.JsonSerializer.Serialize(actual.Items), $"items differ: {System.Text.Json.JsonSerializer.Serialize(options)} page {compared}");
            Assert.True((expected.HasMore, expected.TotalCount) == (actual.HasMore, actual.TotalCount), $"counts differ: {System.Text.Json.JsonSerializer.Serialize(options)} page {compared}");
            Assert.Equal(Decode(cursors, expected.NextCursor), Decode(cursors, actual.NextCursor));
            compared++;
            if (actual.NextCursor is null) break;
            after = actual.NextCursor;
        }
        return compared;
    }

    private static (string Sort, DateTimeOffset? Date, Guid ClaimId, int Kind)? Decode(CursorCodec cursors, string? cursor) =>
        cursor is not null && cursors.TryDecodeReturn(cursor, out var sort, out var date, out var claimId, out var kind) ? (sort, date, claimId, kind) : null;

    private static MarketplaceSalesService NewService(AppDbContext db) =>
        new(db, Cursors, new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid ProviderId);

    private static readonly char[] WhiteSpace = [.. Enumerable.Range(0, char.MaxValue + 1).Select(code => (char)code).Where(char.IsWhiteSpace)];

    // A blank invoice number of its own (numbers are unique per issuer): the blank kind, then two white space characters.
    private static string Blank(string kind, int number) => kind + WhiteSpace[number % WhiteSpace.Length] + WhiteSpace[number / WhiteSpace.Length % WhiteSpace.Length];

    private static ShipmentPackage Package(Guid tenantId, Guid connectionId, Guid orderId, string key, DateTimeOffset at, MarketplaceInvoiceStatus status, string rawStatus) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalPackageId = $"label-pkg-{key}",
        GrossAmount = 100m,
        NetAmount = 100m,
        Status = ShipmentPackageStatus.Delivered,
        RawStatus = rawStatus,
        StatusOccurredAt = at,
        MarketplaceInvoiceStatus = status,
        CreatedAt = at,
        UpdatedAt = at,
        Version = 1
    };

    // One order and claim per case: every invoice status with each marketplace invoice status and invoice number kind;
    // without an invoice every marketplace invoice status with each package raw status; every snapshot without an
    // invoice or package evidence; ties of package date and of invoice date; an invoice of a disabled provider and a
    // correction invoice, which never count.
    private async Task<Seed> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"return-label-{Guid.NewGuid():N}", DisplayName = "return-label", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        PlatformConnection Connection(string platform, string status) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = status, ApiVersion = platform == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1" };
        var marketplace = Connection("TRENDYOL", "ACTIVE");
        var provider = Connection("TRENDYOL_EFATURAM", "ACTIVE");
        var disabled = Connection("TRENDYOL_EFATURAM", "DISABLED");
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = now, UpdatedAt = now };
        var orders = new List<Order>();
        var lines = new List<OrderLine>();
        var claims = new List<ReturnClaim>();
        var returnLines = new List<ReturnLine>();
        var packages = new List<ShipmentPackage>();
        var invoices = new List<Invoice>();
        var number = 0;

        Order NewCase(string snapshot)
        {
            var index = orders.Count;
            var order = new Order
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = marketplace.Id,
                ExternalOrderId = $"LB-{index}",
                OrderNumber = $"LB-{1000 + index}",
                Currency = "TRY",
                GrossAmount = 100m,
                NetAmount = 100m,
                OrderedAt = now.AddDays(-2),
                LastRemoteModifiedAt = now,
                CustomerSnapshotJson = snapshot,
                ShipmentAddressSnapshotJson = "{}",
                InvoiceAddressSnapshotJson = "{}",
                DerivedStatus = "DELIVERED",
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            orders.Add(order);
            var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = $"LB-L-{index}", Sku = $"LB-SKU-{index}", Barcode = $"LB{index:D6}", TitleSnapshot = $"Ürün {index}", OrderedQuantity = 1, DeliveredQuantity = 1, ShippedQuantity = 1, UnitPrice = 50m, VatRate = 20m, RawStatus = "Delivered" };
            lines.Add(line);
            var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = marketplace.Id, OrderId = order.Id, ExternalClaimId = $"lb-claim-{index}", Status = ReturnClaimStatus.Requested, RawStatus = "Created", CreatedAt = now, UpdatedAt = now, Version = 1 };
            claims.Add(claim);
            returnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = line.Id, ExternalLineId = $"lb-r-{index}", Quantity = 1m, RawStatus = "Created" });
            return order;
        }

        Invoice NewInvoice(Order order, InvoiceStatus status, string? invoiceNumber, DateTimeOffset createdAt, Guid? providerId = null, Guid? originalId = null) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            OrderId = order.Id,
            ProviderConnectionId = providerId ?? provider.Id,
            LegalEntityProfileId = profile.Id,
            InvoicePolicyId = policy.Id,
            InvoiceType = "EARSIVFATURA",
            SequencePurpose = "SALE",
            Status = status,
            Currency = "TRY",
            TaxExclusiveTotal = 100m,
            TaxTotal = 20m,
            PayableTotal = 120m,
            Note = "note",
            IdempotencyKey = $"label:{Guid.NewGuid():N}",
            InvoiceNumber = invoiceNumber is null ? null : invoiceNumber.Trim().Length == 0 ? Blank(invoiceNumber, ++number) : $"{invoiceNumber}{++number}",
            OriginalInvoiceId = originalId,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        var at = now.AddDays(-1);
        string?[] numbers = [null, "  ", "　", "LB"];
        foreach (var status in Enum.GetValues<InvoiceStatus>())
            foreach (var marketplaceStatus in Enum.GetValues<MarketplaceInvoiceStatus>())
                foreach (var invoiceNumber in numbers)
                {
                    var order = NewCase("{}");
                    invoices.Add(NewInvoice(order, status, invoiceNumber, at));
                    packages.Add(Package(tenant.Id, marketplace.Id, order.Id, $"{orders.Count}", at, marketplaceStatus, "Delivered"));
                }
        foreach (var marketplaceStatus in Enum.GetValues<MarketplaceInvoiceStatus>())
            foreach (var raw in RawStatuses)
            {
                var order = NewCase("""{"invoiceStatus":"Rejected"}""");
                packages.Add(Package(tenant.Id, marketplace.Id, order.Id, $"{orders.Count}", at, marketplaceStatus, raw));
            }
        foreach (var snapshot in Snapshots)
        {
            NewCase(snapshot);
            var withPackage = NewCase(snapshot);
            packages.Add(Package(tenant.Id, marketplace.Id, withPackage.Id, $"{orders.Count}", at, MarketplaceInvoiceStatus.Unknown, "Delivered"));
        }
        // Ties: two packages of one date, two invoices of one date; the newest id decides.
        for (var tie = 0; tie < 6; tie++)
        {
            var order = NewCase("{}");
            packages.Add(Package(tenant.Id, marketplace.Id, order.Id, $"{orders.Count}-a", at, (MarketplaceInvoiceStatus)(tie % 5), "Delivered"));
            packages.Add(Package(tenant.Id, marketplace.Id, order.Id, $"{orders.Count}-b", at, (MarketplaceInvoiceStatus)((tie + 2) % 5), tie % 2 == 0 ? "Invoiced" : "Delivered"));
            var invoiced = NewCase("{}");
            invoices.Add(NewInvoice(invoiced, (InvoiceStatus)(tie * 3 % 18), "LB", at));
            invoices.Add(NewInvoice(invoiced, (InvoiceStatus)((tie * 5 + 1) % 18), null, at));
        }
        // Never counted: an invoice of a disabled provider, a correction invoice of another one.
        var ignored = NewCase("{}");
        invoices.Add(NewInvoice(ignored, InvoiceStatus.UnknownResult, null, at, disabled.Id));
        var original = NewInvoice(NewCase("{}"), InvoiceStatus.Completed, "LB", at.AddDays(-1));
        invoices.Add(original);
        invoices.Add(NewInvoice(ignored, InvoiceStatus.Rejected, null, at.AddHours(1), originalId: original.Id));
        // A newer invoice of the same order decides.
        var twice = NewCase("{}");
        invoices.Add(NewInvoice(twice, InvoiceStatus.Rejected, null, at));
        invoices.Add(NewInvoice(twice, InvoiceStatus.Cancelled, null, at.AddMinutes(1)));

        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(marketplace, provider, disabled);
        db.LegalEntityProfiles.Add(profile);
        db.InvoicePolicies.Add(policy);
        db.Orders.AddRange(orders);
        db.OrderLines.AddRange(lines);
        db.ShipmentPackages.AddRange(packages);
        db.ReturnClaims.AddRange(claims);
        db.ReturnLines.AddRange(returnLines);
        await db.SaveChangesAsync();
        // The correction points at its original: written after it.
        db.Invoices.AddRange(invoices.Where(x => x.OriginalInvoiceId is null));
        await db.SaveChangesAsync();
        db.Invoices.AddRange(invoices.Where(x => x.OriginalInvoiceId is not null));
        await db.SaveChangesAsync();
        return new(tenant.Id, marketplace.Id, provider.Id);
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.Invoices.Where(row => row.TenantId == tenantId && row.OriginalInvoiceId != null).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
