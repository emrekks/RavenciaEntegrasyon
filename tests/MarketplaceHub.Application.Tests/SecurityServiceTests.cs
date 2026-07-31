using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;

namespace MarketplaceHub.Application.Tests;

public sealed class SecurityServiceTests
{
    [Fact]
    public void Token_digest_is_keyed_and_verifies_in_constant_time_path()
    {
        var hasher = new TokenHasher(Enumerable.Repeat((byte)7, 32).ToArray());
        var digest = hasher.Hash("one-time-value");
        Assert.True(hasher.Verify("one-time-value", digest));
        Assert.False(hasher.Verify("different-value", digest));
        Assert.DoesNotContain("one-time-value", digest, StringComparison.Ordinal);
    }

    [Fact]
    public void Totp_accepts_one_step_skew_and_rejects_replay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        var service = new TotpService(clock); var secret = Enumerable.Range(1, 20).Select(x => (byte)x).ToArray();
        var step = clock.GetUtcNow().ToUnixTimeSeconds() / TotpService.StepSeconds;
        var code = TotpService.Generate(secret, step - 1);
        Assert.True(service.TryValidate(secret, code, null, out var accepted));
        Assert.Equal(step - 1, accepted);
        Assert.False(service.TryValidate(secret, code, accepted, out _));
    }

    [Fact]
    public async Task Private_storage_rejects_traversal_and_cross_tenant_reads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"marketplacehub-files-{Guid.NewGuid():N}");
        try
        {
            var storage = new PrivateFileStorage(root); var tenant = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync(tenant, "../escape.pdf", "application/pdf", new MemoryStream([1]), 10, TestContext.Current.CancellationToken));
            var stored = await storage.SaveAsync(tenant, "evidence.pdf", "application/pdf", new MemoryStream([1, 2, 3]), 10, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.OpenReadAsync(Guid.NewGuid(), stored, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
