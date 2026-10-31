import { describe, expect, it } from 'vitest'
import { channelTakesListPrice, hasAtMostTwoDecimals, inlinePrices, PANEL_PRICE_CHOICE, panelPriceIsFallback, panelPrices, panelPriceTarget, QUICK_EDIT_SYNC_KEYS, formatPriceField, initialCustomChannels, normalizePriceInput, offerMatchesPanel, panelFollowerColumns, panelFollowerWrites, quickEditOfferPrices, quickPriceColumns, quickInlinePrices, quickPriceBody, quickPriceChoiceHelp, quickPriceChoiceLabel, quickPriceTarget } from './quick-price-edit'

describe('panel price choice', () => {
  const product = { defaultListPrice: 300, defaultSalePrice: 250 }
  const variant = { id: 'v-1', version: 7, defaultListPrice: 200, defaultSalePrice: 180, platformStatuses: [{ connectionId: 'conn-1', offerId: 'offer-1', offerVersion: 2, listPrice: 500, salePrice: 450 }] }

  it("reads the variant's own panel price, else the card's", () => {
    expect(panelPrices(variant, product)).toEqual({ listPrice: 200, salePrice: 180 })
    expect(panelPrices({ defaultListPrice: null, defaultSalePrice: null }, product)).toEqual({ listPrice: 300, salePrice: 250 })
    expect(panelPrices({}, {})).toEqual({ listPrice: null, salePrice: null })
  })

  it('falls back to the first channel offer like the product editor when no panel price is saved', () => {
    const imported = { defaultListPrice: null, defaultSalePrice: null, listPrice: 899.9, salePrice: 649.9 }
    expect(panelPrices(imported, {})).toEqual({ listPrice: 899.9, salePrice: 649.9 })
    expect(panelPriceIsFallback(imported, {})).toBe(true)
    expect(panelPrices({ salePrice: 100 }, {})).toEqual({ listPrice: 100, salePrice: 100 })
    // A saved panel price always wins over the offer, and is not a fallback.
    expect(panelPrices({ ...imported, defaultSalePrice: 500 }, { defaultListPrice: 700 })).toEqual({ listPrice: 700, salePrice: 500 })
    expect(panelPriceIsFallback({ ...imported, defaultSalePrice: 500 }, {})).toBe(false)
    expect(panelPriceIsFallback(imported, { defaultSalePrice: 1 })).toBe(false)
    expect(panelPriceIsFallback({}, {})).toBe(false)
  })

  it('builds the matrix columns: panel first, then each connection; sale-only platforms have one field', () => {
    const columns = quickPriceColumns([
      { id: 'c-ty', displayName: 'Trendyol', platformCode: 'TRENDYOL' },
      { id: 'c-hb', displayName: 'Hepsiburada', platformCode: 'HEPSIBURADA' },
      { id: 'c-ptt', displayName: ' ', platformCode: 'PTTAVM' },
    ])
    expect(columns.map(column => [column.id, column.label, column.takesListPrice])).toEqual([
      [PANEL_PRICE_CHOICE, 'Panel Fiyatı', true], ['c-ty', 'Trendyol', true], ['c-hb', 'Hepsiburada', false], ['c-ptt', 'PTTAVM', false],
    ])
  })

  it('refreshes the products list and the product editor after a save', () => {
    expect(QUICK_EDIT_SYNC_KEYS).toEqual([['products'], ['product']])
  })

  it('shows the panel price for Panel and the channel offer for a channel', () => {
    expect(quickInlinePrices(PANEL_PRICE_CHOICE, variant, product)).toEqual({ listPrice: 200, salePrice: 180 })
    expect(quickInlinePrices('conn-1', variant, product)).toEqual({ listPrice: 500, salePrice: 450 })
    expect(quickInlinePrices('conn-2', variant, product)).toEqual({ listPrice: null, salePrice: null })
    expect(quickInlinePrices('', variant, product)).toEqual({ listPrice: null, salePrice: null })
  })

  it('says what each choice changes', () => {
    expect(quickPriceChoiceHelp(PANEL_PRICE_CHOICE)).toContain('pazaryerlerindeki fiyatlar değişmez')
    // With marketplaces following the panel price, the help names them and says their price changes too.
    expect(quickPriceChoiceHelp(PANEL_PRICE_CHOICE, null, ['Trendyol', 'N11'])).toContain('Özel Fiyat işaretli olmayan pazaryerleri (Trendyol, N11) ana fiyatla satılır')
    expect(quickPriceChoiceHelp('conn-1', 'Trendyol')).toBe('Yalnız Trendyol fiyatı değişir; panel ana fiyatı ve diğer kanalların fiyatı değişmez. Bu kanalda fiyatı olmayan varyantın ilk fiyatı oluşturulur.')
    expect(quickPriceChoiceLabel(PANEL_PRICE_CHOICE)).toBe('Panel Fiyatı')
    expect(quickPriceChoiceLabel('conn-1', 'Hepsiburada')).toBe('Hepsiburada')
  })

  it('writes the panel price at the loaded variant version, and refuses without one', () => {
    expect(panelPriceTarget(variant)).toEqual({ kind: 'panel', variantId: 'v-1', version: 7 })
    expect(panelPriceTarget({ id: 'v-2', version: null })).toMatchObject({ kind: 'blocked' })
  })

  it('checks the typed list and sale price as the server does', () => {
    expect(inlinePrices('200', '150')).toEqual({ ok: true, listPrice: 200, salePrice: 150 })
    expect(inlinePrices('', '150')).toEqual({ ok: true, listPrice: 150, salePrice: 150 })
    expect(inlinePrices('100', '150')).toEqual({ ok: false, message: 'Liste fiyatı satış fiyatından küçük olamaz.' })
    expect(inlinePrices('100', '0').ok).toBe(false)
    expect(inlinePrices('100', '').ok).toBe(false)
    expect(inlinePrices('100.005', '90').ok).toBe(false)
    expect(hasAtMostTwoDecimals(19.99)).toBe(true)
    expect(hasAtMostTwoDecimals(0.1 + 0.2)).toBe(true)
    expect(hasAtMostTwoDecimals(1.234)).toBe(false)
  })
})

