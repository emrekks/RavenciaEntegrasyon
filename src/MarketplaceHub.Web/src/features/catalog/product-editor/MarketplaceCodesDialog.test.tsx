import { describe, expect, it } from 'vitest'
import { changedMarketplaceCodes, marketplaceCodeColumnLabel, marketplaceCodeFor, marketplaceCodeKey, type MarketplaceCodes } from './MarketplaceCodesDialog'

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
