import { describe, expect, it } from 'vitest'
import { shopifyStoreDisplayName } from './shopify-store-name'

describe('Shopify store name display', () => {
  it('shows the full myshopify.com address for a saved short store name', () => {
    expect(shopifyStoreDisplayName('e670zv-xa')).toBe('e670zv-xa.myshopify.com')
  })

  it('does not duplicate the domain when a full store address is already saved', () => {
    expect(shopifyStoreDisplayName('shop.example.myshopify.com')).toBe('shop.example.myshopify.com')
  })
})
