using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class F3TrendyolVerticalSlice : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "sales");

        migrationBuilder.CreateTable(
            name: "cargo_provider_mappings",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalProviderId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ExternalProviderName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                LocalProviderCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_cargo_provider_mappings", x => x.Id);
                table.ForeignKey(
                    name: "FK_cargo_provider_mappings_platform_connections_TenantId_Conne~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "connection_sync_policies",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                OverlapSeconds = table.Column<int>(type: "integer", nullable: false),
                JitterSeconds = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_connection_sync_policies", x => x.Id);
                table.CheckConstraint("ck_connection_sync_policy_intervals", "\"IntervalSeconds\" > 0 AND \"OverlapSeconds\" >= 0 AND \"JitterSeconds\" >= 0");
                table.ForeignKey(
                    name: "FK_connection_sync_policies_platform_connections_TenantId_Conn~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "orders",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalOrderId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                OrderNumber = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Currency = table.Column<string>(type: "char(3)", nullable: false),
                GrossAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                DiscountAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                NetAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                OrderedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastRemoteModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CustomerSnapshotJson = table.Column<string>(type: "text", nullable: false),
                ShipmentAddressSnapshotJson = table.Column<string>(type: "text", nullable: false),
                InvoiceAddressSnapshotJson = table.Column<string>(type: "text", nullable: false),
                DerivedStatus = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_orders", x => x.Id);
                table.UniqueConstraint("AK_orders_TenantId_Id", x => new { x.TenantId, x.Id });
                table.CheckConstraint("ck_order_amounts", "\"GrossAmount\" >= 0 AND \"DiscountAmount\" >= 0 AND \"NetAmount\" >= 0");
                table.CheckConstraint("ck_order_currency", "char_length(\"Currency\")=3");
                table.ForeignKey(
                    name: "FK_orders_platform_connections_TenantId_ConnectionId",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "platform_capabilities",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                SupportLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ApiVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Environment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                StoreScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                SourceUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                SourceVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                RequiredScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ConstraintsJson = table.Column<string>(type: "text", nullable: true),
                EvidenceNote = table.Column<string>(type: "text", nullable: true),
                FixtureChecksum = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_capabilities", x => x.Id);
                table.ForeignKey(
                    name: "FK_platform_capabilities_platform_connections_TenantId_Connect~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "platform_credentials",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                CredentialType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ProtectedPayload = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                MaskedHint = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_credentials", x => x.Id);
                table.ForeignKey(
                    name: "FK_platform_credentials_platform_connections_TenantId_Connecti~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "reconciliation_runs",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                Scope = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ComparedCount = table.Column<int>(type: "integer", nullable: false),
                DifferenceCount = table.Column<int>(type: "integer", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reconciliation_runs", x => x.Id);
                table.UniqueConstraint("AK_reconciliation_runs_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_reconciliation_runs_platform_connections_TenantId_Connectio~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "sync_cursors",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                OpaqueCursor = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                LastModifiedWatermark = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sync_cursors", x => x.Id);
                table.ForeignKey(
                    name: "FK_sync_cursors_platform_connections_TenantId_ConnectionId",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "webhook_subscriptions",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                RouteTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AuthenticationType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProtectedVerifierSecret = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                ExternalSubscriptionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_webhook_subscriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_webhook_subscriptions_platform_connections_TenantId_Connect~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "order_lines",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                ExternalLineId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Sku = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Barcode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                TitleSnapshot = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                OrderedQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                CancelledQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                ShippedQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                DeliveredQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                ReturnedQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                UnitPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                VatRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                RawStatus = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_lines", x => x.Id);
                table.UniqueConstraint("AK_order_lines_TenantId_Id", x => new { x.TenantId, x.Id });
                table.CheckConstraint("ck_order_line_quantities", "\"OrderedQuantity\" >= 0 AND \"CancelledQuantity\" >= 0 AND \"ShippedQuantity\" >= 0 AND \"DeliveredQuantity\" >= 0 AND \"ReturnedQuantity\" >= 0 AND \"ShippedQuantity\" <= \"OrderedQuantity\" AND \"DeliveredQuantity\" <= \"ShippedQuantity\" AND \"ReturnedQuantity\" <= \"DeliveredQuantity\"");
                table.ForeignKey(
                    name: "FK_order_lines_orders_TenantId_OrderId",
                    columns: x => new { x.TenantId, x.OrderId },
                    principalSchema: "sales",
                    principalTable: "orders",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "return_claims",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalClaimId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RawStatus = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                ReasonCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                ReasonText = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                ActionDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastRemoteModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_return_claims", x => x.Id);
                table.UniqueConstraint("AK_return_claims_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_return_claims_orders_TenantId_OrderId",
                    columns: x => new { x.TenantId, x.OrderId },
                    principalSchema: "sales",
                    principalTable: "orders",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_return_claims_platform_connections_TenantId_ConnectionId",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "shipment_packages",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalPackageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                OriginExternalPackageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CargoProviderExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CargoTrackingNumber = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RawStatus = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                StatusOccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RemoteVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_shipment_packages", x => x.Id);
                table.UniqueConstraint("AK_shipment_packages_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_shipment_packages_orders_TenantId_OrderId",
                    columns: x => new { x.TenantId, x.OrderId },
                    principalSchema: "sales",
                    principalTable: "orders",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_shipment_packages_platform_connections_TenantId_ConnectionId",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "reconciliation_differences",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                RunId = table.Column<Guid>(type: "uuid", nullable: false),
                EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                EntityKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                FieldName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                LocalValueHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                RemoteValueHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Resolution = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reconciliation_differences", x => x.Id);
                table.ForeignKey(
                    name: "FK_reconciliation_differences_reconciliation_runs_TenantId_Run~",
                    columns: x => new { x.TenantId, x.RunId },
                    principalSchema: "integration",
                    principalTable: "reconciliation_runs",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "order_financial_allocations",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                AllocationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "char(3)", nullable: false),
                SourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_financial_allocations", x => x.Id);
                table.ForeignKey(
                    name: "FK_order_financial_allocations_order_lines_TenantId_OrderLineId",
                    columns: x => new { x.TenantId, x.OrderLineId },
                    principalSchema: "sales",
                    principalTable: "order_lines",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_order_financial_allocations_orders_TenantId_OrderId",
                    columns: x => new { x.TenantId, x.OrderId },
                    principalSchema: "sales",
                    principalTable: "orders",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "return_decisions",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                ReasonCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                Explanation = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ExternalOperationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_return_decisions", x => x.Id);
                table.UniqueConstraint("AK_return_decisions_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_return_decisions_return_claims_TenantId_ClaimId",
                    columns: x => new { x.TenantId, x.ClaimId },
                    principalSchema: "sales",
                    principalTable: "return_claims",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "return_lines",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalLineId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_return_lines", x => x.Id);
                table.UniqueConstraint("AK_return_lines_TenantId_Id", x => new { x.TenantId, x.Id });
                table.CheckConstraint("ck_return_line_quantity", "\"Quantity\" > 0");
                table.ForeignKey(
                    name: "FK_return_lines_order_lines_TenantId_OrderLineId",
                    columns: x => new { x.TenantId, x.OrderLineId },
                    principalSchema: "sales",
                    principalTable: "order_lines",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_return_lines_return_claims_TenantId_ClaimId",
                    columns: x => new { x.TenantId, x.ClaimId },
                    principalSchema: "sales",
                    principalTable: "return_claims",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "order_status_history",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                CanonicalStatus = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                RawStatus = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                SourceEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_status_history", x => x.Id);
                table.ForeignKey(
                    name: "FK_order_status_history_orders_TenantId_OrderId",
                    columns: x => new { x.TenantId, x.OrderId },
                    principalSchema: "sales",
                    principalTable: "orders",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_order_status_history_shipment_packages_TenantId_PackageId",
                    columns: x => new { x.TenantId, x.PackageId },
                    principalSchema: "sales",
                    principalTable: "shipment_packages",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "package_line_allocations",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                AllocatedQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                CancelledQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                ShippedQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                DeliveredQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                ReturnedQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                SourceEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_package_line_allocations", x => x.Id);
                table.CheckConstraint("ck_package_allocation_quantities", "\"AllocatedQuantity\" >= 0 AND \"CancelledQuantity\" >= 0 AND \"ShippedQuantity\" >= 0 AND \"DeliveredQuantity\" >= 0 AND \"ReturnedQuantity\" >= 0");
                table.ForeignKey(
                    name: "FK_package_line_allocations_order_lines_TenantId_OrderLineId",
                    columns: x => new { x.TenantId, x.OrderLineId },
                    principalSchema: "sales",
                    principalTable: "order_lines",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_package_line_allocations_shipment_packages_TenantId_Package~",
                    columns: x => new { x.TenantId, x.PackageId },
                    principalSchema: "sales",
                    principalTable: "shipment_packages",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "shipment_documents",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Checksum = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                DocumentVersion = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_shipment_documents", x => x.Id);
                table.UniqueConstraint("AK_shipment_documents_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_shipment_documents_file_assets_FileAssetId",
                    column: x => x.FileAssetId,
                    principalSchema: "ops",
                    principalTable: "file_assets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_shipment_documents_shipment_packages_TenantId_PackageId",
                    columns: x => new { x.TenantId, x.PackageId },
                    principalSchema: "sales",
                    principalTable: "shipment_packages",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "return_evidence",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                EvidenceKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Checksum = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_return_evidence", x => x.Id);
                table.ForeignKey(
                    name: "FK_return_evidence_file_assets_FileAssetId",
                    column: x => x.FileAssetId,
                    principalSchema: "ops",
                    principalTable: "file_assets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_return_evidence_return_claims_TenantId_ClaimId",
                    columns: x => new { x.TenantId, x.ClaimId },
                    principalSchema: "sales",
                    principalTable: "return_claims",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_return_evidence_return_decisions_TenantId_DecisionId",
                    columns: x => new { x.TenantId, x.DecisionId },
                    principalSchema: "sales",
                    principalTable: "return_decisions",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "return_stock_dispositions",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                ReturnLineId = table.Column<Guid>(type: "uuid", nullable: false),
                InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                Disposition = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_return_stock_dispositions", x => x.Id);
                table.CheckConstraint("ck_return_stock_disposition_quantity", "\"Quantity\" > 0");
                table.ForeignKey(
                    name: "FK_return_stock_dispositions_inventory_items_TenantId_Inventor~",
                    columns: x => new { x.TenantId, x.InventoryItemId },
                    principalSchema: "inventory",
                    principalTable: "inventory_items",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_return_stock_dispositions_return_claims_TenantId_ClaimId",
                    columns: x => new { x.TenantId, x.ClaimId },
                    principalSchema: "sales",
                    principalTable: "return_claims",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_return_stock_dispositions_return_lines_TenantId_ReturnLineId",
                    columns: x => new { x.TenantId, x.ReturnLineId },
                    principalSchema: "sales",
                    principalTable: "return_lines",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "shipment_document_attempts",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ExternalOperationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_shipment_document_attempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_shipment_document_attempts_shipment_documents_TenantId_Docu~",
                    columns: x => new { x.TenantId, x.DocumentId },
                    principalSchema: "sales",
                    principalTable: "shipment_documents",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_shipment_document_attempts_shipment_packages_TenantId_Packa~",
                    columns: x => new { x.TenantId, x.PackageId },
                    principalSchema: "sales",
                    principalTable: "shipment_packages",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_cargo_provider_mappings_TenantId_ConnectionId_ExternalProvi~",
            schema: "sales",
            table: "cargo_provider_mappings",
            columns: new[] { "TenantId", "ConnectionId", "ExternalProviderId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_connection_sync_policies_TenantId_ConnectionId_ResourceType",
            schema: "integration",
            table: "connection_sync_policies",
            columns: new[] { "TenantId", "ConnectionId", "ResourceType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_order_financial_allocations_TenantId_OrderId_SourceKey",
            schema: "sales",
            table: "order_financial_allocations",
            columns: new[] { "TenantId", "OrderId", "SourceKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_order_financial_allocations_TenantId_OrderLineId",
            schema: "sales",
            table: "order_financial_allocations",
            columns: new[] { "TenantId", "OrderLineId" });

        migrationBuilder.CreateIndex(
            name: "IX_order_lines_TenantId_OrderId_ExternalLineId",
            schema: "sales",
            table: "order_lines",
            columns: new[] { "TenantId", "OrderId", "ExternalLineId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_order_status_history_TenantId_OrderId_OccurredAt",
            schema: "sales",
            table: "order_status_history",
            columns: new[] { "TenantId", "OrderId", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_order_status_history_TenantId_OrderId_SourceEventId",
            schema: "sales",
            table: "order_status_history",
            columns: new[] { "TenantId", "OrderId", "SourceEventId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_order_status_history_TenantId_PackageId",
            schema: "sales",
            table: "order_status_history",
            columns: new[] { "TenantId", "PackageId" });

        migrationBuilder.CreateIndex(
            name: "IX_orders_TenantId_ConnectionId_ExternalOrderId",
            schema: "sales",
            table: "orders",
            columns: new[] { "TenantId", "ConnectionId", "ExternalOrderId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_orders_TenantId_OrderedAt_Id",
            schema: "sales",
            table: "orders",
            columns: new[] { "TenantId", "OrderedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_package_line_allocations_TenantId_OrderLineId",
            schema: "sales",
            table: "package_line_allocations",
            columns: new[] { "TenantId", "OrderLineId" });

        migrationBuilder.CreateIndex(
            name: "IX_package_line_allocations_TenantId_PackageId_OrderLineId_Sou~",
            schema: "sales",
            table: "package_line_allocations",
            columns: new[] { "TenantId", "PackageId", "OrderLineId", "SourceEventId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_capabilities_TenantId_ConnectionId_Code_ApiVersion~",
            schema: "integration",
            table: "platform_capabilities",
            columns: new[] { "TenantId", "ConnectionId", "Code", "ApiVersion", "Environment", "StoreScope" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_credentials_TenantId_ConnectionId_RevokedAt",
            schema: "integration",
            table: "platform_credentials",
            columns: new[] { "TenantId", "ConnectionId", "RevokedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_reconciliation_differences_TenantId_RunId_EntityType_Entity~",
            schema: "integration",
            table: "reconciliation_differences",
            columns: new[] { "TenantId", "RunId", "EntityType", "EntityKey", "FieldName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_reconciliation_runs_TenantId_ConnectionId_StartedAt",
            schema: "integration",
            table: "reconciliation_runs",
            columns: new[] { "TenantId", "ConnectionId", "StartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_return_claims_TenantId_ConnectionId_ExternalClaimId",
            schema: "sales",
            table: "return_claims",
            columns: new[] { "TenantId", "ConnectionId", "ExternalClaimId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_return_claims_TenantId_OrderId",
            schema: "sales",
            table: "return_claims",
            columns: new[] { "TenantId", "OrderId" });

        migrationBuilder.CreateIndex(
            name: "IX_return_claims_TenantId_Status_ActionDueAt",
            schema: "sales",
            table: "return_claims",
            columns: new[] { "TenantId", "Status", "ActionDueAt" });

        migrationBuilder.CreateIndex(
            name: "IX_return_decisions_TenantId_ClaimId",
            schema: "sales",
            table: "return_decisions",
            columns: new[] { "TenantId", "ClaimId" });

        migrationBuilder.CreateIndex(
            name: "IX_return_decisions_TenantId_IdempotencyKey",
            schema: "sales",
            table: "return_decisions",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_return_evidence_FileAssetId",
            schema: "sales",
            table: "return_evidence",
            column: "FileAssetId");

        migrationBuilder.CreateIndex(
            name: "IX_return_evidence_TenantId_ClaimId",
            schema: "sales",
            table: "return_evidence",
            columns: new[] { "TenantId", "ClaimId" });

        migrationBuilder.CreateIndex(
            name: "IX_return_evidence_TenantId_DecisionId_FileAssetId",
            schema: "sales",
            table: "return_evidence",
            columns: new[] { "TenantId", "DecisionId", "FileAssetId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_return_lines_TenantId_ClaimId_ExternalLineId",
            schema: "sales",
            table: "return_lines",
            columns: new[] { "TenantId", "ClaimId", "ExternalLineId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_return_lines_TenantId_OrderLineId",
            schema: "sales",
            table: "return_lines",
            columns: new[] { "TenantId", "OrderLineId" });

        migrationBuilder.CreateIndex(
            name: "IX_return_stock_dispositions_TenantId_ClaimId",
            schema: "sales",
            table: "return_stock_dispositions",
            columns: new[] { "TenantId", "ClaimId" });

        migrationBuilder.CreateIndex(
            name: "IX_return_stock_dispositions_TenantId_IdempotencyKey",
            schema: "sales",
            table: "return_stock_dispositions",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_return_stock_dispositions_TenantId_InventoryItemId",
            schema: "sales",
            table: "return_stock_dispositions",
            columns: new[] { "TenantId", "InventoryItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_return_stock_dispositions_TenantId_ReturnLineId",
            schema: "sales",
            table: "return_stock_dispositions",
            columns: new[] { "TenantId", "ReturnLineId" });

        migrationBuilder.CreateIndex(
            name: "IX_shipment_document_attempts_TenantId_DocumentId",
            schema: "sales",
            table: "shipment_document_attempts",
            columns: new[] { "TenantId", "DocumentId" });

        migrationBuilder.CreateIndex(
            name: "IX_shipment_document_attempts_TenantId_IdempotencyKey",
            schema: "sales",
            table: "shipment_document_attempts",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_shipment_document_attempts_TenantId_PackageId",
            schema: "sales",
            table: "shipment_document_attempts",
            columns: new[] { "TenantId", "PackageId" });

        migrationBuilder.CreateIndex(
            name: "IX_shipment_documents_FileAssetId",
            schema: "sales",
            table: "shipment_documents",
            column: "FileAssetId");

        migrationBuilder.CreateIndex(
            name: "IX_shipment_documents_TenantId_PackageId_DocumentKind_Document~",
            schema: "sales",
            table: "shipment_documents",
            columns: new[] { "TenantId", "PackageId", "DocumentKind", "DocumentVersion" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_shipment_packages_TenantId_ConnectionId_ExternalPackageId",
            schema: "sales",
            table: "shipment_packages",
            columns: new[] { "TenantId", "ConnectionId", "ExternalPackageId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_shipment_packages_TenantId_OrderId",
            schema: "sales",
            table: "shipment_packages",
            columns: new[] { "TenantId", "OrderId" });

        migrationBuilder.CreateIndex(
            name: "IX_shipment_packages_TenantId_Status_StatusOccurredAt",
            schema: "sales",
            table: "shipment_packages",
            columns: new[] { "TenantId", "Status", "StatusOccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_sync_cursors_TenantId_ConnectionId_ResourceType",
            schema: "integration",
            table: "sync_cursors",
            columns: new[] { "TenantId", "ConnectionId", "ResourceType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_webhook_subscriptions_TenantId_ConnectionId_Status",
            schema: "integration",
            table: "webhook_subscriptions",
            columns: new[] { "TenantId", "ConnectionId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_webhook_subscriptions_TenantId_RouteTokenHash",
            schema: "integration",
            table: "webhook_subscriptions",
            columns: new[] { "TenantId", "RouteTokenHash" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "cargo_provider_mappings",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "connection_sync_policies",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "order_financial_allocations",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "order_status_history",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "package_line_allocations",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "platform_capabilities",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "platform_credentials",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "reconciliation_differences",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "return_evidence",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "return_stock_dispositions",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "shipment_document_attempts",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "sync_cursors",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "webhook_subscriptions",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "reconciliation_runs",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "return_decisions",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "return_lines",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "shipment_documents",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "order_lines",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "return_claims",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "shipment_packages",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "orders",
            schema: "sales");
    }
}
