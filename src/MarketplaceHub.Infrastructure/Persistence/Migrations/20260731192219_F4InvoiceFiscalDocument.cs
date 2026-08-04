using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class F4InvoiceFiscalDocument : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "billing");

        migrationBuilder.AddUniqueConstraint(
            name: "AK_file_assets_TenantId_Id",
            schema: "ops",
            table: "file_assets",
            columns: new[] { "TenantId", "Id" });

        migrationBuilder.CreateTable(
            name: "invoice_policies",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                TriggerState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PackageScope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DueRule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RoundingRule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AdjustmentRule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AutoSubmit = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_invoice_policies", x => x.Id);
                table.UniqueConstraint("AK_invoice_policies_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_invoice_policies_platform_connections_TenantId_ProviderConn~",
                    columns: x => new { x.TenantId, x.ProviderConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "legal_entity_profiles",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ProtectedTaxId = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                MaskedTaxId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AddressSnapshotJson = table.Column<string>(type: "text", nullable: false),
                ContactSnapshotJson = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_legal_entity_profiles", x => x.Id);
                table.UniqueConstraint("AK_legal_entity_profiles_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_legal_entity_profiles_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "invoices",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                ProviderConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                LegalEntityProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoicePolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SequencePurpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SequenceNumber = table.Column<long>(type: "bigint", nullable: true),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                TaxExclusiveTotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                DiscountTotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                TaxTotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                PayableTotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ExternalReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                InvoiceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                EttnUuid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                OriginalInvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                LastErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_invoices", x => x.Id);
                table.UniqueConstraint("AK_invoices_TenantId_Id", x => new { x.TenantId, x.Id });
                table.CheckConstraint("ck_invoice_totals_nonnegative", "\"TaxExclusiveTotal\" >= 0 AND \"DiscountTotal\" >= 0 AND \"TaxTotal\" >= 0 AND \"PayableTotal\" >= 0");
                table.ForeignKey(
                    name: "FK_invoices_invoice_policies_TenantId_InvoicePolicyId",
                    columns: x => new { x.TenantId, x.InvoicePolicyId },
                    principalSchema: "billing",
                    principalTable: "invoice_policies",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_invoices_invoices_TenantId_OriginalInvoiceId",
                    columns: x => new { x.TenantId, x.OriginalInvoiceId },
                    principalSchema: "billing",
                    principalTable: "invoices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_invoices_legal_entity_profiles_TenantId_LegalEntityProfileId",
                    columns: x => new { x.TenantId, x.LegalEntityProfileId },
                    principalSchema: "billing",
                    principalTable: "legal_entity_profiles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_invoices_orders_TenantId_OrderId",
                    columns: x => new { x.TenantId, x.OrderId },
                    principalSchema: "sales",
                    principalTable: "orders",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_invoices_platform_connections_TenantId_ProviderConnectionId",
                    columns: x => new { x.TenantId, x.ProviderConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_invoices_shipment_packages_TenantId_PackageId",
                    columns: x => new { x.TenantId, x.PackageId },
                    principalSchema: "sales",
                    principalTable: "shipment_packages",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "invoice_documents",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                Sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ExternalDocumentId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_invoice_documents", x => x.Id);
                table.ForeignKey(
                    name: "FK_invoice_documents_file_assets_TenantId_FileAssetId",
                    columns: x => new { x.TenantId, x.FileAssetId },
                    principalSchema: "ops",
                    principalTable: "file_assets",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_invoice_documents_invoices_TenantId_InvoiceId",
                    columns: x => new { x.TenantId, x.InvoiceId },
                    principalSchema: "billing",
                    principalTable: "invoices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "invoice_lines",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                LineSequence = table.Column<int>(type: "integer", nullable: false),
                DescriptionSnapshot = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                SkuSnapshot = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                UnitSnapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                UnitPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                DiscountAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                VatRate = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                VatAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                LineTotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_invoice_lines", x => x.Id);
                table.CheckConstraint("ck_invoice_line_values", "\"LineSequence\" > 0 AND \"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND \"DiscountAmount\" >= 0 AND \"VatRate\" >= 0 AND \"VatAmount\" >= 0 AND \"LineTotal\" >= 0");
                table.ForeignKey(
                    name: "FK_invoice_lines_invoices_TenantId_InvoiceId",
                    columns: x => new { x.TenantId, x.InvoiceId },
                    principalSchema: "billing",
                    principalTable: "invoices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_invoice_lines_order_lines_TenantId_OrderLineId",
                    columns: x => new { x.TenantId, x.OrderLineId },
                    principalSchema: "sales",
                    principalTable: "order_lines",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "invoice_party_snapshots",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProtectedContent = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_invoice_party_snapshots", x => x.Id);
                table.ForeignKey(
                    name: "FK_invoice_party_snapshots_invoices_TenantId_InvoiceId",
                    columns: x => new { x.TenantId, x.InvoiceId },
                    principalSchema: "billing",
                    principalTable: "invoices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "invoice_submission_attempts",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                RequestHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ErrorClass = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                RemoteRequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ExternalReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_invoice_submission_attempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_invoice_submission_attempts_invoices_TenantId_InvoiceId",
                    columns: x => new { x.TenantId, x.InvoiceId },
                    principalSchema: "billing",
                    principalTable: "invoices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "marketplace_deliveries",
            schema: "billing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                RequestHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                DeliveryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ExternalReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_marketplace_deliveries", x => x.Id);
                table.ForeignKey(
                    name: "FK_marketplace_deliveries_invoices_TenantId_InvoiceId",
                    columns: x => new { x.TenantId, x.InvoiceId },
                    principalSchema: "billing",
                    principalTable: "invoices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_marketplace_deliveries_platform_connections_TenantId_Connec~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_marketplace_deliveries_shipment_packages_TenantId_PackageId",
                    columns: x => new { x.TenantId, x.PackageId },
                    principalSchema: "sales",
                    principalTable: "shipment_packages",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_invoice_documents_TenantId_FileAssetId",
            schema: "billing",
            table: "invoice_documents",
            columns: new[] { "TenantId", "FileAssetId" });

        migrationBuilder.CreateIndex(
            name: "IX_invoice_documents_TenantId_InvoiceId_DocumentType_Sha256",
            schema: "billing",
            table: "invoice_documents",
            columns: new[] { "TenantId", "InvoiceId", "DocumentType", "Sha256" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_invoice_lines_TenantId_InvoiceId_LineSequence",
            schema: "billing",
            table: "invoice_lines",
            columns: new[] { "TenantId", "InvoiceId", "LineSequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_invoice_lines_TenantId_OrderLineId",
            schema: "billing",
            table: "invoice_lines",
            columns: new[] { "TenantId", "OrderLineId" });

        migrationBuilder.CreateIndex(
            name: "IX_invoice_party_snapshots_TenantId_InvoiceId_Role",
            schema: "billing",
            table: "invoice_party_snapshots",
            columns: new[] { "TenantId", "InvoiceId", "Role" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_invoice_policies_TenantId_ProviderConnectionId",
            schema: "billing",
            table: "invoice_policies",
            columns: new[] { "TenantId", "ProviderConnectionId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_invoice_submission_attempts_TenantId_InvoiceId_AttemptNumber",
            schema: "billing",
            table: "invoice_submission_attempts",
            columns: new[] { "TenantId", "InvoiceId", "AttemptNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_invoices_TenantId_IdempotencyKey",
            schema: "billing",
            table: "invoices",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_invoices_TenantId_InvoicePolicyId",
            schema: "billing",
            table: "invoices",
            columns: new[] { "TenantId", "InvoicePolicyId" });

        migrationBuilder.CreateIndex(
            name: "IX_invoices_TenantId_LegalEntityProfileId",
            schema: "billing",
            table: "invoices",
            columns: new[] { "TenantId", "LegalEntityProfileId" });

        migrationBuilder.CreateIndex(
            name: "IX_invoices_TenantId_OrderId_Status",
            schema: "billing",
            table: "invoices",
            columns: new[] { "TenantId", "OrderId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_invoices_TenantId_OriginalInvoiceId",
            schema: "billing",
            table: "invoices",
            columns: new[] { "TenantId", "OriginalInvoiceId" });

        migrationBuilder.CreateIndex(
            name: "IX_invoices_TenantId_PackageId",
            schema: "billing",
            table: "invoices",
            columns: new[] { "TenantId", "PackageId" });

        migrationBuilder.CreateIndex(
            name: "IX_invoices_TenantId_ProviderConnectionId",
            schema: "billing",
            table: "invoices",
            columns: new[] { "TenantId", "ProviderConnectionId" });

        migrationBuilder.CreateIndex(
            name: "IX_legal_entity_profiles_TenantId_Title_Status",
            schema: "billing",
            table: "legal_entity_profiles",
            columns: new[] { "TenantId", "Title", "Status" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_deliveries_TenantId_ConnectionId",
            schema: "billing",
            table: "marketplace_deliveries",
            columns: new[] { "TenantId", "ConnectionId" });

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_deliveries_TenantId_IdempotencyKey",
            schema: "billing",
            table: "marketplace_deliveries",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_deliveries_TenantId_InvoiceId_AttemptNumber",
            schema: "billing",
            table: "marketplace_deliveries",
            columns: new[] { "TenantId", "InvoiceId", "AttemptNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_deliveries_TenantId_PackageId",
            schema: "billing",
            table: "marketplace_deliveries",
            columns: new[] { "TenantId", "PackageId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "invoice_documents",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "invoice_lines",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "invoice_party_snapshots",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "invoice_submission_attempts",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "marketplace_deliveries",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "invoices",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "invoice_policies",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "legal_entity_profiles",
            schema: "billing");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_file_assets_TenantId_Id",
            schema: "ops",
            table: "file_assets");
    }
}
