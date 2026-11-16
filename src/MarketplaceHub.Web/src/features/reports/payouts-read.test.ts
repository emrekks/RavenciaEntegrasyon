import { describe, expect, it } from 'vitest'
import { readPayoutLines, readPayouts } from './payouts'

const group = (overrides: Record<string, unknown> = {}) => ({
  key: 'k', kind: 'PAYOUT_DAY', connectionId: 'c', platformCode: 'TRENDYOL', storeName: 'S', region: 'TR_GULF', regionName: null, day: '2026-11-05',
  amount: 880, linesAmount: 900, records: 2, status: 'EXPECTED', estimates: [{ kind: 'CARGO', count: 1, amount: 20, rate: null, missing: 0 }], carried: null, ...overrides
})
const answer = {
  today: '2026-11-04', daysBack: 90, expected: 1180, expectedRecords: 3, next: group(), lastPaid: null, unknown: 0, unknownRecords: 0, earlyPaymentsTaken: 0,
  earlyPayments: 0, regions: [{ region: 'MARS', expected: 1, records: 1, nextDay: null, nextAmount: null, unknown: 0, pendingDelivery: 0, pendingDeliveryOrders: 0 }],
  platforms: [], upcoming: [group({ kind: 'LATER', status: 'LATER', estimates: [{ kind: 'NEW_FEE', count: 1, amount: 5, rate: null, missing: 0 }, { kind: 'STOPPAGE', count: 1, amount: 3, rate: 0.01, missing: 0 }] })],
  past: [], coverage: [{ connectionId: 'c', platformCode: 'TRENDYOL', storeName: 'S', state: 'MAYBE', lastSyncedAt: null }], pendingDelivery: 0, pendingDeliveryOrders: 0,
  paidUnread: 0, paidUnreadRecords: 0, otherCurrencies: null
}

describe('the Hakediş answers as the tab reads them', () => {
  it('keeps every figure and handles each unknown code explicitly', () => {
    const read = readPayouts(answer)
    expect(read.next).toMatchObject({ kind: 'PAYOUT_DAY', region: 'TR_GULF', status: 'EXPECTED', amount: 880, linesAmount: 900 })
    expect(read.regions[0]).toMatchObject({ region: 'UNKNOWN', expected: 1 })
    // An unknown kind reads as an open day, an unknown status as not paid; an unknown deduction leaves the breakdown, the amount stays.
    expect(read.upcoming[0]).toMatchObject({ kind: 'DUE_DAY', status: 'EXPECTED', amount: 880 })
    expect(read.upcoming[0].estimates?.map(item => item.kind)).toEqual(['STOPPAGE'])
    expect(read.coverage[0].state).toBe('NOT_READ')
  })

  it('reads the record lines with their region and status narrowed', () => {
    const line = { externalId: '1', connectionId: 'c', platformCode: 'TRENDYOL', transactionType: null, category: 'SALE', transactionDate: '2026-10-20T10:00:00Z', dueDay: null,
      orderNumber: null, invoiceNumber: null, country: null, region: 'EUROPE', gross: 10, commission: null, net: 8, status: 'PAID', paymentOrderId: null, barcode: null, currency: 'TRY' }
    const read = readPayoutLines({ items: [line, { ...line, region: 'X', status: 'Y' }], totalCount: 2, page: 0, pageSize: 100, net: 16, nextCursor: null, hasMore: false })
    expect(read.items.map(item => [item.region, item.status, item.net])).toEqual([['EUROPE', 'PAID', 8], ['UNKNOWN', 'EXPECTED', 8]])
  })
})
