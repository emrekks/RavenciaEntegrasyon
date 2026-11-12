namespace MarketplaceHub.Infrastructure.Persistence;

public enum PanelResetAction
{
    // Every row of the tenant goes ("TenantId" = tenant).
    Delete,
    // Stays as it is.
    Keep,
    // Handled by its own statement (PanelFactoryResetService.CleanUpSpecialTablesAsync).
    Special
}

public sealed record PanelResetGroup(int Rank, string Key, string Label, string Step);

public sealed record PanelResetTable(string Name, PanelResetAction Action, PanelResetGroup? Group, string Note);

// Every table of the database, classified for Paneli Tamamen Sıfırla. A table that is not listed here stops the reset
// (PANEL_RESET_UNCLASSIFIED) instead of being guessed, and PanelFactoryResetInventoryTests fails when the EF model has a
// table this list misses. The groups run in rank order; within the run the deletes follow the foreign keys (a child
// table before its parent), which the ranks agree with (checked by the tests as well).
public static class PanelFactoryResetInventory
{
    public static readonly PanelResetGroup Invoices = new(1, "INVOICES", "Faturalar, Belgeleri ve Gönderimleri", "Faturalar ve belgeleri siliniyor");
    public static readonly PanelResetGroup Orders = new(2, "ORDERS", "Siparişler, Paketler, İadeler ve Sorular", "Siparişler, paketler, iadeler ve sorular siliniyor");
    public static readonly PanelResetGroup Finance = new(3, "FINANCE", "Finans, Hakediş ve Kargo Faturası Kayıtları", "Finans, hakediş ve kargo faturası kayıtları siliniyor");
    public static readonly PanelResetGroup Stock = new(4, "STOCK", "Stok Hareketleri, Fiyatlar ve Fiyat Geçmişi", "Stok, fiyat ve fiyat geçmişi siliniyor");
    public static readonly PanelResetGroup Products = new(5, "PRODUCTS", "Ürünler, Varyantlar, Görseller ve Pazaryeri Bağları", "Ürünler, varyantlar, görseller ve pazaryeri bağları siliniyor");
    public static readonly PanelResetGroup CatalogLists = new(6, "CATALOG_LISTS", "Kategoriler, Özellikler, Markalar", "Kategori, özellik ve marka listeleri siliniyor");
    public static readonly PanelResetGroup References = new(7, "REFERENCES", "Pazaryeri Referans Listeleri", "Pazaryeri referans listeleri siliniyor");
    public static readonly PanelResetGroup Logs = new(8, "LOGS", "İşler, Eşitleme Kayıtları ve Tüm Loglar", "İşler, eşitleme kayıtları ve loglar siliniyor");
    public static readonly PanelResetGroup Caches = new(9, "CACHES", "Pano Özetleri ve Saklanan Dosyalar", "Pano özetleri, dosyalar ve oturumlar temizleniyor");

    public static readonly IReadOnlyList<PanelResetGroup> Groups = [Invoices, Orders, Finance, Stock, Products, CatalogLists, References, Logs, Caches];

    // The steps the panel lists: waiting for running work, one per group, and the end.
    public static readonly IReadOnlyList<string> Steps =
        ["Süren işlerin bitmesi bekleniyor", .. Groups.Select(x => x.Step), "Tamamlandı"];

    // What stays, as the panel lists it.
    public static readonly IReadOnlyList<string> KeptForPanel =
    [
        "Hesap, kullanıcılar, roller ve açık olan bu oturum",
        "Entegrasyon bağlantıları ve giriş bilgileri (bağlantı ayarları, eşitleme ve stok kuralları, kargo eşleşmeleri, varsayılan özellik değerleri)",
        "Fatura kuralları ve şirket (fatura kesen) bilgileri",
        "Sistem ayarları: güvenlik, yapay zekâ anahtarı, uyarı e-postası, kargo etiketi, görünüm",
        "Depo tanımları, soru cevap şablonları ve Excel içe aktarma kolon profilleri",
    ];

    private static PanelResetTable D(string name, PanelResetGroup group, string note = "") => new(name, PanelResetAction.Delete, group, note);
    private static PanelResetTable K(string name, string note) => new(name, PanelResetAction.Keep, null, note);
    private static PanelResetTable S(string name, PanelResetGroup group, string note) => new(name, PanelResetAction.Special, group, note);

