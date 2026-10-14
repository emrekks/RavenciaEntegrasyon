using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260901090000_AllowRecreatingDeletedConnections")]
partial class AllowRecreatingDeletedConnections
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) { }
}
