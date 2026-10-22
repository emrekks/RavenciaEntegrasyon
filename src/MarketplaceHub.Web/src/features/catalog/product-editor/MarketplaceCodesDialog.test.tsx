import { describe, expect, it } from 'vitest'
import { changedMarketplaceCodes, marketplaceCodeColumnLabel, marketplaceCodeFor, marketplaceCodeKey, marketplaceCodeState, type MarketplaceCodes } from './MarketplaceCodesDialog'

describe('marketplace code state', () => {
  const link = { connectionId: 'hb-1', externalId: 'HBCV1', externalBarcode: null, externalSku: null }
  it('calls a code linked only once an import read it, and never while the marketplace no longer knows it', () => {
    expect(marketplaceCodeState({ typed: 'HBCV1', saved: 'HBCV1', link: { ...link, lastSeenAt: '2026-10-22T10:00:00Z' } })).toBe('linked')
    expect(marketplaceCodeState({ typed: 'HBCV1', saved: 'HBCV1', link })).toBe('unverified')
    expect(marketplaceCodeState({ typed: 'HBCV1', saved: 'HBCV1', link: { ...link, lastSeenAt: '2026-10-22T10:00:00Z', healthStatus: 'NOT_FOUND' } })).toBe('broken')
    expect(marketplaceCodeState({ typed: 'HBCV1', saved: 'HBCV1', link: { ...link, healthStatus: 'REJECTED' } })).toBe('broken')
  })

  it('puts a typing, a refusal and an empty cell before the saved code’s health', () => {
    expect(marketplaceCodeState({ typed: 'HBCV2', saved: 'HBCV1', link: { ...link, healthStatus: 'NOT_FOUND' } })).toBe('changed')
    expect(marketplaceCodeState({ error: 'kullanımda', typed: 'HBCV1', saved: 'HBCV1', link })).toBe('invalid')
    expect(marketplaceCodeState({ typed: '', saved: '' })).toBe('empty')
  })
})

const hepsiburada = { connectionId: 'hb-1', platformCode: 'HEPSIBURADA', displayName: 'Ravencia HB' }
const shopify = { connectionId: 'sh-1', platformCode: 'SHOPIFY', displayName: 'Ravencia Shopify' }
const secondShopify = { connectionId: 'sh-2', platformCode: 'SHOPIFY', displayName: 'Outlet' }
const codes: MarketplaceCodes = {
  connections: [hepsiburada, shopify],
  variants: [
    { variantId: 'v-1', sku: 'KZK-S', barcode: '8690000000011', codes: [{ connectionId: 'hb-1', externalId: 'HBCV00001ABCD', externalBarcode: '8690000000011', externalSku: 'KZK-S' }, { connectionId: 'sh-1', externalId: '44791234567890', externalBarcode: null, externalSku: 'KZK-S' }] },
    { variantId: 'v-2', sku: 'KZK-M', barcode: null, codes: [] }
  ]
}

describe('marketplace codes', () => {
  it('names each column by what its marketplace calls the code and, for a second connection of a platform, by the connection', () => {
    expect(marketplaceCodeColumnLabel(hepsiburada, [hepsiburada, shopify])).toBe('SKU')
    expect(marketplaceCodeColumnLabel(shopify, [hepsiburada, shopify])).toBe('Variant ID')
    expect(marketplaceCodeColumnLabel(secondShopify, [shopify, secondShopify])).toBe('Variant ID · Outlet')
  })

  it('reads the code a connection holds for a variant, and nothing for a variant it never imported', () => {
    expect(marketplaceCodeFor(codes, 'v-1', 'hb-1')).toBe('HBCV00001ABCD')
    expect(marketplaceCodeFor(codes, 'v-1', 'sh-1')).toBe('44791234567890')
    expect(marketplaceCodeFor(codes, 'v-2', 'hb-1')).toBeNull()
  })

  it('saves only the cells whose typed code differs from the saved one; an emptied cell removes its link', () => {
    expect(changedMarketplaceCodes(codes, {
      [marketplaceCodeKey('v-1', 'hb-1')]: ' HBCV00001ABCD ',
      [marketplaceCodeKey('v-1', 'sh-1')]: '',
      [marketplaceCodeKey('v-2', 'hb-1')]: 'HBCV00002EFGH',
      [marketplaceCodeKey('v-2', 'sh-1')]: '  '
    })).toEqual([
      { variantId: 'v-1', connectionId: 'sh-1', code: '' },
      { variantId: 'v-2', connectionId: 'hb-1', code: 'HBCV00002EFGH' }
    ])
  })
})
