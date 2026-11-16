using System.Text.RegularExpressions;
using MarketplaceHub.Infrastructure.Jobs;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-8: the table of job-creating sites (JobEnqueueSites) stays complete and its priority verdicts stay true.
public sealed partial class JobEnqueueSitesTests
{
    [Fact]
    public void EveryFileThatCreatesJobsIsInTheTableWithItsCount()
    {
        var root = RepositoryRoot();
        var found = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("JobEnqueuer.cs", StringComparison.Ordinal)
                && !path.EndsWith("JobEnqueueSites.cs", StringComparison.Ordinal))
            .Select(path => (File: Path.GetRelativePath(root, path).Replace('\\', '/'), Count: CreatingCall().Matches(File.ReadAllText(path)).Count))
            .Where(file => file.Count > 0)
            .ToDictionary(file => file.File, file => file.Count, StringComparer.Ordinal);

        Assert.Equal(JobEnqueueSites.CreatingCallsPerFile.OrderBy(x => x.Key, StringComparer.Ordinal), found.OrderBy(x => x.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void EachSitesAutoFillVerdictMatchesThePriorityPolicy()
    {
        Assert.Equal(46, JobEnqueueSites.All.Count);
        foreach (var site in JobEnqueueSites.All)
            Assert.True(site.AutoFillIdentical == (site.PriorityToday == JobPriorityPolicy.For(site.SampleJobType, site.UserInitiated)),
                $"{site.Location}: today {site.PriorityToday}, policy {JobPriorityPolicy.For(site.SampleJobType, site.UserInitiated)}, table says identical={site.AutoFillIdentical}");
        // Auto-fill would change 22 sites, so the factory keeps taking the priority from the request.
        Assert.Equal(22, JobEnqueueSites.All.Count(site => !site.AutoFillIdentical));
    }

    [Fact]
    public void EveryCallerCheckedSiteNamesItsOwnRule()
    {
        foreach (var site in JobEnqueueSites.All)
        {
            Assert.Contains(site.Dedup, new[] { JobEnqueueSites.CallerChecked, JobEnqueueSites.ReuseAnyWithKey, JobEnqueueSites.ReuseHeldElseRetire, JobEnqueueSites.OnConflict });
            if (site.Dedup == JobEnqueueSites.CallerChecked) Assert.False(string.IsNullOrWhiteSpace(site.CallerRule), site.Location);
        }
    }

    [GeneratedRegex(@"JobEnqueuer\.(EnqueueAsync|Add)\(|IntegrationJobFactory\.New\(|INSERT INTO integration\.jobs")]
    private static partial Regex CreatingCall();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) return directory.FullName;
        throw new InvalidOperationException("MarketplaceHub.sln not found above the test output.");
    }
}
