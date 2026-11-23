import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ calls: [] as Array<{ method: string; path: string; ifMatch: string | null; body: Record<string, unknown> | null }>, refuse: null as null | number }))
vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      api.calls.push({ method: init?.method ?? 'GET', path, ifMatch: new Headers(init?.headers).get('If-Match'), body: init?.body ? JSON.parse(String(init.body)) : null })
      if (api.refuse) throw new original.ApiRequestError('Sürüm değişti.', api.refuse)
      return {}
    })
  }
})

import { EMPTY_EDITOR_FORM } from './editor-form'
import { offerConnectionIdsFor, runOfferWrites, runPanelFollowChoiceWrites, type OfferWritesInput } from './submit-offer-writes'
import type { Product, VariantPlatformStatus } from '../shared'
import type { VariantDraft } from './editor-types'

const status = (connectionId: string, extra: Partial<VariantPlatformStatus> = {}) => ({ connectionId, platformCode: 'TRENDYOL', offerId: `o-${connectionId}`, offerVersion: 3, listPrice: 100, salePrice: 90, currency: 'TRY', vatRate: 20, vatInclusion: 'INCLUDED', roundingMode: 'HALF_EVEN', safetyStock: 0, followsPanelPrice: true, offerStatus: 'ACTIVE', ...extra }) as VariantPlatformStatus
const row = (key: string, extra: Partial<VariantDraft> = {}): VariantDraft => ({ key, optionSignature: key, options: {}, attributeValueIds: {}, sku: `SKU-${key}`, barcode: '', stock: 0, salePrice: 90, listPrice: 100, costPrice: null, mediaRefs: [], ...extra })
const product = (ids: string[]) => ({ id: 'p1', variants: ids.map(id => ({ id, sku: `SKU-${id}` })) }) as unknown as Product
const connections = [{ id: 'ty', platformCode: 'TRENDYOL' }, { id: 'n11', platformCode: 'N11' }]
const input = (extra: Partial<OfferWritesInput>): OfferWritesInput => ({
  product: product(['v1']), rows: [row('v1', { platformStatuses: [status('ty')] })], connectionIds: ['ty'], connections, form: { ...EMPTY_EDITOR_FORM, vatRate: '20' }, platformUpdate: null,
  channelPricing: {}, variantChannelPricing: {}, channelPriceDraft: () => ({ listPrice: '', salePrice: '' }), channelPriceEdited: () => false, connectionPlatformName: () => 'Trendyol', showProgress: () => undefined, ...extra
})

beforeEach(() => { api.calls.length = 0; api.refuse = null })

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
    await runOfferWrites(input({ product: product(['v1', 'v2']), rows: [row('v1', { platformStatuses: [status('ty')] }), row('v2', { platformStatuses: [status('ty', { offerId: 'o-v2', offerVersion: 5 })] })], channelPricing: { ty: { listPrice: '0', salePrice: '0' } }, variantChannelPricing: { 'v1:ty': { listPrice: '180', salePrice: '140' }, 'v2:ty': { listPrice: '200', salePrice: '160' } } }))
    expect(api.calls).toEqual([
      { method: 'PATCH', path: '/channel-offers/o-ty', ifMatch: '"v3"', body: expect.objectContaining({ variantId: 'v1', listPrice: 180, salePrice: 140, followsPanelPrice: false }) },
      { method: 'PATCH', path: '/channel-offers/o-v2', ifMatch: '"v5"', body: expect.objectContaining({ variantId: 'v2', listPrice: 200, salePrice: 160, followsPanelPrice: false }) }
    ])
  })
  it('creates a missing offer (POST) and writes a marketplace’s own card price', async () => {
    await runOfferWrites(input({ rows: [row('v1')], channelPricing: { ty: { listPrice: '130', salePrice: '111' } }, channelPriceDraft: () => ({ listPrice: '130', salePrice: '111' }), channelPriceEdited: () => true }))
    expect(api.calls).toEqual([{ method: 'POST', path: '/channel-offers', ifMatch: null, body: expect.objectContaining({ listPrice: 130, salePrice: 111, followsPanelPrice: false }) }])
  })
  it('says which variants had no price to write', async () => {
    const result = await runOfferWrites(input({ rows: [row('v1', { salePrice: 0, listPrice: 0 })] }))
    expect(api.calls).toEqual([])
    expect(result.unpricedWarning).toContain('SKU-v1')
  })
  it('refuses with the marketplace’s name when the price changed meanwhile', async () => {
    api.refuse = 412
    await expect(runOfferWrites(input({ rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty')] })] })))
      .rejects.toThrow('SKU-v1: Trendyol fiyatı bu form açıkken başka bir işlemle değişti; fiyat yazılmadı. Sayfayı yenileyip tekrar deneyin.')
  })
  it('stops on an offer without its version', async () => {
    await expect(runOfferWrites(input({ rows: [row('v1', { salePrice: 80, platformStatuses: [status('ty', { offerVersion: null })] })] }))).rejects.toThrow('SKU-v1: platform fiyat sürümü eksik; fiyat güncellenmedi.')
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
