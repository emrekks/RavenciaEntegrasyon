using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8, one paging format, the additive half: the page-numbered lists whose order is total also take limit and
// after and also answer nextCursor and hasMore (PagingContract). For each: the old request's body is the old body with
// only the two members added at its end, and the page the old parameters name is the page the cursor reaches, byte for
// byte. Where the order is not total the list gives no cursor and refuses one.
[Collection("postgres-integration")]
public sealed class ApiUnifiedPagingTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private static readonly string[] Added = ["nextCursor", "hasMore"];

    [PostgreSqlFact]
    public async Task Questions_TheCursorReachesThePageThePageNumberNames()
    {
        var connectionId = await ConnectionAsync("TRENDYOL");
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db =>
        {
            for (var index = 0; index < 5; index++)
                db.MarketplaceQuestions.Add(new MarketplaceQuestion
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    ExternalQuestionId = $"q{index}-{fixture.Run}",
                    Kind = "PRODUCT",
                    Status = "WAITING_FOR_ANSWER",
                    QuestionText = $"Soru {index}",
                    CreatedAt = now.AddMinutes(-index),
                    LastSyncedAt = now
                });
        });
        var (client, signIn) = await SignInAsync();
        using (client)
        using (signIn)
        {
            var first = await ReadAsync(client, "/api/v1/questions?limit=2");
            // Round 7: questions also answer the NumberedPage member pageSize, after the cursor members.
            AssertOldBody(first, ["pageSize"], "items", "page", "limit", "totalCount");
            var cursor = Cursor(first);
            var byPage = await ReadAsync(client, "/api/v1/questions?page=2&limit=2");
            var byCursor = await ReadAsync(client, $"/api/v1/questions?limit=2&after={cursor}");
            Assert.Equal(byPage, byCursor);
            // The cursor wins over the page number; the last page has no cursor.
            Assert.Equal(byPage, await ReadAsync(client, $"/api/v1/questions?page=1&limit=2&after={cursor}"));
            var last = await ReadAsync(client, $"/api/v1/questions?limit=2&after={Cursor(byCursor)}");
            Assert.Equal(await ReadAsync(client, "/api/v1/questions?page=3&limit=2"), last);
            Assert.False(JsonDocument.Parse(last).RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(last).RootElement.GetProperty("nextCursor").ValueKind);
            Assert.Equal(5, Ids(first).Concat(Ids(byCursor)).Concat(Ids(last)).Distinct().Count());

            // UPDATED leaves ties in the database's order: no cursor there, and none taken.
            var updated = JsonDocument.Parse(await ReadAsync(client, "/api/v1/questions?limit=2&sort=UPDATED")).RootElement;
            Assert.True(updated.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, updated.GetProperty("nextCursor").ValueKind);
            await RefusedAsync(client, $"/api/v1/questions?limit=2&sort=UPDATED&after={cursor}");
            // NumberedPage: page with pageSize names the page page with limit always named (the form the panel sends
            // where there is no cursor).
            Assert.Equal(await ReadAsync(client, "/api/v1/questions?page=2&limit=2&sort=UPDATED"), await ReadAsync(client, "/api/v1/questions?page=2&pageSize=2&sort=UPDATED"));
            // A cursor cut for another limit, or not a cursor, is refused.
            await RefusedAsync(client, $"/api/v1/questions?limit=3&after={cursor}");
            await RefusedAsync(client, "/api/v1/questions?limit=2&after=bozuk");
        }
    }

    [PostgreSqlFact]
    public async Task CommissionTariffRows_TheCursorReachesThePageThePageNumberNames_InBothShapes()
    {
        var connectionId = await ConnectionAsync("TRENDYOL");
        var tariffId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db =>
        {
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
                FileHash = fixture.Run,
                UploadedAt = now,
                RowCount = 5
            });
            for (var index = 0; index < 5; index++)
                db.CommissionTariffRows.Add(new CommissionTariffRow
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = fixture.TenantId,
                    TariffId = tariffId,
                    Barcode = $"B{index}-{fixture.Run}",
                    ModelCode = $"MD{index}",
                    // Two models share a name: the page order falls back to the model key.
                    ProductName = index < 2 ? "Aynı Ad" : $"Ürün {index}",
                    BasisPrice = 100m + index,
                    CurrentRate = 15m,
                    CurrentSalePrice = 100m + index
                });
        });
        var rows = $"/api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/rows";
        var (client, signIn) = await SignInAsync();
        using (client)
        using (signIn)
        {
            foreach (var shape in new[] { "", "&shape=compact" })
            {
                var first = await ReadAsync(client, $"{rows}?limit=2{shape}");
                if (shape.Length == 0) AssertOldBody(first, "items", "totalCount", "page", "pageSize");
                else AssertOldBody(first, "strings", "rows", "totalCount", "page", "pageSize");
                var byPage = await ReadAsync(client, $"{rows}?page=1&limit=2{shape}");
                var byCursor = await ReadAsync(client, $"{rows}?limit=2&after={Cursor(first)}{shape}");
                Assert.Equal(byPage, byCursor);
                var last = await ReadAsync(client, $"{rows}?limit=2&after={Cursor(byCursor)}{shape}");
                Assert.Equal(await ReadAsync(client, $"{rows}?page=2&limit=2{shape}"), last);
                Assert.False(JsonDocument.Parse(last).RootElement.GetProperty("hasMore").GetBoolean());
            }
            // Sorted in memory: no cursor, and none taken.
            var sorted = JsonDocument.Parse(await ReadAsync(client, $"{rows}?limit=2&sort=price")).RootElement;
            Assert.True(sorted.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, sorted.GetProperty("nextCursor").ValueKind);
            await RefusedAsync(client, $"{rows}?limit=2&sort=price&after={Cursor(await ReadAsync(client, $"{rows}?limit=2"))}");
            // NumberedPage under a sort: page with pageSize is page with limit.
            Assert.Equal(await ReadAsync(client, $"{rows}?page=1&limit=2&sort=price&shape=compact"), await ReadAsync(client, $"{rows}?page=1&pageSize=2&sort=price&shape=compact"));
        }
    }

    [PostgreSqlFact]
    public async Task InvoiceWorkspace_TheCursorReachesThePageThePageNumberNames()
    {
        var connectionId = await ConnectionAsync("TRENDYOL");
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db =>
        {
            for (var index = 0; index < 45; index++)
            {
                var order = new Order
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    ExternalOrderId = $"w{index}-{fixture.Run}",
                    OrderNumber = $"w{index}-{fixture.Run}",
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
                };
                db.Orders.Add(order);
                db.ShipmentPackages.Add(new ShipmentPackage
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = fixture.TenantId,
                    ConnectionId = connectionId,
                    OrderId = order.Id,
                    ExternalPackageId = $"wp{index}-{fixture.Run}",
                    GrossAmount = 100m,
                    NetAmount = 100m,
                    Status = ShipmentPackageStatus.New,
                    RawStatus = "open",
                    // Every third package shares its shipment time with another: the page order falls back to the package id.
                    StatusOccurredAt = now.AddMinutes(-(index / 3)),
                    MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
                    CreatedAt = now.AddDays(-2),
                    UpdatedAt = now,
                    Version = 1
                });
            }
        });
        const string page = "/api/v1/invoice-workspace/page?tab=ALL";
        var (client, signIn) = await SignInAsync();
        using (client)
        using (signIn)
        {
            var first = await ReadAsync(client, $"{page}&pageSize=20");
            AssertOldBody(first, "items", "totalCount", "pageNumber", "pageSize", "totalPages", "uninvoicedCount", "invoicedCount", "dueSoonCount", "hiddenCount",
                "totalPackageCount", "hasPendingMarketplaceInvoices", "shipmentStatuses", "cargoProviders", "invoiceStatuses", "platformCounts");
            Assert.Equal(45, JsonDocument.Parse(first).RootElement.GetProperty("totalCount").GetInt32());
            var byPage = await ReadAsync(client, $"{page}&pageNumber=2&pageSize=20");
            var byCursor = await ReadAsync(client, $"{page}&limit=20&after={Cursor(first)}");
            Assert.Equal(byPage, byCursor);
            // limit wins over pageSize, after over pageNumber.
            Assert.Equal(byPage, await ReadAsync(client, $"{page}&pageNumber=3&pageSize=50&limit=20&after={Cursor(first)}"));
            var last = await ReadAsync(client, $"{page}&limit=20&after={Cursor(byCursor)}");
            Assert.Equal(await ReadAsync(client, $"{page}&pageNumber=3&pageSize=20"), last);
            Assert.False(JsonDocument.Parse(last).RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal(45, PackageIds(first).Concat(PackageIds(byCursor)).Concat(PackageIds(last)).Distinct().Count());
            await RefusedAsync(client, $"{page}&limit=50&after={Cursor(first)}");
        }
    }

    // API-GUVENLIK-8 b: the unified filter names (platforms, dateFrom, dateTo) answer what the old spellings answered.
    [PostgreSqlFact]
    public async Task TheUnifiedFilterNamesAnswerWhatTheOldNamesAnswer()
    {
        await ConnectionAsync("TRENDYOL");
        var (client, signIn) = await SignInAsync();
        using (client)
        using (signIn)
        {
            const string from = "2026-10-01", to = "2026-10-31";
            var moment = Uri.EscapeDataString("2026-10-01T00:00:00+03:00");
            foreach (var (old, unified) in new[]
            {
                ($"/api/v1/reports/sales?from={from}&to={to}&platformCodes=TRENDYOL", $"/api/v1/reports/sales?dateFrom={from}&dateTo={to}&platforms=TRENDYOL"),
                ($"/api/v1/reports/stock?from={from}&to={to}&platformCodes=TRENDYOL", $"/api/v1/reports/stock?dateFrom={from}&dateTo={to}&platforms=TRENDYOL"),
                ($"/api/v1/reports/order-profitability?from={from}&to={to}&platformCodes=TRENDYOL&limit=20", $"/api/v1/reports/order-profitability?dateFrom={from}&dateTo={to}&platforms=TRENDYOL&pageSize=20"),
                ("/api/v1/reports/payouts?platformCodes=TRENDYOL", "/api/v1/reports/payouts?platforms=TRENDYOL"),
                ($"/api/v1/dashboard/performance?from={from}&to={to}", $"/api/v1/dashboard/performance?dateFrom={from}&dateTo={to}"),
                ($"/api/v1/notifications?from={moment}", $"/api/v1/notifications?dateFrom={moment}"),
                ($"/api/v1/product-activity/counts?from={moment}", $"/api/v1/product-activity/counts?dateFrom={moment}"),
                ($"/api/v1/price-stock-send-log/counts?from={moment}", $"/api/v1/price-stock-send-log/counts?dateFrom={moment}"),
                ($"/api/v1/jobs/counts?since={moment}", $"/api/v1/jobs/counts?dateFrom={moment}"),
                ($"/api/v1/invoice-workspace/page?platformCodes=TRENDYOL&from={moment}", $"/api/v1/invoice-workspace/page?platforms=TRENDYOL&dateFrom={moment}"),
            })
                Assert.Equal(Stamped(await ReadAsync(client, old)), Stamped(await ReadAsync(client, unified)));
            // When both come, the unified name wins.
            Assert.Equal(Stamped(await ReadAsync(client, $"/api/v1/reports/sales?dateFrom={from}&dateTo={to}")), Stamped(await ReadAsync(client, $"/api/v1/reports/sales?from=2020-01-01&to=2020-01-02&dateFrom={from}&dateTo={to}")));
        }
    }

    // A report says when it was made (a moment with fractions of a second); two reads differ only there.
    private static string Stamped(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+\+00:00", "T");

    // The old body: exactly the members it had, in their order, then the two cursor members and nothing else; cut
    // them off and what is left is the rest of the body as written.
    private void AssertOldBody(string body, params string[] members) => AssertOldBody(body, [], members);

    private void AssertOldBody(string body, string[] later, params string[] members)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        Assert.Equal([.. members, .. Added, .. later], root.Select(member => member.Key).ToArray());
        foreach (var added in Added.Concat(later)) root.Remove(added);
        var legacy = root.ToJsonString(ApiOptions());
        Assert.True(body.StartsWith(legacy[..^1] + ",\"nextCursor\":", StringComparison.Ordinal), "The added members are not the body's last members.");
    }

    private System.Text.Json.JsonSerializerOptions ApiOptions() =>
        fixture.Host!.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

    private static string Cursor(string body) => Uri.EscapeDataString(JsonDocument.Parse(body).RootElement.GetProperty("nextCursor").GetString()!);

    private static IEnumerable<string> Ids(string body) => JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList();

    private static IEnumerable<string> PackageIds(string body) => JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("packageId").GetString()!).ToList();

    private static async Task<string> ReadAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {body}");
        return body;
    }

    private static async Task RefusedAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<(HttpClient Client, HttpResponseMessage SignIn)> SignInAsync()
    {
        var (client, _, signIn) = await fixture.SignInAsync(MembershipRole.Owner, ownNetwork: true);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        return (client, signIn);
    }

    private async Task<Guid> ConnectionAsync(string platform)
    {
        var id = Guid.CreateVersion7();
        await SeedAsync(db => db.PlatformConnections.Add(new PlatformConnection
        {
            Id = id,
            TenantId = fixture.TenantId,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = platform,
            Environment = "PRODUCTION",
            DisplayName = $"{platform} {id:N}",
            ExternalStoreId = $"store-{id:N}",
            Status = "ACTIVE",
            ApiVersion = "V1",
            SettingsJson = "{}",
            Version = 1
        }));
        return id;
    }

    private async Task SeedAsync(Action<AppDbContext> seed)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}
