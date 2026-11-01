import { describe, expect, it } from 'vitest'
import type { VariantPlatformStatus } from '../shared'
import { channelOfferPostKey, rowsWithSavedOffer } from './marketplace-channel-pricing'
import { mediaFilesLeftAfterFailedSave } from './product-media-save'
import { publishCardVariantLinks, publishCardVariantPresence, withLatestVariantLinks } from './publish-card-variants'

const status = (connectionId: string, extra: Partial<VariantPlatformStatus> = {}): VariantPlatformStatus => ({ platform: 'Trendyol', platformCode: 'TRENDYOL', isLinked: false, connectionId, ...extra })

describe('publish card links after a status check', () => {
  it('reads the links of the latest product, keeping the row offers, so a found card turns live without a reseed', () => {
    // The form was filled before "Mağazada ürünü ara": no links. The refetched product links both variants.
    const rows = [
      { key: 'v1', sku: 'A', platformStatuses: [status('ty', { offerId: 'o1', offerVersion: 4 })] },
      { key: 'v2', sku: 'B', platformStatuses: [status('ty', { offerId: 'o2', offerVersion: 7 })] }
    ]
    const latest = [
      { id: 'v1', platformStatuses: [status('ty', { isLinked: true, isMatched: true, offerId: 'o1', offerVersion: 3 })] },
      { id: 'v2', platformStatuses: [status('ty', { isLinked: true, isMatched: true, offerId: 'o2', offerVersion: 6 })] }
    ]
    expect(publishCardVariantPresence(publishCardVariantLinks(rows, 'ty')).allLinked).toBe(false)
    const merged = withLatestVariantLinks(rows, latest)
    expect(publishCardVariantPresence(publishCardVariantLinks(merged, 'ty')).allLinked).toBe(true)
    // The offer versions the form saved stay the form's own.
    expect(merged.map(row => row.platformStatuses[0].offerVersion)).toEqual([4, 7])
    expect(merged.map(row => row.sku)).toEqual(['A', 'B'])
  })

  it('takes a link that went missing, adds a connection the form did not have, and leaves a new row as it is', () => {
    const rows = [
      { key: 'v1', platformStatuses: [status('ty', { isLinked: true, isMatched: true })] },
      { key: 'new-row', platformStatuses: [] as VariantPlatformStatus[] }
    ]
    const merged = withLatestVariantLinks(rows, [{ id: 'v1', platformStatuses: [status('ty', { isMissing: true }), status('hb', { isLinked: true, isMatched: true })] }])
    expect(publishCardVariantLinks(merged, 'ty')).toEqual({ total: 2, linked: 0, missing: 1 })
    expect(publishCardVariantLinks(merged, 'hb')).toEqual({ total: 2, linked: 1, missing: 0 })
    expect(merged[1]).toBe(rows[1])
  })

  it('keeps the rows when the latest product carries no statuses (a save answer without them)', () => {
    const rows = [{ key: 'v1', platformStatuses: [status('ty', { isMatched: true })] }]
    expect(withLatestVariantLinks(rows, [{ id: 'v1' }])).toEqual(rows)
    expect(withLatestVariantLinks(rows, undefined)).toEqual(rows)
  })
})

describe('a save that stopped after the product was written', () => {
  it('drops only the files it uploaded from the queue', () => {
    const files = [{ id: 'f1' }, { id: 'f2' }, { id: 'f3' }]
    expect(mediaFilesLeftAfterFailedSave(files, new Set(['f1']))).toEqual([{ id: 'f2' }, { id: 'f3' }])
    expect(mediaFilesLeftAfterFailedSave(files, new Set())).toEqual(files)
  })
})

describe('variant platform price table', () => {
  it('writes each saved offer into its row at once, so a later failure does not lose its new version', () => {
    const rows = [
      { key: 'v1', platformStatuses: [status('ty', { offerId: 'o1', offerVersion: 2 }), status('hb', { offerId: 'o3', offerVersion: 5 })] },
      { key: 'v2', platformStatuses: [status('ty', { offerId: 'o2', offerVersion: 9 })] }
    ]
    const next = rowsWithSavedOffer(rows, 'v1', 'ty', { id: 'o1', version: 3, listPrice: 200, salePrice: 150, currency: 'TRY' })
    expect(next[0].platformStatuses?.[0]).toMatchObject({ offerId: 'o1', offerVersion: 3, listPrice: 200, salePrice: 150 })
    expect(next[0].platformStatuses?.[1]).toBe(rows[0].platformStatuses[1])
    expect(next[1]).toBe(rows[1])
  })

  it('reuses a new offer POST key per variant and marketplace for the same price, and takes a new one for another price', () => {
    const store = new Map<string, { key: string; body: string }>()
    let made = 0
    const fresh = () => `key-${++made}`
    const first = channelOfferPostKey(store, 'v1:ty', '{"salePrice":100}', fresh)
    expect(channelOfferPostKey(store, 'v1:ty', '{"salePrice":100}', fresh)).toBe(first)
    expect(channelOfferPostKey(store, 'v2:ty', '{"salePrice":100}', fresh)).not.toBe(first)
    expect(channelOfferPostKey(store, 'v1:ty', '{"salePrice":120}', fresh)).not.toBe(first)
  })
})
