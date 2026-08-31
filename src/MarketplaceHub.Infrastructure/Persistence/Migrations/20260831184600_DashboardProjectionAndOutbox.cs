using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

public partial class DashboardProjectionAndOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "dashboard");
        migrationBuilder.AddColumn<string>(name: "OperationType", schema: "integration", table: "jobs", type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "execute");
        migrationBuilder.AddColumn<string>(name: "ResourceType", schema: "integration", table: "jobs", type: "character varying(48)", maxLength: 48, nullable: false, defaultValue: "jobs");
        migrationBuilder.AddColumn<string>(name: "TriggerType", schema: "integration", table: "jobs", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "system");

        migrationBuilder.CreateTable(name: "snapshot", schema: "dashboard", columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            PendingOrders = table.Column<int>(type: "integer", nullable: false),
            LateOrders = table.Column<int>(type: "integer", nullable: false),
            TodayOrders = table.Column<int>(type: "integer", nullable: false),
            TodayProductQuantity = table.Column<decimal>(type: "numeric", nullable: false),
            MonthOrders = table.Column<int>(type: "integer", nullable: false),
            MonthProductQuantity = table.Column<decimal>(type: "numeric", nullable: false),
            PendingReturns = table.Column<int>(type: "integer", nullable: false),
            DueSoonInvoices = table.Column<int>(type: "integer", nullable: false),
            UninvoicedInvoices = table.Column<int>(type: "integer", nullable: false),
            LowStockProducts = table.Column<int>(type: "integer", nullable: false),
            ActiveConnections = table.Column<int>(type: "integer", nullable: false),
            PendingByPlatformJson = table.Column<string>(type: "jsonb", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Version = table.Column<long>(type: "bigint", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_snapshot", x => x.TenantId);
            table.ForeignKey("FK_snapshot_tenants_TenantId", x => x.TenantId, "iam", "tenants", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "revenue_daily", schema: "dashboard", columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Day = table.Column<DateTime>(type: "date", nullable: false),
            PlatformName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
            Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
            Amount = table.Column<decimal>(type: "numeric", nullable: false),
            OrderCount = table.Column<int>(type: "integer", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_revenue_daily", x => new { x.TenantId, x.Day, x.PlatformName, x.Currency });
            table.ForeignKey("FK_revenue_daily_tenants_TenantId", x => x.TenantId, "iam", "tenants", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "low_stock", schema: "dashboard", columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            ProductId = table.Column<Guid>(type: "uuid", nullable: false),
            Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
            TotalStock = table.Column<decimal>(type: "numeric", nullable: false),
            PrimaryImageUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_low_stock", x => new { x.TenantId, x.ProductId });
            table.ForeignKey("FK_low_stock_tenants_TenantId", x => x.TenantId, "iam", "tenants", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "sync_status", schema: "dashboard", columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            ResourceType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
            DisplayName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
            Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            LastErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Version = table.Column<long>(type: "bigint", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_sync_status", x => new { x.TenantId, x.ResourceType });
            table.ForeignKey("FK_sync_status_tenants_TenantId", x => x.TenantId, "iam", "tenants", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "outbox_events", schema: "integration", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            ResourceType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
            OperationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            AggregateType = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
            AggregateId = table.Column<Guid>(type: "uuid", nullable: true),
            AggregateVersion = table.Column<long>(type: "bigint", nullable: true),
            PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
            PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            DispatchAttempts = table.Column<int>(type: "integer", nullable: false),
            LastDispatchError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_outbox_events", x => x.Id);
            table.ForeignKey("FK_outbox_events_tenants_TenantId", x => x.TenantId, "iam", "tenants", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateIndex(name: "IX_revenue_daily_TenantId_Day", schema: "dashboard", table: "revenue_daily", columns: new[] { "TenantId", "Day" });
        migrationBuilder.CreateIndex(name: "IX_low_stock_TenantId_TotalStock", schema: "dashboard", table: "low_stock", columns: new[] { "TenantId", "TotalStock" });
        migrationBuilder.CreateIndex(name: "IX_outbox_events_PublishedAt_NextAttemptAt_CreatedAt", schema: "integration", table: "outbox_events", columns: new[] { "PublishedAt", "NextAttemptAt", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_outbox_events_TenantId_CreatedAt", schema: "integration", table: "outbox_events", columns: new[] { "TenantId", "CreatedAt" });

        migrationBuilder.Sql("UPDATE integration.jobs SET \"ResourceType\" = CASE \"JobType\" WHEN 'TRENDYOL_ORDER_SYNC' THEN 'orders' WHEN 'TRENDYOL_ORDER_RECOVERY_SYNC' THEN 'orders' WHEN 'TRENDYOL_ORDER_STATUS_SYNC' THEN 'orders' WHEN 'TRENDYOL_ORDER_RECONCILIATION' THEN 'orders' WHEN 'TRENDYOL_WEBHOOK_INGEST' THEN 'orders' WHEN 'TRENDYOL_SHIPMENT_ACTION' THEN 'orders' WHEN 'TRENDYOL_COMMON_LABEL' THEN 'orders' WHEN 'TRENDYOL_RETURN_SYNC' THEN 'returns' WHEN 'TRENDYOL_RETURN_STATUS_SYNC' THEN 'returns' WHEN 'TRENDYOL_RETURN_RECONCILIATION' THEN 'returns' WHEN 'TRENDYOL_RETURN_ACTION' THEN 'returns' WHEN 'TRENDYOL_PRODUCT_SYNC' THEN 'products' WHEN 'TRENDYOL_PRODUCT_CREATE' THEN 'products' WHEN 'TRENDYOL_PRODUCT_APPROVAL_RECONCILE' THEN 'products' WHEN 'TRENDYOL_PRODUCT_UPDATE' THEN 'products' WHEN 'TRENDYOL_PRODUCT_ARCHIVE' THEN 'products' WHEN 'TRENDYOL_PRICE_INVENTORY_SYNC' THEN 'inventory' WHEN 'STOCK_PROJECTION_DISPATCH' THEN 'inventory' WHEN 'TRENDYOL_STOCK_RECONCILIATION' THEN 'inventory' WHEN 'INVOICE_SUBMIT' THEN 'invoices' WHEN 'INVOICE_RECONCILE' THEN 'invoices' WHEN 'INVOICE_DOCUMENT_FETCH' THEN 'invoices' WHEN 'INVOICE_MARKETPLACE_DELIVERY' THEN 'invoices' WHEN 'INVOICE_CANCELLATION' THEN 'invoices' WHEN 'INVOICE_DUE_SCAN' THEN 'invoices' WHEN 'TRENDYOL_CONNECTION_TEST' THEN 'connections' WHEN 'EFATURAM_CONNECTION_TEST' THEN 'connections' WHEN 'TRENDYOL_CAPABILITY_PROBE' THEN 'connections' WHEN 'EFATURAM_STAGE_CAPABILITY_PROBE' THEN 'connections' WHEN 'TRENDYOL_REFERENCE_SYNC' THEN 'connections' ELSE 'jobs' END, \"OperationType\" = CASE WHEN \"JobType\" IN ('IMPORT_PREVIEW', 'IMPORT_APPLY') THEN 'import' ELSE 'sync' END WHERE \"ResourceType\" = 'jobs';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "low_stock", schema: "dashboard");
        migrationBuilder.DropTable(name: "outbox_events", schema: "integration");
        migrationBuilder.DropTable(name: "revenue_daily", schema: "dashboard");
        migrationBuilder.DropTable(name: "snapshot", schema: "dashboard");
        migrationBuilder.DropTable(name: "sync_status", schema: "dashboard");
        migrationBuilder.DropColumn(name: "OperationType", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "ResourceType", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "TriggerType", schema: "integration", table: "jobs");
    }
}
