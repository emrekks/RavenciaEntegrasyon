using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8, round 5: the success answers declared last (files, the authenticator steps, product media, invoice
// workspace writes, user management, off-site backup, mappings, the tariff rows' two shapes) checked against the
// document with real answers, as the first part of this class does. Every operation in Reached must have had a success
// answer validated; the one-time secrets (authenticator key, recovery codes, temporary password, consent address) are
// made by this run and never written anywhere.
public sealed partial class ApiResponseSchemaTests
{
    // The round-5 declarations the test host reaches with a success answer.
    private static readonly string[] Reached =
    [
        "POST /api/v1/auth/mfa/setup", "POST /api/v1/auth/mfa/confirm", "POST /api/v1/auth/recovery-codes/regenerate", "POST /api/v1/auth/mfa/challenge",
        "POST /api/v1/catalog/variant-codes/taken", "POST /api/v1/files/product-media", "GET /api/v1/files/product-media/{assetId}/content",
        "POST /api/v1/files/product-media-url", "PUT /api/v1/files/product-media-reconcile", "PUT /api/v1/files/product-media-family-reorder",
        "DELETE /api/v1/files/product-media-items", "DELETE /api/v1/files/product-media-item", "POST /api/v1/files/return-evidence",
        "PUT /api/v1/products/{id}/size-chart", "GET /api/v1/products/transfer/export.xlsx", "POST /api/v1/products/bulk-delete", "DELETE /api/v1/products/{id}",
        "GET /api/v1/imports/{id}/errors.csv",
        "GET /api/v1/mappings/categories", "GET /api/v1/mappings/brands", "GET /api/v1/mappings/attributes", "GET /api/v1/mappings/attribute-values",
        "GET /api/v1/mappings/categories/{localId}", "GET /api/v1/mappings/brands/{localId}", "GET /api/v1/mappings/attributes/{localId}", "GET /api/v1/mappings/attribute-values/{localId}",
        "GET /api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/rows",
        "PUT /api/v1/invoice-workspace/hidden", "PUT /api/v1/invoice-workspace/manual-status", "POST /api/v1/invoice-workspace/manual-documents",
        "POST /api/v1/invoice-workspace/manual-documents/{id}/delivery-jobs", "POST /api/v1/invoices/{id}/documents/manual",
        "GET /api/v1/invoices/{invoiceId}/documents/latest/content", "PUT /api/v1/invoices/{id}/shopify-status",
        "GET /api/v1/dashboard/integration-matrix", "POST /api/v1/notifications", "POST /api/v1/questions/sync", "GET /api/v1/settings/ai/usage",
        "POST /api/v1/settings/users", "POST /api/v1/settings/users/{id}/password-reset", "POST /api/v1/settings/users/{id}/sessions/revoke",
        "PUT /api/v1/settings/offsite-backup", "POST /api/v1/settings/offsite-backup/google/authorize", "POST /api/v1/settings/offsite-backup/google/disconnect"
    ];

    // A 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [PostgreSqlFact]
    public async Task RealAnswersOfTheRound5DeclarationsSatisfyTheirSchemas()
    {
        var fixture = Fixture;
        var options = fixture.Host!.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var run = new ProbeRun(OpenApiSchemaValidator.FromContractFile(), options);
        var tag = $"{fixture.Run}-r5";

        // The authenticator steps, as the administrator: set up, confirm, new recovery codes, then a new sign-in's second factor.
        var (admin, adminCsrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
        using (admin)
        using (adminSignIn)
        {
            run.Client = admin;
            run.Csrf = adminCsrf;
            var setup = await run.ProbeAsync("AuthEndpoints", "POST", "/api/v1/auth/mfa/setup", body: new { password = ApiEndToEndFixture.Password }, same: typeof(MfaSetupView));
            var secret = FromBase32(setup.GetProperty("secret").GetString()!.Replace(" ", "", StringComparison.Ordinal));
            var code = TotpService.Generate(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpService.StepSeconds);
            var confirmed = await run.ProbeAsync("AuthEndpoints", "POST", "/api/v1/auth/mfa/confirm", body: new { code }, same: typeof(RecoveryCodesView));
            var firstCodes = confirmed.GetProperty("recoveryCodes").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var regenerated = await run.ProbeAsync("AuthEndpoints", "POST", "/api/v1/auth/recovery-codes/regenerate",
                body: new { password = ApiEndToEndFixture.Password, recoveryCode = firstCodes[0] }, same: typeof(RecoveryCodesView));
            var codes = regenerated.GetProperty("recoveryCodes").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var (second, secondCsrf, secondSignIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
            using (second)
            using (secondSignIn)
            {
                run.Client = second;
                run.Csrf = secondCsrf;
                var challenged = await run.ProbeAsync("AuthEndpoints", "POST", "/api/v1/auth/mfa/challenge", body: new { recoveryCode = codes[0] }, same: typeof(SessionStateView));
                Assert.Equal("ACTIVE", challenged.GetProperty("state").GetString());
            }
        }

        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Owner, ownNetwork: true);
        using (client)
        using (signIn)
        {
            run.Client = client;
            run.Csrf = csrf;

            // Catalog: a product to hang pictures on, and two to delete.
            var category = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/catalog/categories", body: new { name = $"Kategori {tag}", parentId = (Guid?)null }, expect: 201);
            var brand = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/catalog/brands", body: new { name = $"Marka {tag}" }, expect: 201);
            async Task<JsonElement> ProductAsync(string suffix) => await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/products", body: new
            {
                title = $"Ürün {tag} {suffix}",
                description = "Açıklama",
                brandId = brand.GetProperty("id").GetGuid(),
                categoryId = category.GetProperty("id").GetGuid(),
                status = "DRAFT",
                variants = new[] { new { sku = $"SKU-{tag}-{suffix}", barcode = (string?)null, modelCode = $"M-{tag}-{suffix}", defaultListPrice = 120.5m, defaultSalePrice = 99.9m, costPrice = 40m, onHand = 3m } }
            }, expect: 201);
            var product = await ProductAsync("a");
            var productId = product.GetProperty("id").GetGuid();
            await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/catalog/variant-codes/taken", body: new { codes = new[] { $"SKU-{tag}-a", "YOK-1" } }, same: typeof(TakenVariantCodesView));

            using (var upload = new MultipartFormDataContent { { Image(), "file", "r5.png" }, { new StringContent(productId.ToString()), "productId" } })
            {
                var media = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/files/product-media", content: upload, expect: 201, same: typeof(ProductMediaView));
                var assetId = media.GetProperty("fileAssetId").GetGuid();
                var stored = $"/api/v1/files/product-media/{assetId}/content";
                await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/files/product-media/{assetId}/content", stored);
                var linked = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/files/product-media-url",
                    body: new { productId, variantId = (Guid?)null, url = $"https://images.example.com/{tag}.jpg", mediaRole = (string?)null, sortOrder = 5, altText = (string?)null }, expect: 201, same: typeof(ProductMediaUrlView));
                await run.ProbeAsync("CatalogEndpoints", "PUT", "/api/v1/files/product-media-reconcile",
                    body: new { productId, variantId = (Guid?)null, items = new[] { new { url = stored, sortOrder = 0 }, new { url = $"https://images.example.com/{tag}.jpg", sortOrder = 5 } }, altText = (string?)null }, same: typeof(ProductMediaReconciledView));
                await run.ProbeAsync("CatalogEndpoints", "PUT", "/api/v1/files/product-media-family-reorder", body: new { productId, urlsInOrder = new[] { stored } }, same: typeof(ProductFamilyMediaReorderedView));
                await run.ProbeAsync("AiEndpoints", "PUT", "/api/v1/products/{id}/size-chart", $"/api/v1/products/{productId}/size-chart", body: new { url = (string?)null }, same: typeof(SizeChartMarkedView));
                await run.ProbeAsync("CatalogEndpoints", "DELETE", "/api/v1/files/product-media-items", body: new { mediaIds = new[] { linked.GetProperty("id").GetGuid() } }, same: typeof(ProductMediaDeletedView));
                var current = await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/products/{id}", $"/api/v1/products/{productId}");
                await run.ProbeAsync("CatalogEndpoints", "DELETE", "/api/v1/files/product-media-item", body: new { productId, url = stored },
                    ifMatch: current.GetProperty("version").GetInt64(), same: typeof(ProductMediaItemDeletedView));
            }
            using (var evidence = new MultipartFormDataContent { { Image(), "file", "kanit.png" } })
                await run.ProbeAsync("MarketplaceEndpoints", "POST", "/api/v1/files/return-evidence", content: evidence, expect: 201, same: typeof(ReturnEvidenceView));
            await run.ProbeAsync("ProductTransferEndpoints", "GET", "/api/v1/products/transfer/export.xlsx");
            var doomed = await ProductAsync("b");
            await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/products/bulk-delete", body: new { productIds = new[] { doomed.GetProperty("id").GetGuid() } }, same: typeof(ProductDeleteResult));
            var single = await ProductAsync("c");
            await run.ProbeAsync("CatalogEndpoints", "DELETE", "/api/v1/products/{id}", $"/api/v1/products/{single.GetProperty("id").GetGuid()}",
                ifMatch: single.GetProperty("version").GetInt64(), same: typeof(ProductDeleteResult));
            var import = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/imports", body: new { sourceType = "CSV", connectionId = (Guid?)null }, expect: 201);
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/imports/{id}/errors.csv", $"/api/v1/imports/{import.GetProperty("id").GetGuid()}/errors.csv");

            // A Shopify store with one order and package, and a tariff of three models, written directly.
            var connectionId = Guid.CreateVersion7();
            var orderId = Guid.CreateVersion7();
            var packageId = Guid.CreateVersion7();
            var tariffId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await using (var scope = fixture.Host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.PlatformConnections.Add(new PlatformConnection
                {
                    Id = connectionId,
                    TenantId = fixture.TenantId,
                    PublicId = Guid.CreateVersion7(),
                    PlatformCode = "SHOPIFY",
                    Environment = "PRODUCTION",
                    DisplayName = $"Shopify {tag}",
                    ExternalStoreId = $"store-{tag}",
                    Status = "ACTIVE",
                    ApiVersion = "V1",
                    SettingsJson = "{}",
                    Version = 1
                });
                db.Orders.Add(new Order
                {
                    Id = orderId,
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    ExternalOrderId = $"o-{tag}",
                    OrderNumber = $"o-{tag}",
                    Currency = "TRY",
                    GrossAmount = 100m,
                    NetAmount = 100m,
                    OrderedAt = now.AddDays(-2),
                    LastRemoteModifiedAt = now.AddDays(-2),
                    CustomerSnapshotJson = "{}",
                    ShipmentAddressSnapshotJson = "{}",
                    InvoiceAddressSnapshotJson = "{}",
                    DerivedStatus = "NEW",
                    CreatedAt = now.AddDays(-2),
                    UpdatedAt = now,
                    Version = 1
                });
                db.ShipmentPackages.Add(new ShipmentPackage
                {
                    Id = packageId,
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    OrderId = orderId,
                    ExternalPackageId = $"p-{tag}",
                    GrossAmount = 100m,
                    NetAmount = 100m,
                    Status = ShipmentPackageStatus.New,
                    RawStatus = "open",
                    StatusOccurredAt = now.AddDays(-2),
                    MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
                    CreatedAt = now.AddDays(-2),
                    UpdatedAt = now,
                    Version = 1
                });
                db.CommissionTariffs.Add(new CommissionTariff
                {
                    Id = tariffId,
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    PlatformCode = "TRENDYOL",
                    PeriodStart = now.AddDays(-1),
                    PeriodEnd = now.AddDays(6),
                    PeriodText = "1 hafta",
                    FileName = "tarife.xlsx",
                    FileHash = tag,
                    UploadedAt = now,
                    RowCount = 3
                });
                for (var index = 0; index < 3; index++)
                    db.CommissionTariffRows.Add(new CommissionTariffRow
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = fixture.TenantId,
                        TariffId = tariffId,
                        Barcode = $"B{index}-{tag}",
                        ModelCode = $"MD{index}",
                        ProductName = $"Ürün {index}",
                        Size = "M",
                        Tier1Min = 300m,
                        Tier2Max = 299.99m,
                        Tier2Min = 200m,
                        Tier3Max = 199.99m,
                        Tier3Min = 100m,
                        Tier4Max = 99.99m,
                        Rate1 = 20m,
                        Rate2 = 18m,
                        Rate3 = 15m,
                        Rate4 = 12m,
                        BasisPrice = 250m,
                        CurrentRate = 18m,
                        CurrentSalePrice = 250m,
                        Stock = 4m
                    });
                await db.SaveChangesAsync();
            }

            var rows = $"/api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/rows";
            await run.ProbeAsync("CommissionTariffEndpoints", "GET", "/api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/rows", $"{rows}?limit=2", same: typeof(CommissionTariffRowPage));
            await run.ProbeAsync("CommissionTariffEndpoints", "GET", "/api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/rows", $"{rows}?limit=2&shape=compact", same: typeof(CommissionTariffCompactRowPage));
            await run.ProbeAsync("CommissionTariffEndpoints", "POST", "/api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/export",
                $"/api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/export", body: new { bulk = "1", tier = 1 }, expect: null);

            // Mappings of every kind: the list, and one record's (the JSON null while it has none); the writes answer an error here.
            foreach (var type in new[] { "categories", "brands", "attributes", "attribute-values" })
            {
                await run.ProbeAsync("CatalogEndpoints", "GET", $"/api/v1/mappings/{type}", $"/api/v1/mappings/{type}?connectionId={connectionId}", same: typeof(IReadOnlyList<CatalogMappingView>));
                await run.ProbeAsync("CatalogEndpoints", "GET", $"/api/v1/mappings/{type}/{{localId}}", $"/api/v1/mappings/{type}/{Guid.NewGuid()}?connectionId={connectionId}");
            }
            await run.ProbeAsync("CatalogEndpoints", "PUT", "/api/v1/mappings/brands/{localId}", $"/api/v1/mappings/brands/{brand.GetProperty("id").GetGuid()}",
                body: new { connectionId, snapshotId = Guid.NewGuid(), externalId = "x", status = "MAPPED" }, expect: null);

            // The invoice workspace's writes on the Shopify package.
            await run.ProbeAsync("InvoicingEndpoints", "PUT", "/api/v1/invoice-workspace/hidden", body: new { packageIds = new[] { packageId }, hidden = true }, same: typeof(InvoiceWorkspaceHiddenView));
            await run.ProbeAsync("InvoicingEndpoints", "PUT", "/api/v1/invoice-workspace/hidden", body: new { packageIds = new[] { packageId }, hidden = false }, same: typeof(InvoiceWorkspaceHiddenView));
            await run.ProbeAsync("InvoicingEndpoints", "PUT", "/api/v1/invoice-workspace/manual-status", body: new { packageIds = new[] { packageId }, status = "PENDING" }, same: typeof(InvoiceWorkspaceManualStatusView));
            await run.ProbeAsync("InvoicingEndpoints", "POST", "/api/v1/invoice-workspace/orders/{orderId}/reconcile-jobs", $"/api/v1/invoice-workspace/orders/{orderId}/reconcile-jobs", expect: null, same: typeof(JobAccepted));
            using (var manual = new MultipartFormDataContent
            {
                { new StringContent(orderId.ToString()), "orderId" }, { new StringContent($"https://invoices.example.com/{tag}.pdf"), "invoiceUrl" },
                { new StringContent("ABC2026000000045"), "invoiceNumber" }, { new StringContent("2026-11-01T10:00:00Z"), "issuedAt" }
            })
            {
                var document = await run.ProbeAsync("InvoicingEndpoints", "POST", "/api/v1/invoice-workspace/manual-documents", content: manual, same: typeof(ManualInvoiceDocumentView));
                await run.ProbeAsync("InvoicingEndpoints", "POST", "/api/v1/invoice-workspace/manual-documents/{id}/delivery-jobs",
                    $"/api/v1/invoice-workspace/manual-documents/{document.GetProperty("id").GetGuid()}/delivery-jobs", expect: 202, same: typeof(ManualInvoiceDeliveryQueuedView));
            }
            await run.ProbeAsync("InvoicingEndpoints", "POST", "/api/v1/invoice-workspace/orders/{orderId}/confirm-no-remote-invoice", $"/api/v1/invoice-workspace/orders/{orderId}/confirm-no-remote-invoice", expect: null);

            // A Shopify invoice on a second package: its manual document (stored, then the same file again), the latest
            // document's bytes, and its manual status.
            var invoiceId = Guid.CreateVersion7();
            await using (var scope = fixture.Host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var invoiceOrderId = Guid.CreateVersion7();
                var invoicePackageId = Guid.CreateVersion7();
                db.Orders.Add(new Order
                {
                    Id = invoiceOrderId,
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    ExternalOrderId = $"i-{tag}",
                    OrderNumber = $"i-{tag}",
                    Currency = "TRY",
                    GrossAmount = 120m,
                    NetAmount = 120m,
                    OrderedAt = now.AddDays(-2),
                    LastRemoteModifiedAt = now.AddDays(-2),
                    CustomerSnapshotJson = "{}",
                    ShipmentAddressSnapshotJson = "{}",
                    InvoiceAddressSnapshotJson = "{}",
                    DerivedStatus = "NEW",
                    CreatedAt = now.AddDays(-2),
                    UpdatedAt = now,
                    Version = 1
                });
                db.ShipmentPackages.Add(new ShipmentPackage
                {
                    Id = invoicePackageId,
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    OrderId = invoiceOrderId,
                    ExternalPackageId = $"ip-{tag}",
                    GrossAmount = 120m,
                    NetAmount = 120m,
                    Status = ShipmentPackageStatus.New,
                    RawStatus = "open",
                    StatusOccurredAt = now.AddDays(-2),
                    MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
                    CreatedAt = now.AddDays(-2),
                    UpdatedAt = now,
                    Version = 1
                });
                var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "masked", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now };
                var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ProviderConnectionId = connectionId, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = now, UpdatedAt = now };
                db.LegalEntityProfiles.Add(profile);
                db.InvoicePolicies.Add(policy);
                db.Invoices.Add(new Invoice
                {
                    Id = invoiceId,
                    TenantId = fixture.TenantId,
                    OrderId = invoiceOrderId,
                    PackageId = invoicePackageId,
                    ProviderConnectionId = connectionId,
                    LegalEntityProfileId = profile.Id,
                    InvoicePolicyId = policy.Id,
                    InvoiceType = "EARSIVFATURA",
                    SequencePurpose = "SALE",
                    Status = InvoiceStatus.Draft,
                    Currency = "TRY",
                    TaxExclusiveTotal = 100m,
                    TaxTotal = 20m,
                    PayableTotal = 120m,
                    Note = "Not",
                    IdempotencyKey = $"r5:{tag}",
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1
                });
                await db.SaveChangesAsync();
            }
            foreach (var duplicate in new[] { false, true })
            {
                using var upload = new MultipartFormDataContent { { Image(), "file", "fatura.png" } };
                var stored = await run.ProbeAsync("InvoicingEndpoints", "POST", "/api/v1/invoices/{id}/documents/manual", $"/api/v1/invoices/{invoiceId}/documents/manual",
                    content: upload, expect: duplicate ? 200 : 201, same: typeof(InvoiceDocumentUploadView));
                Assert.Equal(duplicate, stored.GetProperty("duplicate").GetBoolean());
            }
            await run.ProbeAsync("InvoicingEndpoints", "GET", "/api/v1/invoices/{invoiceId}/documents/latest/content", $"/api/v1/invoices/{invoiceId}/documents/latest/content");
            await run.ProbeAsync("InvoicingEndpoints", "PUT", "/api/v1/invoices/{id}/shopify-status", $"/api/v1/invoices/{invoiceId}/shopify-status", body: new { status = "UPLOADED" }, same: typeof(ShopifyInvoiceStatusView));

            await run.ProbeAsync("DashboardEndpoints", "GET", "/api/v1/dashboard/integration-matrix", same: typeof(IReadOnlyList<DashboardIntegrationRowView>));
            await run.ProbeAsync("NotificationEndpoints", "POST", "/api/v1/notifications",
                body: new { items = new[] { new { clientKey = $"r5-{tag}", severity = "INFO", category = "OTHER", title = "Deneme", message = "İleti", count = 1, shared = false, read = false } } }, same: typeof(NotificationsSavedView));
            await run.ProbeAsync("QuestionEndpoints", "POST", "/api/v1/questions/sync", body: new { kind = (string?)null }, expect: 202, same: typeof(QuestionSyncQueuedView));
            await run.ProbeAsync("AiEndpoints", "GET", "/api/v1/settings/ai/usage", same: typeof(AiUsageView));

            // Users: add one (a temporary password), reset its password, end its sessions.
            var email = $"r5-{tag}@example.invalid";
            await run.ProbeAsync("UserManagementEndpoints", "POST", "/api/v1/settings/users",
                body: new { email, displayName = "R5 Kullanıcı", role = "OPERATIONS", password = ApiEndToEndFixture.Password }, same: typeof(MarketplaceHub.Api.Settings.UserManagementEndpoints.AddedBody));
            Guid userId;
            await using (var scope = fixture.Host.Services.CreateAsyncScope())
                userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            await run.ProbeAsync("UserManagementEndpoints", "POST", "/api/v1/settings/users/{id}/password-reset", $"/api/v1/settings/users/{userId}/password-reset",
                body: new { password = ApiEndToEndFixture.Password }, same: typeof(TemporaryPasswordView));
            await run.ProbeAsync("UserManagementEndpoints", "POST", "/api/v1/settings/users/{id}/sessions/revoke", $"/api/v1/settings/users/{userId}/sessions/revoke",
                body: new { password = ApiEndToEndFixture.Password }, same: typeof(SessionsRevokedView));

            // Off-site backup: an OAuth client saved, Google's consent address, disconnected; the steps that need Google answer an error.
            await run.ProbeAsync("OffsiteBackupEndpoints", "PUT", "/api/v1/settings/offsite-backup", body: new
            {
                enabled = false,
                ageRecipient = (string?)null,
                folderMode = (string?)null,
                folderValue = (string?)null,
                keepDays = (int?)null,
                keepMonths = (int?)null,
                clientId = $"123456789-{fixture.Run}.apps.googleusercontent.com",
                clientSecret = $"r5-{fixture.Run}",
                password = ApiEndToEndFixture.Password
            }, same: typeof(OffsiteBackupSettingsView));
            await run.ProbeAsync("OffsiteBackupEndpoints", "POST", "/api/v1/settings/offsite-backup/google/authorize", body: new { password = ApiEndToEndFixture.Password }, same: typeof(OffsiteBackupAuthorizationView));
            await run.ProbeAsync("OffsiteBackupEndpoints", "POST", "/api/v1/settings/offsite-backup/google/complete", body: new { code = "x", state = "yok", error = (string?)null }, expect: null);
            await run.ProbeAsync("OffsiteBackupEndpoints", "POST", "/api/v1/settings/offsite-backup/google/disconnect", body: new { password = ApiEndToEndFixture.Password }, same: typeof(OffsiteBackupSettingsView));
            await run.ProbeAsync("OffsiteBackupEndpoints", "POST", "/api/v1/settings/offsite-backup/run", expect: null);
        }

        Assert.True(run.Failures.Count == 0, string.Join("\n", run.Failures));
        var missed = Reached.Where(operation => !run.SucceededOperations.Contains(operation)).ToList();
        Assert.True(missed.Count == 0, "No success answer validated for: " + string.Join(", ", missed));
    }

    private static ByteArrayContent Image()
    {
        var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var character in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal); bits += 5;
            if (bits < 8) continue;
            output.Add((byte)(buffer >> (bits - 8))); bits -= 8; buffer &= (1 << bits) - 1;
        }
        return [.. output];
    }
}
