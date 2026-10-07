import { describe, expect, it } from 'vitest'
import { visibleOrderLineVariantOptions } from './order-line-variant-options'

describe('visibleOrderLineVariantOptions', () => {
  it('keeps color and size while hiding marketplace category attributes', () => {
    expect(visibleOrderLineVariantOptions('Renk: İndigo | Beden: XL | Astar Durumu: Astarsız | Kumaş Tipi: Viskon | Desen: Çiçekli'))
      .toEqual([{ label: 'Renk', value: 'İndigo' }, { label: 'Beden', value: 'XL' }])
  })

  it('does not show duplicate panel and web-color fields as separate variant options', () => {
    expect(visibleOrderLineVariantOptions('Web Color: Navy | Renk: Lacivert | Color: Blue | Size: XL'))
      .toEqual([{ label: 'Renk', value: 'Lacivert' }, { label: 'Size', value: 'XL' }])
  })

  it('hides non-variant product attributes and empty signatures', () => {
    expect(visibleOrderLineVariantOptions('Materyal: Viskon | Kalıp: Regular')).toEqual([])
    expect(visibleOrderLineVariantOptions(null)).toEqual([])
  })
})
