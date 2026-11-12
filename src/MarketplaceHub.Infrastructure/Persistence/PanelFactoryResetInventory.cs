namespace MarketplaceHub.Infrastructure.Persistence;

public enum PanelResetAction
{
    // Every row of the tenant goes ("TenantId" = tenant) when its category is chosen.
    Delete,
    // Stays as it is.
    Keep,
    // Handled by its own statement (PanelFactoryResetService), when its category is chosen.
    Special
}

// One category of Ayarlar › Veri: Section is the heading it is listed under; Requires are the categories that must go
// with it (their rows point at its rows and mean nothing without them).
public sealed record PanelResetGroup(int Rank, string Key, string Label, string Description, string Section, string Step, string[] Requires);

public sealed record PanelResetTable(string Name, PanelResetAction Action, PanelResetGroup? Group, string Note);

// A row of another category that points at a category being emptied while its own category stays: it is detached
// (its link cleared, or the row removed when it is only that link's record) before the category's rows go.
public sealed record PanelResetDetach(string ChildTable, string ParentTable, string Sql);

// Every table of the database, classified for Ayarlar › Veri: each table that goes belongs to one category, and choosing
// every category is Paneli Tamamen Sıfırla (the same job and the same statements). A table that is not listed here
// stops a reset (PANEL_RESET_UNCLASSIFIED) instead of being guessed, and PanelFactoryResetInventoryTests fails when the
// EF model has a table this list misses. The categories run in rank order, a child table before its parent.
public static class PanelFactoryResetInventory
{
    public const string SalesSection = "Satış ve Faturalar";
    public const string CatalogSection = "Katalog";
    public const string MarketplaceSection = "Pazaryeri Kayıtları";
    public const string SystemSection = "Sistem Kayıtları";

    public static readonly PanelResetGroup Invoices = new(1, "INVOICES", "Faturalar ve Belgeleri", "Faturalar, fatura PDF'leri, pazaryerine gönderimleri ve elle yüklenen fatura belgeleri.", SalesSection, "Faturalar ve belgeleri siliniyor", []);
    public static readonly PanelResetGroup Orders = new(2, "ORDERS", "Siparişler, Paketler ve İadeler", "Siparişler, kargo paketleri ve etiketleri, iadeler ve iade kararları. Faturaları da seçer.", SalesSection, "Siparişler, paketler ve iadeler siliniyor", ["INVOICES"]);
    public static readonly PanelResetGroup Questions = new(3, "QUESTIONS", "Müşteri Soruları", "Pazaryerlerinden gelen müşteri soruları ve cevapları.", SalesSection, "Müşteri soruları siliniyor", []);
    public static readonly PanelResetGroup Finance = new(4, "FINANCE", "Finans, Hakediş ve Kargo Faturaları", "Pazaryeri finans hareketleri, hakediş siparişleri ve kargo faturası kalemleri.", SalesSection, "Finans, hakediş ve kargo faturası kayıtları siliniyor", []);
    public static readonly PanelResetGroup Stock = new(5, "STOCK", "Stok ve Fiyatlar", "Stok kalemleri, stok hareketleri, rezervasyonlar, kanal fiyatları ve fiyat geçmişi.", CatalogSection, "Stok, fiyat ve fiyat geçmişi siliniyor", []);
    public static readonly PanelResetGroup Options = new(6, "OPTIONS", "Ürün Seçenekleri", "Ürün seçenek grupları, seçenek değerleri ve varyantlarla olan seçenek bağlantıları.", CatalogSection, "Ürün seçenekleri siliniyor", []);
    public static readonly PanelResetGroup Products = new(7, "PRODUCTS", "Ürünler", "Ürünler, varyantlar, görseller, pazaryeri bağları ve ürün aktarım kayıtları. Stok ve fiyatları da seçer.", CatalogSection, "Ürünler, varyantlar ve görseller siliniyor", ["STOCK"]);
    public static readonly PanelResetGroup CategoryAttributes = new(8, "CATEGORY_ATTRIBUTES", "Kategori Özellikleri", "Özellik başlıkları ve değerleri, kategori atamaları, ürünlerin özellik değerleri ve pazaryeri özellik eşleştirmeleri (eşleme şablonları dahil).", CatalogSection, "Kategori özellikleri siliniyor", []);
    public static readonly PanelResetGroup Categories = new(9, "CATEGORIES", "Kategori Listesi", "Kategori ağacı ve pazaryeri kategori eşleştirmeleri; ürünlerin kategori bağı boşalır.", CatalogSection, "Kategori listesi siliniyor", []);
    public static readonly PanelResetGroup Brands = new(10, "BRANDS", "Marka Listesi", "Markalar ve pazaryeri marka eşleştirmeleri; ürünlerin marka bağı boşalır.", CatalogSection, "Marka listesi siliniyor", []);
    public static readonly PanelResetGroup References = new(11, "REFERENCES", "Pazaryeri Referans Listeleri", "Pazaryerlerinden indirilen kategori, özellik ve marka listeleri.", MarketplaceSection, "Pazaryeri referans listeleri siliniyor", []);
    public static readonly PanelResetGroup Promotions = new(12, "PROMOTIONS", "Promosyon, Komisyon ve Reklam Dosyaları", "Yüklenen komisyon tarifeleri, promosyon teklif dosyaları ve reklam raporları.", MarketplaceSection, "Promosyon, komisyon ve reklam dosyaları siliniyor", []);
    public static readonly PanelResetGroup Jobs = new(13, "JOBS", "İş Takibi", "Bütün işler ve denemeleri (çalışan sıfırlama hariç), eşitleme imleçleri, mutabakat ve dış işlem kayıtları; eşitleme baştan başlar.", SystemSection, "İşler ve eşitleme kayıtları siliniyor", []);
    public static readonly PanelResetGroup Logs = new(14, "LOGS", "Loglar", "İşlem ve denetim logu, API istek kayıtları, yapay zekâ kullanım logu, ürün etkinlik logu, fiyat/stok gönderim logu ve gelen webhook kayıtları.", SystemSection, "Loglar siliniyor", []);
    public static readonly PanelResetGroup Notifications = new(15, "NOTIFICATIONS", "Bildirimler", "Sorun bildirimleri ve alarmlar, değişiklik olayları ve bu tarayıcıdaki bildirim geçmişi.", SystemSection, "Bildirimler siliniyor", []);
    public static readonly PanelResetGroup Dashboard = new(16, "DASHBOARD", "Raporlar ve Özet Tabloları", "Pano özeti, günlük ciro, düşük stok ve eşitleme durumu tabloları; yeniden hesaplanır.", SystemSection, "Rapor ve özet tabloları siliniyor", []);

