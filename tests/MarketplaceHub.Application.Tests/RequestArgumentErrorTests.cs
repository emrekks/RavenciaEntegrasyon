using MarketplaceHub.Api.Security;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class RequestArgumentErrorTests
{
    [Fact]
    public void OwnRequestChecksAreShownAsTheyAre()
    {
        Assert.Equal(("INVALID_REQUEST", "limit 1-200 arasında olmalıdır."), RequestArgumentError.From(new ArgumentException("limit 1-200 arasında olmalıdır.")));
        Assert.Equal(("INVALID_CURSOR", "Cursor geçersiz veya süresi dolmuş."), RequestArgumentError.From(new ArgumentException("Cursor geçersiz veya süresi dolmuş.", "cursor")));
    }

    [Fact]
    public void MessagesFromDeeperCodeAreReplaced()
    {
        var (code, message) = RequestArgumentError.From(new ArgumentException("Invoice total equation is invalid.", "source"));
        Assert.Equal("INVALID_REQUEST", code);
        Assert.DoesNotContain("Invoice", message);
        Assert.DoesNotContain("value", RequestArgumentError.From(new ArgumentOutOfRangeException("value")).Message);
    }
}
