using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// n11's subject line is shown apart from the customer's question; rows stored before the split carried it as a prefix.
public sealed class MarketplaceQuestionSubjectTests
{
    [Fact]
    public void AStoredN11PrefixIsShownAsTheSubject()
    {
        Assert.Equal(("urun özelliği", "ürünü aldım ama etiketi yoktu"), MarketplaceQuestionService.SubjectAndText("N11", null, "urun özelliği: ürünü aldım ama etiketi yoktu"));
        Assert.Equal(("Renk Değişimi", "Merhaba!\nLacivert var mı?"), MarketplaceQuestionService.SubjectAndText("N11", null, "Renk Değişimi: Merhaba!\nLacivert var mı?"));
    }

    [Fact]
    public void AKeptSubjectLeavesTheTextAsItIs()
    {
        Assert.Equal(("Beden", "M kaç cm: göğüs?"), MarketplaceQuestionService.SubjectAndText("N11", "Beden", "M kaç cm: göğüs?"));
        // An n11 question with no subject (read after the split) is never cut at its own colon.
        Assert.Equal(((string?)null, "Saat: kaçta gelir?"), MarketplaceQuestionService.SubjectAndText("N11", "", "Saat: kaçta gelir?"));
    }

    [Fact]
    public void OtherMarketplacesAreNeverSplit()
    {
        Assert.Equal(((string?)null, "Not: hediye paketi olur mu?"), MarketplaceQuestionService.SubjectAndText("TRENDYOL", null, "Not: hediye paketi olur mu?"));
        Assert.Equal(((string?)null, "İki nokta yok"), MarketplaceQuestionService.SubjectAndText("N11", null, "İki nokta yok"));
    }
}