describe('sale-price-only marketplaces', () => {
  it('hides the list price for Hepsiburada and PTT AVM only', () => {
    expect(channelTakesListPrice('HEPSIBURADA')).toBe(false)
    expect(channelTakesListPrice('pttavm')).toBe(false)
    expect(channelTakesListPrice('TRENDYOL')).toBe(true)
    expect(channelTakesListPrice('N11')).toBe(true)
    expect(channelTakesListPrice(undefined)).toBe(true)
  })
})

describe('quick price target', () => {
  const variant = { platformStatuses: [
    { connectionId: 'conn-1', offerId: 'offer-1', offerVersion: 4, listPrice: 500, salePrice: 450, offerStatus: 'PASSIVE' },
    { connectionId: 'conn-2', offerId: null, offerVersion: null },
  ] }

  it('updates the offer the chosen channel has, at the loaded version', () => {
    expect(quickPriceTarget(variant, 'conn-1', 2)).toMatchObject({ kind: 'update', offerId: 'offer-1', offerVersion: 4 })
  })

  it('refuses an offer whose version is missing instead of skipping silently', () => {
    expect(quickPriceTarget({ platformStatuses: [{ connectionId: 'conn-1', offerId: 'offer-1', offerVersion: null }] }, 'conn-1', 2)).toMatchObject({ kind: 'blocked' })
  })

  it('creates an offer on a chosen channel that has none, and never picks a channel itself', () => {
    expect(quickPriceTarget(variant, 'conn-2', 2)).toEqual({ kind: 'create', connectionId: 'conn-2' })
    expect(quickPriceTarget(variant, '', 2)).toEqual({ kind: 'blocked', reason: 'Önce Fiyat Kanalı seçin.' })
    expect(quickPriceTarget(variant, '', 0)).toMatchObject({ kind: 'blocked' })
  })

  it('refuses when the per-channel prices were not loaded, so no duplicate offer is created', () => {
    expect(quickPriceTarget({}, 'conn-2', 2)).toMatchObject({ kind: 'blocked' })
  })

  it("keeps the channel offer's own settings and status", () => {
    const target = quickPriceTarget(variant, 'conn-1', 2)
    if (target.kind !== 'update') throw new Error('expected update')
    const body = quickPriceBody(target, { id: 'v1', currency: null, vatRate: 20, vatInclusion: null, roundingMode: null, safetyStock: null }, { listPrice: 600, salePrice: 550 }, 'test')
    expect(body).toMatchObject({ connectionId: '', listPrice: 600, salePrice: 550, status: 'PASSIVE', vatRate: 20 })
  })
})

