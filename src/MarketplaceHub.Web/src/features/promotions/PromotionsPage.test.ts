import { describe, expect, it } from 'vitest'
import { promotionStores, sectionOf } from './PromotionsPage'

describe('promotions page', () => {
  it('opens the page the address names, the commission tariff otherwise', () => {
    expect(sectionOf('plus')).toBe('PLUS')
    expect(sectionOf('flash')).toBe('FLASH')
    expect(sectionOf(null)).toBe('TARIFF')
    expect(sectionOf('nonsense')).toBe('TARIFF')
  })

  it('lists the live stores of a platform whose files it reads', () => {
    const store = (id: string, platformCode: string, status = 'ACTIVE', environment = 'PRODUCTION') => ({ id, platformCode, displayName: id, status, environment })
    expect(promotionStores([store('a', 'TRENDYOL'), store('b', 'HEPSIBURADA'), store('c', 'TRENDYOL', 'DELETED'), store('d', 'TRENDYOL', 'ACTIVE', 'SANDBOX')]).map(item => item.id)).toEqual(['a'])
  })
})
