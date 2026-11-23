import { describe, expect, it } from 'vitest'
import { priceJumps } from '../price-jump'
import { editorPriceChanges, type EditorPriceJumpInput } from './editor-price-jumps'
import type { Product, VariantPlatformStatus } from '../shared'
import type { VariantDraft } from './editor-types'

const offer = (extra: Partial<VariantPlatformStatus> = {}) => ({ connectionId: 'ty', platformCode: 'TRENDYOL', offerId: 'o1', offerVersion: 1, listPrice: 1300, salePrice: 1250, followsPanelPrice: false, ...extra }) as VariantPlatformStatus
const row = (key: string, extra: Partial<VariantDraft> = {}): VariantDraft => ({ key, optionSignature: key, options: {}, attributeValueIds: {}, sku: `SKU-${key}`, barcode: '', stock: 0, salePrice: 1250, listPrice: 1300, costPrice: null, mediaRefs: [], platformStatuses: [offer()], ...extra })
const saved = { id: 'p1', variants: [{ id: 'v1', sku: 'SKU-v1', defaultSalePrice: 1250, defaultListPrice: 1300 }] } as unknown as Product
const input = (extra: Partial<EditorPriceJumpInput>): EditorPriceJumpInput => ({ rows: [row('v1')], saved, channelPricing: {}, variantChannelPricing: {}, channelPriceDraft: () => ({ listPrice: '', salePrice: '' }), channelPriceEdited: () => false, ...extra })
const lines = (extra: Partial<EditorPriceJumpInput>) => priceJumps(editorPriceChanges(input(extra))).lines

describe('the editor save price-jump confirmation', () => {
  it('warns for a panel price typed 1000 times smaller in the variant table (or handed over by Toplu Düzenle)', () => {
    expect(lines({ rows: [row('v1', { salePrice: 1.25, listPrice: 1.3 })] })).toEqual(['1 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 1.250,00 → 1,25'])
  })
  it('warns for a marketplace price staged from Toplu Düzenle', () => {
    expect(lines({ variantChannelPricing: { 'v1:ty': { listPrice: '1,3', salePrice: '1,25' } } })).toEqual(['1 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 1.250,00 → 1,25'])
  })
  it('warns for a marketplace card price the person typed, going to the variant\'s offer', () => {
    expect(lines({ channelPricing: { ty: { listPrice: '5000', salePrice: '4000' } }, channelPriceDraft: () => ({ listPrice: '5000', salePrice: '4000' }), channelPriceEdited: () => true })).toEqual(['1 Varyantın Fiyatı Üç Katından Fazla Artıyor: 1.250,00 → 4.000,00'])
  })
  it('stays quiet for an ordinary change and an untouched save', () => {
    expect(lines({ rows: [row('v1', { salePrice: 1199 })] })).toEqual([])
    expect(lines({})).toEqual([])
  })
  // A new variant and a first offer (POST) have no price before: they are set against the panel price.
  it('sets a new variant against the card\'s panel price, and stays quiet without one', () => {
    const card = { ...saved, defaultSalePrice: 1250, defaultListPrice: 1300 } as Product
    expect(lines({ saved: card, rows: [row('new-1', { salePrice: 1.25, listPrice: 1.3, platformStatuses: [] })] })).toEqual(['1 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 1.250,00 → 1,25'])
    expect(lines({ rows: [row('new-1', { salePrice: 1.25, listPrice: 1.3, platformStatuses: [] })] })).toEqual([])
  })
  it('sets a first offer on a marketplace (POST) against the variant\'s panel price', () => {
    expect(lines({ rows: [row('v1', { platformStatuses: [] })], variantChannelPricing: { 'v1:n11': { listPrice: '13', salePrice: '12,5' } } })).toEqual(['1 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 1.250,00 → 12,50'])
  })
})
