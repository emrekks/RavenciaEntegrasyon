namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record RequiredAttributeMappingReference(Guid SnapshotId, string ExternalId);

internal static class RequiredAttributeMappingPolicy
{
    public static int CountMissing(
        Guid snapshotId,
        IEnumerable<RequiredAttributeMappingReference> requirements,
        IEnumerable<RequiredAttributeMappingReference> verifiedMappings)
        => FindMissingExternalIds(snapshotId, requirements, verifiedMappings).Count;

    public static IReadOnlySet<string> FindMissingExternalIds(
        Guid snapshotId,
        IEnumerable<RequiredAttributeMappingReference> requirements,
        IEnumerable<RequiredAttributeMappingReference> verifiedMappings)
    {
        var mappedExternalIds = verifiedMappings
            .Where(mapping => mapping.SnapshotId == snapshotId)
            .Select(mapping => mapping.ExternalId)
            .ToHashSet(StringComparer.Ordinal);

        return requirements
            .Where(requirement => requirement.SnapshotId == snapshotId && !mappedExternalIds.Contains(requirement.ExternalId))
            .Select(requirement => requirement.ExternalId)
            .ToHashSet(StringComparer.Ordinal);
    }
}
