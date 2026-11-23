import { describe, expect, it } from 'vitest'
import { priceJumps } from './price-jump'

describe('priceJumps', () => {
  it('warns when a price falls below half of its previous value', () => {
    expect(priceJumps([{ key: 'v1', before: 1250, after: 1.25 }]).lines).toEqual(['1 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 1.250,00 → 1,25'])
  })
  it('does not warn for an ordinary change', () => {
    expect(priceJumps([{ key: 'v1', before: 1250, after: 1199 }]).lines).toEqual([])
  })
  it('does not warn for a first price (no previous value)', () => {
    expect(priceJumps([{ key: 'v1', before: null, after: 1250 }, { key: 'v2', before: undefined, after: 1250 }, { key: 'v3', before: 0, after: 1250 }]).lines).toEqual([])
  })
  it('warns when a price rises above three times its previous value', () => {
    expect(priceJumps([{ key: 'v1', before: 100, after: 301 }]).lines).toEqual(['1 Varyantın Fiyatı Üç Katından Fazla Artıyor: 100,00 → 301,00'])
    expect(priceJumps([{ key: 'v1', before: 100, after: 300 }]).lines).toEqual([])
    expect(priceJumps([{ key: 'v1', before: 100, after: 50 }]).lines).toEqual([])
  })
  it('counts each variant once and names both directions', () => {
    const jumps = priceJumps([{ key: 'v1', before: 1250, after: 1.25 }, { key: 'v1', before: 1300, after: 1.3 }, { key: 'v2', before: 900, after: 9 }, { key: 'v3', before: 10, after: 100 }])
    expect(jumps.lines).toEqual(['2 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 1.250,00 → 1,25', '1 Varyantın Fiyatı Üç Katından Fazla Artıyor: 10,00 → 100,00'])
  })
})
