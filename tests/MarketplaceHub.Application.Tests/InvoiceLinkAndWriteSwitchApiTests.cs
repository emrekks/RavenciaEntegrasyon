using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Through the real API pipeline on the test database: a replaced manual invoice PDF stops being served (YENİ-01),
// READONLY does not get bearer invoice links (YENİ-02), the write switches are elevated like the master switch
// (YENİ-03), and an idempotency key holds for its route whatever the path's case or trailing slash (YENİ-06).
[Collection("postgres-integration")]
public sealed class InvoiceLinkAndWriteSwitchApiTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private WebApplicationFactory<SessionAuthMiddleware> Host() => fixture.Host!.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Marketplace:PublicBaseUrl", "https://hub.ravencia-test.com/");
        builder.UseSetting("FeatureFlags:ExternalWrites", "true");
    });

    private static async Task<(HttpClient Client, string Csrf)> SignInAsync(WebApplicationFactory<SessionAuthMiddleware> host, ApiEndToEndFixture fixture, MembershipRole role)
    {
        var client = host.CreateClient();
        using var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        var csrf = (await csrfResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using var signIn = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, new { email = fixture.Email(role), password = ApiEndToEndFixture.Password }));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        return (client, csrf);
    }

    private async Task<(Guid ConnectionId, Guid OrderId, Guid PackageId)> SeedOrderAsync(WebApplicationFactory<SessionAuthMiddleware> host, string code, bool externalWrites)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = $"sec {code}", ExternalStoreId = $"{code}-{fixture.Run}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = externalWrites ? """{"ExternalWritesEnabled":true}""" : "{}" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ConnectionId = connection.Id, ExternalOrderId = $"{code}-{fixture.Run}", OrderNumber = $"{code}-{fixture.Run}", Currency = "TRY", GrossAmount = 120m, NetAmount = 120m, OrderedAt = now.AddDays(-3), LastRemoteModifiedAt = now.AddDays(-3), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var package = new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, ConnectionId = connection.Id, OrderId = order.Id, ExternalPackageId = $"{code}-pkg-{fixture.Run}", GrossAmount = 120m, NetAmount = 120m, CargoProviderExternalId = "YKMP", Status = ShipmentPackageStatus.Delivered, RawStatus = "Delivered", StatusOccurredAt = now.AddDays(-1), MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.PlatformConnections.Add(connection); db.Orders.Add(order); db.ShipmentPackages.Add(package);
        await db.SaveChangesAsync();
        return (connection.Id, order.Id, package.Id);
    }

    private static async Task<JsonElement> UploadPdfAsync(HttpClient client, string csrf, Guid orderId, byte[] pdf)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(orderId.ToString("D")), "orderId");
        var file = new ByteArrayContent(pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "invoice.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoice-workspace/manual-documents") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string PathOf(string url) => new Uri(url).PathAndQuery;

    private static string PdfAlias(string url) => PathOf(url)[..^"content".Length] + "invoice.pdf";

    [PostgreSqlFact]
    public async Task A_replaced_manual_document_link_answers_404_and_its_file_is_archived()
    {
        using var host = Host();
        var (_, orderId, _) = await SeedOrderAsync(host, "y01", externalWrites: false);
        var (admin, csrf) = await SignInAsync(host, fixture, MembershipRole.Administrator);
        using (admin)
        {
            var oldPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% OLD invoice v1\n%%EOF\n");
            var newPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% NEW invoice v2\n%%EOF\n");
            var first = await UploadPdfAsync(admin, csrf, orderId, oldPdf);
            var oldUrl = first.GetProperty("documentUrl").GetString()!;
            using var anonymous = host.CreateClient();
            using (var beforeReplace = await anonymous.GetAsync(PathOf(oldUrl)))
                Assert.Equal(HttpStatusCode.OK, beforeReplace.StatusCode);

            var second = await UploadPdfAsync(admin, csrf, orderId, newPdf);
            var oldId = first.GetProperty("id").GetGuid();
            var newUrl = second.GetProperty("documentUrl").GetString()!;

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var oldRow = await db.ManualOrderInvoiceDocuments.AsNoTracking().SingleAsync(x => x.Id == oldId);
                Assert.Equal("REPLACED", oldRow.Status);
                var asset = await db.FileAssets.AsNoTracking().SingleAsync(x => x.Id == oldRow.FileAssetId);
                Assert.Equal("ARCHIVED", asset.Status);
                Assert.NotNull(asset.ArchivedAt);
            }

            using var content = await anonymous.GetAsync(PathOf(oldUrl));
            using var pdfAlias = await anonymous.GetAsync(PdfAlias(oldUrl));
            Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, pdfAlias.StatusCode);

            using var current = await anonymous.GetAsync(PathOf(newUrl));
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal(newPdf, await current.Content.ReadAsByteArrayAsync());
        }
    }

    [PostgreSqlFact]
    public async Task ReadOnly_gets_orders_and_the_invoice_workspace_without_bearer_invoice_links()
    {
        using var host = Host();
        var (_, orderId, packageId) = await SeedOrderAsync(host, "y02", externalWrites: false);
        var (admin, csrf) = await SignInAsync(host, fixture, MembershipRole.Administrator);
        string link;
        using (admin)
        {
            var uploaded = await UploadPdfAsync(admin, csrf, orderId, Encoding.ASCII.GetBytes("%PDF-1.4\n% READONLY\n%%EOF\n"));
            link = uploaded.GetProperty("documentUrl").GetString()!;
            // What a confirmed manual delivery records on the package (InvoicingJobProcessor).
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.ShipmentPackages.Where(x => x.Id == packageId).ExecuteUpdateAsync(u => u.SetProperty(x => x.MarketplaceInvoiceUrl, link));
            }
            // The roles that open invoice documents still get the link.
            using var adminOrders = await admin.GetAsync("/api/v1/orders?limit=100");
            Assert.Contains(link, await adminOrders.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var adminDetail = await admin.GetAsync($"/api/v1/orders/{orderId:D}");
            Assert.Contains(link, await adminDetail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var adminWorkspace = await admin.GetAsync("/api/v1/invoice-workspace/page?pageSize=100&tab=all");
            Assert.Contains(link, await adminWorkspace.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var (operations, _) = await SignInAsync(host, fixture, MembershipRole.Operations);
        using (operations)
        {
            using var operationsOrders = await operations.GetAsync("/api/v1/orders?limit=100");
            Assert.Contains(link, await operationsOrders.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var (readOnly, _) = await SignInAsync(host, fixture, MembershipRole.ReadOnly);
        using (readOnly)
        {
            using var orders = await readOnly.GetAsync("/api/v1/orders?limit=100");
            var ordersText = await orders.Content.ReadAsStringAsync();
            using var detail = await readOnly.GetAsync($"/api/v1/orders/{orderId:D}");
            var detailText = await detail.Content.ReadAsStringAsync();
            using var workspace = await readOnly.GetAsync("/api/v1/invoice-workspace/page?pageSize=100&tab=all");
            var workspaceText = await workspace.Content.ReadAsStringAsync();
            using var workspaceDefault = await readOnly.GetAsync("/api/v1/invoice-workspace/page?pageSize=100");
            var workspaceDefaultText = await workspaceDefault.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, orders.StatusCode);
            Assert.Contains($"{orderId:D}", ordersText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.Equal(HttpStatusCode.OK, workspace.StatusCode);
            Assert.Contains($"{orderId:D}", workspaceText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.OK, workspaceDefault.StatusCode);
            foreach (var text in new[] { ordersText, detailText, workspaceText, workspaceDefaultText })
            {
                Assert.DoesNotContain(link, text, StringComparison.Ordinal);
                Assert.DoesNotContain("/public/manual-invoice-documents/", text, StringComparison.Ordinal);
            }
        }
    }

    [PostgreSqlFact]
    public async Task Operations_may_not_change_a_write_switch_but_an_administrator_may()
    {
        using var host = Host();
        var (connectionId, _, _) = await SeedOrderAsync(host, "y03", externalWrites: true);
        var (operations, csrf) = await SignInAsync(host, fixture, MembershipRole.Operations);
        using (operations)
        {
            foreach (var type in new[] { "PRICE_WRITE", "STOCK_WRITE", "price_write", "SHIPMENT_WRITE" })
            {
                using var put = await operations.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"/api/v1/connections/{connectionId:D}/sync-policies/{type}", csrf, new { intervalSeconds = 0, overlapSeconds = 0, jitterSeconds = 0, enabled = true }));
                Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
            }
            // A read flow stays operational work.
            using var orders = await operations.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"/api/v1/connections/{connectionId:D}/sync-policies/ORDERS", csrf, new { intervalSeconds = 300, overlapSeconds = 0, jitterSeconds = 0, enabled = true }));
            Assert.NotEqual(HttpStatusCode.Forbidden, orders.StatusCode);
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var enabled = await db.ConnectionSyncPolicies.AsNoTracking().Where(x => x.ConnectionId == connectionId && x.Enabled).Select(x => x.ResourceType).ToListAsync();
            Assert.DoesNotContain("PRICE_WRITE", enabled);
            Assert.DoesNotContain("STOCK_WRITE", enabled);
            Assert.DoesNotContain("SHIPMENT_WRITE", enabled);
        }

        var (admin, adminCsrf) = await SignInAsync(host, fixture, MembershipRole.Administrator);
        using (admin)
        {
            using var put = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"/api/v1/connections/{connectionId:D}/sync-policies/STOCK_WRITE", adminCsrf, new { intervalSeconds = 0, overlapSeconds = 0, jitterSeconds = 0, enabled = false }));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }
    }

    [PostgreSqlFact]
    public async Task The_same_key_on_the_same_route_in_another_case_or_with_a_trailing_slash_does_not_run_again()
    {
        using var host = Host();
        var (admin, csrf) = await SignInAsync(host, fixture, MembershipRole.Administrator);
        using (admin)
        {
            var key = Guid.NewGuid().ToString();
            var body = new { printer = $"zebra-y06-{fixture.Run}" };
            HttpRequestMessage Keyed(string path) { var r = ApiTestHost.Json(HttpMethod.Put, path, csrf, body); r.Headers.Add("Idempotency-Key", key); return r; }

            using var first = await admin.SendAsync(Keyed("/api/v1/settings/shipping-label"));
            using var exactReplay = await admin.SendAsync(Keyed("/api/v1/settings/shipping-label"));
            using var slash = await admin.SendAsync(Keyed("/api/v1/settings/shipping-label/"));
            using var doubled = await admin.SendAsync(Keyed("/api/v1//settings/shipping-label"));
            using var upper = await admin.SendAsync(Keyed("/api/v1/settings/Shipping-Label"));

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.True(exactReplay.Headers.Contains("Idempotency-Replayed"));
            // The same request in another form: the first answer is replayed, the action does not run again.
            Assert.True(slash.Headers.Contains("Idempotency-Replayed"));
            Assert.Equal(first.Headers.ETag, slash.Headers.ETag);
            Assert.True(doubled.Headers.Contains("Idempotency-Replayed"));
            // Another case is refused as a reused key (a path value may be case-sensitive), never run again.
            Assert.Equal(HttpStatusCode.Conflict, upper.StatusCode);
            Assert.Contains("IDEMPOTENCY_KEY_REUSED", await upper.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var routes = await db.ApiIdempotencyRecords.AsNoTracking().Where(x => x.TenantId == fixture.TenantId && x.RouteTemplate.ToLower().Contains("shipping-label") && x.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5)).Select(x => x.RouteTemplate).ToListAsync();
            Assert.All(routes, route => Assert.Equal("/api/v1/settings/shipping-label", route));
        }
    }

    // Authorization matrix (6 Nov): the N11 return-shipment-company list is read from N11 live on every call, so like
    // the rejection reasons it is for the roles that work with orders, not READONLY.
    [PostgreSqlFact]
    public async Task ReadOnly_cannot_spend_marketplace_quota_on_return_shipment_companies()
    {
        using var host = Host();
        var path = $"/api/v1/returns/{Guid.NewGuid():D}/return-shipment-companies";
        var (readOnly, _) = await SignInAsync(host, fixture, MembershipRole.ReadOnly);
        using (readOnly)
        {
            using var refused = await readOnly.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("PERMISSION_DENIED", (await ApiTestHost.ProblemAsync(refused)).GetProperty("code").GetString());
        }
        var (operations, _) = await SignInAsync(host, fixture, MembershipRole.Operations);
        using (operations)
        {
            // Past the gate: the unknown return is the endpoint's own answer.
            using var unknown = await operations.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
    }

    // No invoice goes out without the owner's approval (6 Nov): issuing, cancelling, and sending or removing the
    // invoice link at the marketplace are for the owner and administrators; operations and accounting keep drafts,
    // previews, uploads and reads.
    public static TheoryData<string, string> InvoiceOutboundRoutes() => new()
    {
        { "POST", $"/api/v1/invoices/{Guid.NewGuid():D}/submit-jobs" },
        { "POST", $"/api/v1/invoices/{Guid.NewGuid():D}/cancellation-jobs" },
        { "POST", $"/api/v1/invoices/{Guid.NewGuid():D}/marketplace-delivery-jobs" },
        { "POST", $"/api/v1/invoices/{Guid.NewGuid():D}/delivery-retry-jobs" },
        { "POST", $"/api/v1/invoices/{Guid.NewGuid():D}/marketplace-link-revocations/{Guid.NewGuid():D}" },
        { "POST", "/api/v1/invoice-workspace/confirm" },
        { "POST", $"/api/v1/invoice-workspace/manual-documents/{Guid.NewGuid():D}/delivery-jobs" }
    };

    private static HttpRequestMessage Outbound(string method, string path, string csrf, object? body = null)
    {
        var request = ApiTestHost.Json(new HttpMethod(method), path, csrf, body ?? new { confirmed = true });
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
        return request;
    }

    [PostgreSqlTheory]
    [MemberData(nameof(InvoiceOutboundRoutes))]
    public async Task Only_the_owner_and_administrators_issue_cancel_or_send_invoices(string method, string path)
    {
        using var host = Host();
        foreach (var role in new[] { MembershipRole.Operations, MembershipRole.Accounting })
        {
            var (client, csrf) = await SignInAsync(host, fixture, role);
            using (client)
            {
                using var refused = await client.SendAsync(Outbound(method, path, csrf));
                Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{role} {method} {path}: {(int)refused.StatusCode}");
                Assert.Equal("PERMISSION_DENIED", (await ApiTestHost.ProblemAsync(refused)).GetProperty("code").GetString());
            }
        }
        var (admin, adminCsrf) = await SignInAsync(host, fixture, MembershipRole.Administrator);
        using (admin)
        {
            // Past the gate: the unknown invoice (or the unmatched preview) is the endpoint's own answer.
            using var passed = await admin.SendAsync(Outbound(method, path, adminCsrf));
            Assert.True(passed.StatusCode != HttpStatusCode.Forbidden, $"Administrator {method} {path}: {(int)passed.StatusCode} {await passed.Content.ReadAsStringAsync()}");
        }
    }

    [PostgreSqlFact]
    public async Task Invoice_drafting_and_reads_stay_open_to_operations_and_accounting()
    {
        using var host = Host();
        foreach (var role in new[] { MembershipRole.Operations, MembershipRole.Accounting })
        {
            var (client, csrf) = await SignInAsync(host, fixture, role);
            using (client)
            {
                using var validate = await client.SendAsync(Outbound("POST", $"/api/v1/invoices/{Guid.NewGuid():D}/validate", csrf));
                using var reconcile = await client.SendAsync(Outbound("POST", $"/api/v1/invoices/{Guid.NewGuid():D}/reconcile-jobs", csrf));
                using var preview = await client.SendAsync(Outbound("POST", "/api/v1/invoice-workspace/preview", csrf, new { orderIds = Array.Empty<Guid>() }));
                using var list = await client.GetAsync("/api/v1/invoices");
                Assert.All(new[] { validate, reconcile, preview, list }, response => Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode));
            }
        }
    }

    // INVOICED (the package's invoice number sent to the marketplace) is elevated like the invoice routes; the other
    // package actions stay operational work.
    [PostgreSqlFact]
    public async Task Only_the_owner_and_administrators_send_the_invoiced_package_status()
    {
        using var host = Host();
        var path = $"/api/v1/shipments/{Guid.NewGuid():D}/actions";
        var (operations, csrf) = await SignInAsync(host, fixture, MembershipRole.Operations);
        using (operations)
        {
            using var invoiced = await operations.SendAsync(Outbound("POST", path, csrf, new { action = " invoiced ", payloadJson = "{}" }));
            Assert.Equal(HttpStatusCode.Forbidden, invoiced.StatusCode);
            Assert.Equal("PERMISSION_DENIED", (await ApiTestHost.ProblemAsync(invoiced)).GetProperty("code").GetString());
            using var picking = await operations.SendAsync(Outbound("POST", path, csrf, new { action = "PICKING", payloadJson = "{}" }));
            Assert.Equal(HttpStatusCode.NotFound, picking.StatusCode);
        }
        var (admin, adminCsrf) = await SignInAsync(host, fixture, MembershipRole.Administrator);
        using (admin)
        {
            using var invoiced = await admin.SendAsync(Outbound("POST", path, adminCsrf, new { action = "INVOICED", payloadJson = "{}" }));
            Assert.Equal(HttpStatusCode.NotFound, invoiced.StatusCode);
        }
    }

    // The panel's copy of a hand-uploaded invoice PDF is personal data and stays out of browser and proxy caches,
    // like the system invoice documents and the public link.
    [PostgreSqlFact]
    public async Task A_manual_invoice_document_opened_in_the_panel_is_not_cached()
    {
        using var host = Host();
        var (_, orderId, _) = await SeedOrderAsync(host, "y07", externalWrites: false);
        var (admin, csrf) = await SignInAsync(host, fixture, MembershipRole.Administrator);
        using (admin)
        {
            var uploaded = await UploadPdfAsync(admin, csrf, orderId, Encoding.ASCII.GetBytes("%PDF-1.4\n% CACHE\n%%EOF\n"));
            using var content = await admin.GetAsync($"/api/v1/invoice-workspace/manual-documents/{uploaded.GetProperty("id").GetGuid():D}/content");
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.True(content.Headers.CacheControl is { NoStore: true, Private: true }, $"Cache-Control: {content.Headers.CacheControl}");
        }
    }
}