describe('bulk edit offer prices', () => {
  it('takes the typed list price as the sale price of a variant without one', () => {
    expect(quickEditOfferPrices({ listPrice: null, salePrice: null }, 799.9, null)).toEqual({ ok: true, listPrice: 799.9, salePrice: 799.9, saleDefaulted: true })
  })

  it('treats a stored sale price of zero as none', () => {
    expect(quickEditOfferPrices({ listPrice: 0, salePrice: 0 }, 120, null)).toEqual({ ok: true, listPrice: 120, salePrice: 120, saleDefaulted: true })
  })

  it('keeps the offer’s own sale price when only the list price changes', () => {
    expect(quickEditOfferPrices({ listPrice: 150, salePrice: 120 }, 160, null)).toEqual({ ok: true, listPrice: 160, salePrice: 120, saleDefaulted: false })
  })

  it('uses a typed sale price and keeps the list price at least as high', () => {
    expect(quickEditOfferPrices({ listPrice: null, salePrice: null }, null, 99)).toEqual({ ok: true, listPrice: 99, salePrice: 99, saleDefaulted: false })
    expect(quickEditOfferPrices({ listPrice: 150, salePrice: 120 }, null, 140)).toEqual({ ok: true, listPrice: 150, salePrice: 140, saleDefaulted: false })
  })

  it('never sends a sale price of zero or one above the list price', () => {
    expect(quickEditOfferPrices({ listPrice: null, salePrice: null }, 0, null).ok).toBe(false)
    expect(quickEditOfferPrices({ listPrice: 100, salePrice: 90 }, null, 0).ok).toBe(false)
    expect(quickEditOfferPrices({ listPrice: 100, salePrice: 90 }, 80, null)).toEqual({ ok: false, message: 'liste fiyatı satış fiyatından küçük olamaz.' })
  })
})

