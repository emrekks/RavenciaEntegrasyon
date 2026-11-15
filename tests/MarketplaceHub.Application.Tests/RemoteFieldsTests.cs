using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Webhooks;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class RemoteFieldsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("{\"a\":\" x \"}", "x")]
    [InlineData("{\"a\":12.50}", "12.50")]
    [InlineData("{\"a\":\"  \"}", null)]
    [InlineData("{\"a\":true}", null)]
    [InlineData("{\"a\":null}", null)]
    [InlineData("{\"b\":\"x\"}", null)]
    [InlineData("[1]", null)]
    [InlineData("{\"a\":\"null\"}", "null")]
    public void TextReadsStringsAndNumbers(string json, string? expected) => Assert.Equal(expected, RemoteFields.Text(Json(json), "a"));

    [Theory]
    [InlineData("{\"a\":\" null \"}", null)]
    [InlineData("{\"a\":\"nullx\"}", "nullx")]
    [InlineData("{\"a\":5}", "5")]
    public void TextOrNullWordDropsTheNullWord(string json, string? expected) => Assert.Equal(expected, RemoteFields.TextOrNullWord(Json(json), "a"));

    [Theory]
    [InlineData("{\"a\":\" x \"}", "x")]
    [InlineData("{\"a\":5}", null)]
    [InlineData("{\"a\":\"\"}", null)]
    public void StringTextReadsStringsOnly(string json, string? expected) => Assert.Equal(expected, RemoteFields.StringText(Json(json), "a"));

    [Fact]
    public void NumbersAndFlagsKeepTheirKinds()
    {
        Assert.Equal(1234.5m, RemoteFields.Decimal(Json("{\"a\":\"1234.5\"}"), "a"));
        Assert.Null(RemoteFields.Decimal(Json("{\"a\":\"1234,50\"}"), "a"));
        Assert.Null(RemoteFields.Decimal(Json("[1]"), "a"));
        Assert.True(RemoteFields.Bool(Json("{\"a\":true}"), "a"));
        Assert.Null(RemoteFields.Bool(Json("{\"a\":\"true\"}"), "a"));
        Assert.Equal(42L, RemoteFields.Long(Json("{\"a\":42}"), "a"));
        Assert.Null(RemoteFields.Long(Json("{\"a\":\"42\"}"), "a"));
    }

    [Fact]
    public void WebhookAuthChecksApiKeyAndBasicCredentials()
    {
        var basic = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("u:p"));
        Assert.True(WebhookAuth.IsAuthenticated("API_KEY", null, null, "k", new Dictionary<string, string> { ["X-Api-Key"] = "k" }));
        Assert.False(WebhookAuth.IsAuthenticated("API_KEY", null, null, "k", new Dictionary<string, string> { ["x-api-key"] = "K" }));
        Assert.True(WebhookAuth.IsAuthenticated("BASIC_AUTHENTICATION", "u", "p", null, new Dictionary<string, string> { ["authorization"] = basic }));
        Assert.False(WebhookAuth.IsAuthenticated("BASIC_AUTHENTICATION", "u", null, null, new Dictionary<string, string> { ["authorization"] = basic }));
        Assert.False(WebhookAuth.IsAuthenticated("OTHER", "u", "p", "k", new Dictionary<string, string> { ["authorization"] = basic }));
    }

    [Fact]
    public void CanonicalJsonSortsPropertiesAndKeepsNumbersAsWritten()
    {
        Assert.Equal("{\"a\":[1.50,{\"x\":null,\"y\":true}],\"b\":\"s\"}", CanonicalJson.Write(Json("{ \"b\": \"s\", \"a\": [1.50, {\"y\": true, \"x\": null}] }")));
    }
}
