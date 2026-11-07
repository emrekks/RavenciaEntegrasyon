import { describe, expect, it } from 'vitest'
import { productPlatformDisplayLabel, productPlatformDisplayState, withProductPlatformSaleState, type ProductPlatformSaleCounts } from './product-platform-status'
import { publishStatusSteps } from './product-editor/publish-status-steps'
import { isOffSale, offSaleSummary, saleReasonLabel, saleStateLabel } from './sale-state'

const platforms = ['Trendyol', 'Hepsiburada', 'N11', 'Pazarama', 'PTT AVM', 'Shopify']

// The list's dot and tooltip for one marketplace, as ProductsPage builds them.
function display(platform: string, statuses: string[], counts: ProductPlatformSaleCounts) {
  const state = productPlatformDisplayState(statuses, false, counts.matched, counts.variantCount)
  const label = productPlatformDisplayLabel(platform, statuses, counts.matched, counts.variantCount, state)
  return withProductPlatformSaleState(platform, state, label, counts)
}

const counts = (matched: number, values: Partial<ProductPlatformSaleCounts> = {}): ProductPlatformSaleCounts =>
  ({ matched, variantCount: matched, onSale: 0, offSale: 0, offSaleStockZero: 0, reasons: {}, ...values })

describe('sale state', () => {
  it('names the three states, unknown included, and never names a stock of 0 as a reason', () => {
    expect(saleStateLabel('ON_SALE')).toBe('Satışta')
    expect(saleStateLabel('OFF_SALE')).toBe('Satışa Kapalı')
    expect(saleStateLabel(null)).toBe('Bilinmiyor')
    expect(saleStateLabel('SOMETHING')).toBe('Bilinmiyor')
    expect(saleReasonLabel('BLACKLISTED')).toBe('Kara Listede')
    expect(saleReasonLabel('PRICE_ZERO')).toBe('Fiyat 0')
    expect(saleReasonLabel('STOCK_ZERO')).toBeNull()
    expect(saleReasonLabel(null)).toBeNull()
    expect(isOffSale('OFF_SALE')).toBe(true)
    expect(isOffSale(null)).toBe(false)
  })

  it('counts only the closures beyond a stock of 0 and names the reason when there is one kind', () => {
    expect(offSaleSummary(4, 0)).toBeNull()
    expect(offSaleSummary(0, 2)).toBeNull()
    // Closed only for a stock of 0: no closure at all.
    expect(offSaleSummary(4, 4, 4)).toBeNull()
    expect(offSaleSummary(4, 2, 2)).toBeNull()
    expect(offSaleSummary(4, 4)).toMatchObject({ allClosed: true, reason: null, blocking: false, label: 'Satışa Kapalı' })
    expect(offSaleSummary(4, 4, 0, { ARCHIVED: 4 })).toMatchObject({ allClosed: true, reason: 'ARCHIVED', label: 'Satışa Kapalı · Arşivde' })
    expect(offSaleSummary(7, 5, 3, { BLACKLISTED: 2 })).toMatchObject({ closed: 2, allClosed: false, blocking: true, label: '2/7 Varyant Satışa Kapalı (Kara Listede)' })
    // Two kinds (one of them unnamed): no reason.
    expect(offSaleSummary(7, 3, 0, { BLACKLISTED: 2 })).toMatchObject({ closed: 3, reason: null, blocking: true, label: '3/7 Varyant Satışa Kapalı' })
    expect(offSaleSummary(7, 2, 0, { ARCHIVED: 1, BY_SELLER: 1 })).toMatchObject({ reason: null, blocking: false, label: '2/7 Varyant Satışa Kapalı' })
    // A stock-0 count sent among the reasons is still no closure.
    expect(offSaleSummary(4, 2, 2, { STOCK_ZERO: 2 })).toBeNull()
  })

  it('shows 10/10 matched with 5 closed only for a stock of 0 as Ürün Yayında (green) on every marketplace', () => {
    for (const platform of platforms) {
      const live = display(platform, ['LIVE'], counts(10, { onSale: 5, offSale: 5, offSaleStockZero: 5 }))
      expect(live).toEqual({ state: 'active', label: `${platform}: Ürün Yayında (10/10)` })
      expect(live.label).not.toMatch(/stok/i)
      const imported = display(platform, ['LINKED'], counts(10, { onSale: 5, offSale: 5, offSaleStockZero: 5 }))
      expect(imported).toEqual({ state: 'active', label: `${platform}: Ürün Yayında (10/10)` })
    }
  })

  it('reads every variant closed only for a stock of 0 as Ürün Yayında', () => {
    expect(display('Trendyol', ['LIVE'], counts(3, { offSale: 3, offSaleStockZero: 3 }))).toEqual({ state: 'active', label: 'Trendyol: Ürün Yayında (3/3)' })
  })

  it('shows some variants blacklisted as red Kısmen Satışta with the reason', () => {
    expect(display('Trendyol', ['LIVE'], counts(7, { onSale: 5, offSale: 2, reasons: { BLACKLISTED: 2 } })))
      .toEqual({ state: 'blocked', label: 'Trendyol: Kısmen Satışta · 2/7 Varyant Satışa Kapalı (Kara Listede)' })
    // Red wins over yellow when any variant is blacklisted or locked, even beside another reason.
    expect(display('N11', ['LIVE'], counts(7, { onSale: 4, offSale: 3, reasons: { LOCKED: 1, ARCHIVED: 2 } })))
      .toEqual({ state: 'blocked', label: 'N11: Kısmen Satışta · 3/7 Varyant Satışa Kapalı' })
  })

  it('shows every variant blacklisted or locked as red Satışa Kapalı', () => {
    expect(display('Hepsiburada', ['LIVE'], counts(3, { offSale: 3, reasons: { BLACKLISTED: 3 } }))).toEqual({ state: 'blocked', label: 'Hepsiburada: Satışa Kapalı · Kara Listede' })
    expect(display('Pazarama', ['LIVE'], counts(3, { offSale: 3, reasons: { LOCKED: 3 } }))).toEqual({ state: 'blocked', label: 'Pazarama: Satışa Kapalı · Kilitli' })
  })

  it('shows some variants archived as yellow Kısmen Satışta, the stock-0 ones left out of the count', () => {
    expect(display('Trendyol', ['LIVE'], counts(7, { onSale: 3, offSale: 4, offSaleStockZero: 2, reasons: { ARCHIVED: 2 } })))
      .toEqual({ state: 'partial', label: 'Trendyol: Kısmen Satışta · 2/7 Varyant Satışa Kapalı (Arşivde)' })
  })

  it('shows a Hepsiburada price of 0 as its reason', () => {
    expect(display('Hepsiburada', ['LIVE'], counts(2, { offSale: 2, reasons: { PRICE_ZERO: 2 } }))).toEqual({ state: 'closed', label: 'Hepsiburada: Satışa Kapalı · Fiyat 0' })
    expect(display('Hepsiburada', ['LIVE'], counts(4, { onSale: 3, offSale: 1, reasons: { PRICE_ZERO: 1 } })))
      .toEqual({ state: 'partial', label: 'Hepsiburada: Kısmen Satışta · 1/4 Varyant Satışa Kapalı (Fiyat 0)' })
  })

  it('names any other closure in orange, and a closure without a reason as plain Satışa Kapalı', () => {
    for (const [reason, text] of [['ARCHIVED', 'Arşivde'], ['BY_SELLER', 'Satıcı Kapattı'], ['SUSPENDED', 'Askıda'], ['FROZEN', 'Donduruldu'], ['DRAFT', 'Taslak'], ['BEFORE_SALE', 'Satış Öncesi']]) {
      expect(display('Shopify', ['LIVE'], counts(2, { offSale: 2, reasons: { [reason]: 2 } }))).toEqual({ state: 'closed', label: `Shopify: Satışa Kapalı · ${text}` })
    }
    expect(display('PTT AVM', ['LIVE'], counts(2, { offSale: 2 }))).toEqual({ state: 'closed', label: 'PTT AVM: Satışa Kapalı' })
  })

  it('shows LIVE and LINKED colour cards at full coverage as Ürün Yayında (MZ028IH: 1 LIVE + 9 LINKED)', () => {
    const statuses = ['LIVE', ...Array(9).fill('LINKED')]
    expect(display('Trendyol', statuses, counts(10, { onSale: 5, offSale: 5, offSaleStockZero: 5 }))).toEqual({ state: 'active', label: 'Trendyol: Ürün Yayında (10/10)' })
    // Not every variant matched: still partly live.
    expect(productPlatformDisplayState(statuses, false, 9, 10)).toBe('partial')
  })

  it('shows an imported card as Ürün Yayında only when every matched variant has a known sale state', () => {
    expect(display('Trendyol', ['LINKED'], counts(4, { onSale: 2, offSale: 2, offSaleStockZero: 2 }))).toEqual({ state: 'active', label: 'Trendyol: Ürün Yayında (4/4)' })
    expect(display('Trendyol', ['LINKED'], counts(4, { onSale: 2, offSale: 1, offSaleStockZero: 1 }))).toEqual({ state: 'linked', label: 'Trendyol Bağlantısı Var (4/4 Varyant)' })
  })

  it('adds no sale words when the sale state is unknown', () => {
    expect(display('Trendyol', ['LIVE'], counts(3))).toEqual({ state: 'active', label: 'Trendyol: Ürün Yayında (3/3)' })
    expect(display('Trendyol', ['LINKED'], counts(3))).toEqual({ state: 'linked', label: 'Trendyol Bağlantısı Var (3/3 Varyant)' })
    expect(withProductPlatformSaleState('Trendyol', 'active', 'x', null)).toEqual({ state: 'active', label: 'x' })
  })

  it('shows a listing archived there as orange Satışa Kapalı · Arşivde, never green', () => {
    expect(display('Trendyol', ['ARCHIVED'], counts(3, { onSale: 3 }))).toEqual({ state: 'closed', label: 'Trendyol: Satışa Kapalı · Arşivde' })
  })

  it('leaves the publication state alone when the card is not there', () => {
    expect(withProductPlatformSaleState('Trendyol', 'processing', 'x', counts(2, { offSale: 2 }))).toEqual({ state: 'processing', label: 'x' })
    expect(withProductPlatformSaleState('Trendyol', 'missing', 'x', counts(2, { offSale: 2 }))).toEqual({ state: 'missing', label: 'x' })
  })

  it('keeps the publish steps done and reads the sale label on a live card closed to sale', () => {
    const base = { requiredGaps: 0, optionalWarnings: 0, live: true, fallbackLabel: '' }
    expect(publishStatusSteps(base).label).toBe('Yayında')
    const closed = publishStatusSteps({ ...base, saleClosedLabel: 'Satışa Kapalı · Kara Listede' })
    expect(closed.label).toBe('Satışa Kapalı · Kara Listede')
    expect(closed.steps).toEqual(['done', 'done', 'done', 'done'])
  })
})
