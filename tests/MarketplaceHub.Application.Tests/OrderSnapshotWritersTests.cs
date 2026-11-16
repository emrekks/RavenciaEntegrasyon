using System.Text.RegularExpressions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-9: the Returns search reads Order.CustomerSearchName, which the change tracker writes with the snapshots
// (OrderCustomerSearchNames). A snapshot written outside the tracker empties the name (the database trigger) and sends
// the order's claims to the capped view path until the backfill. The only such writer is the invoice check
// (MarketplaceJobProcessor.InvoiceReconciliation), and it writes the name with the snapshots; a new one fails here.
public sealed partial class OrderSnapshotWritersTests
{
    private static readonly string[] AllowedWriters = ["src/MarketplaceHub.Infrastructure/Persistence/JobProcessing/MarketplaceJobProcessor.InvoiceReconciliation.cs"];

    [Fact]
    public void OnlyTheInvoiceCheckWritesSnapshotsOutsideTheTracker_AndItWritesTheSearchName()
    {
        var root = RepositoryRoot();
        var writers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => SnapshotWrite().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(AllowedWriters, writers);
        foreach (var writer in AllowedWriters)
            Assert.Matches(SearchNameWrite(), File.ReadAllText(Path.Combine(root, writer)));
    }

    // A bulk update setting a snapshot column, or a raw statement assigning one.
    [GeneratedRegex(@"SetProperty\(\s*\w+\s*=>\s*\w+\.(CustomerSnapshotJson|InvoiceAddressSnapshotJson|ShipmentAddressSnapshotJson)\b|""(CustomerSnapshotJson|InvoiceAddressSnapshotJson|ShipmentAddressSnapshotJson)""\s*=")]
    private static partial Regex SnapshotWrite();

    [GeneratedRegex(@"SetProperty\(\s*\w+\s*=>\s*\w+\.CustomerSearchName\b")]
    private static partial Regex SearchNameWrite();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) return directory.FullName;
        throw new InvalidOperationException("MarketplaceHub.sln not found above the test output.");
    }
}
