import { describe, expect, it } from 'vitest'
import { orderCountText, orderTabCount, summaryMetric, type OrderSummary } from './order-summary'

const summary = (patch: Partial<OrderSummary>): OrderSummary => ({ all: 10, new: 4, processing: 2, shipped: 1, delivered: 1, resent: 0, onHold: 0, cancelled: 0, returned: 0, returnInTransit: 0, partiallyCancelled: 0, manualReview: 0, pending: 6, unverified: 0, paymentPending: 0, ...patch })

describe('order tab counters', () => {
  it('counts the Geciken tab from the filtered summary only', () => {
    expect(orderTabCount('LATE', summary({ late: 2 }))).toBe(2)
    expect(orderTabCount('LATE', summary({ late: 0 }))).toBe(0)
  })

  it('says nothing, never 0 or an unfiltered count, when the summary does not give a counter', () => {
    expect(orderTabCount('LATE', summary({}))).toBeUndefined()
    expect(orderTabCount('LATE', undefined)).toBeUndefined()
    expect(orderTabCount('UNDELIVERED', summary({}))).toBeUndefined()
    expect(orderTabCount('SOMETHING_ELSE', summary({}))).toBeUndefined()
  })

  it('reads the other tabs from the summary', () => {
    expect(orderTabCount('NEW', summary({}))).toBe(4)
    expect(orderTabCount('UNDELIVERED', summary({ undelivered: 0 }))).toBe(0)
  })

  it('shows a dash for an unknown or unread counter and dots only while loading', () => {
    expect(orderCountText(1234, { loading: false, error: false })).toBe('1.234')
    expect(orderCountText(0, { loading: false, error: false })).toBe('0')
    expect(orderCountText(undefined, { loading: false, error: false })).toBe('—')
    expect(orderCountText(undefined, { loading: true, error: false })).toBe('…')
    expect(orderCountText(5, { loading: true, error: true })).toBe('—')
  })
})

describe('summary card state', () => {
  const pick = (value: OrderSummary) => value.new
  it('shows the number once the summary answered', () => {
    expect(summaryMetric({ data: summary({}), isError: false, isPending: false }, pick)).toEqual({ value: 4, loading: false, error: false })
  })

  it('loads only while no answer has come', () => {
    expect(summaryMetric({ data: undefined, isError: false, isPending: true }, pick)).toEqual({ value: undefined, loading: true, error: false })
  })

  it('turns to an error, not an endless "…" or another filter\'s number, when the summary fails', () => {
    expect(summaryMetric({ data: undefined, isError: true, isPending: false }, pick)).toEqual({ value: undefined, loading: false, error: true })
    // keepPreviousData keeps the previous filter's answer as data; it is not shown for a failed request.
    expect(summaryMetric({ data: summary({ new: 99 }), isError: true, isPending: false }, pick)).toEqual({ value: undefined, loading: false, error: true })
  })
})
