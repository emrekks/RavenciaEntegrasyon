using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ImportedHtmlSanitizerTests
{
    [Fact]
    public void RemovesActiveMarkupAndUnsafeAttributes()
    {
        var sanitized = ImportedHtmlSanitizer.Sanitize("<p>Ürün</p><script>alert(1)</script><img src=\"javascript:alert(1)\" onerror=\"alert(1)\" style=\"color:red\"><iframe src=\"https://evil.example\"></iframe>");

        Assert.Contains("<p>Ürün</p>", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("script", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservesAllowlistedFormattingAndSafeUris()
    {
        var sanitized = ImportedHtmlSanitizer.Sanitize("<strong>Kalın</strong><a href=\"https://example.test/item\" target=\"_blank\">Ürün</a><img src=\"/images/item.jpg\" alt=\"Ürün\">");

        Assert.Contains("<strong>Kalın</strong>", sanitized, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.test/item\"", sanitized, StringComparison.Ordinal);
        Assert.Contains("src=\"/images/item.jpg\"", sanitized, StringComparison.Ordinal);
        Assert.Contains("alt=\"Ürün\"", sanitized, StringComparison.Ordinal);
    }
}