    public static readonly IReadOnlyList<PanelResetGroup> Groups =
        [Invoices, Orders, Questions, Finance, Stock, Options, Products, CategoryAttributes, Categories, Brands, References, Promotions, Jobs, Logs, Notifications, Dashboard];

    public const string WaitStep = "Süren işlerin bitmesi bekleniyor";
    public const string FilesStep = "Kullanılmayan dosyalar temizleniyor";
    public const string DoneStep = "Tamamlandı";

    // The steps of a run: waiting for running work, one per chosen category, the files, the end.
    public static IReadOnlyList<string> StepsFor(IEnumerable<PanelResetGroup> chosen) =>
        [WaitStep, .. chosen.OrderBy(x => x.Rank).Select(x => x.Step), FilesStep, DoneStep];

    public static readonly IReadOnlyList<string> Steps = StepsFor(Groups);

    // The chosen categories with the ones they require; null for unknown keys.
    public static IReadOnlyList<PanelResetGroup>? Close(IEnumerable<string> keys)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(keys.Select(x => x.Trim().ToUpperInvariant()));
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            var group = Groups.FirstOrDefault(x => x.Key == key);
            if (group is null) return null;
            if (!chosen.Add(key)) continue;
            foreach (var required in group.Requires) queue.Enqueue(required);
        }
        return Groups.Where(x => chosen.Contains(x.Key)).ToList();
    }

    // What stays, as the panel lists it.
    public static readonly IReadOnlyList<string> KeptForPanel =
    [
        "Hesap, kullanıcılar ve bu oturum",
        "Entegrasyon bağlantıları ve giriş bilgileri",
        "Sistem ayarları ve sizin hazırladığınız şablonlar (soru cevap şablonları, Excel kolon profilleri)",
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
        K("billing.legal_entity_profiles", "e-fatura bağlantısının yer tutucu profili; şirket bilgisi E-Faturam hesabında"),

        // sales
        D("sales.order_financial_allocations", Orders), D("sales.order_lines", Orders), D("sales.order_status_history", Orders), D("sales.orders", Orders),
        D("sales.package_line_allocations", Orders), D("sales.shipment_document_attempts", Orders), D("sales.shipment_documents", Orders), D("sales.shipment_packages", Orders),
        D("sales.return_claims", Orders), D("sales.return_decisions", Orders), D("sales.return_evidence", Orders), D("sales.return_lines", Orders), D("sales.return_stock_dispositions", Orders),
        D("sales.marketplace_invoice_reference_history", Orders),
        D("sales.marketplace_questions", Questions), D("integration.marketplace_question_sync_states", Questions),
        D("sales.marketplace_cargo_invoice_items", Finance), D("sales.marketplace_finance_transactions", Finance), D("sales.marketplace_payout_orders", Finance),
        D("integration.marketplace_finance_sync_states", Finance),
        K("sales.cargo_provider_mappings", "bağlantının kargo firması eşleşmeleri (bağlantı ayarı)"),
        K("sales.marketplace_question_templates", "kullanıcının yazdığı soru cevap şablonları (ayar)"),

        // inventory
        D("inventory.channel_inventory_observations", Stock), D("inventory.channel_offers", Stock), D("inventory.channel_price_history", Stock),
        D("inventory.inventory_items", Stock), D("inventory.stock_ledger_entries", Stock), D("inventory.stock_reservations", Stock),
        K("inventory.inventory_locations", "depo tanımları (ayar)"),
        K("inventory.connection_inventory_policies", "bağlantının stok kuralları (bağlantı ayarı)"),
        K("inventory.connection_location_mappings", "bağlantının depo eşleşmeleri (bağlantı ayarı)"),

        // catalog: options, products and what hangs on them
        D("catalog.product_options", Options), D("catalog.product_option_values", Options), D("catalog.variant_option_values", Options),
        D("catalog.products", Products), D("catalog.product_variants", Products), D("catalog.product_media", Products),
        D("catalog.product_attribute_assignments", Products), D("catalog.product_size_charts", Products),
        D("catalog.marketplace_product_links", Products), D("catalog.marketplace_variant_links", Products), D("catalog.marketplace_listing_states", Products),
        D("catalog.external_identifier_aliases", Products), D("catalog.channel_listing_profiles", Products), D("catalog.channel_listing_attributes", Products),
        D("catalog.channel_listing_variants", Products), D("catalog.channel_media_order", Products),
        D("catalog.import_sessions", Products), D("catalog.import_staging_records", Products), D("catalog.import_match_candidates", Products),
        D("catalog.import_decisions", Products), D("catalog.field_provenance", Products),
        D("catalog.product_import_sessions", Products), D("catalog.product_import_staging_records", Products), D("catalog.catalog_structure_runs", Products),
        K("catalog.connection_attribute_defaults", "bağlantının varsayılan özellik değerleri (bağlantı ayarı)"),
        K("catalog.import_column_profiles", "Excel içe aktarma kolon profilleri (ayar)"),
        K("catalog.import_column_mappings", "Excel içe aktarma kolon profilleri (ayar)"),

        // catalog: the panel's own lists and their marketplace mappings
        D("catalog.attribute_definitions", CategoryAttributes), D("catalog.attribute_values", CategoryAttributes), D("catalog.category_attribute_requirements", CategoryAttributes),
        D("catalog.attribute_mappings", CategoryAttributes), D("catalog.attribute_value_mappings", CategoryAttributes),
        D("catalog.attribute_mapping_templates", CategoryAttributes), D("catalog.attribute_value_mapping_templates", CategoryAttributes), D("catalog.mapping_template_dismissals", CategoryAttributes),
        D("catalog.categories", Categories), D("catalog.category_mappings", Categories),
        D("catalog.brands", Brands), D("catalog.brand_mappings", Brands),

        // marketplace files and lists
        D("integration.reference_items", References), D("integration.reference_snapshots", References),
        D("catalog.commission_tariffs", Promotions), D("catalog.commission_tariff_rows", Promotions), D("catalog.commission_tariff_picks", Promotions),
        D("catalog.commission_tariff_selections", Promotions), D("catalog.commission_tariff_source_files", Promotions),
        D("catalog.promotion_offer_files", Promotions), D("catalog.ad_report_files", Promotions),

        // integration: jobs and sync state
        D("integration.jobs", Jobs, "çalışan sıfırlama işi kalır"), D("integration.job_attempts", Jobs, "çalışan sıfırlama işininki kalır"),
        D("integration.job_read_summaries", Jobs), D("integration.job_variant_results", Jobs),
        D("integration.external_effect_records", Jobs), D("integration.reconciliation_runs", Jobs), D("integration.reconciliation_differences", Jobs),
        D("integration.sync_cursors", Jobs, "eşitleme imleçleri: yeniden çekim baştan başlar"),
        S("integration.connection_backoffs", Jobs, "bağlantıların bekleme süreleri (TenantId yok; bağlantı kimliğiyle)"),
        S("integration.platform_connections", Jobs, "bağlantı kalır; yalnız son hata kodu temizlenir"),
        K("integration.platform_credentials", "bağlantı giriş bilgileri (kalır)"),
        K("integration.platform_capabilities", "bağlantının yetenek bilgisi (bağlantı ayarı)"),
        K("integration.connection_sync_policies", "bağlantının eşitleme kuralları (bağlantı ayarı)"),
        K("integration.webhook_subscriptions", "bağlantının bildirim kaydı (bağlantı ayarı)"),
        K("integration.rate_limit_buckets", "sistem geneli kota kovası; kiracı sütunu yok"),
        K("integration.rate_limit_usage", "sistem geneli dakikalık kota sayacı; kiracı sütunu yok, kendi saklama süresiyle silinir"),

        // logs
        D("ops.audit_logs", Logs, "işlem/etkinlik logu; tam sıfırlamanın tek satırı sonra yazılır"), D("ops.ai_usage_logs", Logs), D("ops.api_idempotency_records", Logs, "API istek kayıtları"),
        D("catalog.product_change_entries", Logs, "ürün etkinlik logu"), D("integration.price_stock_send_log", Logs, "fiyat/stok gönderim logu"),
        D("integration.inbox_messages", Logs, "gelen webhook kayıtları"),

        // notifications
        D("ops.operational_issues", Notifications, "sorun bildirimleri / alarmlar"), D("integration.outbox_events", Notifications, "değişiklik olayları"),

        // dashboard
        D("dashboard.snapshot", Dashboard), D("dashboard.revenue_daily", Dashboard), D("dashboard.low_stock", Dashboard), D("dashboard.sync_status", Dashboard),

        // files: the ones no remaining row uses go after every run (PanelFactoryResetService.FileReferences)
        K("ops.file_assets", "kullanılmayan dosyalar her sıfırlamanın sonunda silinmek üzere işaretlenir"),
        K("ops.feature_flags", "sistem geneli; kiracı sütunu yok"),

        // iam
        S("iam.tenant_settings", Invoices, "ayarlar kalır; yalnız fatura çalışma alanının gizlenen paket listesi silinir"),
        K("iam.user_sessions", "tam sıfırlamada isteyen oturum kalır, diğerleri silinir"),
        K("iam.tenants", "hesap"), K("iam.tenant_memberships", "kullanıcı üyelikleri"), K("iam.bootstrap_state", "ilk kurulum kaydı"),
        K("iam.users", "kullanıcılar"), K("iam.roles", "roller"), K("iam.user_roles", "roller"), K("iam.role_claims", "roller"),
        K("iam.user_claims", "kullanıcı"), K("iam.user_logins", "kullanıcı"), K("iam.user_tokens", "kullanıcı (doğrulayıcı anahtarı)"),
        K("iam.user_security", "iki adımlı doğrulama"), K("iam.recovery_codes", "kurtarma kodları"),

        // Out of the application's model: EF's own history.
        K("public.__EFMigrationsHistory", "veritabanı sürüm geçmişi"),
    ];

    // The rows of a category that stays which point at a category being emptied: cleared first.
    public static readonly IReadOnlyList<PanelResetDetach> Detaches =
    [
        new("sales.return_stock_dispositions", "inventory.inventory_items", """DELETE FROM sales.return_stock_dispositions WHERE "TenantId" = @tenant"""),
        new("catalog.product_attribute_assignments", "catalog.attribute_definitions", """DELETE FROM catalog.product_attribute_assignments WHERE "TenantId" = @tenant"""),
        new("catalog.product_attribute_assignments", "catalog.attribute_values", """DELETE FROM catalog.product_attribute_assignments WHERE "TenantId" = @tenant"""),
        new("catalog.products", "catalog.categories", """UPDATE catalog.products SET "CategoryId" = NULL WHERE "TenantId" = @tenant AND "CategoryId" IS NOT NULL"""),
        new("catalog.products", "catalog.brands", """UPDATE catalog.products SET "BrandId" = NULL WHERE "TenantId" = @tenant AND "BrandId" IS NOT NULL"""),
        // No key, but the same link: an order line keeps its text and loses its variant.
        new("sales.order_lines", "catalog.product_variants", """UPDATE sales.order_lines SET "VariantId" = NULL WHERE "TenantId" = @tenant AND "VariantId" IS NOT NULL"""),
    ];

    // Tenant settings that hold history rather than configuration.
    public static readonly string[] HistorySettingKeys = ["invoice-workspace-hidden-packages"];

    // The schemas a reset inspects for tables.
    public static readonly string[] Schemas = ["billing", "catalog", "dashboard", "iam", "integration", "inventory", "ops", "sales", "public"];

    public static PanelResetTable? Find(string name) => Tables.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
}
