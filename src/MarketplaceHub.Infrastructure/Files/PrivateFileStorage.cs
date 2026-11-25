using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Files;

public sealed class PrivateFileStorage(string root) : IPrivateFileStorage
{
    private readonly string _root = Path.GetFullPath(root);
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        { "application/pdf", "text/csv", "application/json", "image/jpeg", "image/png", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };

    // An uncommitted write's name: the final name plus this suffix, in the same directory (so the commit is a rename on
    // one file system). The hourly leftover sweep removes such files once they are StagedMaximumAge old.
    internal const string StagedSuffix = ".staged";
    public static readonly TimeSpan StagedMaximumAge = TimeSpan.FromDays(1);

    // Writes and commits at once: a write that fails half-way still never leaves a file under the final name.
    public async Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var staged = await StageAsync(tenantId, relativeName, mimeType, content, maximumBytes, cancellationToken);
        staged.Commit();
        return staged.RelativePath;
    }

    public async Task<IStagedPrivateFile> StageAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken)
    {
        if (!AllowedMimeTypes.Contains(mimeType)) throw new InvalidOperationException("MIME type is not allowed.");
        var safeName = Path.GetFileName(relativeName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName != relativeName) throw new InvalidOperationException("Unsafe file name.");
        var relative = Path.Combine(tenantId.ToString("N"), $"{Guid.NewGuid():N}-{safeName}");
        var target = Resolve(relative);
        var temporary = target + StagedSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            long written = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                written += read;
                if (written > maximumBytes) throw new InvalidOperationException("File exceeds size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        return new StagedFile(this, temporary, target, relative.Replace('\\', '/'));
    }

    // How a staged file reaches its final name; tests replace it to make the rename fail.
    internal Action<string, string> MoveFile { get; init; } = (from, to) => File.Move(from, to, overwrite: false);

    // A temporary file older than the cut-off whose final name a database row records was saved but never renamed (the
    // rename after the commit failed): it is renamed now, never deleted. Any other old temporary file is a write whose
    // row was never saved (a process that died in between) and goes.
    public async Task<StagedSweepResult> SweepStagedAsync(DateTimeOffset olderThan, Func<string, CancellationToken, Task<bool>> isReferenced, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root)) return new(0, 0, 0);
        int removed = 0, completed = 0, kept = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(_root, "*" + StagedSuffix, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) >= olderThan.UtcDateTime) continue;
            var target = file[..^StagedSuffix.Length];
            var relative = Path.GetRelativePath(_root, target).Replace('\\', '/');
            if (await isReferenced(relative, cancellationToken))
            {
                if (TryComplete(file, target)) completed++;
                else kept++;
                continue;
            }
            if (TryDelete(file)) removed++;
        }
        return new(removed, completed, kept);
    }

    private bool TryComplete(string temporary, string target)
    {
        if (File.Exists(target)) return false;
        try
        {
            MoveFile(temporary, target);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed class StagedFile(PrivateFileStorage storage, string temporary, string target, string relativePath) : IStagedPrivateFile
    {
        // Set before the rename: Commit is called only after the row is saved, so from then on the file is never deleted
        // here, whether the rename worked or not.
        private bool _commitStarted;
        public string RelativePath { get; } = relativePath;

        // A rename that fails after the commit leaves the temporary file in place and does not fail the request (its row
        // is saved): the next read of the file, or the hourly sweep, finishes the rename.
        public void Commit()
        {
            if (_commitStarted) return;
            _commitStarted = true;
            storage.TryComplete(temporary, target);
        }

        public ValueTask DisposeAsync()
        {
            if (!_commitStarted) TryDelete(temporary);
            return ValueTask.CompletedTask;
        }
    }

    public Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken)
    {
        var full = ResolveForTenant(tenantId, storedPath);
        // A saved row whose rename failed: its file still has the temporary name; finish the rename once.
        if (!File.Exists(full) && File.Exists(full + StagedSuffix)) TryComplete(full + StagedSuffix, full);
        Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }

    public Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken)
    {
        var full = ResolveForTenant(tenantId, storedPath);
        if (!File.Exists(full)) return Task.FromResult(false);
        File.Delete(full);
        return Task.FromResult(true);
    }

    // The tenant boundary is checked on the normalised path: "{tenant}/../{other tenant}/file" starts with
    // the right prefix as typed but resolves into another tenant's folder.
    private string ResolveForTenant(Guid tenantId, string storedPath)
    {
        var full = Resolve(storedPath);
        var tenantRoot = Path.Combine(_root, tenantId.ToString("N")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(tenantRoot, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Tenant file boundary violation.");
        return full;
    }

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Path traversal rejected.");
        return full;
    }
}
