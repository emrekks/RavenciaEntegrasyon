namespace MarketplaceHub.Infrastructure.Persistence;

// Every table that keeps rows of one platform connection, and what a full reset does with them
// (OperationalDataMaintenanceService.RunFullResetAsync). Removed: the connection's operational records, issued invoices
// included (30 Oct 2026 owner decision: the invoices stay in E-Faturam, the removed platform's data goes). Kept: the
// connection's settings, which a reset of its data does not touch. A table that gains a connection column must be
// placed in one of the two lists (ConnectionDataInventoryTests).
public static class ConnectionDataInventory
{
    public static readonly IReadOnlyList<string> Removed =
    [
        "sales.orders", "sales.shipment_packages", "sales.shipment_documents", "sales.return_claims",
        "sales.marketplace_invoice_reference_history", "sales.marketplace_questions",
        "sales.marketplace_finance_transactions", "sales.marketplace_cargo_invoice_items",
        "billing.invoices", "billing.marketplace_deliveries", "billing.marketplace_delivery_states",
        "billing.manual_order_invoice_documents", "billing.manual_order_invoice_delivery_states",
        "catalog.marketplace_product_links", "catalog.marketplace_variant_links", "catalog.marketplace_listing_states",
        "catalog.channel_listing_profiles", "catalog.external_identifier_aliases",
        "catalog.category_mappings", "catalog.brand_mappings", "catalog.attribute_mappings", "catalog.attribute_value_mappings",
        "catalog.commission_tariffs", "catalog.promotion_offer_files",
        "catalog.import_sessions", "catalog.product_import_sessions", "catalog.product_import_staging_records", "catalog.catalog_structure_runs",
        "inventory.channel_offers", "inventory.channel_inventory_observations",
        "integration.jobs", "integration.external_effect_records", "integration.reconciliation_runs",
        "integration.sync_cursors", "integration.connection_backoffs",
        "integration.reference_snapshots", "integration.reference_items",
        "integration.marketplace_question_sync_states", "integration.marketplace_finance_sync_states",
    ];

    public static readonly IReadOnlyList<string> Kept =
    [
        "integration.platform_credentials", "integration.platform_capabilities", "integration.connection_sync_policies",
        "integration.webhook_subscriptions", "billing.invoice_policies",
        "inventory.connection_location_mappings", "inventory.connection_inventory_policies",
        "catalog.attribute_mapping_templates", "catalog.attribute_value_mapping_templates", "catalog.mapping_template_dismissals",
        "catalog.connection_attribute_defaults", "sales.cargo_provider_mappings",
    ];
}
