using System.Net;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 23 Nov 2026: Excel exports had no concurrency limit. A tenant runs at most ExportGate.PerTenant at once; one more is
// answered 429 EXPORT_BUSY ("Dışa Aktarım Sürüyor") at once, and a finished export frees its slot.
public sealed class ExportGateTests
{
    [Fact]
    public void A_tenant_runs_two_exports_at_once_and_a_finished_one_frees_its_slot()
    {
        var gate = new ExportGate();
        var tenant = Guid.NewGuid();
        var first = gate.TryEnter(tenant);
        var second = gate.TryEnter(tenant);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(gate.TryEnter(tenant));
        // Another tenant is not held up by this one.
        using (var other = gate.TryEnter(Guid.NewGuid())) Assert.NotNull(other);
        first!.Dispose();
        first.Dispose();
        Assert.Equal(1, gate.Running(tenant));
        using (var third = gate.TryEnter(tenant)) Assert.NotNull(third);
        second!.Dispose();
        Assert.Equal(0, gate.Running(tenant));
    }
}

[Collection("postgres-integration")]
public sealed class ExportGateEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task A_third_export_while_two_run_is_refused_with_429_and_runs_once_a_slot_is_free()
    {
        var (owner, _, signIn) = await fixture.SignInAsync(MembershipRole.Owner);
        using (owner)
        using (signIn)
        {
            var gate = fixture.Host!.Services.GetRequiredService<ExportGate>();
            var running = new[] { gate.TryEnter(fixture.TenantId)!, gate.TryEnter(fixture.TenantId)! };
            using (var busy = await owner.GetAsync("/api/v1/products/transfer/export.xlsx"))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
                var problem = await ApiTestHost.ProblemAsync(busy);
                Assert.Equal("EXPORT_BUSY", problem.GetProperty("code").GetString());
                Assert.Contains("Dışa Aktarım Sürüyor", problem.GetProperty("title").GetString());
            }
            foreach (var lease in running) lease.Dispose();
            using var free = await owner.GetAsync("/api/v1/products/transfer/export.xlsx");
            Assert.Equal(HttpStatusCode.OK, free.StatusCode);
            await free.Content.ReadAsByteArrayAsync();
            // The answer was sent: its slot is free again.
            Assert.Equal(0, gate.Running(fixture.TenantId));
        }
    }
}
