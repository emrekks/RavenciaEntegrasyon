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
