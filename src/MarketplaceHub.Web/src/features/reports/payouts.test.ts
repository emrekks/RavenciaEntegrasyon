import { describe, expect, it } from 'vitest'
import { payoutGap, payoutLinesQuery, type PayoutGroup } from './payouts'

const group = (overrides: Partial<PayoutGroup>): PayoutGroup => ({ key: 'due:abc:2026-11-05:TR_GULF', kind: 'DUE', connectionId: 'abc', platformCode: 'TRENDYOL', storeName: 'Ravencia',
  region: 'TR_GULF', regionName: null, day: '2026-11-05', amount: 100, linesAmount: null, records: 1, status: 'EXPECTED', ...overrides })

describe('payout lines query', () => {
  it('asks a due group by its store, day and region', () => {
    expect(payoutLinesQuery({ platforms: ['TRENDYOL'], group: group({}) })).toBe('platformCodes=TRENDYOL&connectionId=abc&day=2026-11-05&region=TR_GULF')
  })
  it('asks a payout by its id, a search alone by its number', () => {
    expect(payoutLinesQuery({ platforms: null, group: group({ kind: 'PAYOUT', key: 'payout:abc:78542975', status: 'PAID' }) })).toBe('connectionId=abc&payoutId=78542975')
    expect(payoutLinesQuery({ platforms: null, group: group({}), search: ' 10234 ', page: 2 })).toBe('q=10234&page=2')
  })
})

describe('payout gap', () => {
  it('shows a difference only between a payout and the records read for it', () => {
    expect(payoutGap(group({}))).toBeNull()
    expect(payoutGap(group({ kind: 'PAYOUT', amount: 780, linesAmount: null }))).toBeNull()
    expect(payoutGap(group({ kind: 'PAYOUT', amount: 780, linesAmount: 780 }))).toBeNull()
    expect(payoutGap(group({ kind: 'PAYOUT', amount: 780, linesAmount: 800.5 }))).toBe(-20.5)
  })
})
