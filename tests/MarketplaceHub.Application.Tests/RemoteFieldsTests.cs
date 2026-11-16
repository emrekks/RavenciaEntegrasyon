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

    [Theory]
    // The earlier name wins whatever order the JSON lists the properties in.
    [InlineData("{\"OrderNo\":\"b\",\"orderNumber\":\"a\"}", "a")]
    // Case is ignored; of two properties matching the same name the first listed wins.
    [InlineData("{\"ORDERNUMBER\":\"first\",\"orderNumber\":\"second\"}", "first")]
    [InlineData("{\"x\":1,\"orderno\":\"b\"}", "b")]
    [InlineData("{\"x\":1}", null)]
    [InlineData("[{\"orderNumber\":\"a\"}]", null)]
    public void TryFindIgnoringCaseTakesTheFirstNamePresentInOneWalk(string json, string? expected)
    {
        var found = RemoteFields.TryFindIgnoringCase(Json(json), out var value, "orderNumber", "orderNo");

        Assert.Equal(expected is not null, found);
        Assert.Equal(expected, found ? value.GetString() : null);
        if (!found) Assert.Equal(JsonValueKind.Undefined, value.ValueKind);
    }

    [Fact]
    public void TryFindIgnoringCaseReturnsAPresentNullAsFound()
    {
        Assert.True(RemoteFields.TryFindIgnoringCase(Json("{\"OrderNumber\":null,\"orderNo\":\"b\"}"), out var value, "orderNumber", "orderNo"));
        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    [Fact]
    public void CanonicalJsonSortsPropertiesAndKeepsNumbersAsWritten()
    {
        Assert.Equal("{\"a\":[1.50,{\"x\":null,\"y\":true}],\"b\":\"s\"}", CanonicalJson.Write(Json("{ \"b\": \"s\", \"a\": [1.50, {\"y\": true, \"x\": null}] }")));
    }

    // ADAPTOR-TY-HB-8: the named variants keep each mapper's reading.
    [Fact]
    public void RawTextIsAnyNonNullValueAsWritten()
    {
        Assert.Equal(" x ", RemoteFields.RawText(Json("{\"a\":null,\"b\":\" x \"}"), "a", "b"));
        Assert.Equal("12.50", RemoteFields.RawText(Json("{\"a\":12.50}"), "a"));
        Assert.Equal("{\"k\":1}", RemoteFields.RawText(Json("{\"a\":{\"k\":1}}"), "a"));
        Assert.Equal("", RemoteFields.RawText(Json("{\"a\":\"\"}"), "a"));
        Assert.Null(RemoteFields.RawText(Json("{}"), "a"));
        Assert.Throws<InvalidOperationException>(() => RemoteFields.RawText(Json("[]"), "a"));
    }

    [Fact]
    public void FirstDecimalSkipsAnUnreadableValue()
    {
        Assert.Equal(5m, RemoteFields.FirstDecimal(Json("{\"a\":\"1234,50\",\"b\":\"5\"}"), "a", "b"));
        Assert.Null(RemoteFields.FirstDecimal(Json("{\"a\":\"x\"}"), "a"));
    }

    [Theory]
    [InlineData("{\"a\":true}", true, true)]
    [InlineData("{\"a\":\"TRUE\"}", true, true)]
    [InlineData("{\"a\":1}", true, null)]
    [InlineData("{\"a\":0}", false, null)]
    [InlineData("{\"a\":\"false\"}", false, false)]
    [InlineData("{\"a\":false}", false, false)]
    [InlineData("{}", false, null)]
    public void LooseTruthAndLooseYesNo(string json, bool looseTrue, bool? looseBool)
    {
        Assert.Equal(looseTrue, RemoteFields.LooseTrue(Json(json), "a"));
        Assert.Equal(looseBool, RemoteFields.LooseBool(Json(json), "a"));
    }

    [Fact]
    public void JsonNumberReadsNumbersOnly()
    {
        Assert.Equal(1180.00m, RemoteFields.JsonNumber(Json("{\"value\":1180.00}"), "value"));
        Assert.Null(RemoteFields.JsonNumber(Json("{\"value\":\"1180.00\"}"), "value"));
    }

    [Fact]
    public void TextIgnoringCaseVariantsDifferOnAValueOfAnotherKind()
    {
        var item = Json("{\"From\":null,\"author\":\" Ayşe \",\"flag\":true}");
        // The first name present decides: a null there is no text.
        Assert.Null(RemoteFields.TextIgnoringCase(item, "from", "author"));
        // The question reader gives way to the next name.
        Assert.Equal(" Ayşe ", RemoteFields.FirstTextIgnoringCase(item, "from", "author"));
        Assert.Equal("True", RemoteFields.TextIgnoringCase(item, "FLAG"));
        // By JSON order, string or number only: true is skipped.
        Assert.Equal(" Ayşe ", RemoteFields.FirstListedTextIgnoringCase(Json("{\"flag\":true,\"Author\":\" Ayşe \",\"from\":\"x\"}"), "from", "author", "flag"));
    }

    [Theory]
    [InlineData("{\"price\":\"130,50\"}", "130.50")]
    [InlineData("{\"Price\":\"1.234,50\"}", "1234.50")]
    [InlineData("{\"price\":{\"Amount\":12.5}}", "12.5")]
    [InlineData("{\"price\":\"1234,5000\"}", null)]
    [InlineData("{\"price\":\"99.90\"}", "99.90")]
    public void MoneyIgnoringCaseReadsTurkishMoneyAndAmountObjects(string json, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), RemoteFields.MoneyIgnoringCase(Json(json), "price"));
}
