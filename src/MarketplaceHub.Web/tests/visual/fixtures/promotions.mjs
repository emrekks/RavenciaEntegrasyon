// Promosyon Kârlılık Analizi (/promotions): one Trendyol store (c-ty) with a weekly commission tariff and a Plus file.
// The capture clock stands at 2026-10-20 09:30 (+03:00).

const productImage = '/pack/brand/ravencia-symbol-128.webp'
const costs = (cost, price) => ({ productCost: cost, cargo: 42.5, serviceFee: 10.19, withholding: Math.round(price * 0.01 * 100) / 100, netVat: 18.4 })
const choice = (tier, price, rate, cost) => {
  const commission = Math.round(price * rate) / 100
  return { tier, price, rate, commission, remaining: cost === null ? null : Math.round((price - commission - cost - 71.09 - price * 0.01) * 100) / 100, costs: cost === null ? null : costs(cost, price) }
}
const bands = [{ tier: 1, min: 470.64, max: null, rate: 21 }, { tier: 2, min: 435.98, max: 470.63, rate: 18.5 }, { tier: 3, min: 388.64, max: 435.97, rate: 16 }, { tier: 4, min: null, max: 388.63, rate: 13.5 }]
const row = (id, name, model, color, size, stock, cost, price) => ({
  id, barcode: `86900${id.padStart(8, '0')}`, sellerSku: `RV-${model}-${size}`, modelCode: model, productName: name, size, color, stock, costPrice: cost,
  basisPrice: price, currentSalePrice: price, currentRate: 21, currentTier: 1, currentBandRate: 21, bands, nextLowerBand: bands[1], matchesPanel: true,
  imageUrl: productImage, recommendedTier: 1,
  choices: [choice(1, 499.9, 21, cost), choice(2, 459.9, 18.5, cost), choice(3, 419.9, 16, cost), choice(4, 379.9, 13.5, cost)]
})
const rows = [
  row('1', 'Kadın Yüksek Bel Mom Jean', 'MJ-204', 'Mavi', 'S', 14, 182.4, 499.9),
  row('2', 'Kadın Yüksek Bel Mom Jean', 'MJ-204', 'Mavi', 'M', 22, 182.4, 499.9),
  row('3', 'Kadın Yüksek Bel Mom Jean', 'MJ-204', 'Mavi', 'L', 9, 182.4, 499.9),
  row('4', 'Oversize Basic Tişört', 'TS-118', 'Siyah', 'STD', 41, null, 289.9),
]

const tariffs = [
  { id: 't1', platformCode: 'TRENDYOL', periodStart: '2026-10-19T21:00:00Z', periodEnd: '2026-10-26T21:00:00Z', periodText: '20 – 26 Ekim', fileName: 'komisyon-tarifesi-20-26-ekim.xlsx', uploadedAt: '2026-10-19T08:12:00Z', rowCount: 1284, matchedRowCount: 1197, isCurrent: true },
  { id: 't0', platformCode: 'TRENDYOL', periodStart: '2026-10-12T21:00:00Z', periodEnd: '2026-10-19T21:00:00Z', periodText: '13 – 19 Ekim', fileName: 'komisyon-tarifesi-13-19-ekim.xlsx', uploadedAt: '2026-10-12T07:40:00Z', rowCount: 1279, matchedRowCount: 1190, isCurrent: false },
]

const price = (value, rate, cost) => {
  const commission = Math.round(value * rate) / 100
  return { price: value, rate, commission, remaining: cost === null ? null : Math.round((value - commission - cost) * 100) / 100, costs: cost === null ? null : costs(cost, value) }
}
const plusRow = (barcode, name, model, color, size, stock, cost, now, plus) => ({
  barcode, sellerSku: `RV-${model}-${size}`, size, costPrice: cost, modelCode: model, productName: name, color, imageUrl: productImage, stock, promotions: null,
  category: 'Jean', brand: 'Ravencia', now: price(now, 21, cost), plus: price(plus, 15, cost),
  gain: cost === null ? null : Math.round(((plus - plus * 0.15) - (now - now * 0.21)) * 100) / 100
})
const plus = [
  plusRow('8690000000011', 'Kadın Yüksek Bel Mom Jean', 'MJ-204', 'Mavi', 'S', 14, 182.4, 499.9, 469.9),
  plusRow('8690000000012', 'Kadın Yüksek Bel Mom Jean', 'MJ-204', 'Mavi', 'M', 22, 182.4, 499.9, 469.9),
  plusRow('8690000000041', 'Oversize Basic Tişört', 'TS-118', 'Siyah', 'STD', 41, 96.5, 289.9, 279.9),
]

export const promotions = [
  ['GET /connections/c-ty/commission-tariffs', tariffs],
  ['^/connections/c-ty/commission-tariffs/t[01]/rows$', { items: rows, totalCount: 120, page: 0, pageSize: 50 }],
  ['^/connections/c-ty/commission-tariffs/t[01]/facets$', { categories: ['Jean', 'Tişört'], brands: ['Ravencia'] }],
  ['GET /connections/c-ty/promotion-offers', [{ kind: 'PLUS', fileName: 'plus-komisyon-tarifesi.xlsx', uploadedAt: '2026-10-19T09:00:00Z', periodText: '20 Ekim – 2 Kasım', rowCount: 3, matchedRowCount: 3 }]],
  ['GET /connections/c-ty/promotion-offers/PLUS', { offer: { kind: 'PLUS', fileName: 'plus-komisyon-tarifesi.xlsx', uploadedAt: '2026-10-19T09:00:00Z', periodText: '20 Ekim – 2 Kasım', rowCount: 3, matchedRowCount: 3 }, plus, advantage: null, flash: null }],
]
