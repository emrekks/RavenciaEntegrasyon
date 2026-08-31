using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260831184600_DashboardProjectionAndOutbox")]
partial class DashboardProjectionAndOutbox
{
    // The checked-in AppDbContextModelSnapshot is the complete target model.
    // Keeping this migration's target hook small avoids duplicating a 200KB
    // generated model while retaining normal EF migration discovery.
    protected override void BuildTargetModel(ModelBuilder modelBuilder) { }
}
