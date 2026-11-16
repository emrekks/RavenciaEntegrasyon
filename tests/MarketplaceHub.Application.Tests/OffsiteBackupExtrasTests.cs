using System.Formats.Tar;
using System.Security.Cryptography;
using MarketplaceHub.Infrastructure.Backups;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// VERITABANI-DAGITIM-8/-9: the host's age-encrypted escrow and WAL archive ride along in every off-site copy as
// <set>/extra/<part>/<file>; a plain, unverified or mismatching part is left out and named in the notes.
public sealed class OffsiteBackupExtrasTests
{
    [Fact]
    public async Task EncryptedVerifiedPartsTravelWithTheSet_PlainOrIncompletePartsAreLeftOut()
    {
        using var root = new TempBackupRoot();
        root.WriteSet("20261116T085900Z");
        WritePart(root, "escrow", ("ravencia-secrets.tar.age", Age("secrets")));
        WritePart(root, "wal", ("wal-archive.tar.age", Age("wal")));
        WritePart(root, "plain", ("credential_key.txt.age", "not encrypted at all"u8.ToArray()));
        WritePart(root, "named-wrong", ("credential_key.txt", Age("x")));
        WritePart(root, "unverified", false, ("a.tar.age", Age("a")));
        WritePart(root, "changed", ("b.tar.age", Age("b")));
        File.AppendAllText(Path.Combine(root.Path, BackupSetDirectory.ExtraFolder, "changed", "b.tar.age"), "tampered");

        var (set, notes) = await new BackupSetDirectory(root.Path).OpenNewestVerifiedAsync(CancellationToken.None);

        Assert.NotNull(set);
        await using (set)
        {
            using var tar = new MemoryStream();
            await set.WriteTarAsync(tar, CancellationToken.None);
            tar.Position = 0;
            var names = new List<string>();
            await using var reader = new TarReader(tar);
            while (await reader.GetNextEntryAsync() is { } entry) names.Add(entry.Name);
            Assert.Equal(
                ["20261116T085900Z/", "20261116T085900Z/manifest.json", "20261116T085900Z/database.dump", "20261116T085900Z/private-volumes.tar.gz", "20261116T085900Z/SHA256SUMS",
                 "20261116T085900Z/extra/escrow/ravencia-secrets.tar.age", "20261116T085900Z/extra/wal/wal-archive.tar.age"],
                names);
        }
        Assert.Contains(notes, note => note.Contains("plain:", StringComparison.Ordinal) && note.Contains("age ile", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("named-wrong:", StringComparison.Ordinal) && note.Contains(".age", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("unverified:", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("changed:", StringComparison.Ordinal) && note.Contains("sağlama", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheExtraFolderTheSetIsUnchanged()
    {
        using var root = new TempBackupRoot();
        root.WriteSet("20261116T085900Z");
        var (set, notes) = await new BackupSetDirectory(root.Path).OpenNewestVerifiedAsync(CancellationToken.None);
        Assert.NotNull(set);
        await using (set) Assert.DoesNotContain(set.Files, file => file.Name.StartsWith("extra/", StringComparison.Ordinal));
        Assert.Empty(notes);
    }

    private static byte[] Age(string body) => [.. "age-encryption.org/v1\n-> X25519 stub\n"u8.ToArray(), .. System.Text.Encoding.UTF8.GetBytes(body)];

    private static void WritePart(TempBackupRoot root, string part, params (string Name, byte[] Content)[] files) => WritePart(root, part, true, files);

    private static void WritePart(TempBackupRoot root, string part, bool verified, params (string Name, byte[] Content)[] files)
    {
        var directory = Path.Combine(root.Path, BackupSetDirectory.ExtraFolder, part);
        Directory.CreateDirectory(directory);
        var sums = new List<string>();
        foreach (var (name, content) in files)
        {
            File.WriteAllBytes(Path.Combine(directory, name), content);
            sums.Add($"{Convert.ToHexStringLower(SHA256.HashData(content))}  {name}");
        }
        File.WriteAllLines(Path.Combine(directory, "SHA256SUMS"), sums);
        if (verified) File.WriteAllText(Path.Combine(directory, BackupSetDirectory.VerifiedMarker), "ok");
    }
}
