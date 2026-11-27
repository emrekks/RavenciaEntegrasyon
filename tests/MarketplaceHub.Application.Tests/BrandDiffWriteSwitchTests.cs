using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Kuyruk Faz 1B: the brand diff write is on by default and goes back to the full copy through configuration alone. The
// production compose file passes no such variable (production.env only fills compose's own ${...} values), so the
// documented rollback is a release that sets References:BrandDiffWrites to false in the worker's appsettings.json
// (docs/RUNBOOK-GERI-ALMA.md, section 11).
public sealed class BrandDiffWriteSwitchTests
{
    [Fact]
    public void TheSwitchIsOnWithoutConfigurationAndInTheWorkerSettings()
    {
        Assert.True(ReferenceWriteSwitches.BrandDiffWrites(new ConfigurationBuilder().Build()));
        Assert.True(ReferenceWriteSwitches.BrandDiffWrites(WorkerSettings().Build()));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    public void ConfigurationFlipsTheSwitch(string value, bool expected)
    {
        var flipped = WorkerSettings()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ReferenceWriteSwitches.BrandDiffWritesKey] = value })
            .Build();
        Assert.Equal(expected, ReferenceWriteSwitches.BrandDiffWrites(flipped));
    }

    [Fact]
    public void TheWorkerSettingsFileCarriesTheKeyTheRollbackFlips()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MarketplaceHub.Worker", "appsettings.json"));
        Assert.Contains("\"References\": { \"BrandDiffWrites\": true }", text);
        var rolledBack = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"BrandDiffWrites\": true", "\"BrandDiffWrites\": false", StringComparison.Ordinal))))
            .Build();
        Assert.False(ReferenceWriteSwitches.BrandDiffWrites(rolledBack));
    }

    private static IConfigurationBuilder WorkerSettings() =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(RepositoryRoot(), "src", "MarketplaceHub.Worker", "appsettings.json"), optional: false);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) return directory.FullName;
        throw new InvalidOperationException("MarketplaceHub.sln not found above the test output.");
    }
}
