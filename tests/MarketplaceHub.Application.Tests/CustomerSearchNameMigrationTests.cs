using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Migration review of CustomerSearchName (M1): EF commits its open transaction at the first suppressTransaction command, so
// the CONCURRENTLY statements must come before every transactional one. A failed index build then leaves nothing applied
// and a rerun clears the INVALID index; the columns, function, trigger and history row commit together.
public sealed class CustomerSearchNameMigrationTests
{
    [Fact]
    public void TheConcurrentIndexStatements_RunBeforeEveryTransactionalOne()
    {
        var operations = new CustomerSearchName().UpOperations;
        var suppressed = operations.Select(operation => operation is SqlOperation { SuppressTransaction: true }).ToList();
        var firstTransactional = suppressed.IndexOf(false);
        Assert.True(firstTransactional > 0);
        Assert.DoesNotContain(true, suppressed.Skip(firstTransactional));
        Assert.Contains(operations.Take(firstTransactional), operation => operation is SqlOperation sql && sql.Sql.Contains("CREATE INDEX CONCURRENTLY", StringComparison.Ordinal));
    }
}
