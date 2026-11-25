import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ calls: [] as Array<{ method: string; path: string; ifMatch: string | null; body: Record<string, unknown> | null }>, refuse: null as null | number, refusePath: null as null | string }))
vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      api.calls.push({ method: init?.method ?? 'GET', path, ifMatch: new Headers(init?.headers).get('If-Match'), body: init?.body ? JSON.parse(String(init.body)) : null })
      if (api.refuse && (!api.refusePath || api.refusePath === path)) throw new original.ApiRequestError('Sürüm değişti.', api.refuse)
      return {}
    })
  }
})

import { EMPTY_EDITOR_FORM } from './editor-form'
import { connectionsWithWrittenOffers, offerConnectionIdsFor, offerWriteFailureWarnings, runOfferWrites, runPanelFollowChoiceWrites, type OfferWritesInput } from './submit-offer-writes'
import type { Product, VariantPlatformStatus } from '../shared'
import type { VariantDraft } from './editor-types'
import { resetSeededFields, seedChannelFields, seedField } from './seeded-fields'

const status = (connectionId: string, extra: Partial<VariantPlatformStatus> = {}) => ({ connectionId, platformCode: 'TRENDYOL', offerId: `o-${connectionId}`, offerVersion: 3, listPrice: 100, salePrice: 90, currency: 'TRY', vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 0, followsPanelPrice: true, offerStatus: 'ACTIVE', ...extra }) as VariantPlatformStatus
const row = (key: string, extra: Partial<VariantDraft> = {}): VariantDraft => ({ key, optionSignature: key, options: {}, attributeValueIds: {}, sku: `SKU-${key}`, barcode: '', stock: 0, salePrice: 90, listPrice: 100, costPrice: null, mediaRefs: [], ...extra })
const product = (ids: string[]) => ({ id: 'p1', variants: ids.map(id => ({ id, sku: `SKU-${id}` })) }) as unknown as Product
const connections = [{ id: 'ty', platformCode: 'TRENDYOL' }, { id: 'n11', platformCode: 'N11' }]
const input = (extra: Partial<OfferWritesInput>): OfferWritesInput => ({
  product: product(['v1']), rows: [row('v1', { platformStatuses: [status('ty')] })], connectionIds: ['ty'], connections, form: { ...EMPTY_EDITOR_FORM, vatRate: '20' }, platformUpdate: null,
  channelPricing: {}, variantChannelPricing: {}, channelPriceDraft: () => ({ listPrice: '', salePrice: '' }), channelPriceEdited: () => false, connectionPlatformName: () => 'Trendyol', showProgress: () => undefined, ...extra
})

beforeEach(() => { api.calls.length = 0; api.refuse = null; api.refusePath = null })

