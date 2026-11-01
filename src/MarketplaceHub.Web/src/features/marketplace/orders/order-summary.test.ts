import { describe, expect, it } from 'vitest'
import { orderTabCount, type OrderSummary } from './order-summary'

const summary = (patch: Partial<OrderSummary>): OrderSummary => ({ all: 10, new: 4, processing: 2, shipped: 1, delivered: 1, resent: 0, onHold: 0, cancelled: 0, returned: 0, returnInTransit: 0, partiallyCancelled: 0, manualReview: 0, pending: 6, unverified: 0, paymentPending: 0, ...patch })

describe('order tab counters', () => {
  it('counts the Geciken tab from the filtered summary, not the unfiltered dashboard total', () => {
    expect(orderTabCount('LATE', summary({ late: 2 }), 37)).toBe(2)
    expect(orderTabCount('LATE', summary({ late: 0 }), 37)).toBe(0)
  })

  it('falls back to the dashboard count only while the summary has no late counter', () => {
    expect(orderTabCount('LATE', summary({}), 37)).toBe(37)
    expect(orderTabCount('LATE', undefined, undefined)).toBe(0)
  })

  it('reads the other tabs from the summary', () => {
    expect(orderTabCount('NEW', summary({}), 37)).toBe(4)
    expect(orderTabCount('UNDELIVERED', summary({}), 37)).toBe(0)
  })
})
