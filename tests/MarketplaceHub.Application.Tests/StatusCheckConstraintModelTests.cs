using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// VERITABANI-DAGITIM-15: the Status CHECK lists of integration.jobs and billing.invoices must hold exactly what the code
// writes. A new status that is not added here (and to a migration) fails this test instead of every write in production.
public sealed class StatusCheckConstraintModelTests
{
    private static AppDbContext ModelOnly() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=schema-model-only;Username=none;Password=none").Options);

    private static string[] Listed(IEntityType entity, string name)
    {
        var constraint = Assert.Single(entity.GetCheckConstraints(), candidate => candidate.ModelName == name);
        Assert.Matches(new Regex("^\"Status\" IN \\(('[A-Za-z_]+'(, )?)+\\)$"), constraint.Sql);
        return [.. Regex.Matches(constraint.Sql, "'([A-Za-z_]+)'").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void JobStatusCheckListsEveryStoredJobStatus()
    {
        using var db = ModelOnly();
        var job = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(IntegrationJob))!;
        var converter = job.FindProperty(nameof(IntegrationJob.Status))!.GetValueConverter()!;
        var stored = Enum.GetValues<JobStatus>().Select(status => (string)converter.ConvertToProvider(status)!).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(stored, Listed(job, "ck_jobs_status"));
    }

    [Fact]
    public void InvoiceStatusCheckListsEveryInvoiceStatus()
    {
        using var db = ModelOnly();
        var invoice = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Invoice))!;

        Assert.Equal(Enum.GetNames<InvoiceStatus>().Order(StringComparer.Ordinal).ToArray(), Listed(invoice, "ck_invoices_status"));
    }
}
