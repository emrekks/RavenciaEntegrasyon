using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A customer's earlier questions are shown with a new one: the same customer number on the same marketplace, asked
// before it, newest first, with the answers they got.
[Collection("postgres-integration")]
public sealed class MarketplaceQuestionCustomerHistoryPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task EarlierQuestionsOfTheSameCustomerOnTheSameMarketplaceComeNewestFirst()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"question-history-{Guid.NewGuid():N}", DisplayName = "question-history", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        var trendyol = Connection(tenant.Id, "TRENDYOL", "Trendyol mağazası");
        var otherTrendyol = Connection(tenant.Id, "TRENDYOL", "İkinci Trendyol mağazası");
        var hepsiburada = Connection(tenant.Id, "HEPSIBURADA", "Hepsiburada mağazası");
        var now = fixture.Now;
        var current = Question(tenant.Id, trendyol.Id, "C-1", "Bu bluzun 46 bedeni ne zaman gelir?", now.AddHours(-1));
        var dayBefore = Question(tenant.Id, trendyol.Id, "C-1", "Bej rengi açık mı?", now.AddDays(-2), answer: "Açık bej, krem tonunda.");
        var otherStore = Question(tenant.Id, otherTrendyol.Id, "C-1", "Kumaşı terletir mi?", now.AddDays(-10));
        var later = Question(tenant.Id, trendyol.Id, "C-1", "Kargo ne zaman çıkar?", now.AddMinutes(-5));
        var otherCustomer = Question(tenant.Id, trendyol.Id, "C-2", "Yıkamada çeker mi?", now.AddDays(-3));
        var otherMarketplace = Question(tenant.Id, hepsiburada.Id, "C-1", "Faturalı mı?", now.AddDays(-4));
        var anonymous = Question(tenant.Id, trendyol.Id, null, "Astarlı mı?", now.AddHours(-2));
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(trendyol, otherTrendyol, hepsiburada);
        db.MarketplaceQuestions.AddRange(current, dayBefore, otherStore, later, otherCustomer, otherMarketplace, anonymous);
        await db.SaveChangesAsync();
        var service = new MarketplaceQuestionService(db, null!, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);

        var history = await service.CustomerHistoryAsync(tenant.Id, current.Id, CancellationToken.None);

        Assert.NotNull(history);
        Assert.Equal([dayBefore.Id, otherStore.Id], history.Select(item => item.Id));
        Assert.Equal("Açık bej, krem tonunda.", history[0].Conversations.Last().Text);
        Assert.Equal("İkinci Trendyol mağazası", history[1].StoreName);
        Assert.Empty((await service.CustomerHistoryAsync(tenant.Id, anonymous.Id, CancellationToken.None))!);
        Assert.Null(await service.CustomerHistoryAsync(tenant.Id, Guid.CreateVersion7(), CancellationToken.None));
    }

    // SIPARIS-STOK-7: the search ignores case the Turkish way, like the order search; a hidden store's question is not
    // opened, nor shown in a customer's history, as the list already hides it.
    [PostgreSqlFact]
    public async Task SearchIgnoresCase_AndAHiddenStoresQuestionIsNotOpened()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"question-search-{Guid.NewGuid():N}", DisplayName = "question-search", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        var active = Connection(tenant.Id, "TRENDYOL", "Açık mağaza");
        var hidden = Connection(tenant.Id, "TRENDYOL", "Gizli mağaza");
        hidden.Status = "DISABLED";
        var now = fixture.Now;
        var cargo = Question(tenant.Id, active.Id, "C-1", "Kargo ne zaman gelir?", now.AddHours(-1));
        var light = Question(tenant.Id, active.Id, "C-1", "IŞIKLI model var mı?", now.AddHours(-2));
        var hiddenEarlier = Question(tenant.Id, hidden.Id, "C-1", "Kargo ücretli mi?", now.AddDays(-1));
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(active, hidden);
        db.MarketplaceQuestions.AddRange(cargo, light, hiddenEarlier);
        await db.SaveChangesAsync();
        var service = new MarketplaceQuestionService(db, null!, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);

        var found = await service.ListAsync(tenant.Id, new QuestionListQuery("ALL", null, null, null, null, null, "kargo"), CancellationToken.None);
        Assert.Equal([cargo.Id], found.Items.Select(item => item.Id));
        var folded = await service.ListAsync(tenant.Id, new QuestionListQuery("ALL", null, null, null, null, null, "ışıklı"), CancellationToken.None);
        Assert.Equal([light.Id], folded.Items.Select(item => item.Id));
        Assert.Null(await service.GetAsync(tenant.Id, hiddenEarlier.Id, CancellationToken.None));
        Assert.Null(await service.CustomerHistoryAsync(tenant.Id, hiddenEarlier.Id, CancellationToken.None));
        Assert.DoesNotContain((await service.CustomerHistoryAsync(tenant.Id, cargo.Id, CancellationToken.None))!, item => item.Id == hiddenEarlier.Id);
    }

    private static PlatformConnection Connection(Guid tenantId, string platformCode, string name) =>
        new() { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = name, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };

    private MarketplaceQuestion Question(Guid tenantId, Guid connectionId, string? customerKey, string text, DateTimeOffset createdAt, string? answer = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalQuestionId = $"question-{Guid.NewGuid():N}",
        Kind = "PRODUCT",
        Status = answer is null ? "WAITING_FOR_ANSWER" : "ANSWERED",
        QuestionText = text,
        CustomerKey = customerKey,
        HistoryJson = JsonSerializer.Serialize(answer is null
            ? new[] { new RemoteQuestionConversation("Customer", text, createdAt) }
            : [new RemoteQuestionConversation("Customer", text, createdAt), new RemoteQuestionConversation("Merchant", answer, createdAt.AddHours(1))]),
        CreatedAt = createdAt,
        LastRemoteModifiedAt = createdAt,
        LastSyncedAt = fixture.Now,
        Version = 1
    };
}
