using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                schema: "catalog",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_models",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    ModelCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ModelCodeKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TitleColor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    VatRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    VatInclusion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    WarrantyMonths = table.Column<int>(type: "integer", nullable: true),
                    DeliveryDays = table.Column<int>(type: "integer", nullable: true),
                    FieldsSavedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_models", x => x.Id);
                    table.UniqueConstraint("AK_product_models_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_product_models_brands_TenantId_BrandId",
                        columns: x => new { x.TenantId, x.BrandId },
                        principalSchema: "catalog",
                        principalTable: "brands",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_product_models_categories_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_product_models_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_products_FamilyId",
                schema: "catalog",
                table: "products",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_product_models_TenantId_BrandId_ModelCodeKey",
                schema: "catalog",
                table: "product_models",
                columns: new[] { "TenantId", "BrandId", "ModelCodeKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_product_models_TenantId_CategoryId",
                schema: "catalog",
                table: "product_models",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_models_TenantId_ModelCodeKey",
                schema: "catalog",
                table: "product_models",
                columns: new[] { "TenantId", "ModelCodeKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_products_product_models_FamilyId",
                schema: "catalog",
                table: "products",
                column: "FamilyId",
                principalSchema: "catalog",
                principalTable: "product_models",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Backfill (MODEL-KAYDI-PLANI Faz 1). Only new rows and products."FamilyId" are written; no other column of
            // any card or variant changes. Safe to run again: models are opened only where missing, only unlinked cards
            // are linked, an issue only where none is.
            foreach (var sql in BackfillSql) migrationBuilder.Sql(sql);
        }

        // Every card's model code (its first variant's, by sort order and id, that is not blank) with the shared catalog key
        // (CatalogKeyNormalizer.Key; ProductModelLinks.SqlKey is the same expression). A code with letters other than ASCII
        // and Turkish ones is left out: SQL cannot fold it the way the application does, and the card stays unlinked until
        // the application links it. ord: the card's place in its group, oldest first (the editor's ModelValues tie rule).
        private const string CardsCte = """
            WITH first_code AS (
                SELECT DISTINCT ON (v."ProductId") v."ProductId", btrim(v."ModelCode") AS code
                FROM catalog.product_variants v
                WHERE v."ModelCode" IS NOT NULL AND v."ModelCode" ~ '[^[:space:]]'
                ORDER BY v."ProductId", v."SortOrder", v."Id"
            ), keyed AS (
                SELECT f."ProductId", f.code,
                       left(btrim(regexp_replace(translate(f.code, 'abcdefghijklmnopqrstuvwxyzçÇğĞıİöÖşŞüÜ', 'ABCDEFGHIJKLMNOPQRSTUVWXYZCCGGIIOOSSUU'), '[^ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789]+', '-', 'g'), '-'), 160) AS key
                FROM first_code f
                WHERE octet_length(translate(f.code, 'çÇğĞıİöÖşŞüÜ', 'cCgGiIoOsSuU')) = char_length(f.code)
            ), cards AS (
                SELECT p."TenantId", p."Id" AS "ProductId", p."BrandId", p."CategoryId", p."VatRate", p."VatInclusion", p."WarrantyMonths", p."DeliveryDays",
                       btrim(p."Title") AS title, btrim(p."Description") AS description, k.code, k.key,
                       (SELECT btrim(substring(c."OptionSignature" FROM '(?:^|\|)\s*(?:[Rr][Ee][Nn][Kk]|[Cc][Oo][Ll][Oo][Uu]?[Rr])\s*[:=]\s*([^|]+)'))
                        FROM catalog.product_variants c
                        WHERE c."ProductId" = p."Id" AND c."OptionSignature" ~ '(?:^|\|)\s*(?:[Rr][Ee][Nn][Kk]|[Cc][Oo][Ll][Oo][Uu]?[Rr])\s*[:=]'
                        ORDER BY c."SortOrder", c."Id" LIMIT 1) AS color,
                       row_number() OVER (PARTITION BY p."TenantId", k.key ORDER BY p."CreatedAt", p."Id") AS ord
                FROM catalog.products p JOIN keyed k ON k."ProductId" = p."Id"
                WHERE k.key <> ''
            )
            """;

        // The backfill statements, in order (models, links, issues); PostgreSqlProductModelLinksTests runs them twice.
        internal static readonly string[] BackfillSql =
        [
            CardsCte + """
                , single AS (
                    SELECT "TenantId", key FROM cards GROUP BY "TenantId", key
                    HAVING count(DISTINCT coalesce("BrandId"::text, '-')) = 1)
                INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "TitleColor", "Description", "CategoryId", "VatRate", "VatInclusion", "WarrantyMonths", "DeliveryDays", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                SELECT gen_random_uuid(), s."TenantId", o."BrandId", left(o.code, 160), s.key,
                       left(t.title, 320),
                       CASE WHEN t.color IS NOT NULL AND position(lower(t.color) IN lower(t.title)) > 0 THEN left(t.color, 120) END,
                       d.description, c."CategoryId", vat."VatRate", vat."VatInclusion", w."WarrantyMonths", dd."DeliveryDays",
                       NULL, now(), now(), 1
                FROM single s
                CROSS JOIN LATERAL (SELECT x."BrandId", x.code FROM cards x WHERE x."TenantId" = s."TenantId" AND x.key = s.key ORDER BY x.ord LIMIT 1) o
                CROSS JOIN LATERAL (SELECT x.title, (array_agg(x.color ORDER BY x.ord))[1] AS color FROM cards x WHERE x."TenantId" = s."TenantId" AND x.key = s.key GROUP BY x.title ORDER BY count(*) DESC, min(x.ord) LIMIT 1) t
                CROSS JOIN LATERAL (SELECT x.description FROM cards x WHERE x."TenantId" = s."TenantId" AND x.key = s.key GROUP BY x.description ORDER BY count(*) DESC, min(x.ord) LIMIT 1) d
                CROSS JOIN LATERAL (SELECT x."CategoryId" FROM cards x WHERE x."TenantId" = s."TenantId" AND x.key = s.key GROUP BY x."CategoryId" ORDER BY count(*) DESC, min(x.ord) LIMIT 1) c
                CROSS JOIN LATERAL (SELECT x."VatRate", (array_agg(x."VatInclusion" ORDER BY x.ord))[1] AS "VatInclusion" FROM cards x WHERE x."TenantId" = s."TenantId" AND x.key = s.key GROUP BY x."VatRate", upper(x."VatInclusion") ORDER BY count(*) DESC, min(x.ord) LIMIT 1) vat
                CROSS JOIN LATERAL (SELECT x."WarrantyMonths" FROM cards x WHERE x."TenantId" = s."TenantId" AND x.key = s.key GROUP BY x."WarrantyMonths" ORDER BY count(*) DESC, min(x.ord) LIMIT 1) w
                CROSS JOIN LATERAL (SELECT x."DeliveryDays" FROM cards x WHERE x."TenantId" = s."TenantId" AND x.key = s.key GROUP BY x."DeliveryDays" ORDER BY count(*) DESC, min(x.ord) LIMIT 1) dd
                WHERE NOT EXISTS (SELECT 1 FROM catalog.product_models m WHERE m."TenantId" = s."TenantId" AND m."BrandId" IS NOT DISTINCT FROM o."BrandId" AND m."ModelCodeKey" = s.key)
                ON CONFLICT DO NOTHING;
                """,
            CardsCte + """
                , single AS (
                    SELECT "TenantId", key FROM cards GROUP BY "TenantId", key
                    HAVING count(DISTINCT coalesce("BrandId"::text, '-')) = 1)
                UPDATE catalog.products p SET "FamilyId" = m."Id"
                FROM cards x
                JOIN single s ON s."TenantId" = x."TenantId" AND s.key = x.key
                JOIN catalog.product_models m ON m."TenantId" = x."TenantId" AND m."ModelCodeKey" = x.key AND m."BrandId" IS NOT DISTINCT FROM x."BrandId"
                WHERE p."Id" = x."ProductId" AND p."FamilyId" IS NULL;
                """,
            CardsCte + """
                , multi AS (
                    SELECT x."TenantId", x.key, (array_agg(x.code ORDER BY x.ord))[1] AS code, count(*) AS card_count,
                           count(DISTINCT coalesce(x."BrandId"::text, '-')) AS brand_count,
                           string_agg(DISTINCT coalesce(b."Name", 'markasız'), ', ') AS brand_names
                    FROM cards x LEFT JOIN catalog.brands b ON b."TenantId" = x."TenantId" AND b."Id" = x."BrandId"
                    GROUP BY x."TenantId", x.key
                    HAVING count(DISTINCT coalesce(x."BrandId"::text, '-')) > 1)
                INSERT INTO ops.operational_issues ("Id", "TenantId", "DedupeKey", "Code", "Summary", "Status", "FirstSeenAt", "LastSeenAt", "OccurrenceCount", "ResolvedAt")
                SELECT gen_random_uuid(), m."TenantId", 'product-model-ambiguous:' || m.key, 'PRODUCT_MODEL_AMBIGUOUS',
                       left('Model kodu ' || m.code || ' ' || m.brand_count || ' ayrı markada kullanılıyor (' || m.brand_names || ', ' || m.card_count || ' kart): kartlar bir modele bağlanmadı, hiçbir şey birleştirilmedi; hangi modele ait olduklarına siz karar verin.', 1000),
                       'Open', now(), now(), 1, NULL
                FROM multi m
                ON CONFLICT ("TenantId", "DedupeKey") DO NOTHING;
                """,
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing is left to resolve the model reports once the models are gone.
            migrationBuilder.Sql("""DELETE FROM ops.operational_issues WHERE "Code" = 'PRODUCT_MODEL_AMBIGUOUS' AND "DedupeKey" LIKE 'product-model-ambiguous:%';""");
            migrationBuilder.DropForeignKey(
                name: "FK_products_product_models_FamilyId",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropTable(
                name: "product_models",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_products_FamilyId",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                schema: "catalog",
                table: "products");
        }
    }
}
