import { describe, expect, it } from 'vitest'
import { optionText, orderProfitQuery, profitScope, stateText, unknownProfit, type OrderProfit, type OrderProfitPage } from './OrderProfitability'

const page = (totals: Partial<OrderProfitPage>): OrderProfitPage => ({ items: [], totalCount: 100, page: 0, pageSize: 50, amount: 10_000, profit: 1_000, missingCost: 0,
  profitAmount: 10_000, profitOrders: 100, margin: 10, missingCargo: 0, ...totals })

describe('order profitability', () => {
  it('asks for the report\'s period and platforms, the search, the order and the page', () => {
    expect(orderProfitQuery({ from: '2026-09-25', to: '2026-10-25', platforms: ['TRENDYOL'], search: ' 1175 ', sort: 'profit', descending: false, page: 2 }))
      .toBe('from=2026-09-25&to=2026-10-25&sort=profit&dir=asc&page=2&limit=50&platformCodes=TRENDYOL&search=1175')
    expect(orderProfitQuery({ from: '2026-10-01', to: '2026-10-25', platforms: null, search: '', sort: 'date', descending: true, page: 0 }))
      .toBe('from=2026-10-01&to=2026-10-25&sort=date&dir=desc&page=0&limit=50')
  })

  it('writes a variant\'s options and a returned order\'s state', () => {
    expect(optionText('Renk: Siyah | Beden: M')).toBe('Siyah · M')
    expect(optionText('Renk: Zümrüt | Beden: L | Astar Durumu: Astarsız | Kalıp: Regular | Menşei: TR')).toBe('Zümrüt · L')
    expect(optionText(null)).toBe('')
    expect(stateText.RETURNED).toBe('İade')
    expect(stateText.SOLD).toBeNull()
  })

  it('says the profit and the margin are only the orders\' whose profit is known', () => {
    expect(profitScope(page({}))).toBeNull()
    // 100 orders, 10,000 TL; 40 without a cost and 18 with an unknown cargo: the margin is 1,000 / 5,000, not / 10,000.
    expect(profitScope(page({ profitOrders: 50, profitAmount: 5_000, margin: 20, missingCost: 40, missingCargo: 18 })))
      .toBe('Kâr ve Kâr Marjı yalnız kârı bilinen 50 / 100 siparişten (₺5.000,00 tutar) hesaplandı; maliyeti eksik 40, kargosu bilinmeyen 18 sipariş dışarıda.')
    expect(profitScope(page({ profit: null, profitOrders: 0, profitAmount: 0, margin: null, missingCost: 100 })))
      .toBe('Hiçbir siparişin kârı bilinmiyor (maliyeti eksik 100 sipariş); Kâr ve Kâr Marjı hesaplanamadı.')
  })

  it('tells why an order\'s profit is unknown', () => {
    expect(unknownProfit({ cost: null } as OrderProfit)).toBe('Maliyet eksik')
    expect(unknownProfit({ cost: 160 } as OrderProfit)).toBe('Kargo bilinmiyor')
  })
})
