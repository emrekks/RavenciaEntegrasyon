using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan C-11 (13 Nov 2026): when several templates name one marketplace attribute, the most recently updated one is
// applied, whatever order the database returns them in.
public sealed class MappingTemplateOrderTests
{
    [Fact]
    public void TheMostRecentlyUpdatedTemplateOfAnAttributeComesFirst_WhateverTheStoredOrder()
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var older = Template("renk", now.AddDays(-2));
        var newer = Template("renk", now);
        var other = Template("beden", now.AddDays(-5));

        foreach (var stored in new[] { new[] { older, newer, other }, new[] { newer, other, older }, new[] { other, older, newer } })
        {
            var ordered = MappingTemplateFiller.OrderTemplates(stored, x => x.ExternalAttributeId, x => x.UpdatedAt, x => x.Id).ToList();
            Assert.Equal([other.Id, newer.Id, older.Id], ordered.Select(x => x.Id));
        }
    }

    private static AttributeMappingTemplate Template(string externalAttributeId, DateTimeOffset updatedAt) =>
        new() { Id = Guid.CreateVersion7(), TenantId = Guid.Empty, ConnectionId = Guid.Empty, ExternalAttributeId = externalAttributeId, LocalAttributeId = Guid.NewGuid(), UpdatedAt = updatedAt };
}
