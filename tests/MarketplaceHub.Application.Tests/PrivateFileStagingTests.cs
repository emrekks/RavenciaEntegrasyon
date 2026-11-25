using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Files;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan report E-5: an upload is written under a temporary name and gets its final name only after the database row that
// points at it is saved; a failed save leaves nothing behind, and a temporary file a crash left is swept after a day.
public sealed class PrivateFileStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "marketplacehub-staging-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _tenant = Guid.CreateVersion7();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string[] TenantFiles() =>
        Directory.Exists(Path.Combine(_root, _tenant.ToString("N"))) ? Directory.GetFiles(Path.Combine(_root, _tenant.ToString("N"))) : [];

    [Fact]
    public async Task AFailedDatabaseWriteLeavesNoFileUnderTheFinalName()
    {
        IPrivateFileStorage storage = new PrivateFileStorage(_root);
        string? relativePath = null;

        // The callers' shape: stage, save the row, commit; here the save throws as a refused INSERT would.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var staged = await storage.StageAsync(_tenant, "a.pdf", "application/pdf", new MemoryStream([1, 2, 3]), 1024, CancellationToken.None);
            relativePath = staged.RelativePath;
            Assert.DoesNotContain(TenantFiles(), file => !file.EndsWith(".staged", StringComparison.Ordinal));
            throw new InvalidOperationException("database write failed");
        });

        Assert.NotNull(relativePath);
        Assert.False(File.Exists(Path.Combine(_root, relativePath!)));
        Assert.Empty(TenantFiles());
    }

    [Fact]
    public async Task ACommittedFileIsReadableUnderItsFinalNameAndNoTemporaryFileStays()
    {
        IPrivateFileStorage storage = new PrivateFileStorage(_root);
        string relativePath;
        await using (var staged = await storage.StageAsync(_tenant, "a.pdf", "application/pdf", new MemoryStream([1, 2, 3]), 1024, CancellationToken.None))
        {
            relativePath = staged.RelativePath;
            staged.Commit();
        }

        await using (var opened = await storage.OpenReadAsync(_tenant, relativePath, CancellationToken.None)) Assert.Equal(3, opened.Length);
        Assert.Single(TenantFiles());
    }

    [Fact]
    public async Task AnOversizedWriteLeavesNothing()
    {
        IPrivateFileStorage storage = new PrivateFileStorage(_root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync(_tenant, "a.pdf", "application/pdf", new MemoryStream(new byte[2048]), 1024, CancellationToken.None));
        Assert.Empty(TenantFiles());
    }

    [Fact]
    public async Task TheSweepRemovesOnlyTemporaryFilesOlderThanTheCutOff()
    {
        IPrivateFileStorage storage = new PrivateFileStorage(_root);
        var kept = await storage.SaveAsync(_tenant, "kept.pdf", "application/pdf", new MemoryStream([1]), 1024, CancellationToken.None);
        var directory = Path.Combine(_root, _tenant.ToString("N"));
        var old = Path.Combine(directory, "old.pdf.staged");
        var fresh = Path.Combine(directory, "fresh.pdf.staged");
        await File.WriteAllBytesAsync(old, [1]);
        await File.WriteAllBytesAsync(fresh, [1]);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(old, now.UtcDateTime - TimeSpan.FromDays(2));

        Assert.Equal(1, await storage.SweepStagedAsync(now - PrivateFileStorage.StagedMaximumAge, CancellationToken.None));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(_root, kept)));
    }
}
