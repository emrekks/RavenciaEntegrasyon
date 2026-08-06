#!/usr/bin/env python3
"""Static acceptance checks for the Ravencia v8 operational workspaces.

This script intentionally validates source-level acceptance criteria without contacting
Trendyol or mutating a marketplace account. It complements, but does not replace,
.NET/React unit and integration test suites.
"""
from __future__ import annotations

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]

CHECKS: dict[str, tuple[str, list[str]]] = {
    "orders-ui": (
        "src/MarketplaceHub.Web/src/F3Pages.tsx",
        [
            "['NEW', 'Yeni']",
            "['PROCESSING', 'İşleme Alınmış']",
            "['SHIPPED', 'Kargoya Verilmiş']",
            "['DELIVERED', 'Teslim Edilmiş']",
            "['CANCELLED', 'İptal Edilmiş']",
            "isDeadlineCritical",
            "Barkod / Etiket",
            "customerTaxOrIdentityNumber",
            "optionSignature",
        ],
    ),
    "returns-ui": (
        "src/MarketplaceHub.Web/src/F3Pages.tsx",
        [
            "['ALL', 'İade Talepleri']",
            "['SHIPPING', 'Kargo Aşamasında']",
            "['ACTION_REQUIRED', 'Aksiyon Bekleyen']",
            "['APPROVED', 'Onaylanmış']",
            "['REJECTED', 'Reddedilmiş']",
            "['REVIEW', 'İnceleniyor']",
            "/rejection-reasons",
            "Satılabilir — stoğa ekle",
            "Karantina — stoğa ekleme",
            "Hasarlı — stoğa ekleme",
            "Teslim alınmadı",
        ],
    ),
    "returns-api": (
        "src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs",
        [
            "IssueReasonsAsync",
            "ClaimIssueReasons",
            "ApproveClaim",
            "RejectClaim",
        ],
    ),
    "invoice-workspace": (
        "src/MarketplaceHub.Web/src/F4Pages.tsx",
        [
            "Faturalandırılmamışlar",
            "Faturalandırılmışlar",
            "Süresi Yaklaşanlar",
            "/invoice-workspace?limit=200",
            "Fatura kes",
        ],
    ),
    "invoice-rules": (
        "src/MarketplaceHub.Infrastructure/Persistence/F4BillingService.cs",
        [
            "AddDays(7)",
            "AddDays(5)",
            "INVOICE_ALREADY_EXISTS",
            "OriginalInvoiceId == null",
        ],
    ),
    "products-ui": (
        "src/MarketplaceHub.Web/src/F2Pages.tsx",
        [
            "Ürün adı, model, SKU veya barkod",
            "Hızlı stok/fiyat",
            "Ürün görseli URL",
            "Kargo ölçüleri ve desi",
            "Başlangıç stoğu",
            "/channel-offers",
            "Yalnız yaprak kategori seçilebilir",
        ],
    ),
    "dashboard": (
        "src/MarketplaceHub.Web/src/App.tsx",
        [
            "BEKLEYEN SİPARİŞ",
            "GECİKEN SİPARİŞ",
            "BUGÜNKÜ SİPARİŞ",
            "BU AY",
            "Kargo bazlı operasyon",
            "En düşük stoklu ürünler",
            "/invoice-workspace?limit=200",
        ],
    ),
    "api-surface": (
        "src/MarketplaceHub.Api/F4/F4Endpoints.cs",
        ["/invoice-workspace"],
    ),
}

failures: list[str] = []
for name, (relative, required) in CHECKS.items():
    path = ROOT / relative
    if not path.exists():
        failures.append(f"{name}: dosya bulunamadı: {relative}")
        continue
    text = path.read_text(encoding="utf-8")
    missing = [token for token in required if token not in text]
    if missing:
        failures.append(f"{name}: eksik kabul işaretleri: {', '.join(missing)}")
    else:
        print(f"PASS {name}")

if failures:
    print("\nSTATIC ACCEPTANCE FAILED", file=sys.stderr)
    for failure in failures:
        print(f"- {failure}", file=sys.stderr)
    raise SystemExit(1)

print(f"\nPASS {len(CHECKS)} operational acceptance groups")
