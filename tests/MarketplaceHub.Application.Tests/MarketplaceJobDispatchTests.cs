using System.Runtime.CompilerServices;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-9 step 1: every job type the processor accepts has a way to run, through its own IJobHandler class or one of
// the processor's dispatch tables; a type in none of them would end Blocked F3_JOB_REJECTED without running.
public sealed class MarketplaceJobDispatchTests
{
    private static readonly IJobHandler[] Handlers = [.. typeof(MarketplaceJobProcessor).Assembly.GetTypes()
        .Where(type => typeof(IJobHandler).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
        .Select(type => (IJobHandler)RuntimeHelpers.GetUninitializedObject(type))];

    [Fact]
    public void EveryProcessorJobTypeHasAHandlerOrADispatchEntry()
    {
        var missing = MarketplaceJobTypes.ProcessorJobTypes
            .Where(jobType => !Handlers.Any(handler => handler.Handles(jobType))
                && !MarketplaceJobProcessor.DirectDispatchJobTypes.Contains(jobType)
                && !MarketplaceJobProcessor.BoolDispatchJobTypes.Contains(jobType))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void DispatchTablesListOnlyProcessorJobTypesWithoutAHandlerClass()
    {
        var listed = MarketplaceJobProcessor.DirectDispatchJobTypes.Concat(MarketplaceJobProcessor.BoolDispatchJobTypes).ToList();
        Assert.All(listed, jobType => Assert.Contains(jobType, MarketplaceJobTypes.ProcessorJobTypes));
        Assert.All(listed, jobType => Assert.DoesNotContain(Handlers, handler => handler.Handles(jobType)));
    }

    // ISLEYICI-9 stage 4: each run job handler's types are dispatched through the processor's tables (so they keep the
    // processor's lock, telemetry and read records), no type has two run handlers, and none has a public IJobHandler too.
    [Fact]
    public void RunJobHandlersRunTypesOfTheDispatchTablesOnly()
    {
        var processor = new MarketplaceJobProcessor(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);
        var types = processor.RunJobHandlers.SelectMany(handler => handler.JobTypes).ToList();
        Assert.NotEmpty(types);
        Assert.Equal(types.Count, types.Distinct(StringComparer.Ordinal).Count());
        var tables = MarketplaceJobProcessor.DirectDispatchJobTypes.Concat(MarketplaceJobProcessor.BoolDispatchJobTypes).ToHashSet(StringComparer.Ordinal);
        Assert.All(types, jobType => Assert.Contains(jobType, tables));
        Assert.All(types, jobType => Assert.DoesNotContain(Handlers, handler => handler.Handles(jobType)));
    }

    [Fact]
    public void OnlyTheTrendyolOnlyPathsAreInBothTables()
    {
        var both = MarketplaceJobProcessor.DirectDispatchJobTypes.Intersect(MarketplaceJobProcessor.BoolDispatchJobTypes).Order(StringComparer.Ordinal);
        Assert.Equal([MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation], both);
    }
}
