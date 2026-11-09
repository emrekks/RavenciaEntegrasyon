using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record OrderPackageIdentityConflict(string ExternalPackageId, string FirstOrderId, string ConflictingOrderId);

internal static class OrderPackageIdentityGuard
{
    public static IReadOnlyList<OrderPackageIdentityConflict> FindConflicts(IEnumerable<RemoteOrder> remotes)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<OrderPackageIdentityConflict>();

        foreach (var remote in remotes)
        {
            if (string.IsNullOrWhiteSpace(remote.ExternalOrderId)) continue;
            foreach (var package in remote.Packages)
            {
                // Trendyol's "0" names no package: every order awaiting payment carries it until its real one exists.
                if (string.IsNullOrWhiteSpace(package.ExternalPackageId) || ShipmentPackageSupersession.IsPlaceholder(package.ExternalPackageId)) continue;
                if (!owners.TryGetValue(package.ExternalPackageId, out var owner))
                {
                    owners[package.ExternalPackageId] = remote.ExternalOrderId;
                    continue;
                }

                if (string.Equals(owner, remote.ExternalOrderId, StringComparison.Ordinal)) continue;
                if (conflicts.Any(x => string.Equals(x.ExternalPackageId, package.ExternalPackageId, StringComparison.Ordinal)
                    && string.Equals(x.ConflictingOrderId, remote.ExternalOrderId, StringComparison.Ordinal))) continue;
                conflicts.Add(new(package.ExternalPackageId, owner, remote.ExternalOrderId));
            }
        }

        return conflicts;
    }
}
