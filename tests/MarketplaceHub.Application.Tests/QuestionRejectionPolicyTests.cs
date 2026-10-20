using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Hepsiburada's REJECTED is the question closed after the seller reported it; elsewhere a rejected answer can be
// rewritten until the question expires (20 Oct 2026: reported spam questions sat under "Cevap Bekleyenler").
public sealed class QuestionRejectionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("REJECTED", "TRENDYOL", 1, true)]
    [InlineData("REJECTED", "TRENDYOL", -1, false)]
    [InlineData("REJECTED", "PAZARAMA", 1, true)]
    [InlineData("REJECTED", "HEPSIBURADA", 1, false)]
    [InlineData("WAITING_FOR_ANSWER", "TRENDYOL", 1, false)]
    public void OnlyARejectionTheSellerCanStillAnswerIsOpen(string status, string platform, int hoursLeft, bool open) =>
        Assert.Equal(open, MarketplaceQuestionService.IsOpenRejection(status, platform, Now.AddHours(hoursLeft), Now));

    [Fact]
    public void ARejectionWithoutADeadlineStaysOpen() =>
        Assert.True(MarketplaceQuestionService.IsOpenRejection("REJECTED", "TRENDYOL", null, Now));
}
