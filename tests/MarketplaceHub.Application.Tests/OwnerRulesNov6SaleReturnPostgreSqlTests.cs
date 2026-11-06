using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner's rule of 6 Nov 2026 for sale invoices not issued yet, on a real database: an order returned in full gets no
// invoice and leaves the workspace's lists of work; a partly returned order is drafted for its kept units only; an
// open claim keeps its units on the invoice; returns after the approval stop the issue at the last check.
[Collection("postgres-integration")]
public sealed class OwnerRulesNov6SaleReturnPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AnOrderEveryUnitOfWhichCameBackGetsNoSaleInvoiceAndLeavesTheListsOfWork()
    {
        // Delivered, its order status not RETURNED: only the approved claim says every unit came back (live: 23 such
        // Trendyol orders without an invoice on 6 Nov 2026).
        var seed = NewSeed("nov6-full", gross: 300m, discount: 0m);
        var line = NewLine(seed, "Tam iade ürünü", 3m, 100m);
        var claim = NewClaim(seed, ReturnClaimStatus.Approved, "Accepted");
        await SeedAsync(seed, db =>
        {
            db.OrderLines.Add(line);
            db.ReturnClaims.Add(claim);
            db.ReturnLines.Add(NewReturnLine(seed, claim, line, 3m, "Accepted"));
        });

        try
        {
            await using var db = fixture.CreateContext();
            var service = NewBillingService(db);
            var preview = await service.PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id)], true), CancellationToken.None);
            var draft = await service.CreateDraftAsync(seed.Tenant.Id,
                new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null, true), "nov6-full", CancellationToken.None);

            Assert.Equal(InvoiceSaleReturnPolicy.FullyReturnedCode, preview.Error?.Code);
            Assert.Equal("Tüm ürünler iade edildi, fatura kesilmez.", preview.Error?.Message);
            Assert.Equal(InvoiceSaleReturnPolicy.FullyReturnedCode, draft.Error?.Code);
            Assert.Equal(422, draft.Error?.Status);

            var uninvoiced = await service.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(Tab: "UNINVOICED"), CancellationToken.None);
            var invoiced = await service.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(Tab: "INVOICED"), CancellationToken.None);
            var all = await service.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(Tab: "ALL"), CancellationToken.None);
            var summary = await service.WorkspaceSummaryAsync(seed.Tenant.Id, CancellationToken.None);
            Assert.Empty(uninvoiced.Items);
            Assert.Empty(invoiced.Items);
            var item = Assert.Single(all.Items);
            Assert.False(item.CanCreateInvoice);
            Assert.Equal(InvoicingBillingService.FullyReturnedNoInvoiceLabel, item.InvoiceStatus);
            Assert.Equal("FULL", item.SaleReturnScope);
            Assert.Equal("Tüm ürünler iade edildi, fatura kesilmez.", item.SaleReturnNote);
            Assert.Equal(3m, Assert.Single(item.Lines!).ReturnedQuantity);
            Assert.DoesNotContain("CREATE_FISCAL_INVOICE", item.AllowedActions ?? []);
            Assert.Equal(0, summary.UninvoicedCount);

            await using var verification = fixture.CreateContext();
            Assert.False(await verification.Invoices.AnyAsync(invoice => invoice.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task APartlyReturnedOrderIsDraftedForItsKeptUnitsOnly()
    {
        // Three items: a line of 2 with 1 returned and kept 1, and a line of 1 kept; 250 less a 25 discount.
        var seed = NewSeed("nov6-partial", gross: 250m, discount: 25m);
        var first = NewLine(seed, "Kısmi iade ürünü", 2m, 100m);
        var second = NewLine(seed, "Kalan ürün", 1m, 50m);
        var claim = NewClaim(seed, ReturnClaimStatus.Approved, "Accepted");
        await SeedAsync(seed, db =>
        {
            db.OrderLines.AddRange(first, second);
            db.ReturnClaims.Add(claim);
            db.ReturnLines.Add(NewReturnLine(seed, claim, first, 1m, "Accepted"));
        });

        try
        {
            await using var db = fixture.CreateContext();
            var service = NewBillingService(db);
            var previewed = await service.PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id)], true), CancellationToken.None);
            Assert.True(previewed.Succeeded, previewed.Error?.Message);
            var preview = Assert.Single(previewed.Value!);
            Assert.Equal(135m, preview.PayableTotal);
            Assert.Equal([1m, 1m], preview.Lines.Select(line => line.Quantity));
            var returned = Assert.Single(preview.ReturnedLines!);
            Assert.Equal(("Kısmi iade ürünü", 2m, 1m), (returned.Description, returned.Quantity, returned.ReturnedQuantity));
            // Still to invoice, with the returned item named.
            var uninvoiced = await service.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(Tab: "UNINVOICED"), CancellationToken.None);
            var item = Assert.Single(uninvoiced.Items);
            Assert.True(item.CanCreateInvoice);
            Assert.Equal("PARTIAL", item.SaleReturnScope);
            Assert.Equal("İade edilen ürünler faturaya girmez: Kısmi iade ürünü (1 adet).", item.SaleReturnNote);
            Assert.Equal([1m, 0m], item.Lines!.OrderByDescending(line => line.UnitPrice).Select(line => line.ReturnedQuantity));

            var draft = await service.CreateDraftAsync(seed.Tenant.Id,
                new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null, true), "nov6-partial", CancellationToken.None);
            Assert.True(draft.Succeeded, draft.Error?.Message);
            Assert.Equal(preview.PayableTotal, draft.Value!.PayableTotal);
            Assert.Equal(preview.TaxTotal, draft.Value.TaxTotal);
            await using var verification = fixture.CreateContext();
            var lines = await verification.InvoiceLines.Where(line => line.TenantId == seed.Tenant.Id).OrderBy(line => line.LineSequence).ToListAsync();
            // The lines follow the order lines' id order, which a version 7 id does not fix within one millisecond.
            Assert.Equal([(first.Id, 1m), (second.Id, 1m)], lines.Select(line => (line.OrderLineId!.Value, line.Quantity)).OrderBy(line => line.Value == second.Id));
            Assert.Equal(135m, lines.Sum(line => line.LineTotal));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AnOpenOrRejectedClaimKeepsItsUnitsOnTheInvoice()
    {
        var seed = NewSeed("nov6-open", gross: 300m, discount: 0m);
        var line = NewLine(seed, "Açık talep ürünü", 3m, 100m);
        var open = NewClaim(seed, ReturnClaimStatus.Requested, "Created");
        var rejected = NewClaim(seed, ReturnClaimStatus.Rejected, "Rejected");
        await SeedAsync(seed, db =>
        {
            db.OrderLines.Add(line);
            db.ReturnClaims.AddRange(open, rejected);
            db.ReturnLines.AddRange(NewReturnLine(seed, open, line, 1m, "Created"), NewReturnLine(seed, rejected, line, 1m, "Rejected"));
        });

        try
        {
            await using var db = fixture.CreateContext();
            var previewed = await NewBillingService(db).PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id)], true), CancellationToken.None);
            Assert.True(previewed.Succeeded, previewed.Error?.Message);
            var preview = Assert.Single(previewed.Value!);
            Assert.Equal(300m, preview.PayableTotal);
            Assert.Equal(3m, Assert.Single(preview.Lines).Quantity);
            Assert.Empty(preview.ReturnedLines!);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AReturnedPackageOfAMultiPackageOrderLeavesOnlyItsUnitsOut()
    {
        var seed = NewSeed("nov6-package", gross: 150m, discount: 0m, packageGross: 100m);
        var kept = NewLine(seed, "Teslim edilen ürün", 1m, 100m);
        var back = NewLine(seed, "İade paketi ürünü", 1m, 50m);
        var returnedPackage = NewPackage(seed, "nov6-package-returned", 50m, ShipmentPackageStatus.Returned);
        await SeedAsync(seed, db =>
        {
            db.OrderLines.AddRange(kept, back);
            db.ShipmentPackages.Add(returnedPackage);
            db.PackageLineAllocations.AddRange(NewAllocation(seed, seed.Package, kept), NewAllocation(seed, returnedPackage, back));
        });

        try
        {
            await using var db = fixture.CreateContext();
            var previewed = await NewBillingService(db).PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id)], true), CancellationToken.None);
            Assert.True(previewed.Succeeded, previewed.Error?.Message);
            var preview = Assert.Single(previewed.Value!);
            Assert.Equal(100m, preview.PayableTotal);
            Assert.Equal("Teslim edilen ürün", Assert.Single(preview.Lines).Description);
            Assert.Equal("İade paketi ürünü", Assert.Single(preview.ReturnedLines!).Description);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ReturnsApprovedAfterTheDraftStopTheIssueAtTheLastCheck()
    {
        var seed = NewSeed("nov6-last-check", gross: 300m, discount: 0m);
        var line = NewLine(seed, "Son kontrol ürünü", 3m, 100m);
        await SeedAsync(seed, db => db.OrderLines.Add(line));

        try
        {
            Guid invoiceId;
            await using (var db = fixture.CreateContext())
            {
                var draft = await NewBillingService(db).CreateDraftAsync(seed.Tenant.Id,
                    new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null, true), "nov6-last-check", CancellationToken.None);
                Assert.True(draft.Succeeded, draft.Error?.Message);
                Assert.Equal(300m, draft.Value!.PayableTotal);
                invoiceId = draft.Value.Id;
            }

            // One unit is returned after the approval: the remainder is never issued on its own, the owner is asked.
            var partial = NewClaim(seed, ReturnClaimStatus.Approved, "Accepted");
            await using (var db = fixture.CreateContext())
            {
                db.ReturnClaims.Add(partial);
                db.ReturnLines.Add(NewReturnLine(seed, partial, line, 1m, "Accepted"));
                await db.SaveChangesAsync();
            }
            await using (var db = fixture.CreateContext())
            {
                var invoice = await db.Invoices.AsNoTracking().SingleAsync(item => item.Id == invoiceId);
                var refusal = await InvoicePreIssueCheck.CheckAsync(db, invoice, CancellationToken.None);
                Assert.Equal(InvoiceIssuePolicy.PreIssueChangedCode, refusal?.Code);
                Assert.Contains("onaylanan 300,00 yerine şimdi 200,00", refusal!.Message);
            }

            // Then the rest comes back too: the order is returned in full and nothing is issued.
            var rest = NewClaim(seed, ReturnClaimStatus.Approved, "Accepted");
            await using (var db = fixture.CreateContext())
            {
                db.ReturnClaims.Add(rest);
                db.ReturnLines.Add(NewReturnLine(seed, rest, line, 2m, "Accepted"));
                await db.SaveChangesAsync();
            }
            await using (var db = fixture.CreateContext())
            {
                var invoice = await db.Invoices.AsNoTracking().SingleAsync(item => item.Id == invoiceId);
                Assert.Equal(InvoiceSaleReturnPolicy.FullyReturnedCode, (await InvoicePreIssueCheck.CheckAsync(db, invoice, CancellationToken.None))?.Code);
                var submit = await NewBillingService(db).EnqueueSubmitAsync(seed.Tenant.Id, invoice.Id, invoice.Version, "nov6-submit", "nov6-submit", CancellationToken.None);
                Assert.Equal(InvoiceSaleReturnPolicy.FullyReturnedCode, submit.Error?.Code);
                Assert.Equal("Tüm ürünler iade edildi, fatura kesilmez.", submit.Error?.Message);
            }
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private InvoicingBillingService NewBillingService(AppDbContext db)
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return new InvoicingBillingService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(), fixture.TimeProvider, null!);
    }

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package);

    private Seed NewSeed(string code, decimal gross, decimal discount, decimal? packageGross = null)
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"{code}-{Guid.NewGuid():N}",
            DisplayName = code,
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var marketplace = NewConnection(tenant.Id, "TRENDYOL", "seller-nov6");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", "efaturam-nov6");
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            ExternalOrderId = $"{code}-order",
            OrderNumber = $"{code}-order",
            Currency = "TRY",
            GrossAmount = gross,
            DiscountAmount = discount,
            NetAmount = gross - discount,
            OrderedAt = fixture.Now.AddDays(-5),
            LastRemoteModifiedAt = fixture.Now.AddDays(-5),
            CustomerSnapshotJson = "{\"customerFirstName\":\"Test\",\"customerLastName\":\"Müşteri\"}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var seed = new Seed(tenant, marketplace, provider, order, null!);
        var packageAmount = packageGross ?? gross;
        var package = NewPackage(seed, $"{code}-package", packageAmount, ShipmentPackageStatus.Delivered);
        package.DiscountAmount = packageGross is null ? discount : 0m;
        package.NetAmount = packageAmount - package.DiscountAmount;
        return seed with { Package = package };
    }

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} 6 Kasım testi",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = platformCode == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1"
    };

    private ShipmentPackage NewPackage(Seed seed, string externalId, decimal amount, ShipmentPackageStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ConnectionId = seed.Marketplace.Id,
        OrderId = seed.Order.Id,
        ExternalPackageId = externalId,
        GrossAmount = amount,
        NetAmount = amount,
        CargoProviderExternalId = "YKMP",
        Status = status,
        RawStatus = status.ToString(),
        StatusOccurredAt = fixture.Now.AddDays(-2),
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private static OrderLine NewLine(Seed seed, string title, decimal quantity, decimal unitPrice) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        OrderId = seed.Order.Id,
        ExternalLineId = $"line-{Guid.NewGuid():N}",
        Sku = $"SKU-{Guid.NewGuid():N}"[..12],
        TitleSnapshot = title,
        OrderedQuantity = quantity,
        UnitPrice = unitPrice,
        VatRate = 20m,
        SourceSnapshotJson = """{"vatRate":20}""",
        RawStatus = "Delivered"
    };

    private static PackageLineAllocation NewAllocation(Seed seed, ShipmentPackage package, OrderLine line) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        PackageId = package.Id,
        OrderLineId = line.Id,
        AllocatedQuantity = line.OrderedQuantity,
        SourceEventId = $"nov6:{package.Id:N}:1"
    };

    private ReturnClaim NewClaim(Seed seed, ReturnClaimStatus status, string rawStatus) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ConnectionId = seed.Marketplace.Id,
        OrderId = seed.Order.Id,
        ExternalClaimId = $"claim-{Guid.NewGuid():N}",
        Status = status,
        RawStatus = rawStatus,
        LastRemoteModifiedAt = fixture.Now.AddDays(-1),
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private static ReturnLine NewReturnLine(Seed seed, ReturnClaim claim, OrderLine line, decimal quantity, string rawStatus) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ClaimId = claim.Id,
        OrderLineId = line.Id,
        ExternalLineId = $"claim-line-{Guid.NewGuid():N}",
        Quantity = quantity,
        RawStatus = rawStatus
    };

    private async Task SeedAsync(Seed seed, Action<AppDbContext> extra)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.ShipmentPackages.Add(seed.Package);
        extra(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceSubmissionAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePartySnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PackageLineAllocations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
