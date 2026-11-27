using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: the response schemas of the contract file are what the API really writes. A signed-in owner calls a
// representative set of operations of every endpoint file against PostgreSQL, after creating a little data through the
// API itself, and every answer - success or error - must be a status the document declares for that operation, in its
// media type, and its JSON must satisfy the declared schema (OpenApiSchemaValidator: no undeclared member, required
// members present, types, nulls and formats). For the answers a handler writes as an anonymous object and the document
// names with a record (Common/ResponseShapes.cs), the record re-serialized with the API's options is byte for byte the
// answer.
[Collection("postgres-integration")]
public sealed partial class ApiResponseSchemaTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    // The fixture for the class's other part (ApiResponseSchemaRound5Tests.cs).
    private ApiEndToEndFixture Fixture => fixture;

    // Every endpoint file of the API; each must have at least one probe whose success answer was checked.
    private static readonly string[] EndpointFiles =
    [
        "AuthEndpoints", "CatalogEndpoints", "CommissionTariffEndpoints", "ProductTransferEndpoints", "DashboardEndpoints", "InvoicingEndpoints",
        "MarketplaceEndpoints", "QuestionEndpoints", "JobEndpoints", "NotificationEndpoints", "PriceStockSendLogEndpoints", "ProductActivityEndpoints",
        "AdReportEndpoints", "ReportsEndpoints", "AiEndpoints", "AlertEmailEndpoints", "OffsiteBackupEndpoints", "SettingsEndpoints", "UserManagementEndpoints"
    ];

    // The validator itself refuses what the document does not allow.
    [Fact]
    public void TheValidatorRefusesAnswersTheSchemaDoesNotAllow()
    {
        var validator = OpenApiSchemaValidator.FromContractFile();
        var schema = validator.Response("GET", "/api/v1/catalog/categories/{id}", 200)!.Value.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
        const string valid = """{"id":"01a1e009-fd98-7e15-8291-67a3f5c72c66","parentId":null,"name":"A","path":"A","depth":0,"isLeaf":true,"isActive":true,"version":1}""";

        Assert.Empty(validator.Validate(Parse(valid), schema));
        Assert.Contains(validator.Validate(Parse(valid.Replace("\"isLeaf\":true,", "\"isLeaf\":true,\"extra\":1,")), schema), error => error.Contains("member extra is not declared"));
        Assert.Contains(validator.Validate(Parse(valid.Replace("\"name\":\"A\",", "")), schema), error => error.Contains("required member name is missing"));
        Assert.Contains(validator.Validate(Parse(valid.Replace("\"version\":1", "\"version\":\"1\"")), schema), error => error.Contains("$.version: string"));
        Assert.Contains(validator.Validate(Parse(valid.Replace("\"name\":\"A\"", "\"name\":null")), schema), error => error.Contains("$.name: null"));
        Assert.Contains(validator.Validate(Parse(valid.Replace("01a1e009-fd98-7e15-8291-67a3f5c72c66", "x")), schema), error => error.Contains("not a valid uuid"));
        Assert.Null(validator.Response("GET", "/api/v1/catalog/categories/{id}", 418));
    }

    [PostgreSqlFact]
    public async Task RealAnswersSatisfyTheDeclaredResponseSchemas()
    {
        // The API's own JSON options (web defaults; its encoder writes Turkish letters unescaped).
        var options = fixture.Host!.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var run = new ProbeRun(OpenApiSchemaValidator.FromContractFile(), options);
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Owner, ownNetwork: true);
        using (client)
        using (signIn)
        {
            await run.CheckAsync("AuthEndpoints", "POST", "/api/v1/auth/login", signIn);
            await run.SameJsonAsync<SessionStateView>(signIn);
            run.Csrf = csrf;
            run.Client = client;

            var token = await run.ProbeAsync("AuthEndpoints", "GET", "/api/v1/auth/csrf");
            run.Csrf = token.GetProperty("token").GetString()!;
            await run.ProbeAsync("AuthEndpoints", "GET", "/api/v1/auth/me", same: typeof(CurrentUserView));
            await run.ProbeAsync("AuthEndpoints", "GET", "/api/v1/auth/security-status", same: typeof(SecurityStatusView));
            await run.ProbeAsync("AuthEndpoints", "GET", "/api/v1/auth/sessions", same: typeof(IReadOnlyList<SignInSessionView>));

            // Catalog: a category, a brand, an attribute with values and a draft product, then their reads.
            var category = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/catalog/categories", body: new { name = $"Kategori {fixture.Run}", parentId = (Guid?)null }, expect: 201);
            var categoryId = category.GetProperty("id").GetGuid();
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/categories/{id}", $"/api/v1/catalog/categories/{categoryId}");
            await run.ProbeAsync("CatalogEndpoints", "PATCH", "/api/v1/catalog/categories/{id}", $"/api/v1/catalog/categories/{categoryId}",
                body: new { name = $"Kategori {fixture.Run} 2", parentId = (Guid?)null, isActive = true }, ifMatch: category.GetProperty("version").GetInt64());
            await run.ProbeAsync("CatalogEndpoints", "PATCH", "/api/v1/catalog/categories/{id}", $"/api/v1/catalog/categories/{categoryId}",
                body: new { name = "Eski", parentId = (Guid?)null, isActive = true }, ifMatch: 999_999, expect: 412);
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/categories");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/categories/{id}/attribute-requirements", $"/api/v1/catalog/categories/{categoryId}/attribute-requirements");
            var brand = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/catalog/brands", body: new { name = $"Marka {fixture.Run}" }, expect: 201);
            var brandId = brand.GetProperty("id").GetGuid();
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/brands");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/brands/{id}", $"/api/v1/catalog/brands/{brandId}");
            var attribute = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/catalog/attributes", body: new
            {
                code = $"renk-{fixture.Run}",
                name = "Renk",
                dataType = "SINGLE_SELECT",
                selectionMode = (string?)null,
                unit = (string?)null,
                values = new[] { new { value = "Kırmızı", sortOrder = 1 }, new { value = "Mavi", sortOrder = 2 } }
            }, expect: 201);
            var attributeId = attribute.GetProperty("id").GetGuid();
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/attributes");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/attributes/{id}/used-values", $"/api/v1/catalog/attributes/{attributeId}/used-values");
            var product = await run.ProbeAsync("CatalogEndpoints", "POST", "/api/v1/products", body: new
            {
                title = $"Ürün {fixture.Run}",
                description = "Açıklama",
                brandId,
                categoryId,
                status = "DRAFT",
                variants = new[] { new { sku = $"SKU-{fixture.Run}", barcode = (string?)null, modelCode = $"M-{fixture.Run}", defaultListPrice = 120.5m, defaultSalePrice = 99.9m, costPrice = 40m, onHand = 3m } }
            }, expect: 201);
            var productId = product.GetProperty("id").GetGuid();
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/products/{id}", $"/api/v1/products/{productId}");
            // The list leaves its null members out (ProductListJson): the schema must allow it.
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/products");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/products/summary");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/products/{id}/marketplace-codes", $"/api/v1/products/{productId}/marketplace-codes");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/inventory");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/imports");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/model-codes/holder", $"/api/v1/catalog/model-codes/holder?code=M-{fixture.Run}", same: typeof(ModelCodeHolderView));
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/model-codes/holder", "/api/v1/catalog/model-codes/holder?code=YOK", same: typeof(ModelCodeHolderView));
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/products/{id}", $"/api/v1/products/{Guid.NewGuid()}", expect: 404);
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/catalog/categories", "/api/v1/catalog/categories?limit=0", expect: 400);

            // A store connection, written directly (creating one through the API checks the store's credentials live).
            var connectionId = Guid.CreateVersion7();
            await using (var scope = fixture.Host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MarketplaceHub.Infrastructure.Persistence.AppDbContext>();
                db.PlatformConnections.Add(new PlatformConnection
                {
                    Id = connectionId,
                    TenantId = fixture.TenantId,
                    PublicId = Guid.CreateVersion7(),
                    PlatformCode = "TRENDYOL",
                    Environment = "PRODUCTION",
                    DisplayName = $"Trendyol {fixture.Run}",
                    ExternalStoreId = $"store-{fixture.Run}",
                    Status = "ACTIVE",
                    ApiVersion = "V1",
                    SettingsJson = "{}",
                    Version = 1
                });
                await db.SaveChangesAsync();
            }
            await run.ProbeAsync("CommissionTariffEndpoints", "GET", "/api/v1/connections/{connectionId}/commission-tariffs", $"/api/v1/connections/{connectionId}/commission-tariffs");
            await run.ProbeAsync("CommissionTariffEndpoints", "GET", "/api/v1/connections/{connectionId}/promotion-offers", $"/api/v1/connections/{connectionId}/promotion-offers");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/connections/{id}", $"/api/v1/connections/{connectionId}");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/connections/{id}/sync-policies", $"/api/v1/connections/{connectionId}/sync-policies");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/connections/{id}/capabilities", $"/api/v1/connections/{connectionId}/capabilities");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/connections/{id}/full-reset", $"/api/v1/connections/{connectionId}/full-reset");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/connections/{connectionId}/catalog-structure-runs", $"/api/v1/connections/{connectionId}/catalog-structure-runs");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/connections/{connectionId}/attribute-defaults", $"/api/v1/connections/{connectionId}/attribute-defaults");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/mappings/conflicts", $"/api/v1/mappings/conflicts?connectionId={connectionId}");
            await run.ProbeAsync("CatalogEndpoints", "GET", "/api/v1/products/{id}/listing-profiles/{connectionId}", $"/api/v1/products/{productId}/listing-profiles/{connectionId}", expect: null);
            await run.ProbeAsync("ProductTransferEndpoints", "GET", "/api/v1/products/transfer/summary");

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var monthAgo = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            await run.ProbeAsync("DashboardEndpoints", "GET", "/api/v1/dashboard/bootstrap");
            await run.ProbeAsync("DashboardEndpoints", "GET", "/api/v1/dashboard/performance", $"/api/v1/dashboard/performance?from={monthAgo}&to={today}");
            await run.ProbeAsync("DashboardEndpoints", "GET", "/api/v1/dashboard/cancellations", $"/api/v1/dashboard/cancellations?day={today}");

            await run.ProbeAsync("InvoicingEndpoints", "GET", "/api/v1/invoices");
            await run.ProbeAsync("InvoicingEndpoints", "GET", "/api/v1/invoice-workspace/summary");
            await run.ProbeAsync("InvoicingEndpoints", "GET", "/api/v1/invoice-workspace/page");
            await run.ProbeAsync("InvoicingEndpoints", "GET", "/api/v1/invoices/{id}", $"/api/v1/invoices/{Guid.NewGuid()}", expect: 404);

            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/connections");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/orders");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/orders/summary");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/orders/facets");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/shipments");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/returns");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/returns/summary");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/price-stock-monitor");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/api-health-signals");
            await run.ProbeAsync("MarketplaceEndpoints", "GET", "/api/v1/orders/{id}", $"/api/v1/orders/{Guid.NewGuid()}", expect: 404);

            var template = await run.ProbeAsync("QuestionEndpoints", "POST", "/api/v1/question-templates", body: new { title = "Kargo", text = "Siparişiniz yarın kargoda." }, expect: 201);
            await run.ProbeAsync("QuestionEndpoints", "PUT", "/api/v1/question-templates/{id}", $"/api/v1/question-templates/{template.GetProperty("id").GetGuid()}",
                body: new { title = "Kargo", text = "Siparişiniz bugün kargoda." }, ifMatch: template.GetProperty("version").GetInt64());
            await run.ProbeAsync("QuestionEndpoints", "GET", "/api/v1/question-templates");
            await run.ProbeAsync("QuestionEndpoints", "GET", "/api/v1/questions");
            await run.ProbeAsync("QuestionEndpoints", "GET", "/api/v1/questions/sync-state");
            await run.ProbeAsync("QuestionEndpoints", "GET", "/api/v1/questions/report-reasons", "/api/v1/questions/report-reasons?platform=TRENDYOL");

            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/page");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/counts");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/metrics");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/metrics/lanes");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/metrics/quota");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/metrics/order-latency");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/attention");
            await run.ProbeAsync("JobEndpoints", "GET", "/api/v1/jobs/{id}", $"/api/v1/jobs/{Guid.NewGuid()}", expect: 404);

            await run.ProbeAsync("NotificationEndpoints", "GET", "/api/v1/notifications");
            await run.ProbeAsync("NotificationEndpoints", "GET", "/api/v1/notifications/summary");
            await run.ProbeAsync("NotificationEndpoints", "POST", "/api/v1/notifications/read", body: new { ids = new[] { Guid.NewGuid() } }, same: typeof(NotificationsUpdatedView));

            await run.ProbeAsync("PriceStockSendLogEndpoints", "GET", "/api/v1/price-stock-send-log");
            await run.ProbeAsync("PriceStockSendLogEndpoints", "GET", "/api/v1/price-stock-send-log/counts");
            await run.ProbeAsync("ProductActivityEndpoints", "GET", "/api/v1/product-activity");
            await run.ProbeAsync("ProductActivityEndpoints", "GET", "/api/v1/product-activity/counts");
            await run.ProbeAsync("AdReportEndpoints", "GET", "/api/v1/reports/ad-report");

            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/sales", $"/api/v1/reports/sales?from={monthAgo}&to={today}");
            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/stock", $"/api/v1/reports/stock?from={monthAgo}&to={today}");
            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/order-profitability", $"/api/v1/reports/order-profitability?from={monthAgo}&to={today}");
            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/profit-rate");
            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/price-assumptions");
            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/payouts");
            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/payouts/lines", "/api/v1/reports/payouts/lines?q=yok");
            await run.ProbeAsync("ReportsEndpoints", "GET", "/api/v1/reports/sales", "/api/v1/reports/sales?from=dün&to=bugün", expect: 400);

            await run.ProbeAsync("AiEndpoints", "GET", "/api/v1/settings/ai");
            await run.ProbeAsync("AiEndpoints", "GET", "/api/v1/products/{id}/size-chart", $"/api/v1/products/{productId}/size-chart");
            await run.ProbeAsync("AlertEmailEndpoints", "GET", "/api/v1/settings/alert-email", same: typeof(MarketplaceHub.Infrastructure.Alerts.AlertEmailSettingsView));
            await run.ProbeAsync("OffsiteBackupEndpoints", "GET", "/api/v1/settings/offsite-backup", same: typeof(OffsiteBackupSettingsView));
            await run.ProbeAsync("UserManagementEndpoints", "GET", "/api/v1/settings/users", same: typeof(TenantUsersView));

            await run.ProbeAsync("SettingsEndpoints", "GET", "/api/v1/settings/shipping-label", same: typeof(VersionedSettingsView));
            var saved = await run.ProbeAsync("SettingsEndpoints", "PUT", "/api/v1/settings/shipping-label", body: new { printer = $"zebra-{fixture.Run}" }, same: typeof(VersionedSettingsView));
            await run.ProbeAsync("SettingsEndpoints", "PUT", "/api/v1/settings/shipping-label", body: new { printer = "x" }, expect: 428);
            await run.ProbeAsync("SettingsEndpoints", "GET", "/api/v1/settings/shipping-label", same: typeof(VersionedSettingsView));
            Assert.Equal(1, saved.GetProperty("version").GetInt64());
            await run.ProbeAsync("SettingsEndpoints", "GET", "/api/v1/settings/appearance", same: typeof(VersionedSettingsView));
            await run.ProbeAsync("SettingsEndpoints", "GET", "/api/v1/settings/resend-reminder", same: typeof(ResendReminderView));
            await run.ProbeAsync("SettingsEndpoints", "GET", "/api/v1/settings/platform-update", same: typeof(VersionedSettingsView));
        }

        Assert.True(run.Failures.Count == 0, string.Join("\n", run.Failures));
        var uncovered = EndpointFiles.Where(file => !run.CheckedSuccess.Contains(file)).ToList();
        Assert.True(uncovered.Count == 0, "No success answer checked for: " + string.Join(", ", uncovered));
        Assert.True(run.CheckedOperations.Count >= 80, $"Only {run.CheckedOperations.Count} answers checked.");
    }

    private sealed class ProbeRun(OpenApiSchemaValidator validator, JsonSerializerOptions api)
    {
        public HttpClient Client { get; set; } = null!;
        public string Csrf { get; set; } = string.Empty;
        public List<string> Failures { get; } = [];
        public HashSet<string> CheckedSuccess { get; } = new(StringComparer.Ordinal);
        public List<string> CheckedOperations { get; } = [];
        public HashSet<string> SucceededOperations { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Sends the request and checks the answer; returns its JSON (default when it has none). <paramref name="expect"/>
        /// is the status the probe must get (200 by default for a read; null accepts any status the document declares).
        /// </summary>
        public async Task<JsonElement> ProbeAsync(string file, string method, string path, string? url = null, object? body = null, long? ifMatch = null, int? expect = 200, Type? same = null, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url ?? path);
            if (method is not "GET")
            {
                request.Headers.Add("X-CSRF-TOKEN", Csrf);
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                if (content is not null) request.Content = content;
                else if (body is not null) request.Content = JsonContent.Create(body);
            }
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"v{ifMatch}\"");
            using var response = await Client.SendAsync(request);
            if (expect is { } status && (int)response.StatusCode != status && !(status == 200 && (int)response.StatusCode is 201 or 202 or 204 && method is not "GET"))
            {
                Failures.Add($"{method} {url ?? path}: {(int)response.StatusCode} where the probe expects {status}: {await response.Content.ReadAsStringAsync()}");
                return default;
            }
            var json = await CheckAsync(file, method, path, response);
            if (same is not null && (int)response.StatusCode is >= 200 and < 300) await SameJsonAsync(same, response);
            return json;
        }

        public async Task<JsonElement> CheckAsync(string file, string method, string path, HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync();
            var where = $"{method} {path} → {status}";
            JsonElement declared;
            try
            {
                if (validator.Response(method, path, status) is not { } found) { Failures.Add($"{where}: the document declares no {status} answer. Body: {Cut(text)}"); return default; }
                declared = found;
            }
            catch (InvalidOperationException exception) { Failures.Add($"{where}: {exception.Message}"); return default; }

            if (!declared.TryGetProperty("content", out var content))
            {
                if (text.Length > 0) Failures.Add($"{where}: the document declares no body, the answer has one: {Cut(text)}");
                return default;
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!content.TryGetProperty(mediaType, out var media)) { Failures.Add($"{where}: media type {mediaType} is not declared."); return default; }
            if (mediaType != "application/json" && !mediaType.EndsWith("+json", StringComparison.Ordinal))
            {
                // A file or a text: the document must say the body is a string (bytes in format binary, or text).
                if (!validator.IsStringSchema(media.GetProperty("schema"))) Failures.Add($"{where}: the {mediaType} answer is not declared as a string.");
                else if (text.Length == 0) Failures.Add($"{where}: the {mediaType} answer is empty.");
                else
                {
                    CheckedOperations.Add(where);
                    if (status is >= 200 and < 300) { CheckedSuccess.Add(file); SucceededOperations.Add($"{method} {path}"); }
                }
                return default;
            }
            var json = JsonDocument.Parse(text).RootElement.Clone();
            var errors = validator.Validate(json, media.GetProperty("schema"));
            foreach (var error in errors) Failures.Add($"{where}: {error}");
            CheckedOperations.Add(where);
            if (status is >= 200 and < 300 && errors.Count == 0) { CheckedSuccess.Add(file); SucceededOperations.Add($"{method} {path}"); }
            return json;
        }

        public Task SameJsonAsync<T>(HttpResponseMessage response) => SameJsonAsync(typeof(T), response);

        // The record the document names an anonymous answer by, read from the answer and written again with the API's
        // JSON options, is exactly the answer: the same members, in the same order, with the same values and nulls.
        public async Task SameJsonAsync(Type type, HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (text == "null") return;
            var value = JsonSerializer.Deserialize(text, type, api);
            var again = JsonSerializer.Serialize(value, type, api);
            if (again != text) Failures.Add($"{type.Name}: the record writes {Cut(again)} where the API wrote {Cut(text)}");
        }

        private static string Cut(string text) => text.Length > 400 ? text[..400] + "…" : text;
    }
}
