using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceQuestionHistoryTests
{
    [Fact]
    public void DeduplicatesRepeatedAnswersButKeepsLaterIdenticalAnswers()
    {
        var createdAt = DateTimeOffset.Parse("2026-10-05T22:09:00Z");
        var messages = new[]
        {
            new RemoteQuestionConversation("Merchant", "Beden tablosuna bakabilirsiniz.", createdAt),
            new RemoteQuestionConversation("Seller", "Beden tablosuna bakabilirsiniz.", createdAt, "İlk cevap reddedildi."),
            new RemoteQuestionConversation("Merchant", "Beden tablosuna bakabilirsiniz.", createdAt.AddMinutes(2))
        };

        var history = MarketplaceQuestionHistory.Deduplicate(messages);

        Assert.Equal(2, history.Count);
        Assert.Equal("İlk cevap reddedildi.", history[0].RejectionReason);
        Assert.Equal(createdAt.AddMinutes(2), history[1].CreatedAt);
    }
}
