using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Infrastructure.Adapters.Shopify;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F5ShopifyContractTests
{
    [Fact]
    public void Version_and_shop_scope_are_pinned_and_canonical()
    {
        Assert.Equal("2026-07", ShopifyContract.ApiVersion);
        Assert.True(ShopifyContract.TryNormalizeShopDomain("sample-store.myshopify.com", out var domain));
        Assert.Equal("sample-store.myshopify.com", domain);
        Assert.False(ShopifyContract.TryNormalizeShopDomain("https://sample-store.myshopify.com/admin", out _));
        Assert.False(ShopifyContract.TryNormalizeShopDomain("sample-store.example.com", out _));
    }

    [Fact]
    public void Graphql_top_level_and_mutation_user_errors_are_separate()
    {
        Assert.Equal(["denied"], ShopifyGraphQlContract.Errors("{\"errors\":[{\"message\":\"denied\"}]}"));
        var json = "{\"data\":{\"productSet\":{\"userErrors\":[{\"field\":[\"input\"],\"message\":\"invalid\"}]}}}";
        Assert.Equal(["invalid"], ShopifyGraphQlContract.UserErrors(json, "productSet"));
        Assert.Empty(ShopifyGraphQlContract.Errors(json));
    }

    [Fact]
    public async Task Bulk_jsonl_stream_resumes_after_completed_line()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}\n{\"id\":2}\n{\"id\":3}\n"));
        var lines = new List<ShopifyBulkLine>();
        await foreach (var line in ShopifyBulkJsonl.ReadAsync(stream, 1, TestContext.Current.CancellationToken)) lines.Add(line);
        Assert.Equal([2L, 3L], lines.Select(x => x.LineNumber));
        Assert.Equal("3", lines[^1].Checkpoint);
    }

    [Fact]
    public void Webhook_hmac_is_calculated_over_unchanged_raw_body()
    {
        var raw = Encoding.UTF8.GetBytes("{\"id\":123,\"note\":\"ç\"}"); const string secret = "fixture-secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)); var signature = Convert.ToBase64String(hmac.ComputeHash(raw));
        Assert.True(ShopifyWebhookVerifier.VerifySignature(raw, signature, secret));
        Assert.False(ShopifyWebhookVerifier.VerifySignature(Encoding.UTF8.GetBytes("{\"id\":123}"), signature, secret));
        Assert.False(ShopifyWebhookVerifier.VerifySignature(raw, "not-base64", secret));
    }

    [Fact]
    public void Shopify_specific_types_do_not_cross_domain_or_application_boundary()
    {
        var root = FindRoot();
        foreach (var area in new[] { "MarketplaceHub.Domain", "MarketplaceHub.Application" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", area), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("Shopify", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
