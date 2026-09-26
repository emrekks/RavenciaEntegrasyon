using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductCopyCodePolicyTests
{
    [Fact]
    public void WithUniqueCopySuffix_AppendsCopyMarkerAndTracksValue()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "SKU-01-ABC123-KOPYA" };

        var result = ProductCopyCodePolicy.WithUniqueCopySuffix("SKU-01", "abc123", existing);

        Assert.EndsWith("-KOPYA", result, StringComparison.Ordinal);
        Assert.Equal("SKU-01-ABC123-2-KOPYA", result);
        Assert.Contains(result.ToUpperInvariant(), existing);
    }

    [Fact]
    public void WithUniqueCopySuffix_TruncatesLongCodesWithoutLosingSuffix()
    {
        var result = ProductCopyCodePolicy.WithUniqueCopySuffix(new string('X', 200), "12345678", new HashSet<string>(), 160);

        Assert.Equal(160, result.Length);
        Assert.EndsWith("-12345678-KOPYA", result, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCopySuffix_AlwaysEndsWithCopyMarkerWithinLimit()
    {
        var result = ProductCopyCodePolicy.WithCopySuffix(new string('Ü', 400), 320);

        Assert.Equal(320, result.Length);
        Assert.EndsWith("Kopya", result, StringComparison.Ordinal);
    }
}
