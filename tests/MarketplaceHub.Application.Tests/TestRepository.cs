namespace MarketplaceHub.Application.Tests;

// Where the checked-out repository is. Found from the test output, not from [CallerFilePath]: CI builds with
// deterministic source paths (/_/…), which do not exist on disk.
internal static class TestRepository
{
    public static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) return directory.FullName;
        throw new InvalidOperationException("MarketplaceHub.sln not found above the test output.");
    }

    public static string TestProject() => Path.Combine(Root(), "tests", "MarketplaceHub.Application.Tests");
}
