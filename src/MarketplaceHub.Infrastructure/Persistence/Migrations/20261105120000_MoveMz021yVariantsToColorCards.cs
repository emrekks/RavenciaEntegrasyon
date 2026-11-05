using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration(MigrationId)]
public sealed class MoveMz021yVariantsToColorCards : Migration
{
    internal const string MigrationId = "20261105120000_MoveMz021yVariantsToColorCards";

    // On 1 Nov 2026 21:25Z a save of the MZ021Y-CD Mavi card (01a178e0-9ecb-…) put 21 new rows on that card, before the
    // editor placed a new row on the family card of its own colour (ProductFamilyVariantPlacement). 18 of them are other
    // colours: Bordo, Haki, Kahverengi, Kiremit and Siyah L/XL/2XL and Mürdüm 3XL/4XL/5XL. Here they move to their colour's
    // card; nothing is deleted. Live on 5 Nov none of them had a marketplace link, a listing state, a listing variant, an
    // order line or a picture; each has its channel offer, MAIN stock row and (15 of them) a MANUAL stock movement, which
    // hang on the variant and stay as they are, as do stock, prices, barcodes and SKUs.
    //
    // A row moves only when it is still exactly the row found live: its id, SKU, model code, "Renk: … | Beden: …"
    // signature and the Mavi card, with no marketplace link, listing state, listing variant, order line or picture, and
    // the target is a card of the same tenant, on the row's status, that already carries a row of the same model code and
    // colour but none of the same colour and size. The rows are locked before the link/order checks. Anywhere else
    // (CI, another tenant, a row moved, deleted or linked meanwhile) nothing changes, and a second run finds no row.
    //
    // The move does what the editor's save does for a new row on a colour card: the variant row takes the card (version
    // +1); its variant-scoped attribute rows, change rows, field provenance and import candidates follow; its option values
    // are kept in the target card's own Renk/Beden options (PersistVariantOptionsAsync: the option and value are found by
    // key or added); both cards take a new version and their marketplace links are marked as local product changes
    // (CatalogLocalEdits.MarkLinksDirtyAsync). Each move is written to the audit log.
    internal const string Move = """
        DO $$
        BEGIN
            DROP TABLE IF EXISTS pg_temp.mz021y_card_moves;
            CREATE TEMP TABLE mz021y_card_moves ON COMMIT DROP AS
            SELECT v."TenantId" AS tenant_id, m.variant_id, m.sku, m.from_product, m.to_product,
                   m.colour, m.colour_key, m.size, m.size_key, m.size_order
            FROM (VALUES
                ('01a19900-aa47-7fbc-9b55-5f3fb7fe83ff'::uuid, 'MZ021Y-CD-01', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-9186-7325-964c-02c23b7bfc32'::uuid, 'Bordo', 'BORDO', 'L', 'L', 0),
                ('01a19900-aa48-7c16-8e6c-5066a4408d27'::uuid, 'MZ021Y-CD-02', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-9186-7325-964c-02c23b7bfc32'::uuid, 'Bordo', 'BORDO', 'XL', 'XL', 1),
                ('01a19900-aa48-744b-a6a1-e454e2e14bae'::uuid, 'MZ021Y-CD-03', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-9186-7325-964c-02c23b7bfc32'::uuid, 'Bordo', 'BORDO', '2XL', '2XL', 2),
                ('01a19900-aa48-77b3-bf2b-5f1ac4ff5941'::uuid, 'MZ021Y-CD-07', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-a37a-7b28-b55e-7e2fb6ab847d'::uuid, 'Haki', 'HAKI', 'L', 'L', 0),
                ('01a19900-aa48-78c4-9471-295351cc775f'::uuid, 'MZ021Y-CD-08', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-a37a-7b28-b55e-7e2fb6ab847d'::uuid, 'Haki', 'HAKI', 'XL', 'XL', 1),
                ('01a19900-aa48-7fe2-8518-500b9fee7acf'::uuid, 'MZ021Y-CD-09', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-a37a-7b28-b55e-7e2fb6ab847d'::uuid, 'Haki', 'HAKI', '2XL', '2XL', 2),
                ('01a19900-aa48-7140-9ccd-facf91f09f23'::uuid, 'MZ021Y-CD-13', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-8d6f-775d-b33f-0715ef263c6a'::uuid, 'Kahverengi', 'KAHVERENGI', 'L', 'L', 0),
                ('01a19900-aa48-7488-88e4-bc9b70b1323e'::uuid, 'MZ021Y-CD-14', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-8d6f-775d-b33f-0715ef263c6a'::uuid, 'Kahverengi', 'KAHVERENGI', 'XL', 'XL', 1),
                ('01a19900-aa48-783e-b909-0b54d8aaeb4d'::uuid, 'MZ021Y-CD-15', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-8d6f-775d-b33f-0715ef263c6a'::uuid, 'Kahverengi', 'KAHVERENGI', '2XL', '2XL', 2),
                ('01a19900-aa48-7a9b-8772-55e63456c0ba'::uuid, 'MZ021Y-CD-19', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-9a59-7878-92b5-f8cdb7225a9c'::uuid, 'Kiremit', 'KIREMIT', 'L', 'L', 0),
                ('01a19900-aa48-73be-9720-484c51aa8a55'::uuid, 'MZ021Y-CD-20', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-9a59-7878-92b5-f8cdb7225a9c'::uuid, 'Kiremit', 'KIREMIT', 'XL', 'XL', 1),
                ('01a19900-aa48-7b56-a146-f3d230311b78'::uuid, 'MZ021Y-CD-21', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-9a59-7878-92b5-f8cdb7225a9c'::uuid, 'Kiremit', 'KIREMIT', '2XL', '2XL', 2),
                ('01a19900-aa48-7eab-807c-b9f1b3cba9bc'::uuid, 'MZ021Y-CD-34', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a198bc-37ca-7c58-903f-7ca1f48682fd'::uuid, 'Mürdüm', 'MURDUM', '3XL', '3XL', 0),
                ('01a19900-aa48-76f9-ad38-0640b2546a78'::uuid, 'MZ021Y-CD-35', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a198bc-37ca-7c58-903f-7ca1f48682fd'::uuid, 'Mürdüm', 'MURDUM', '4XL', '4XL', 1),
                ('01a19900-aa48-70b0-953b-3b924ffa277e'::uuid, 'MZ021Y-CD-36', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a198bc-37ca-7c58-903f-7ca1f48682fd'::uuid, 'Mürdüm', 'MURDUM', '5XL', '5XL', 2),
                ('01a19900-aa48-713c-baa9-2c432948b8cf'::uuid, 'MZ021Y-CD-37', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-95ef-7dec-a4e3-5e7fca8d8f38'::uuid, 'Siyah', 'SIYAH', 'L', 'L', 0),
                ('01a19900-aa48-77f0-b282-fabd9cce73ac'::uuid, 'MZ021Y-CD-38', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-95ef-7dec-a4e3-5e7fca8d8f38'::uuid, 'Siyah', 'SIYAH', 'XL', 'XL', 1),
                ('01a19900-aa48-7b5e-a2bf-a9d35ffa7fa6'::uuid, 'MZ021Y-CD-39', '01a178e0-9ecb-790a-bbf7-30da96781869'::uuid, '01a178e0-95ef-7dec-a4e3-5e7fca8d8f38'::uuid, 'Siyah', 'SIYAH', '2XL', '2XL', 2)
            ) AS m(variant_id, sku, from_product, to_product, colour, colour_key, size, size_key, size_order)
            JOIN catalog.product_variants v
              ON v."Id" = m.variant_id AND v."Sku" = m.sku AND v."ProductId" = m.from_product AND v."ModelCode" = 'MZ021Y-CD'
             AND v."OptionSignature" = 'Renk: ' || m.colour || ' | Beden: ' || m.size
            JOIN catalog.products target ON target."TenantId" = v."TenantId" AND target."Id" = m.to_product AND target."Status" = v."Status"
            WHERE EXISTS (SELECT 1 FROM catalog.product_variants s
                          WHERE s."TenantId" = v."TenantId" AND s."ProductId" = m.to_product AND s."ModelCode" = v."ModelCode"
                            AND s."OptionSignature" LIKE 'Renk: ' || m.colour || ' |%')
              AND NOT EXISTS (SELECT 1 FROM catalog.product_variants d
                              WHERE d."TenantId" = v."TenantId" AND d."ProductId" = m.to_product AND d."OptionSignature" = v."OptionSignature");

            IF NOT EXISTS (SELECT 1 FROM mz021y_card_moves) THEN
                RETURN;
            END IF;

            -- The rows are locked before the marketplace and order checks, so a link or order line written by the running
            -- API or worker meanwhile either waits for the move or is seen here and keeps its row where it is.
            PERFORM 1 FROM catalog.product_variants v JOIN mz021y_card_moves m ON v."Id" = m.variant_id ORDER BY v."Id" FOR UPDATE OF v;
            DELETE FROM mz021y_card_moves m
            WHERE NOT EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v."TenantId" = m.tenant_id AND v."Id" = m.variant_id AND v."ProductId" = m.from_product)
               OR EXISTS (SELECT 1 FROM catalog.marketplace_variant_links x WHERE x."TenantId" = m.tenant_id AND x."VariantId" = m.variant_id)
               OR EXISTS (SELECT 1 FROM catalog.marketplace_listing_states x WHERE x."TenantId" = m.tenant_id AND x."VariantId" = m.variant_id)
               OR EXISTS (SELECT 1 FROM catalog.channel_listing_variants x WHERE x."TenantId" = m.tenant_id AND x."VariantId" = m.variant_id)
               OR EXISTS (SELECT 1 FROM sales.order_lines x WHERE x."TenantId" = m.tenant_id AND x."VariantId" = m.variant_id)
               OR EXISTS (SELECT 1 FROM catalog.product_media x WHERE x."TenantId" = m.tenant_id AND x."VariantId" = m.variant_id);

            IF NOT EXISTS (SELECT 1 FROM mz021y_card_moves) THEN
                RETURN;
            END IF;

            -- The target card's Renk and Beden options (the target cards carry both already; added only if missing).
            INSERT INTO catalog.product_options ("Id", "TenantId", "ProductId", "Label", "NormalizedKey", "SortOrder")
            SELECT uuidv7(), c.tenant_id, c.to_product, k.label, k.option_key, k.sort_order
            FROM (SELECT DISTINCT tenant_id, to_product FROM mz021y_card_moves) c
            CROSS JOIN (VALUES ('Beden', 'BEDEN', 0), ('Renk', 'RENK', 1)) AS k(label, option_key, sort_order)
            WHERE NOT EXISTS (SELECT 1 FROM catalog.product_options o
                              WHERE o."TenantId" = c.tenant_id AND o."ProductId" = c.to_product AND o."NormalizedKey" = k.option_key);

            DROP TABLE IF EXISTS pg_temp.mz021y_option_rows;
            CREATE TEMP TABLE mz021y_option_rows ON COMMIT DROP AS
            SELECT m.tenant_id, m.variant_id, m.from_product, m.to_product, r.option_key, r.label, r.value_key, r.sort_order
            FROM mz021y_card_moves m
            CROSS JOIN LATERAL (VALUES ('RENK', m.colour, m.colour_key, 0), ('BEDEN', m.size, m.size_key, m.size_order)) AS r(option_key, label, value_key, sort_order);

            -- The value in the target card's option (a stored legacy upper-case key counts as the same value).
            INSERT INTO catalog.product_option_values ("Id", "TenantId", "OptionId", "Label", "NormalizedKey", "SortOrder")
            SELECT uuidv7(), n.tenant_id, n.option_id, n.label, n.value_key, n.sort_order
            FROM (SELECT DISTINCT ON (o."Id", r.value_key) r.tenant_id, o."Id" AS option_id, r.label, r.value_key, r.sort_order
                  FROM mz021y_option_rows r
                  JOIN catalog.product_options o ON o."TenantId" = r.tenant_id AND o."ProductId" = r.to_product AND o."NormalizedKey" = r.option_key
                  ORDER BY o."Id", r.value_key, r.sort_order) n
            WHERE NOT EXISTS (SELECT 1 FROM catalog.product_option_values ov
                              WHERE ov."TenantId" = n.tenant_id AND ov."OptionId" = n.option_id AND ov."NormalizedKey" IN (n.value_key, upper(n.label)));

            DROP TABLE IF EXISTS pg_temp.mz021y_option_targets;
            CREATE TEMP TABLE mz021y_option_targets ON COMMIT DROP AS
            SELECT r.tenant_id, r.variant_id, r.from_product, r.option_key, o."Id" AS option_id, ov.value_id
            FROM mz021y_option_rows r
            JOIN catalog.product_options o ON o."TenantId" = r.tenant_id AND o."ProductId" = r.to_product AND o."NormalizedKey" = r.option_key
            CROSS JOIN LATERAL (SELECT x."Id" AS value_id FROM catalog.product_option_values x
                                WHERE x."TenantId" = r.tenant_id AND x."OptionId" = o."Id" AND x."NormalizedKey" IN (r.value_key, upper(r.label))
                                ORDER BY (x."NormalizedKey" = r.value_key) DESC, x."Id" LIMIT 1) ov;

            -- A row the variant already has under the same option of its old card moves to the target card's option;
            -- a missing one is added.
            UPDATE catalog.variant_option_values vov
            SET "OptionId" = t.option_id, "OptionValueId" = t.value_id
            FROM mz021y_option_targets t, catalog.product_options old
            WHERE vov."TenantId" = t.tenant_id AND vov."VariantId" = t.variant_id
              AND old."TenantId" = t.tenant_id AND old."Id" = vov."OptionId" AND old."ProductId" = t.from_product AND old."NormalizedKey" = t.option_key
              AND NOT EXISTS (SELECT 1 FROM catalog.variant_option_values y
                              WHERE y."TenantId" = t.tenant_id AND y."VariantId" = t.variant_id AND y."OptionId" = t.option_id);
            INSERT INTO catalog.variant_option_values ("Id", "TenantId", "VariantId", "OptionId", "OptionValueId")
            SELECT uuidv7(), t.tenant_id, t.variant_id, t.option_id, t.value_id
            FROM mz021y_option_targets t
            WHERE NOT EXISTS (SELECT 1 FROM catalog.variant_option_values y
                              WHERE y."TenantId" = t.tenant_id AND y."VariantId" = t.variant_id AND y."OptionId" = t.option_id);

            UPDATE catalog.product_variants v
            SET "ProductId" = m.to_product, "Version" = v."Version" + 1, "UpdatedAt" = now()
            FROM mz021y_card_moves m
            WHERE v."TenantId" = m.tenant_id AND v."Id" = m.variant_id AND v."ProductId" = m.from_product;

            UPDATE catalog.product_attribute_assignments a
            SET "ProductId" = m.to_product, "Version" = a."Version" + 1
            FROM mz021y_card_moves m
            WHERE a."TenantId" = m.tenant_id AND a."VariantId" = m.variant_id AND a."ProductId" = m.from_product;

            UPDATE catalog.product_change_entries c
            SET "ProductId" = m.to_product
            FROM mz021y_card_moves m
            WHERE c."TenantId" = m.tenant_id AND c."VariantId" = m.variant_id AND c."ProductId" = m.from_product;

            UPDATE catalog.field_provenance f
            SET "ProductId" = m.to_product
            FROM mz021y_card_moves m
            WHERE f."TenantId" = m.tenant_id AND f."VariantId" = m.variant_id AND f."ProductId" = m.from_product;

            UPDATE catalog.import_match_candidates i
            SET "ProductId" = m.to_product
            FROM mz021y_card_moves m
            WHERE i."TenantId" = m.tenant_id AND i."VariantId" = m.variant_id AND i."ProductId" = m.from_product;

            UPDATE catalog.import_decisions d
            SET "LinkProductId" = m.to_product
            FROM mz021y_card_moves m
            WHERE d."TenantId" = m.tenant_id AND d."LinkVariantId" = m.variant_id AND d."LinkProductId" = m.from_product;

            UPDATE catalog.products p
            SET "Version" = p."Version" + 1, "UpdatedAt" = now()
            FROM (SELECT tenant_id, from_product AS product_id FROM mz021y_card_moves
                  UNION SELECT tenant_id, to_product FROM mz021y_card_moves) cards
            WHERE p."TenantId" = cards.tenant_id AND p."Id" = cards.product_id;

            UPDATE catalog.marketplace_product_links l
            SET "SyncStatus" = 'LOCAL_CHANGES_PENDING',
                "DirtyFieldsJson" = CASE
                    WHEN jsonb_typeof(l."DirtyFieldsJson") = 'array' AND l."DirtyFieldsJson" @> '["product"]'::jsonb THEN l."DirtyFieldsJson"
                    WHEN jsonb_typeof(l."DirtyFieldsJson") = 'array' THEN l."DirtyFieldsJson" || '["product"]'::jsonb
                    ELSE '["product"]'::jsonb
                END,
                "Version" = l."Version" + 1
            FROM (SELECT tenant_id, from_product AS product_id FROM mz021y_card_moves
                  UNION SELECT tenant_id, to_product FROM mz021y_card_moves) cards
            WHERE l."TenantId" = cards.tenant_id AND l."ProductId" = cards.product_id
              AND NOT (l."SyncStatus" = 'LOCAL_CHANGES_PENDING'
                       AND COALESCE(jsonb_typeof(l."DirtyFieldsJson") = 'array' AND l."DirtyFieldsJson" @> '["product"]'::jsonb, false));

            INSERT INTO ops.audit_logs ("TenantId", "ActorUserId", "Action", "TargetType", "TargetId", "Reason", "CorrelationId", "CreatedAt")
            SELECT m.tenant_id, NULL, 'VARIANT_MOVED_TO_COLOR_CARD', 'ProductVariant', m.variant_id::text,
                   m.sku || ' (' || m.colour || ' ' || m.size || ') Mavi kartından ' || m.colour || ' kartına taşındı: ' || m.from_product::text || ' -> ' || m.to_product::text,
                   'migration:20261105120000_MoveMz021yVariantsToColorCards', now()
            FROM mz021y_card_moves m;
        END
        $$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Move);

    // Not reversed: moving the rows back onto the wrong card is not wanted. Should it ever be needed, ops.audit_logs
    // (CorrelationId 'migration:20261105120000_MoveMz021yVariantsToColorCards') keeps each row's old and new card.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
