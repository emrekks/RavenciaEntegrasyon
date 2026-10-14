using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Keeps the connection-wide mapping templates in step with the category-scoped mappings a user
// saves: the latest decision for a marketplace attribute (or for a panel value inside it) becomes
// the template other categories are filled from. Changes are added to the context; the caller saves.
internal static class MappingTemplateStore
{
    // Value mapping scopes are "{categoryExternalId}/{attributeExternalId}".
    public static string? AttributeOfValueScope(string scope)
    {
        var separator = scope.IndexOf('/');
        return separator <= 0 || separator == scope.Length - 1 ? null : scope[(separator + 1)..];
    }

    public static async Task RecordAttributeAsync(AppDbContext db, Guid tenantId, Guid connectionId, string externalAttributeId, Guid localAttributeId, string role, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var template = db.AttributeMappingTemplates.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == externalAttributeId && x.LocalAttributeId == localAttributeId)
            ?? await db.AttributeMappingTemplates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == externalAttributeId && x.LocalAttributeId == localAttributeId, cancellationToken);
        if (template is null)
        {
            db.AttributeMappingTemplates.Add(new AttributeMappingTemplate
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ExternalAttributeId = externalAttributeId,
                LocalAttributeId = localAttributeId,
                Role = role,
                UpdatedAt = now,
                Version = 1
            });
            return;
        }
        template.Role = role;
        template.UpdatedAt = now;
        template.Version++;
    }

    public static async Task RecordValueAsync(AppDbContext db, Guid tenantId, Guid connectionId, string valueScope, Guid localValueId, string externalValueId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (AttributeOfValueScope(valueScope) is not { } externalAttributeId) return;
        var template = db.AttributeValueMappingTemplates.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == externalAttributeId && x.LocalValueId == localValueId)
            ?? await db.AttributeValueMappingTemplates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == externalAttributeId && x.LocalValueId == localValueId, cancellationToken);
        if (template is null)
        {
            db.AttributeValueMappingTemplates.Add(new AttributeValueMappingTemplate
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ExternalAttributeId = externalAttributeId,
                LocalValueId = localValueId,
                ExternalValueId = externalValueId,
                UpdatedAt = now,
                Version = 1
            });
            return;
        }
        template.ExternalValueId = externalValueId;
        template.UpdatedAt = now;
        template.Version++;
    }

    // A removed mapping only takes the template with it when no other category still holds the
    // same decision; otherwise the template keeps serving those categories.
    public static async Task ForgetAttributeAsync(AppDbContext db, Guid tenantId, Guid connectionId, string externalAttributeId, Guid localAttributeId, Guid removedMappingId, CancellationToken cancellationToken)
    {
        var stillUsed = await db.AttributeMappings.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id != removedMappingId
            && x.ExternalId == externalAttributeId && x.LocalId == localAttributeId && x.Status == "VERIFIED", cancellationToken);
        if (stillUsed) return;
        await db.AttributeMappingTemplates
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == externalAttributeId && x.LocalAttributeId == localAttributeId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // After a value mapping is removed the template falls back to the latest decision still held by
    // another category (e.g. Bluz Siyah→14 removed, T-shirt Siyah→13 kept: the template becomes 13),
    // and disappears only when no category maps the value any more.
    public static async Task ForgetValueAsync(AppDbContext db, Guid tenantId, Guid connectionId, string valueScope, Guid localValueId, string externalValueId, Guid removedMappingId, CancellationToken cancellationToken)
    {
        if (AttributeOfValueScope(valueScope) is not { } externalAttributeId) return;
        var suffix = "/" + externalAttributeId;
        var remaining = await db.AttributeValueMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id != removedMappingId
                && x.LocalId == localValueId && x.Status == "VERIFIED" && x.ScopeExternalId.EndsWith(suffix))
            .OrderByDescending(x => x.VerifiedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var template = await db.AttributeValueMappingTemplates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == externalAttributeId && x.LocalValueId == localValueId, cancellationToken);
        if (template is null) return;
        if (remaining is null) db.AttributeValueMappingTemplates.Remove(template);
        else if (template.ExternalValueId != remaining.ExternalId)
        {
            template.ExternalValueId = remaining.ExternalId;
            template.UpdatedAt = remaining.VerifiedAt ?? template.UpdatedAt;
            template.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
