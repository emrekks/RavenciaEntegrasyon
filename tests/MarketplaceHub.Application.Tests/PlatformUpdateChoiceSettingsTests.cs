using System.Net;
using System.Text.Json;
using MarketplaceHub.Api.Settings;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The Hızlı Güncelle choice ("Platformlarda Güncelle" → Kaydet) is kept per tenant on the server: known field codes,
// group names and connection ids only, within a size limit.
public sealed class PlatformUpdateChoiceSchemaTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Normalize_KeepsAKnownChoiceAndDropsDuplicatesAndUnknownProperties()
    {
        var id = Guid.NewGuid();
        var ok = PlatformUpdateChoiceSchema.TryNormalize(Json($$"""
            { "fields": ["TITLE", "PRICE", "STOCK", "PRICE"], "openGroups": ["sales"], "knownFields": ["TITLE", "ORIGIN"], "knownGroups": ["product", "sales"],
              "connectionIds": ["{{id.ToString("D").ToUpperInvariant()}}", "{{id:D}}"], "script": "<x>" }
            """), out var normalized, out var error);

        Assert.True(ok, error);
        var expected = $$"""{"fields":["TITLE","PRICE","STOCK"],"openGroups":["sales"],"knownFields":["TITLE","ORIGIN"],"knownGroups":["product","sales"],"connectionIds":["{{id:D}}"]}""";
        Assert.Equal(expected, normalized.ToJsonString());
    }

    [Fact]
    public void Normalize_KeepsNoSavedStoresApartFromAnEmptyPick()
    {
        Assert.True(PlatformUpdateChoiceSchema.TryNormalize(Json("""{ "fields": [] }"""), out var none, out _));
        Assert.Null(none["connectionIds"]);
        Assert.True(PlatformUpdateChoiceSchema.TryNormalize(Json("""{ "fields": ["PRICE"], "connectionIds": [] }"""), out var empty, out _));
        Assert.Equal("[]", empty["connectionIds"]!.ToJsonString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "openGroups": [] }""")]
    [InlineData("""{ "fields": "PRICE" }""")]
    [InlineData("""{ "fields": ["PRICE", "VIDEO"] }""")]
    [InlineData("""{ "fields": ["price"] }""")]
    [InlineData("""{ "fields": [1] }""")]
    [InlineData("""{ "fields": [], "openGroups": ["other"] }""")]
    [InlineData("""{ "fields": [], "knownFields": ["<script>"] }""")]
    [InlineData("""{ "fields": [], "knownGroups": ["Sales"] }""")]
    [InlineData("""{ "fields": [], "connectionIds": ["not-a-guid"] }""")]
    [InlineData("""{ "fields": [], "connectionIds": ["00000000-0000-0000-0000-000000000000"] }""")]
    [InlineData("""{ "fields": [], "connectionIds": "ty" }""")]
    public void Normalize_RefusesAnythingElse(string body)
    {
        Assert.False(PlatformUpdateChoiceSchema.TryNormalize(Json(body), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Normalize_RefusesTooManyStores()
    {
        var ids = string.Join(",", Enumerable.Range(0, PlatformUpdateChoiceSchema.MaximumConnections + 1).Select(_ => $"\"{Guid.NewGuid():D}\""));
        Assert.False(PlatformUpdateChoiceSchema.TryNormalize(Json($$"""{ "fields": [], "connectionIds": [{{ids}}] }"""), out _, out _));
    }
}

[Collection("postgres-integration")]
public sealed class PlatformUpdateChoiceEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task Choice_IsSavedPerTenant_ValidatedAndWrittenByOperations()
    {
        var (operations, csrf, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (operations)
        using (operationsSignIn)
        {
            HttpRequestMessage Put(object body, System.Net.Http.Headers.EntityTagHeaderValue? etag)
            {
                var request = ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/platform-update", csrf, body);
                if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag.Tag);
                return request;
            }

            using var before = await operations.GetAsync("/api/v1/settings/platform-update");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            using var invalid = await operations.SendAsync(Put(new { fields = new[] { "VIDEO" } }, before.Headers.ETag));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var tooLarge = await operations.SendAsync(Put(new { fields = new[] { "PRICE" }, padding = new string('x', PlatformUpdateChoiceSchema.MaximumJsonCharacters) }, before.Headers.ETag));
            Assert.Equal((HttpStatusCode)413, tooLarge.StatusCode);

            var store = Guid.NewGuid();
            using var saved = await operations.SendAsync(Put(new { fields = new[] { "TITLE", "STOCK" }, openGroups = new[] { "sales" }, connectionIds = new[] { store } }, before.Headers.ETag));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            using var read = await operations.GetAsync("/api/v1/settings/platform-update");
            var settings = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("settings");
            Assert.Equal(["TITLE", "STOCK"], settings.GetProperty("fields").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(store.ToString("D"), settings.GetProperty("connectionIds")[0].GetString());

            // A stored choice is written only over the version read.
            using var stale = await operations.SendAsync(Put(new { fields = new[] { "PRICE" } }, null));
            Assert.Equal((HttpStatusCode)428, stale.StatusCode);
        }
    }
}
