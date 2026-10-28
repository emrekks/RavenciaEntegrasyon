using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ConnectionReferencePlanTests
{
    private static readonly Guid ConnectionId = Guid.Parse("01a05a69-86cd-7a57-af4f-d9d7276a5437");

    [Theory]
    [InlineData("TRENDYOL", new[] { "CATEGORIES", "BRANDS" })]
    [InlineData("PAZARAMA", new[] { "CATEGORIES", "BRANDS" })]
    [InlineData("N11", new[] { "CATEGORIES" })]
    [InlineData("PTTAVM", new[] { "CATEGORIES" })]
    [InlineData("SHOPIFY", new[] { "CATEGORIES" })]
    [InlineData("HEPSIBURADA", new[] { "CATEGORIES" })]
    [InlineData("TRENDYOL_EFATURAM", new string[0])]
    public void RootListsFollowWhatEachMarketplaceOffers(string platform, string[] expected)
    {
        Assert.Equal(expected, ConnectionReferencePlan.RootResources(platform, includeHepsiburadaCatalog: true));
    }

    [Fact]
    public void HepsiburadaWithoutAConfirmedCatalogReadsNoLists()
    {
        Assert.Empty(ConnectionReferencePlan.RootResources("HEPSIBURADA", includeHepsiburadaCatalog: false));
        Assert.Equal(["CATEGORIES"], ConnectionReferencePlan.RootResources("N11", includeHepsiburadaCatalog: false));
    }

    [Theory]
    [InlineData("TRENDYOL", true)]
    [InlineData("HEPSIBURADA", true)]
    [InlineData("N11", true)]
    [InlineData("PAZARAMA", true)]
    [InlineData("PTTAVM", false)]
    [InlineData("SHOPIFY", false)]
    public void FieldListsAreReadWhereTheMarketplaceHasThem(string platform, bool supported)
    {
        Assert.Equal(supported, ConnectionReferencePlan.SupportsCategoryAttributes(platform));
    }

    [Theory]
    [InlineData("TRENDYOL", true)]
    [InlineData("N11", true)]
    [InlineData("PAZARAMA", true)]
    [InlineData("HEPSIBURADA", true)]
    [InlineData("PTTAVM", false)]
    [InlineData("SHOPIFY", false)]
    public void TheActivationTreeReadQueuesTheMappedCategoriesFieldsWhereThereAreAny(string platform, bool follows)
    {
        var plan = MarketplaceConnectionService.CreateInitialDataSyncPlan(platform, ConnectionId, includeHepsiburadaCatalog: true);
        var categories = plan.Single(item => item.KeySuffix == "categories");
        Assert.Equal(MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.ReferenceSync), categories.JobType);
        using var payload = JsonDocument.Parse(categories.PayloadJson);
        Assert.Equal("CATEGORIES", payload.RootElement.GetProperty("resourceType").GetString());
        Assert.Equal(follows, ConnectionReferencePlan.ReadFollowUp(categories.PayloadJson).FollowMappedCategories);
    }

    [Fact]
    public void ActivationQueuesTheBrandListOnTrendyolAndPazaramaOnly()
    {
        foreach (var platform in new[] { "TRENDYOL", "PAZARAMA" })
        {
            var brands = MarketplaceConnectionService.CreateInitialDataSyncPlan(platform, ConnectionId, true).Single(item => item.KeySuffix == "brands");
            Assert.Equal(MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.ReferenceSync), brands.JobType);
            Assert.Contains("\"resourceType\":\"BRANDS\"", brands.PayloadJson);
            Assert.False(ConnectionReferencePlan.ReadFollowUp(brands.PayloadJson).FollowMappedCategories);
        }
        foreach (var platform in new[] { "HEPSIBURADA", "N11", "PTTAVM", "SHOPIFY" })
            Assert.DoesNotContain(MarketplaceConnectionService.CreateInitialDataSyncPlan(platform, ConnectionId, true), item => item.KeySuffix == "brands");
    }

    [Fact]
    public void AFieldReadCarriesItsValueListsAndRefreshBoundary()
    {
        var boundary = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(3));
        var payload = ConnectionReferencePlan.CategoryAttributesPayload(ConnectionId, "411", boundary);
        using (var document = JsonDocument.Parse(payload))
        {
            Assert.Equal("CATEGORY_ATTRIBUTES", document.RootElement.GetProperty("resourceType").GetString());
            Assert.Equal("411", document.RootElement.GetProperty("parentExternalId").GetString());
        }
        var followUp = ConnectionReferencePlan.ReadFollowUp(payload);
        Assert.True(followUp.IncludeValues);
        Assert.False(followUp.FollowMappedCategories);
        Assert.Equal(boundary, followUp.RefreshBefore);
        Assert.Equal(TimeSpan.Zero, followUp.RefreshBefore!.Value.Offset);

        Assert.Null(ConnectionReferencePlan.ReadFollowUp(ConnectionReferencePlan.CategoryAttributesPayload(ConnectionId, "411", null)).RefreshBefore);
        Assert.Equal(new ConnectionReferencePlan.FollowUp(false, false, null), ConnectionReferencePlan.ReadFollowUp("{\"resourceType\":\"CATEGORIES\"}"));
        Assert.Equal(new ConnectionReferencePlan.FollowUp(false, false, null), ConnectionReferencePlan.ReadFollowUp("not json"));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, null, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void ValueListsAreReadForSelectableActiveFields(bool isActive, bool? allowsCustomValue, bool expected)
    {
        Assert.Equal(expected, ConnectionReferencePlan.NeedsValueList(isActive, allowsCustomValue));
    }

    [Fact]
    public void ACopyIsFreshWhenItExistsAndIsNotOlderThanTheBoundary()
    {
        var now = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        Assert.False(ConnectionReferencePlan.IsFresh(null, null));
        Assert.True(ConnectionReferencePlan.IsFresh(now.AddDays(-30), null));
        Assert.True(ConnectionReferencePlan.IsFresh(now.AddHours(-2), now - ConnectionReferencePlan.FreshFor));
        Assert.False(ConnectionReferencePlan.IsFresh(now.AddHours(-25), now - ConnectionReferencePlan.FreshFor));
    }

    [Fact]
    public void AValueListRefusedForOneFieldIsSkippedButQuotaOutagesAndCredentialsEndTheRun()
    {
        Assert.True(ConnectionReferencePlan.SkipsValueListFailure(JobExecutionResult.FromAdapterError(new(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_ATTRIBUTE_NOT_ENUM", "not enum", null, null, null))));
        Assert.True(ConnectionReferencePlan.SkipsValueListFailure(JobExecutionResult.ManualReview("REFERENCE_CONTRACT_INVALID")));
        Assert.False(ConnectionReferencePlan.SkipsValueListFailure(JobExecutionResult.FromAdapterError(new(AdapterErrorClass.RateLimit, "RATE_LIMITED", "slow down", null, null, null))));
        Assert.False(ConnectionReferencePlan.SkipsValueListFailure(JobExecutionResult.FromAdapterError(new(AdapterErrorClass.TransientNetwork, "TIMEOUT", "timeout", null, null, null))));
        Assert.False(ConnectionReferencePlan.SkipsValueListFailure(JobExecutionResult.FromAdapterError(new(AdapterErrorClass.Authentication, "AUTH", "credentials", null, null, null))));
    }

    [Fact]
    public void BackfillQueuesMissingListsAndTheMappedCategoriesOnceTheTreeIsThere()
    {
        var trendyolRoots = ConnectionReferencePlan.RootResources("TRENDYOL", true);
        Assert.Equal(
            [new ReferenceSnapshotBackfill.Gap("BRANDS", null), new ReferenceSnapshotBackfill.Gap("CATEGORY_ATTRIBUTES", "411")],
            ReferenceSnapshotBackfill.Gaps("TRENDYOL", trendyolRoots, ["CATEGORIES"], ["411"]));
        // Without the tree a field read would be refused (its category is checked against the current tree).
        Assert.Equal(
            [new ReferenceSnapshotBackfill.Gap("CATEGORIES", null), new ReferenceSnapshotBackfill.Gap("BRANDS", null)],
            ReferenceSnapshotBackfill.Gaps("TRENDYOL", trendyolRoots, [], ["411"]));
        Assert.Empty(ReferenceSnapshotBackfill.Gaps("PAZARAMA", ConnectionReferencePlan.RootResources("PAZARAMA", true), ["CATEGORIES", "BRANDS"], []));
        Assert.Empty(ReferenceSnapshotBackfill.Gaps("PTTAVM", ConnectionReferencePlan.RootResources("PTTAVM", true), ["CATEGORIES"], ["99"]));
        Assert.Equal([new ReferenceSnapshotBackfill.Gap("CATEGORIES", null)], ReferenceSnapshotBackfill.Gaps("SHOPIFY", ConnectionReferencePlan.RootResources("SHOPIFY", true), [], []));
    }

    [Fact]
    public void ReferenceReadsAreNotHeldBehindAFirstImport()
    {
        foreach (var jobType in new[] { MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.HepsiburadaReferenceSync, MarketplaceJobTypes.N11ReferenceSync, MarketplaceJobTypes.PazaramaReferenceSync, MarketplaceJobTypes.PttAvmReferenceSync })
            Assert.False(MarketplaceJobProcessor.IsBootstrapManagedJob(jobType));
        Assert.True(MarketplaceJobProcessor.IsBootstrapManagedJob(MarketplaceJobTypes.OrderSync));
        Assert.True(MarketplaceJobProcessor.IsBootstrapManagedJob(MarketplaceJobTypes.ReturnSync));
    }

    [Fact]
    public void LongDedupKeysStayWithinTheColumn()
    {
        var key = ConnectionReferencePlan.Truncate($"activation-bootstrap:{ConnectionId:N}:7:categories:attributes:{new string('9', 300)}");
        Assert.True(key.Length <= 256);
        Assert.NotEqual(key, ConnectionReferencePlan.Truncate($"activation-bootstrap:{ConnectionId:N}:7:categories:attributes:{new string('9', 299)}8"));
        Assert.Equal("short", ConnectionReferencePlan.Truncate("short"));
    }
}