    public static readonly IReadOnlyList<PanelResetTable> Tables =
    [
        // billing
        D("billing.invoice_documents", Invoices), D("billing.invoice_lines", Invoices), D("billing.invoice_party_snapshots", Invoices),
        D("billing.invoice_submission_attempts", Invoices), D("billing.invoices", Invoices),
        D("billing.manual_order_invoice_delivery_states", Invoices), D("billing.manual_order_invoice_documents", Invoices),
        D("billing.marketplace_deliveries", Invoices), D("billing.marketplace_delivery_states", Invoices),
        K("billing.invoice_policies", "e-fatura bağlantısının fatura kuralları (bağlantı ayarı)"),
        K("billing.legal_entity_profiles", "fatura kesen şirketin bilgileri (ayar)"),

        // sales
        D("sales.order_financial_allocations", Orders), D("sales.order_lines", Orders), D("sales.order_status_history", Orders), D("sales.orders", Orders),
        D("sales.package_line_allocations", Orders), D("sales.shipment_document_attempts", Orders), D("sales.shipment_documents", Orders), D("sales.shipment_packages", Orders),
        D("sales.return_claims", Orders), D("sales.return_decisions", Orders), D("sales.return_evidence", Orders), D("sales.return_lines", Orders), D("sales.return_stock_dispositions", Orders),
        D("sales.marketplace_invoice_reference_history", Orders), D("sales.marketplace_questions", Orders),
        D("sales.marketplace_cargo_invoice_items", Finance), D("sales.marketplace_finance_transactions", Finance), D("sales.marketplace_payout_orders", Finance),
        K("sales.cargo_provider_mappings", "bağlantının kargo firması eşleşmeleri (bağlantı ayarı)"),
        K("sales.marketplace_question_templates", "kullanıcının yazdığı soru cevap şablonları (ayar)"),

        // inventory
        D("inventory.channel_inventory_observations", Stock), D("inventory.channel_offers", Stock), D("inventory.channel_price_history", Stock),
        D("inventory.inventory_items", Stock), D("inventory.stock_ledger_entries", Stock), D("inventory.stock_reservations", Stock),
        D("integration.price_stock_send_log", Stock, "fiyat/stok gönderim logu"),
        K("inventory.inventory_locations", "depo tanımları (ayar)"),
        K("inventory.connection_inventory_policies", "bağlantının stok kuralları (bağlantı ayarı)"),
        K("inventory.connection_location_mappings", "bağlantının depo eşleşmeleri (bağlantı ayarı)"),

        // catalog: products and what hangs on them
        D("catalog.products", Products), D("catalog.product_variants", Products), D("catalog.product_media", Products), D("catalog.product_options", Products),
        D("catalog.product_option_values", Products), D("catalog.variant_option_values", Products), D("catalog.product_attribute_assignments", Products),
        D("catalog.product_size_charts", Products), D("catalog.product_change_entries", Products, "ürün etkinlik logu"),
        D("catalog.marketplace_product_links", Products), D("catalog.marketplace_variant_links", Products), D("catalog.marketplace_listing_states", Products),
        D("catalog.external_identifier_aliases", Products), D("catalog.channel_listing_profiles", Products), D("catalog.channel_listing_attributes", Products),
        D("catalog.channel_listing_variants", Products), D("catalog.channel_media_order", Products),
        D("catalog.import_sessions", Products), D("catalog.import_staging_records", Products), D("catalog.import_match_candidates", Products),
        D("catalog.import_decisions", Products), D("catalog.field_provenance", Products),
        D("catalog.product_import_sessions", Products), D("catalog.product_import_staging_records", Products), D("catalog.catalog_structure_runs", Products),
        D("catalog.commission_tariffs", Products), D("catalog.commission_tariff_rows", Products), D("catalog.commission_tariff_picks", Products),
        D("catalog.commission_tariff_selections", Products), D("catalog.commission_tariff_source_files", Products),
        D("catalog.promotion_offer_files", Products), D("catalog.ad_report_files", Products),
        D("catalog.category_mappings", Products, "yerel kategoriye bağlı; kategoriler de gidiyor"), D("catalog.brand_mappings", Products),
        D("catalog.attribute_mappings", Products), D("catalog.attribute_value_mappings", Products),
        D("catalog.attribute_mapping_templates", Products, "yerel özelliklere bağlı; özellikler de gidiyor"), D("catalog.attribute_value_mapping_templates", Products),
        D("catalog.mapping_template_dismissals", Products),
        K("catalog.connection_attribute_defaults", "bağlantının varsayılan özellik değerleri (bağlantı ayarı)"),
        K("catalog.import_column_profiles", "Excel içe aktarma kolon profilleri (ayar)"),
        K("catalog.import_column_mappings", "Excel içe aktarma kolon profilleri (ayar)"),

        // catalog: the panel's own lists
        D("catalog.categories", CatalogLists), D("catalog.category_attribute_requirements", CatalogLists), D("catalog.attribute_definitions", CatalogLists),
        D("catalog.attribute_values", CatalogLists), D("catalog.brands", CatalogLists),

        // integration
        D("integration.reference_items", References), D("integration.reference_snapshots", References),
        D("integration.jobs", Logs, "çalışan sıfırlama işi kalır"), D("integration.job_attempts", Logs, "çalışan sıfırlama işininki kalır"),
        D("integration.job_read_summaries", Logs), D("integration.job_variant_results", Logs),
        D("integration.outbox_events", Logs), D("integration.inbox_messages", Logs), D("integration.external_effect_records", Logs),
        D("integration.reconciliation_runs", Logs), D("integration.reconciliation_differences", Logs),
        D("integration.sync_cursors", Logs, "eşitleme imleçleri: yeniden çekim baştan başlar"),
        D("integration.marketplace_question_sync_states", Logs), D("integration.marketplace_finance_sync_states", Logs),
        S("integration.connection_backoffs", Logs, "bağlantıların bekleme süreleri (TenantId yok; bağlantı kimliğiyle)"),
        S("integration.platform_connections", Logs, "bağlantı kalır; yalnız son hata kodu temizlenir"),
        K("integration.platform_credentials", "bağlantı giriş bilgileri (kalır)"),
        K("integration.platform_capabilities", "bağlantının yetenek bilgisi (bağlantı ayarı)"),
        K("integration.connection_sync_policies", "bağlantının eşitleme kuralları (bağlantı ayarı)"),
        K("integration.webhook_subscriptions", "bağlantının bildirim kaydı (bağlantı ayarı)"),
        K("integration.rate_limit_buckets", "sistem geneli kota kovası; kiracı sütunu yok"),
        K("integration.rate_limit_usage", "sistem geneli dakikalık kota sayacı; kiracı sütunu yok, kendi saklama süresiyle silinir"),

        // ops: logs
        D("ops.audit_logs", Logs, "işlem/etkinlik logu; sıfırlamanın tek satırı sonra yazılır"), D("ops.ai_usage_logs", Logs), D("ops.api_idempotency_records", Logs, "API istek kayıtları"),
        D("ops.operational_issues", Logs, "hata/sorun kayıtları"),
        S("ops.file_assets", Caches, "dosyalar silinmek üzere işaretlenir; dosya ve satır saatlik süpürmede gider"),
        K("ops.feature_flags", "sistem geneli; kiracı sütunu yok"),

        // dashboard
        D("dashboard.snapshot", Caches), D("dashboard.revenue_daily", Caches), D("dashboard.low_stock", Caches), D("dashboard.sync_status", Caches),

        // iam
        S("iam.user_sessions", Caches, "isteyen oturum kalır, diğerleri silinir"),
        S("iam.tenant_settings", Caches, "ayarlar kalır; yalnız geçmiş tutan anahtarlar silinir"),
        K("iam.tenants", "hesap"), K("iam.tenant_memberships", "kullanıcı üyelikleri"), K("iam.bootstrap_state", "ilk kurulum kaydı"),
        K("iam.users", "kullanıcılar"), K("iam.roles", "roller"), K("iam.user_roles", "roller"), K("iam.role_claims", "roller"),
        K("iam.user_claims", "kullanıcı"), K("iam.user_logins", "kullanıcı"), K("iam.user_tokens", "kullanıcı (doğrulayıcı anahtarı)"),
        K("iam.user_security", "iki adımlı doğrulama"), K("iam.recovery_codes", "kurtarma kodları"),

        // Out of the application's model: EF's own history.
        K("public.__EFMigrationsHistory", "veritabanı sürüm geçmişi"),
    ];

    // Tenant settings that hold history rather than configuration.
    public static readonly string[] HistorySettingKeys = ["invoice-workspace-hidden-packages"];

    // The schemas the reset inspects for tables.
    public static readonly string[] Schemas = ["billing", "catalog", "dashboard", "iam", "integration", "inventory", "ops", "sales", "public"];

    public static PanelResetTable? Find(string name) => Tables.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
}
