using System.Reflection;
using System.Security.Cryptography;

namespace MarketplaceHub.Infrastructure.Ai;

public sealed record AiSkillDefinition(string Key, string Title, string Description, IReadOnlyList<ClaudeSkillFile> Files, string BundleHash)
{
    // The name in its SKILL.md (a new Skills API version must keep the skill's name).
    public string Name => AiSkillPackage.FrontmatterName(Files.Single(file => file.Path == $"{Key}/SKILL.md").Content).Name ?? Key;
}

// The Agent Skills the panel can upload, read from the repository's skills/<key>/ folder (embedded in this assembly).
// BundleHash fingerprints the files, so the settings page can tell when the uploaded copy is older than the code.
public static class AiSkillCatalog
{
    public const string SizeChartKey = "beden-tablosu";

    private static readonly (string Key, string Title, string Description)[] Known =
    [
        (SizeChartKey, "Beden Tablosu", "Ürün ekranındaki ölçülerden Ravencia tasarımında 1200×1800 beden tablosu görseli çizer.")
    ];

    private static readonly Lazy<IReadOnlyList<AiSkillDefinition>> Loaded = new(Load);

    public static IReadOnlyList<AiSkillDefinition> All => Loaded.Value;

    public static AiSkillDefinition? Find(string? key) => All.FirstOrDefault(skill => string.Equals(skill.Key, key, StringComparison.Ordinal));

    private static IReadOnlyList<AiSkillDefinition> Load()
    {
        var assembly = typeof(AiSkillCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames();
        return Known.Select(known =>
        {
            var prefix = $"skills/{known.Key}/";
            var files = resources
                .Select(name => (Resource: name, Path: name.Replace('\\', '/')))
                .Where(entry => entry.Path.StartsWith(prefix, StringComparison.Ordinal) && !entry.Path.Contains("/__pycache__/", StringComparison.Ordinal))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .Select(entry => new ClaudeSkillFile(entry.Path["skills/".Length..], Read(assembly, entry.Resource)))
                .ToList();
            return new AiSkillDefinition(known.Key, known.Title, known.Description, files, Hash(files));
        }).Where(skill => skill.Files.Any(file => file.Path == $"{skill.Key}/SKILL.md")).ToList();
    }

    private static byte[] Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static string Hash(IReadOnlyList<ClaudeSkillFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.Path + "\n"));
            hash.AppendData(SHA256.HashData(file.Content));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }
}
