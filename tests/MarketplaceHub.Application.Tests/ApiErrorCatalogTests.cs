using System.Text.RegularExpressions;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: ApiErrorCatalog lists every code the API layer answers with. A code written into the API source
// (an error's "CODE", "message"[, status] or the user-management Conflict/Refused helpers) must be listed with that
// status, and every code the route-table probes met must be listed or come from a service.
public sealed partial class ApiErrorCatalogTests
{
    // Upper-case literals followed by a message that are not error codes (an AI call kind, a reference resource type).
    private static readonly HashSet<string> NotErrorCodes = ["SKILL_UPLOAD", "ATTRIBUTE_VALUES"];

    [GeneratedRegex("\"(?<code>[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)+)\",\\s*\\$?@?\"")]
    private static partial Regex CodeWithMessage();

    [GeneratedRegex("\"(?<code>[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)+)\",\\s*(?:\\$?@?\"(?:[^\"\\\\]|\\\\.)*\"|[A-Za-z_.]+)\\s*,\\s*(?:StatusCodes\\.Status(?<status>\\d{3})\\w*|(?<status>\\d{3}))")]
    private static partial Regex CodeWithMessageAndStatus();

    [GeneratedRegex("\\b(?<helper>Conflict|Refused)\\(\"(?<code>[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)+)\"")]
    private static partial Regex HelperCode();

    [GeneratedRegex("\"(?<code>[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)+)\"")]
    private static partial Regex AnyCode();

    [Fact]
    public void EveryCodeTheApiSourceNamesIsCatalogued()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (file, text) in Sources("src/MarketplaceHub.Api"))
        {
            foreach (Match match in CodeWithMessage().Matches(text))
            {
                var code = match.Groups["code"].Value;
                if (!NotErrorCodes.Contains(code) && ApiErrorCatalog.Find(code) is null) missing.Add($"{code} ({file})");
            }
            foreach (Match match in CodeWithMessageAndStatus().Matches(text))
            {
                var code = match.Groups["code"].Value;
                var status = int.Parse(match.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (ApiErrorCatalog.Find(code) is { } entry && entry.Status != status) missing.Add($"{code} {status} (catalogued as {entry.Status}; {file})");
            }
            foreach (Match match in HelperCode().Matches(text))
            {
                var status = match.Groups["helper"].Value == "Conflict" ? 409 : 403;
                if (ApiErrorCatalog.Find(match.Groups["code"].Value) is not { } entry || entry.Status != status) missing.Add($"{match.Groups["code"].Value} {status} ({file})");
            }
        }
        Assert.True(missing.Count == 0, "Add these to ApiErrorCatalog:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TheCatalogueListsEachCodeOnceAndOnlyCodesTheApiNames()
    {
        Assert.Equal(ApiErrorCatalog.All.Count, ApiErrorCatalog.All.Select(entry => entry.Code).Distinct(StringComparer.Ordinal).Count());
        var named = Sources("src/MarketplaceHub.Api").SelectMany(source => AnyCode().Matches(source.Text).Select(match => match.Groups["code"].Value)).ToHashSet(StringComparer.Ordinal);
        var dead = ApiErrorCatalog.All.Where(entry => !named.Contains(entry.Code)).Select(entry => entry.Code).ToList();
        Assert.True(dead.Count == 0, "Catalogued but named nowhere in the API: " + string.Join(", ", dead));
        Assert.All(ApiErrorCatalog.All, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Title)));
    }

    // The probes of ApiRouteTableSnapshotTests met these codes: the pipeline's own (no session, another site) are all
    // catalogued; a signed-in owner's may also come from a service.
    [Fact]
    public void EveryCodeTheRouteProbesMetIsCataloguedOrAServiceCode()
    {
        var serviceCodes = Sources("src/MarketplaceHub.Application").Concat(Sources("src/MarketplaceHub.Infrastructure"))
            .SelectMany(source => AnyCode().Matches(source.Text).Select(match => match.Groups["code"].Value)).ToHashSet(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(Root(), "tests/MarketplaceHub.Application.Tests/ApiRouteTable.snapshot.txt")))
        {
            foreach (var cell in line.Split('|').Skip(2).Select(cell => cell.Trim()))
            {
                var parts = cell.Split(['=', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) continue;
                var (probe, status, code) = (parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2]);
                var entry = ApiErrorCatalog.Find(code);
                var allowed = probe is "anon" or "cross" ? entry is not null && entry.Status == status : entry is not null || serviceCodes.Contains(code);
                if (!allowed) unknown.Add($"{probe}: {code} {status}");
            }
        }
        Assert.True(unknown.Count == 0, "Codes the probes met that are not catalogued:\n" + string.Join("\n", unknown));
    }

    // The catalogue's retryable flag is the one the body carries.
    [Fact]
    public void RetryableIsTheBodysFlag()
    {
        foreach (var entry in ApiErrorCatalog.All)
            Assert.Equal(entry.Retryable, (bool)ApiProblems.Body(new ServiceError(entry.Code, entry.Title, entry.Status), "c")["retryable"]!);
    }

    private static IEnumerable<(string File, string Text)> Sources(string directory) =>
        Directory.EnumerateFiles(Path.Combine(Root(), directory), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.EndsWith("ApiErrorCatalog.cs", StringComparison.Ordinal))
            .Select(file => (Path.GetFileName(file), File.ReadAllText(file)));

    private static string Root() => TestRepository.Root();
}
