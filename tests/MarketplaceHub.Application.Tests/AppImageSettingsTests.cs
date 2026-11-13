using Xunit;

namespace MarketplaceHub.Application.Tests;

// The application image binds the API with the base image's own ASPNETCORE_HTTP_PORTS. ASPNETCORE_URLS next to it made
// every API start on live log "Overriding HTTP_PORTS ... Binding to values defined by URLS instead" as a warning
// (46 in one day, 13 Nov 2026), noise in the error and warning reads.
public sealed class AppImageSettingsTests
{
    [Fact]
    public void The_api_port_is_set_with_http_ports_only()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot(), "Dockerfile"));
        var settings = dockerfile.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("ENV ", StringComparison.Ordinal)).ToList();

        Assert.Contains(settings, line => line.Contains("ASPNETCORE_HTTP_PORTS=8080", StringComparison.Ordinal));
        Assert.DoesNotContain(settings, line => line.Contains("ASPNETCORE_URLS", StringComparison.Ordinal));
        // The container health check probes the same port.
        Assert.Contains("http://127.0.0.1:8080/health/ready", File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MarketplaceHub.Api", "Program.cs")), StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) return directory.FullName;
        throw new InvalidOperationException("MarketplaceHub.sln not found above the test output.");
    }
}
