// The hints beside Fiyat Hesapla's Komisyon Oranı, Kargo Ücreti and Hizmet Bedeli: the marketplaces' PUBLISHED figures
// for women's clothing (researched 6 Nov 2026), not figures measured on our own sales. The measured endpoint
// (GET /reports/price-assumptions) stays on the server but the dialog no longer calls it.
export type HintRow = { store: string; value: string }
export type HintContent = { rows: HintRow[]; notes: string[]; source: string }

const source = 'Kaynak: Trendyol / Hepsiburada / n11 komisyon: Ideasoft & Paraşüt (Eki 2026); kargo: pazaryeri kargo listeleri (Eki 2026); ayrıntı docs/KARGO-TARIFELERI-2026.md (6 Kasım 2026).'

export const commissionHint: HintContent = {
  rows: [
    { store: 'Trendyol', value: '%21,5 (KDV dahil; kaynaklar %21,36–%22,5 arası)' },
    { store: 'Hepsiburada', value: '%18 + %20 KDV (kutuya %21,6 girin)' },
    { store: 'n11', value: '%20 (KDV dahil; kaynak Aralık 2024)' },
    { store: 'Pazarama', value: 'kategori oranı bulunamadı; genel aralık %0–19, üstüne %20 KDV' },
    { store: 'PTT AVM', value: 'kategori oranı bulunamadı; genel aralık %5–15' },
    { store: 'Shopify', value: 'pazaryeri komisyonu yok; kart: iyzico %4,29 + 0,25 ₺ ve Shopify işlem ücreti %2 (toplam ≈ %6,29 + 0,25 ₺)' }
  ],
  notes: ['Kesin oran mağaza panelindeki kategori tablosundadır.'],
  source
}

export const cargoHint: HintContent = {
  rows: [
    { store: 'Trendyol', value: '0–2 desi 81,95 ₺ (KDV dahil 98,34 ₺), 3 desi 100,20 ₺' },
    { store: 'Hepsiburada', value: 'hepsiJET 0–2 desi 81,99 ₺, 3 desi 99,99 ₺' },
    { store: 'n11', value: 'PTT 0–2 desi 81,82 ₺, 3 desi 101,29 ₺ (+%2,35 PHB)' },
    { store: 'Pazarama', value: 'Aras 1 desi 91,46 ₺, 2 desi 101,36 ₺, 3 desi 113,34 ₺ (+%2,35 PHB)' },
    { store: 'PTT AVM', value: '1–2 desi 81,25 ₺ KDV dahil' },
    { store: 'Shopify', value: 'kendi anlaşmanız (≈120 ₺)' }
  ],
  notes: ['1–3 desi, anlaşmalı kargo, KDV hariç.'],
  source
}

export const serviceFeeHint: HintContent = {
  rows: [
    { store: 'Trendyol', value: '10,99 + KDV = 13,19 ₺ / paket' },
    { store: 'Hepsiburada', value: '10,99 + KDV = 13,19 ₺ / teslimat (21 Eylül 2026\'dan beri)' },
    { store: 'n11', value: 'sabit ücret yok; pazarlama %1 + pazaryeri %0,67, KDV üstüne' },
    { store: 'Pazarama', value: '6,90 + KDV = 8,28 ₺ / sipariş' },
    { store: 'PTT AVM', value: 'ödeme altyapı bedeli %1,5' },
    { store: 'Shopify', value: 'yok' }
  ],
  notes: [],
  source
}
