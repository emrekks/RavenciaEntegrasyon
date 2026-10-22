import { describe, expect, it } from 'vitest'
import { marketplaceCodeColumnLabel, marketplaceCodeFor, type MarketplaceCodes } from './MarketplaceCodesDialog'

const hepsiburada = { connectionId: 'hb-1', platformCode: 'HEPSIBURADA', displayName: 'Ravencia HB' }
const shopify = { connectionId: 'sh-1', platformCode: 'SHOPIFY', displayName: 'Ravencia Shopify' }
const secondShopify = { connectionId: 'sh-2', platformCode: 'SHOPIFY', displayName: 'Outlet' }

describe('marketplace codes', () => {
  it('names each column by its platform and, for a second connection of a platform, by the connection', () => {
    expect(marketplaceCodeColumnLabel(hepsiburada, [hepsiburada, shopify])).toBe('HB Kod')
    expect(marketplaceCodeColumnLabel(shopify, [hepsiburada, shopify])).toBe('Shopify Kod')
    expect(marketplaceCodeColumnLabel(secondShopify, [shopify, secondShopify])).toBe('Shopify Kod · Outlet')
  })

  it('reads the code a connection holds for a variant, and nothing for a variant it never imported', () => {
    const codes: MarketplaceCodes = {
      connections: [hepsiburada, shopify],
      variants: [
        { variantId: 'v-1', sku: 'KZK-S', barcode: '8690000000011', codes: [{ connectionId: 'hb-1', externalId: 'HBCV00001ABCD', externalBarcode: '8690000000011', externalSku: 'KZK-S' }, { connectionId: 'sh-1', externalId: '44791234567890', externalBarcode: null, externalSku: 'KZK-S' }] },
        { variantId: 'v-2', sku: 'KZK-M', barcode: null, codes: [] }
      ]
    }
    expect(marketplaceCodeFor(codes, 'v-1', 'hb-1')).toBe('HBCV00001ABCD')
    expect(marketplaceCodeFor(codes, 'v-1', 'sh-1')).toBe('44791234567890')
    expect(marketplaceCodeFor(codes, 'v-2', 'hb-1')).toBeNull()
  })
})
