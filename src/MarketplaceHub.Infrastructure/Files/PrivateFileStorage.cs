using System.Security.Cryptography;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Files;

public sealed class PrivateFileStorage(string root) : IPrivateFileStorage
{
    private readonly string _root = Path.GetFullPath(root);
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        { "application/pdf", "text/csv", "application/json", "image/jpeg", "image/png" };

    public async Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken)
    {
        if (!AllowedMimeTypes.Contains(mimeType)) throw new InvalidOperationException("MIME type is not allowed.");
        var safeName = Path.GetFileName(relativeName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName != relativeName) throw new InvalidOperationException("Unsafe file name.");
        var relative = Path.Combine(tenantId.ToString("N"), $"{Guid.NewGuid():N}-{safeName}");
        var target = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            written += read;
            if (written > maximumBytes) { output.Close(); File.Delete(target); throw new InvalidOperationException("File exceeds size limit."); }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return relative.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken)
    {
        var prefix = tenantId.ToString("N") + "/";
        if (!storedPath.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Tenant file boundary violation.");
        Stream stream = new FileStream(Resolve(storedPath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Path traversal rejected.");
        return full;
    }
}
