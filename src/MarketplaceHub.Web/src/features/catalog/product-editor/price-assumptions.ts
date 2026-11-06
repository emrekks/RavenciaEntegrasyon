// The hints beside Fiyat Hesapla's Komisyon Oranı, Kargo Ücreti and Hizmet Bedeli: the marketplaces' published figures
// for women's clothing, one short value each (owner, 6 Nov 2026: "kısa kısa, uyarı olmasın"). Sources (6 Nov 2026):
// commission Ideasoft / Paraşüt (Oct 2026); cargo the marketplaces' contracted cargo lists (Oct 2026, 1–3 desi, KDV
// included); fees docs/KARGO-TARIFELERI-2026.md. The measured endpoint (GET /reports/price-assumptions) stays unused.
export type HintRow = { store: string; value: string }
export type HintContent = { rows: HintRow[]; notes: string[]; source: string }

export const commissionHint: HintContent = {
  rows: [
    { store: 'Trendyol', value: '%21,5' },
    { store: 'Hepsiburada', value: '%21,6' },
    { store: 'n11', value: '%20' },
    { store: 'Pazarama', value: '%19' },
    { store: 'PTT AVM', value: '%15' },
    { store: 'Shopify', value: '%6,29' }
  ],
  notes: [],
  source: ''
}

export const cargoHint: HintContent = {
  rows: [
    { store: 'Trendyol', value: '98,34 ₺' },
    { store: 'Hepsiburada', value: '98,39 ₺' },
    { store: 'n11', value: '100,50 ₺' },
    { store: 'Pazarama', value: '112,33 ₺' },
    { store: 'PTT AVM', value: '81,25 ₺' },
    { store: 'Shopify', value: '120 ₺' }
  ],
  notes: [],
  source: ''
}

export const serviceFeeHint: HintContent = {
  rows: [
    { store: 'Trendyol', value: '13,19 ₺' },
    { store: 'Hepsiburada', value: '13,19 ₺' },
    { store: 'n11', value: '%2' },
    { store: 'Pazarama', value: '8,28 ₺' },
    { store: 'PTT AVM', value: '%1,5' },
    { store: 'Shopify', value: '0 ₺' }
  ],
  notes: [],
  source: ''
}
