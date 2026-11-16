using System.Text.Json;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8 a/b: the old paging and filter parameters stay on the wire in this release so a browser tab still on
// the old bundle keeps working; the contract marks them deprecated. This is the removal list: once no supported bundle
// sends them (the web sends only the unified names since round 7, 16 Nov 2026), a later release removes each one
// here and in its endpoint. A newly deprecated parameter has to be added on purpose.
public sealed class ApiDeprecatedParametersTests
{
    private static readonly string[] ToRemoveLater =
    [
        "GET /api/v1/jobs platform",
        "GET /api/v1/jobs/counts platform",
        "GET /api/v1/jobs/metrics platform",
        "GET /api/v1/jobs/page platform",
        "GET /api/v1/orders platform",
        "GET /api/v1/orders/facets platform",
        "GET /api/v1/orders/summary platform",
        "GET /api/v1/price-stock-send-log platform",
        "GET /api/v1/price-stock-send-log/counts platform",
        "GET /api/v1/product-activity platform",
        "GET /api/v1/product-activity/counts platform",
        "GET /api/v1/dashboard/performance from",
        "GET /api/v1/dashboard/performance to",
        "GET /api/v1/invoice-workspace/page from",
        "GET /api/v1/invoice-workspace/page pageNumber",
        "GET /api/v1/invoice-workspace/page pageSize",
        "GET /api/v1/invoice-workspace/page platformCodes",
        "GET /api/v1/invoice-workspace/page to",
        "GET /api/v1/jobs since",
        "GET /api/v1/jobs/counts since",
        "GET /api/v1/jobs/page since",
        "GET /api/v1/notifications from",
        "GET /api/v1/notifications to",
        "GET /api/v1/price-stock-send-log from",
        "GET /api/v1/price-stock-send-log to",
        "GET /api/v1/price-stock-send-log/counts from",
        "GET /api/v1/price-stock-send-log/counts to",
        "GET /api/v1/product-activity from",
        "GET /api/v1/products platform",
        "GET /api/v1/product-activity to",
        "GET /api/v1/product-activity/counts from",
        "GET /api/v1/product-activity/counts to",
        "GET /api/v1/questions platform",
        "GET /api/v1/reports/order-profitability from",
        "GET /api/v1/reports/order-profitability page",
        "GET /api/v1/reports/order-profitability pageSize",
        "GET /api/v1/reports/order-profitability platformCodes",
        "GET /api/v1/reports/order-profitability to",
        "GET /api/v1/reports/payouts platformCodes",
        "GET /api/v1/reports/payouts/lines page",
        "GET /api/v1/reports/payouts/lines platformCodes",
        "GET /api/v1/reports/profit-rate platformCodes",
        "GET /api/v1/reports/sales from",
        "GET /api/v1/reports/sales platformCodes",
        "GET /api/v1/reports/sales to",
        "GET /api/v1/reports/stock from",
        "GET /api/v1/reports/stock platformCodes",
        "GET /api/v1/reports/stock to",
    ];

    [Fact]
    public void TheDeprecatedParametersAreTheListedOnes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.TestProject(), "ApiContract", "openapi.v1.json")));
        var deprecated = new List<string>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                if (operation.Value.TryGetProperty("parameters", out var parameters))
                    foreach (var parameter in parameters.EnumerateArray())
                        if (parameter.TryGetProperty("deprecated", out var flag) && flag.GetBoolean())
                            deprecated.Add($"{operation.Name.ToUpperInvariant()} {path.Name} {parameter.GetProperty("name").GetString()}");
        Assert.Equal(ToRemoveLater.Order(StringComparer.Ordinal), deprecated.Order(StringComparer.Ordinal));
    }
}
