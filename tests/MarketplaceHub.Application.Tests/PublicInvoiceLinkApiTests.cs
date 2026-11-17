using System.Net;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decision 17 Nov 2026 (MarketplaceInvoiceLinkPolicy): new deliveries carry E-Faturam's URL, but every panel link
// already delivered keeps answering through the real API pipeline, under /content and its .pdf alias, without signing in.
// A token minted for E-Faturam's PDF row (whose link is not a panel link) is not served.
[Collection("postgres-integration")]
public sealed class PublicInvoiceLinkApiTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private const string PublicBaseUrl = "https://hub.ravencia-test.com/";

    private WebApplicationFactory<SessionAuthMiddleware> Host() => fixture.Host!.WithWebHostBuilder(builder => builder.UseSetting("Marketplace:PublicBaseUrl", PublicBaseUrl));

    [PostgreSqlFact]
    public async Task AnAlreadyDeliveredPanelLinkStillServesThePdf()
    {
        using var host = Host();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% OLD PANEL LINK\n%%EOF\n");
        string panelLink, providerRowToken;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<IPrivateFileStorage>();
            var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            var now = DateTimeOffset.UtcNow;
            var tenantId = fixture.TenantId;
            var marketplace = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "PTTAVM", Environment = "PRODUCTION", DisplayName = $"ptt {fixture.Run}", ExternalStoreId = $"pub-ptt-{fixture.Run}", Status = "ACTIVE", ApiVersion = "V1" };
            var provider = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL_EFATURAM", Environment = "PRODUCTION", DisplayName = $"efaturam {fixture.Run}", ExternalStoreId = $"pub-ef-{fixture.Run}", Status = "ACTIVE", ApiVersion = "1.0.0" };
            var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = marketplace.Id, ExternalOrderId = $"pub-{fixture.Run}", OrderNumber = $"pub-{fixture.Run}", Currency = "TRY", GrossAmount = 120m, NetAmount = 120m, OrderedAt = now.AddDays(-3), LastRemoteModifiedAt = now.AddDays(-3), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
            var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now };
            var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenantId, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = now, UpdatedAt = now };
            var invoice = new Invoice { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, OrderFiscalGuarded = true, ProviderConnectionId = provider.Id, LegalEntityProfileId = profile.Id, InvoicePolicyId = policy.Id, InvoiceType = "EARSIVFATURA", SequencePurpose = "SALE", Status = InvoiceStatus.Completed, Currency = "TRY", TaxExclusiveTotal = 100m, TaxTotal = 20m, PayableTotal = 120m, Note = InvoiceAmounts.TurkishInvoiceNote(120m), IdempotencyKey = $"pub:{Guid.NewGuid():N}", ExternalReference = $"remote-{Guid.NewGuid():N}", InvoiceNumber = "DAP2026000000555", IssuedAt = now.AddDays(-1), CreatedAt = now.AddDays(-1), UpdatedAt = now.AddDays(-1), Version = 1 };
            var assetId = Guid.CreateVersion7();
            await using var content = new MemoryStream(pdf, writable: false);
            var stored = await files.SaveAsync(tenantId, $"{assetId:N}-invoice.pdf", "application/pdf", content, pdf.LongLength, CancellationToken.None);
            var asset = new FileAsset { Id = assetId, TenantId = tenantId, Classification = "INVOICE_DOCUMENT", RelativePath = stored, OriginalNameSafe = "invoice.pdf", MimeType = "application/pdf", SizeBytes = pdf.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(pdf)), Status = "ACTIVE", CreatedAt = now };
            var providerRow = new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, DocumentType = "PDF", FileAssetId = asset.Id, Sha256 = asset.Sha256, PermanentUrl = "https://apigateway.trendyolecozum.com/api/invoice/documents/download/5072747", CreatedAt = now };
            var panelRow = new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, DocumentType = InvoiceDeliveredLinks.MarketplaceDeliveryDocumentType, FileAssetId = asset.Id, Sha256 = asset.Sha256, CreatedAt = now.AddMinutes(1) };
            panelRow.PermanentUrl = InvoiceDocumentLink.Create(protection, PublicBaseUrl, tenantId, invoice.Id, panelRow.Id);
            panelLink = panelRow.PermanentUrl;
            providerRowToken = InvoiceDocumentLink.Create(protection, PublicBaseUrl, tenantId, invoice.Id, providerRow.Id);
            db.PlatformConnections.AddRange(marketplace, provider);
            db.Orders.Add(order);
            db.LegalEntityProfiles.Add(profile);
            db.InvoicePolicies.Add(policy);
            db.Invoices.Add(invoice);
            db.FileAssets.Add(asset);
            db.InvoiceDocuments.AddRange(providerRow, panelRow);
            await db.SaveChangesAsync();
        }

        using var anonymous = host.CreateClient();
        foreach (var link in new[] { panelLink, InvoiceDocumentLink.PdfAlias(panelLink) })
        {
            using var response = await anonymous.GetAsync(new Uri(link).PathAndQuery);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(pdf, await response.Content.ReadAsByteArrayAsync());
        }
        using var notPanel = await anonymous.GetAsync(new Uri(providerRowToken).PathAndQuery);
        Assert.Equal(HttpStatusCode.NotFound, notPanel.StatusCode);
    }
}
