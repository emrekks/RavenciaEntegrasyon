using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Backups;

// A set backup.sh wrote and verified, with its files opened: the copy streams these handles, the same ones whose
// checksums were checked, so nothing can change between the check and the upload.
public sealed class VerifiedBackupSet(string name, IReadOnlyList<(string Name, FileStream Stream)> files) : IAsyncDisposable
{
    public string Name { get; } = name;
    public IReadOnlyList<(string Name, FileStream Stream)> Files { get; } = files;
    public long PlainBytes => Files.Sum(file => file.Stream.Length);

    // The same layout as `tar -C <backup root> -cf - <set>` (offsite-copy.sh): <set>/ and its files.
    public async Task WriteTarAsync(Stream output, CancellationToken cancellationToken)
    {
        await using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, Name + "/") { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute }, cancellationToken);
        foreach (var (fileName, stream) in Files)
        {
            stream.Position = 0;
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{Name}/{fileName}")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                ModificationTime = File.GetLastWriteTimeUtc(stream.Name),
                DataStream = stream
            };
            await writer.WriteEntryAsync(entry, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, stream) in Files) await stream.DisposeAsync();
    }
}

public interface IBackupSetSource
{
    // The newest set whose files are all present, readable and match SHA256SUMS; null when there is none. Problems with
    // newer sets (still being written, unreadable, checksum mismatch) are returned as notes.
    Task<(VerifiedBackupSet? Set, IReadOnlyList<string> Notes)> OpenNewestVerifiedAsync(CancellationToken cancellationToken);
}

// The worker mounts the backup volume read-only (deploy/compose/compose.yaml, OffsiteBackup:BackupRoot). backup.sh
// gives each new set to the app's group (BACKUP_READER_GID), so the worker can read it without any capability.
public sealed class BackupSetDirectory(string root) : IBackupSetSource
{
    private static readonly string[] RequiredFiles = ["manifest.json", "database.dump", "private-volumes.tar.gz"];

    public string Root { get; } = root;

    public async Task<(VerifiedBackupSet? Set, IReadOnlyList<string> Notes)> OpenNewestVerifiedAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        if (!Directory.Exists(Root)) return (null, [$"Yedek klasörü bulunamadı ({Root}); worker'a yedek birimi bağlanmamış."]);
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateDirectories(Root).Select(Path.GetFileName).OfType<string>()
                .Where(name => OffsiteBackupRules.TryParseSetTime(name, out _)).OrderDescending(StringComparer.Ordinal).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return (null, ["Yedek klasörü okunamıyor (izin yok)."]);
        }
        foreach (var name in names.Take(10))
        {
            var (set, problem) = await TryOpenAsync(name, cancellationToken);
            if (set is not null) return (set, notes);
            notes.Add($"{name}: {problem}");
        }
        return (null, notes);
    }

    internal async Task<(VerifiedBackupSet? Set, string? Problem)> TryOpenAsync(string name, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Root, name);
        var sumsPath = Path.Combine(directory, "SHA256SUMS");
        Dictionary<string, string> sums;
        try
        {
            if (!File.Exists(sumsPath)) return (null, "SHA256SUMS yok (set yazılıyor ya da eksik).");
            sums = ParseSums(await File.ReadAllLinesAsync(sumsPath, cancellationToken));
        }
        catch (UnauthorizedAccessException) { return (null, "okuma izni yok (bu set uygulamanın okuyabileceği izinlerle yazılmamış)."); }
        catch (IOException exception) { return (null, $"okunamadı ({exception.Message})."); }
        if (!RequiredFiles.Skip(1).All(sums.ContainsKey)) return (null, "SHA256SUMS dökümü ve dosya arşivini kapsamıyor.");

        var opened = new List<(string, FileStream)>();
        try
        {
            foreach (var file in RequiredFiles.Concat(["SHA256SUMS"]))
            {
                var path = Path.Combine(directory, file);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                opened.Add((file, stream));
                if (stream.Length == 0) return (await DisposeAll(opened), $"{file} boş.");
                if (sums.TryGetValue(file, out var expected))
                {
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return (await DisposeAll(opened), $"{file} sağlama toplamı tutmuyor.");
                }
            }
            return (new VerifiedBackupSet(name, opened), null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            await DisposeAll(opened);
            return (null, exception is UnauthorizedAccessException ? "okuma izni yok (bu set uygulamanın okuyabileceği izinlerle yazılmamış)." : $"dosya eksik ya da okunamadı ({exception.Message}).");
        }
        catch
        {
            await DisposeAll(opened);
            throw;
        }
    }

    private static async Task<VerifiedBackupSet?> DisposeAll(List<(string, FileStream)> opened)
    {
        foreach (var (_, stream) in opened) await stream.DisposeAsync();
        opened.Clear();
        return null;
    }

    // "<sha256>  <path>" lines; backup.sh writes absolute paths inside its container (/backup/<set>/<file>), so only the
    // file name counts.
    internal static Dictionary<string, string> ParseSums(IEnumerable<string> lines)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 66 || trimmed[64] != ' ') continue;
            var hash = trimmed[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var path = trimmed[65..].TrimStart(' ', '*');
            var fileName = path.Replace('\\', '/').Split('/').Last();
            if (fileName.Length > 0) sums[fileName] = hash.ToLowerInvariant();
        }
        return sums;
    }
}

public interface IBackupEncryptor
{
    // Encrypts what writePlain writes to the age recipient and writes the ciphertext to output.
    Task EncryptAsync(string recipient, Func<Stream, CancellationToken, Task> writePlain, Stream output, CancellationToken cancellationToken);
}

// The official age binary (Ubuntu package "age", BSD-3-Clause, installed in the app image): X25519 to the owner's public
// key, so the server can encrypt but never decrypt. Nothing is written to disk.
public sealed class AgeCommandEncryptor(string executable = "age") : IBackupEncryptor
{
    public async Task EncryptAsync(string recipient, Func<Stream, CancellationToken, Task> writePlain, Stream output, CancellationToken cancellationToken)
    {
        if (!OffsiteBackupRules.IsAgeRecipient(recipient)) throw new InvalidOperationException("The age recipient is not valid.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--encrypt");
        start.ArgumentList.Add("--recipient");
        start.ArgumentList.Add(recipient);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception exception) { throw new GoogleDriveException("AGE_MISSING", "age şifreleme programı sunucuda bulunamadı (uygulama imajında olmalı).", inner: exception); }
        using var registration = cancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, 1 << 16, cancellationToken);
        try
        {
            await using (var input = process.StandardInput.BaseStream)
                await writePlain(input, cancellationToken);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await copy;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var message = (await errors).Trim();
            throw new GoogleDriveException("AGE_FAILED", $"age şifrelemesi başarısız (çıkış {process.ExitCode}{(message.Length > 0 ? ": " + (message.Length > 200 ? message[..200] : message) : string.Empty)}).");
        }
    }
}
