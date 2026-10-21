import { describe, expect, it } from 'vitest'
import { quickPriceTarget } from './quick-price-edit'

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
