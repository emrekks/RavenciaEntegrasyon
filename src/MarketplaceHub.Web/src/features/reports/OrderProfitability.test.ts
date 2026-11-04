import { describe, expect, it } from 'vitest'
import { optionText, orderProfitQuery, stateText, unknownProfit, type OrderProfit, type OrderProfitPage } from './OrderProfitability'

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

  it('tells why an order\'s profit is unknown', () => {
    expect(unknownProfit({ cost: null } as OrderProfit)).toBe('Maliyet eksik')
    expect(unknownProfit({ cost: 160 } as OrderProfit)).toBe('Kargo bilinmiyor')
  })
})
