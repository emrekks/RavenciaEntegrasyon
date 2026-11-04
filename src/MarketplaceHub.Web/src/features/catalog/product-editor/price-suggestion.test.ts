import { describe, expect, it } from 'vitest'
import { unitProfit } from '../../marketplace/integrations/unit-profit'
import { appliedPrices, evaluatePrice, suggestPrice, type PriceSuggestionInput } from './price-suggestion'

const base: PriceSuggestionInput = { cost: 200, targetRate: 50, basis: 'cost', commissionRate: 20, cargo: 62, serviceFee: 13.19, vatRate: 20, vatIncluded: true }

describe('suggestPrice', () => {
  it('finds the price whose tariff profit is the wanted share of the cost (owner example: 200 TL, 50 %)', () => {
    const suggestion = suggestPrice(base)!
    expect(suggestion.price).toBeGreaterThan(490)
    expect(suggestion.price).toBeLessThan(510)
    // The same profit the commission tariff's ProfitBadge shows for that price.
    const { profit } = unitProfit(suggestion.grossPrice, suggestion.commission, 200, { cargo: 62, serviceFee: 13.19, vatRate: 20 })
    expect(profit).toBe(suggestion.profit)
    expect(Math.abs(profit - 100)).toBeLessThanOrEqual(0.01)
  })

  const cases: Array<Partial<PriceSuggestionInput>> = [
    {}, { cost: 160, vatRate: 10, cargo: 98.34, commissionRate: 21.5, targetRate: 63 }, { cost: 37.5, targetRate: 12.5, vatRate: 1 },
    { cost: 999.99, targetRate: 80, commissionRate: 8, cargo: 0 }, { cost: 120, targetRate: 35, vatIncluded: false }, { cost: 75, vatRate: 0, targetRate: 5 },
    { basis: 'sale', targetRate: 20 }, { basis: 'sale', targetRate: 7.5, vatIncluded: false, vatRate: 10 }, { basis: 'sale', cost: 0, targetRate: 15, cargo: 40 }
  ]
  it.each(cases)('round trip: the suggested price evaluates back to the target within a kuruş (%o)', overrides => {
    const input = { ...base, ...overrides }
    const suggestion = suggestPrice(input)!
    expect(suggestion).not.toBeNull()
    const again = evaluatePrice(suggestion.price, input)
    expect(again.profit).toBe(suggestion.profit)
    const target = input.basis === 'cost' ? input.cost * input.targetRate / 100 : again.grossPrice * input.targetRate / 100
    expect(Math.abs(again.profit - target)).toBeLessThanOrEqual(0.01)
  })

  it('adds the VAT on top when KDV Dahil mi is Hayır', () => {
    const included = suggestPrice(base)!
    const excluded = suggestPrice({ ...base, vatIncluded: false })!
    expect(Math.abs(excluded.grossPrice - included.grossPrice)).toBeLessThanOrEqual(0.02)
    expect(Math.abs(excluded.price * 1.2 - excluded.grossPrice)).toBeLessThanOrEqual(0.01)
  })

  it('has no price for a margin the fees never leave, nor for a cost target without a cost', () => {
    expect(suggestPrice({ ...base, basis: 'sale', targetRate: 90 })).toBeNull()
    expect(suggestPrice({ ...base, cost: 0 })).toBeNull()
    expect(suggestPrice({ ...base, commissionRate: 100 })).toBeNull()
  })
})

describe('appliedPrices', () => {
  it('raises an empty or lower list price to the sale price and leaves a higher one', () => {
    expect(appliedPrices(499.9, '')).toEqual({ salePrice: '499.9', listPrice: '499.9' })
    expect(appliedPrices(499.9, '450')).toEqual({ salePrice: '499.9', listPrice: '499.9' })
    expect(appliedPrices(499.9, '600')).toEqual({ salePrice: '499.9', listPrice: '600' })
  })
})
