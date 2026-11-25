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

        var saved = Path.Combine(directory, "saved.pdf");
        await File.WriteAllBytesAsync(saved + ".staged", [7]);
        File.SetLastWriteTimeUtc(saved + ".staged", now.UtcDateTime - TimeSpan.FromDays(2));
        var savedRelative = $"{_tenant:N}/saved.pdf";

        var result = await storage.SweepStagedAsync(now - PrivateFileStorage.StagedMaximumAge,
            (relative, _) => Task.FromResult(relative == savedRelative), CancellationToken.None);

        Assert.Equal(new StagedSweepResult(1, 1, 0), result);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(_root, kept)));
        // The saved row's file is finished, never deleted.
        Assert.True(File.Exists(saved));
        Assert.False(File.Exists(saved + ".staged"));
    }

    [Fact]
    public async Task ARenameThatFailsAfterTheSaveKeepsTheFileAndTheNextReadFinishesIt()
    {
        var failing = new PrivateFileStorage(_root) { MoveFile = (_, _) => throw new IOException("disk busy") };
        string relativePath;
        await using (var staged = await ((IPrivateFileStorage)failing).StageAsync(_tenant, "a.pdf", "application/pdf", new MemoryStream([1, 2, 3]), 1024, CancellationToken.None))
        {
            relativePath = staged.RelativePath;
            staged.Commit();
        }

        Assert.False(File.Exists(Path.Combine(_root, relativePath)));
        Assert.True(File.Exists(Path.Combine(_root, relativePath) + ".staged"));

        IPrivateFileStorage storage = new PrivateFileStorage(_root);
        await using (var opened = await storage.OpenReadAsync(_tenant, relativePath, CancellationToken.None)) Assert.Equal(3, opened.Length);
        Assert.True(File.Exists(Path.Combine(_root, relativePath)));
        Assert.False(File.Exists(Path.Combine(_root, relativePath) + ".staged"));
    }

    [Fact]
    public async Task TheSweepKeepsASavedFileItCannotRename()
    {
        var failing = new PrivateFileStorage(_root) { MoveFile = (_, _) => throw new IOException("disk busy") };
        var directory = Path.Combine(_root, _tenant.ToString("N"));
        Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, "saved.pdf.staged");
        await File.WriteAllBytesAsync(staged, [1]);
        File.SetLastWriteTimeUtc(staged, DateTime.UtcNow - TimeSpan.FromDays(2));

        var result = await failing.SweepStagedAsync(DateTimeOffset.UtcNow - PrivateFileStorage.StagedMaximumAge, (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(new StagedSweepResult(0, 0, 1), result);
        Assert.True(File.Exists(staged));
    }
}