describe('Özel Fiyat: marketplaces that follow the panel price', () => {
  const columns = quickPriceColumns([
    { id: 'c-ty', displayName: 'Trendyol', platformCode: 'TRENDYOL' },
    { id: 'c-hb', displayName: 'Hepsiburada', platformCode: 'HEPSIBURADA' },
    { id: 'c-n11', displayName: 'N11', platformCode: 'N11' },
  ])
  const ty = columns[1]; const hb = columns[2]; const n11 = columns[3]
  const product = { defaultListPrice: 300, defaultSalePrice: 250 }
  const status = (connectionId: string, listPrice: number | null, salePrice: number | null, offerVersion: number | null = 3) => ({ connectionId, offerId: `o-${connectionId}`, offerVersion, listPrice, salePrice })

  it('compares an offer with the panel price; a sale-only marketplace by its sale price', () => {
    expect(offerMatchesPanel({ listPrice: 300, salePrice: 250 }, { listPrice: 300, salePrice: 250 }, true)).toBe(true)
    expect(offerMatchesPanel({ listPrice: 310, salePrice: 250 }, { listPrice: 300, salePrice: 250 }, true)).toBe(false)
    expect(offerMatchesPanel({ listPrice: 250, salePrice: 250 }, { listPrice: 300, salePrice: 250 }, false)).toBe(true)
    // An unknown panel price is never taken as equal.
    expect(offerMatchesPanel({ listPrice: 300, salePrice: 250 }, { listPrice: null, salePrice: null }, true)).toBe(false)
  })

  it('starts ticked only for a marketplace with an offer at another price', () => {
    const items = [
      { product, variant: { platformStatuses: [status('c-ty', 300, 250), status('c-hb', 250, 250), status('c-n11', 400, 350)] } },
      { product, variant: { platformStatuses: [status('c-ty', 300, 250)] } },
    ]
    expect(initialCustomChannels(items, columns)).toEqual({ 'c-ty': false, 'c-hb': false, 'c-n11': true })
    // No offer anywhere: every marketplace follows the panel, as the editor shows a new choice.
    expect(initialCustomChannels([{ product, variant: { platformStatuses: [] } }], columns)).toEqual({ 'c-ty': false, 'c-hb': false, 'c-n11': false })
    // One differing variant is enough to keep that marketplace's own prices.
    expect(initialCustomChannels([...items, { product, variant: { defaultSalePrice: 200, platformStatuses: [status('c-ty', 300, 250)] } }], columns)['c-ty']).toBe(true)
  })

  it('lists the unticked marketplaces, never the panel column', () => {
    expect(panelFollowerColumns(columns, { 'c-n11': true }).map(column => column.id)).toEqual(['c-ty', 'c-hb'])
    expect(panelFollowerColumns(columns, { 'c-ty': true, 'c-hb': true, 'c-n11': true })).toEqual([])
  })

  it('writes a panel save to each following offer, list = sale on a sale-only marketplace, nothing new created', () => {
    const variant = { platformStatuses: [status('c-ty', 300, 250, 4), status('c-hb', 250, 250, 9)] }
    expect(panelFollowerWrites(variant, [ty, hb, n11], { listPrice: 320, salePrice: 280 })).toEqual([
      expect.objectContaining({ kind: 'update', column: ty, offerId: 'o-c-ty', offerVersion: 4, listPrice: 320, salePrice: 280 }),
      expect.objectContaining({ kind: 'update', column: hb, offerId: 'o-c-hb', offerVersion: 9, listPrice: 280, salePrice: 280 }),
    ])
    // An offer already at the price is left alone; no follower means no marketplace write at all.
    expect(panelFollowerWrites(variant, [ty, hb], { listPrice: 300, salePrice: 250 })).toEqual([])
    expect(panelFollowerWrites(variant, [], { listPrice: 1, salePrice: 1 })).toEqual([])
  })

  it('blocks a follower write it cannot place safely', () => {
    expect(panelFollowerWrites({ platformStatuses: [status('c-ty', 300, 250, null)] }, [ty], { listPrice: 320, salePrice: 280 })).toEqual([{ kind: 'blocked', column: ty, reason: 'Fiyat sürümü okunamadı; listeyi yenileyip tekrar deneyin.' }])
    expect(panelFollowerWrites({}, [ty], { listPrice: 320, salePrice: 280 })).toMatchObject([{ kind: 'blocked', column: ty }])
  })

  it('formats prices outside editing and reads a Turkish decimal comma', () => {
    expect(formatPriceField('1249.9')).toBe('1.249,90')
    expect(formatPriceField('')).toBe('')
    expect(normalizePriceInput('1.249,90')).toBe('1249.90')
    expect(normalizePriceInput('249,5 ₺')).toBe('249.5')
    expect(normalizePriceInput('249.5')).toBe('249.5')
  })
})