describe('runOfferWrites', () => {
  it('skips an offer the write would leave as it is', async () => {
    await runOfferWrites(input({}))
    expect(api.calls).toEqual([])
  })
  it('writes a changed offer at the version the form loaded (PATCH + If-Match)', async () => {
    await runOfferWrites(input({ rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty')] })] }))
    expect(api.calls).toEqual([{ method: 'PATCH', path: '/channel-offers/o-ty', ifMatch: '"v3"', body: expect.objectContaining({ connectionId: 'ty', variantId: 'v1', listPrice: 100, salePrice: 80, followsPanelPrice: true, vatRate: 20, status: 'ACTIVE' }) }])
  })
  it('writes the prices staged from Toplu Düzenle as one PATCH per variant, each at its loaded version and as its own Özel Fiyat', async () => {
    await runOfferWrites(input({ product: product(['v1', 'v2']), rows: [row('v1', { platformStatuses: [status('ty')] }), row('v2', { platformStatuses: [status('ty', { offerId: 'o-v2', offerVersion: 5 })] })], channelPricing: { ty: { listPrice: '0', salePrice: '0' } }, variantChannelPricing: { 'v1:ty': { listPrice: 180, salePrice: 140 }, 'v2:ty': { listPrice: 200, salePrice: 160 } } }))
    expect(api.calls).toEqual([
      { method: 'PATCH', path: '/channel-offers/o-ty', ifMatch: '"v3"', body: expect.objectContaining({ variantId: 'v1', listPrice: 180, salePrice: 140, followsPanelPrice: false }) },
      { method: 'PATCH', path: '/channel-offers/o-v2', ifMatch: '"v5"', body: expect.objectContaining({ variantId: 'v2', listPrice: 200, salePrice: 160, followsPanelPrice: false }) }
    ])
  })
  it('creates a missing offer (POST) and writes a marketplace’s own card price', async () => {
    await runOfferWrites(input({ rows: [row('v1')], channelPricing: { ty: { listPrice: '130', salePrice: '111' } }, channelPriceDraft: () => ({ listPrice: '130', salePrice: '111' }), channelPriceEdited: () => true }))
    expect(api.calls).toEqual([{ method: 'POST', path: '/channel-offers', ifMatch: null, body: expect.objectContaining({ listPrice: 130, salePrice: 111, followsPanelPrice: false }) }])
  })
  // O1 (scan 23 Nov 2026): a marketplace where only some variants have Özel Fiyat on opens with its switch on; Kaydet
  // without touching anything turned Özel Fiyat on for every variant. Untouched, the change set is empty.
  const mixedRows = [row('v1', { platformStatuses: [status('ty', { offerId: 'o-v1', listPrice: 130, salePrice: 120, followsPanelPrice: false })] }), row('v2', { platformStatuses: [status('ty', { offerId: 'o-v2', followsPanelPrice: true })] })]
  const mixed = { product: product(['v1', 'v2']), rows: mixedRows, channelPricing: { ty: { listPrice: '130', salePrice: '120' } }, channelPriceDraft: () => ({ listPrice: '130', salePrice: '120' }) }
  it('sends nothing for a mixed marketplace saved untouched', async () => {
    await runOfferWrites(input({ ...mixed, choiceUntouched: () => true }))
    expect(api.calls).toEqual([])
  })
  it('a following variant of an untouched mixed marketplace gets the panel price and keeps following it', async () => {
    await runOfferWrites(input({ ...mixed, rows: [mixedRows[0], row('v2', { salePrice: 85, platformStatuses: mixedRows[1].platformStatuses })], choiceUntouched: () => true }))
    expect(api.calls).toEqual([{ method: 'PATCH', path: '/channel-offers/o-v2', ifMatch: '"v3"', body: expect.objectContaining({ variantId: 'v2', listPrice: 100, salePrice: 85, followsPanelPrice: true }) }])
  })
  // B6 (pinned as is; the coordinator decides): a following variant whose offer drifted from the panel price (offer 160,
  // panel 150) gets the panel price on an untouched save, by the owner's rule "Özel Fiyat açık değilse panel".
  it('pins: an untouched mixed save writes the panel price to a following variant whose offer drifted (160 -> 150)', async () => {
    const drifted = row('v2', { salePrice: 150, listPrice: 150, platformStatuses: [status('ty', { offerId: 'o-v2', listPrice: 160, salePrice: 160, followsPanelPrice: true })] })
    await runOfferWrites(input({ ...mixed, rows: [mixedRows[0], drifted], choiceUntouched: () => true }))
    expect(api.calls).toEqual([{ method: 'PATCH', path: '/channel-offers/o-v2', ifMatch: '"v3"', body: expect.objectContaining({ variantId: 'v2', listPrice: 150, salePrice: 150, followsPanelPrice: true }) }])
  })
  it('a mixed marketplace whose card price the owner typed gives every variant that price as its own', async () => {
    await runOfferWrites(input({ ...mixed, channelPriceDraft: () => ({ listPrice: '140', salePrice: '125' }), channelPriceEdited: () => true, choiceUntouched: () => true }))
    expect(api.calls.map(call => [call.path, call.body?.salePrice, call.body?.followsPanelPrice])).toEqual([['/channel-offers/o-v1', 125, false], ['/channel-offers/o-v2', 125, false]])
  })

  // B1: the marketplace card and the per-variant price table are Turkish text boxes; "1.250" goes as 1250.
  it('writes "1.250" typed in a marketplace card price as 1250', async () => {
    await runOfferWrites(input({ channelPricing: { ty: { listPrice: '1.250', salePrice: '1.200,50' } }, channelPriceDraft: () => ({ listPrice: '1.250', salePrice: '1.200,50' }), channelPriceEdited: () => true }))
    expect(api.calls).toEqual([{ method: 'PATCH', path: '/channel-offers/o-ty', ifMatch: '"v3"', body: expect.objectContaining({ listPrice: 1250, salePrice: 1200.5, followsPanelPrice: false }) }])
  })
  it('writes "1.250" staged in the variant price table as 1250', async () => {
    await runOfferWrites(input({ channelPricing: { ty: { listPrice: '0', salePrice: '0' } }, variantChannelPricing: { 'v1:ty': { listPrice: 1250, salePrice: 1250 } } }))
    expect(api.calls).toEqual([{ method: 'PATCH', path: '/channel-offers/o-ty', ifMatch: '"v3"', body: expect.objectContaining({ listPrice: 1250, salePrice: 1250 }) }])
  })

  // O2: a marketplace card loaded with a stored 99.999 sends it as it is while untouched (never NaN, never skipped).
  it('sends an untouched card loaded with a four-decimal price as stored', async () => {
    resetSeededFields()
    seedChannelFields({ ty: { listPrice: '99,999', salePrice: '99,999' } })
    await runOfferWrites(input({ rows: [row('v1')], channelPricing: { ty: { listPrice: '99,999', salePrice: '99,999' } }, channelPriceDraft: () => ({ listPrice: '99,999', salePrice: '99,999' }) }))
    expect(api.calls).toEqual([{ method: 'POST', path: '/channel-offers', ifMatch: null, body: expect.objectContaining({ listPrice: 99.999, salePrice: 99.999 }) }])
    resetSeededFields()
  })

  // Review 23 Nov (iv): Hepsiburada takes a sale price only and stores list = sale (90 / 90); the panel list price (100)
  // made the untouched offer "changed" and a Platformlarda Güncelle or Kaydet wrote it again.
  it('writes nothing for an untouched Hepsiburada offer whose stored list price is its sale price', async () => {
    const hb = [{ id: 'hb', platformCode: 'HEPSIBURADA' }]
    await runOfferWrites(input({ connections: hb, connectionIds: ['hb'], rows: [row('v1', { platformStatuses: [status('hb', { platformCode: 'HEPSIBURADA', listPrice: 90, salePrice: 90 })] })] }))
    await runOfferWrites(input({ connections: hb, connectionIds: ['hb'], platformUpdate: { connectionIds: ['hb'], fields: ['PRICE'] }, rows: [row('v1', { platformStatuses: [status('hb', { platformCode: 'HEPSIBURADA', listPrice: 90, salePrice: 90 })] })] }))
    expect(api.calls).toEqual([])
    // A sale price change still goes.
    await runOfferWrites(input({ connections: hb, connectionIds: ['hb'], rows: [row('v1', { salePrice: 85, platformStatuses: [status('hb', { platformCode: 'HEPSIBURADA', listPrice: 90, salePrice: 90 })] })] }))
    expect(api.calls.map(call => [call.method, call.body?.salePrice])).toEqual([['PATCH', 85]])
  })

  // D-2 (scan 25 Nov 2026): every offer was written ACTIVE with the first variant's safety stock.
  it('keeps a closed offer closed and each offer’s own safety stock while Güvenlik Stoğu is untouched', async () => {
    resetSeededFields()
    seedField('safetyStock', '2')
    await runOfferWrites(input({ form: { ...EMPTY_EDITOR_FORM, vatRate: '20', safetyStock: '2' }, rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty', { offerStatus: 'PASSIVE', safetyStock: 5 })] })] }))
    expect(api.calls).toEqual([{ method: 'PATCH', path: '/channel-offers/o-ty', ifMatch: '"v3"', body: expect.objectContaining({ status: 'PASSIVE', safetyStock: 5, salePrice: 80 }) }])
    resetSeededFields()
  })
  it('writes nothing for an untouched closed offer whose price is unchanged', async () => {
    resetSeededFields()
    seedField('safetyStock', '0')
    await runOfferWrites(input({ rows: [row('v1', { platformStatuses: [status('ty', { offerStatus: 'PASSIVE', safetyStock: 4 })] })] }))
    expect(api.calls).toEqual([])
    resetSeededFields()
  })
  it('writes the typed safety stock to every offer once Güvenlik Stoğu was changed', async () => {
    resetSeededFields()
    seedField('safetyStock', '2')
    await runOfferWrites(input({ form: { ...EMPTY_EDITOR_FORM, vatRate: '20', safetyStock: '7' }, rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty', { safetyStock: 5 })] })] }))
    expect(api.calls).toEqual([{ method: 'PATCH', path: '/channel-offers/o-ty', ifMatch: '"v3"', body: expect.objectContaining({ status: 'ACTIVE', safetyStock: 7 }) }])
    resetSeededFields()
  })

  it('says which variants had no price to write', async () => {
    const result = await runOfferWrites(input({ rows: [row('v1', { salePrice: 0, listPrice: 0 })] }))
    expect(api.calls).toEqual([])
    expect(result.unpricedWarning).toContain('SKU-v1')
  })
  it('returns a refused write with the marketplace’s name instead of throwing', async () => {
    api.refuse = 412
    const result = await runOfferWrites(input({ rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty')] })] }))
    expect(result.failures).toEqual([{ sku: 'SKU-v1', connectionId: 'ty', message: 'SKU-v1: Trendyol fiyatı bu form açıkken başka bir işlemle değişti; fiyat yazılmadı. Sayfayı yenileyip tekrar deneyin.' }])
  })
  it('returns an offer without its version as that marketplace’s failure', async () => {
    const result = await runOfferWrites(input({ rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty', { offerVersion: null })] })] }))
    expect(api.calls).toEqual([])
    expect(result.failures).toEqual([{ sku: 'SKU-v1', connectionId: 'ty', message: 'SKU-v1: platform fiyat sürümü eksik; fiyat güncellenmedi.' }])
  })
  // A-6 (scan 25 Nov 2026): one refused offer (412) stopped every later write and every send.
  it('writes the other marketplaces when one refuses its offer (412) and names only that one', async () => {
    const three = [{ id: 'ty', platformCode: 'TRENDYOL' }, { id: 'n11', platformCode: 'N11' }, { id: 'pz', platformCode: 'PAZARAMA' }]
    const names: Record<string, string> = { ty: 'Trendyol', n11: 'N11', pz: 'Pazarama' }
    api.refuse = 412
    api.refusePath = '/channel-offers/o-n11'
    const result = await runOfferWrites(input({ connections: three, connectionIds: ['ty', 'n11', 'pz'], connectionPlatformName: id => names[id], rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty'), status('n11', { platformCode: 'N11' }), status('pz', { platformCode: 'PAZARAMA' })] })] }))
    expect(api.calls.map(call => [call.method, call.path, call.ifMatch, call.body?.salePrice])).toEqual([['PATCH', '/channel-offers/o-ty', '"v3"', 80], ['PATCH', '/channel-offers/o-n11', '"v3"', 80], ['PATCH', '/channel-offers/o-pz', '"v3"', 80]])
    expect(result.failures.map(failure => failure.connectionId)).toEqual(['n11'])
    expect(connectionsWithWrittenOffers(['ty', 'n11', 'pz'], result.failures)).toEqual(['ty', 'pz'])
    expect(offerWriteFailureWarnings(result.failures, id => names[id])).toEqual(['N11: fiyat yazılamadığı için bu platforma gönderilmedi. SKU-v1: N11 fiyatı bu form açıkken başka bir işlemle değişti; fiyat yazılmadı. Sayfayı yenileyip tekrar deneyin.'])
  })
})

describe('offerConnectionIdsFor', () => {
  it('keeps every target on a save, and on a platform update only those whose send carries a price or the stock', () => {
    expect(offerConnectionIdsFor(null, ['ty', 'n11'], connections)).toEqual(['ty', 'n11'])
    expect(offerConnectionIdsFor({ connectionIds: ['ty', 'n11'], fields: ['IMAGES'] }, ['ty', 'n11'], connections)).toEqual([])
  })
})

describe('runPanelFollowChoiceWrites', () => {
  it('stores a switched "Özel Fiyat" choice on the card’s offers there and returns the new choice', async () => {
    const custom = await runPanelFollowChoiceWrites({ connections, channelPricing: { ty: { listPrice: '1', salePrice: '1' } }, initialCustomChannelIds: new Set(), rows: [row('v1', { platformStatuses: [status('ty')] })] })
    expect(api.calls).toEqual([{ method: 'PUT', path: '/channel-offers/panel-price-follow', ifMatch: null, body: { connectionId: 'ty', variantIds: ['v1'], followsPanelPrice: false } }])
    expect([...custom]).toEqual(['ty'])
  })
})
