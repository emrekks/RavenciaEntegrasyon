using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Infrastructure.Ai;

// A skill as it is sent to the Skills API: its SKILL.md name, its files (paths under one top folder) and a fingerprint.
public sealed record AiSkillBundle(string Name, IReadOnlyList<ClaudeSkillFile> Files, string BundleHash);

// A .skill / .zip file the owner uploads in Ayarlar › Yapay Zekâ: one top folder holding SKILL.md with a valid
// frontmatter name and description, no path leaving the folder, within the size limits. Nothing in it is run here.
public static partial class AiSkillPackage
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    public const long MaxUnpackedBytes = 25 * 1024 * 1024;
    public const int MaxFiles = 200;

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex NamePattern();

    public static (AiSkillBundle? Bundle, string? Error) Read(Stream content)
    {
        ZipArchive archive;
        try { archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { return (null, "Dosya geçerli bir .skill / .zip arşivi değil."); }
        using (archive)
        {
            var files = new List<ClaudeSkillFile>();
            long total = 0;
            try
            {
                foreach (var entry in archive.Entries)
                {
                    var path = entry.FullName.Replace('\\', '/');
                    if (path.EndsWith('/')) continue; // a folder entry
                    if (path.StartsWith("__MACOSX/", StringComparison.Ordinal) || path.EndsWith("/.DS_Store", StringComparison.Ordinal) || path == ".DS_Store") continue;
                    var parts = path.Split('/');
                    if (path.StartsWith('/') || path.Contains(':') || parts.Any(part => part is "" or "." or ".."))
                        return (null, $"Arşivde izin verilmeyen bir dosya yolu var: {Short(path)}");
                    if (files.Count >= MaxFiles) return (null, $"Arşivde en fazla {MaxFiles} dosya olabilir.");
                    total += entry.Length;
                    if (entry.Length > MaxUnpackedBytes || total > MaxUnpackedBytes) return (null, $"Arşivin açılmış boyutu en fazla {MaxUnpackedBytes / 1024 / 1024} MB olabilir.");
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    // Never trust the declared size: read at most one byte past it (zip bomb guard).
                    var chunk = new byte[81920];
                    int read;
                    while (buffer.Length <= entry.Length && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, entry.Length + 1 - buffer.Length))) > 0)
                        buffer.Write(chunk, 0, read);
                    if (buffer.Length != entry.Length) return (null, "Arşiv bozuk: bir dosyanın boyutu tutmuyor.");
                    files.Add(new ClaudeSkillFile(path, buffer.ToArray()));
                }
            }
            catch (InvalidDataException) { return (null, "Arşiv bozuk; açılamadı."); }

            var tops = files.Select(file => file.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
            if (files.Count == 0) return (null, "Arşiv boş.");
            if (tops.Count != 1 || files.Any(file => !file.Path.Contains('/')))
                return (null, "Arşivde tek bir üst klasör olmalı ve SKILL.md o klasörün içinde bulunmalı.");
            var top = tops[0];
            var skillMd = files.SingleOrDefault(file => file.Path == $"{top}/SKILL.md");
            if (skillMd is null) return (null, $"'{top}/SKILL.md' dosyası arşivde yok.");
            var (name, error) = FrontmatterName(skillMd.Content);
            if (name is null) return (null, error);
            files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
            return (new AiSkillBundle(name, files, AiSkillCatalog.Hash(files)), null);
        }
    }

    // SKILL.md's YAML frontmatter: a name the Skills API accepts and a non-empty description of at most 1,024 characters.
    public static (string? Name, string? Error) FrontmatterName(byte[] skillMd)
    {
        var text = Encoding.UTF8.GetString(skillMd).Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (null, "SKILL.md '---' ile başlayan bir ön bilgi (frontmatter) bloğuyla başlamalı.");
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (null, "SKILL.md ön bilgi bloğu kapanmamış.");
        string? name = null, description = null;
        foreach (var line in text[4..end].Split('\n'))
        {
            if (line.StartsWith("name:", StringComparison.Ordinal)) name = Unquote(line[5..]);
            else if (line.StartsWith("description:", StringComparison.Ordinal)) description = Unquote(line[12..]);
        }
        if (string.IsNullOrEmpty(name)) return (null, "SKILL.md ön bilgisinde 'name' yok.");
        if (!NamePattern().IsMatch(name) || name.Contains("anthropic", StringComparison.Ordinal) || name.Contains("claude", StringComparison.Ordinal))
            return (null, $"SKILL.md 'name' değeri geçersiz ({Short(name)}): en fazla 64 küçük harf, rakam ya da tire olmalı.");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 1024) return (null, "SKILL.md 'description' boş olamaz ve en fazla 1024 karakter olabilir.");
        return (name, null);
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && (trimmed[0] == '"' && trimmed[^1] == '"' || trimmed[0] == '\'' && trimmed[^1] == '\'') ? trimmed[1..^1] : trimmed;
    }

    private static string Short(string value) => value.Length <= 80 ? value : value[..80] + "…";
}
