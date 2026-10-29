import { describe, expect, it } from 'vitest'
import { quickEditOfferPrices, quickPriceTarget } from './quick-price-edit'

describe('quick price target', () => {
  it('updates the existing offer at the loaded version', () => {
    expect(quickPriceTarget({ offerId: 'offer-1', offerVersion: 4 }, '', 2)).toEqual({ kind: 'update', offerId: 'offer-1', offerVersion: 4 })
  })

  it('refuses an offer whose version is missing instead of skipping silently', () => {
    expect(quickPriceTarget({ offerId: 'offer-1', offerVersion: null }, 'conn-1', 2)).toMatchObject({ kind: 'blocked' })
  })

  it('creates a first offer only on the channel the person chose', () => {
    expect(quickPriceTarget({ offerId: null, offerVersion: null }, '', 2)).toEqual({ kind: 'blocked', reason: 'İlk fiyat için önce Fiyat Kanalı seçin.' })
    expect(quickPriceTarget({ offerId: null, offerVersion: null }, 'conn-2', 2)).toEqual({ kind: 'create', connectionId: 'conn-2' })
    expect(quickPriceTarget({ offerId: null, offerVersion: null }, '', 0)).toMatchObject({ kind: 'blocked' })
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
