using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration(MigrationId)]
public sealed class FillVariantOptionRowsFromSignature : Migration
{
    internal const string MigrationId = "20261105140000_FillVariantOptionRowsFromSignature";

    // A variant's colour and size live in its option value rows (catalog.variant_option_values); its option signature
    // ("Renk: Mavi | Beden: XL") is the same choice as text. Live on 5 Nov 2026 102 variants had the signature but no
    // option value row: 99 written on 26 Oct (39 colour cards, MZ021Y-CD to MZ054YNC) and MZ021Y-CD-25..27 written by
    // an editor save on 1 Nov. The editor showed their Renk and Beden, but the publication read no Renk, so Trendyol's Web
    // Color (taken from Renk) was missing and MZ021Y-CD-26 was refused.
    //
    // Each such variant gets the rows its signature names, under its own card's option and value of that label; every
    // one of the 102 had both on its card. A variant is filled only when every "Label: Value" of its signature names
    // exactly one option of its card and one value of that option; anything else (a value the card lacks, a variant
    // already holding some row) stays as it is. Nothing is deleted, no option or value is added, and the signature,
    // prices, stock and marketplace links are untouched (the choice itself does not change). Each filled variant is
    // written to the audit log. A second run finds no variant to fill.
    internal const string Fill = """
        DO $$
        BEGIN
            DROP TABLE IF EXISTS pg_temp.signature_option_fills;
            CREATE TEMP TABLE signature_option_fills ON COMMIT DROP AS
            WITH missing AS (
                SELECT v."Id" AS variant_id, v."TenantId" AS tenant_id, v."ProductId" AS product_id, v."OptionSignature" AS signature
                FROM catalog.product_variants v
                WHERE v."OptionSignature" LIKE '%:%'
                  AND NOT EXISTS (SELECT 1 FROM catalog.variant_option_values vo
                                  WHERE vo."TenantId" = v."TenantId" AND vo."VariantId" = v."Id")
            ), pairs AS (
                SELECT m.variant_id, m.tenant_id, m.product_id, m.signature,
                       trim(split_part(part, ':', 1)) AS label, trim(substr(part, strpos(part, ':') + 1)) AS value
                FROM missing m
                CROSS JOIN LATERAL unnest(string_to_array(m.signature, ' | ')) AS part
                WHERE strpos(part, ':') > 1
            ), matched AS (
                SELECT p.variant_id, p.tenant_id, p.product_id, p.signature, p.label, o."Id" AS option_id, ov."Id" AS value_id
                FROM pairs p
                JOIN catalog.product_options o ON o."TenantId" = p.tenant_id AND o."ProductId" = p.product_id AND lower(o."Label") = lower(p.label)
                JOIN catalog.product_option_values ov ON ov."TenantId" = p.tenant_id AND ov."OptionId" = o."Id" AND lower(ov."Label") = lower(p.value)
            ), complete AS (
                SELECT p.variant_id
                FROM pairs p
                GROUP BY p.variant_id
                HAVING count(*) = (SELECT count(*) FROM matched x WHERE x.variant_id = p.variant_id)
                   AND count(*) = (SELECT count(DISTINCT x.option_id) FROM matched x WHERE x.variant_id = p.variant_id)
                   AND count(*) = count(DISTINCT lower(p.label))
            )
            SELECT m.variant_id, m.tenant_id, m.product_id, m.signature, m.option_id, m.value_id
            FROM matched m
            JOIN complete c ON c.variant_id = m.variant_id;

            INSERT INTO catalog.variant_option_values ("Id", "TenantId", "VariantId", "OptionId", "OptionValueId")
            SELECT uuidv7(), f.tenant_id, f.variant_id, f.option_id, f.value_id
            FROM signature_option_fills f
            WHERE NOT EXISTS (SELECT 1 FROM catalog.variant_option_values y
                              WHERE y."TenantId" = f.tenant_id AND y."VariantId" = f.variant_id AND y."OptionId" = f.option_id);

            INSERT INTO ops.audit_logs ("TenantId", "ActorUserId", "Action", "TargetType", "TargetId", "Reason", "CorrelationId", "CreatedAt")
            SELECT DISTINCT f.tenant_id, NULL::uuid, 'VARIANT_OPTIONS_FILLED_FROM_SIGNATURE', 'ProductVariant', f.variant_id::text,
                   'Seçenek satırları imzadan tamamlandı: ' || f.signature || ' (kart ' || f.product_id::text || ')',
                   'migration:20261105140000_FillVariantOptionRowsFromSignature', now()
            FROM signature_option_fills f;

            TRUNCATE signature_option_fills;
        END
        $$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Fill);

    // Not reversed: the rows say what the signature already said. ops.audit_logs (CorrelationId
    // 'migration:20261105140000_FillVariantOptionRowsFromSignature') lists each filled variant.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
