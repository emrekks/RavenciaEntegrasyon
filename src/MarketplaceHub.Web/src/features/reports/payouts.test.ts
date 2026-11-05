import { describe, expect, it } from 'vitest'
import { parseAmount, payoutGap, payoutLinesQuery, type PayoutGroup } from './payouts'

describe('amount filter', () => {
  it('reads Turkish thousands and decimals, and a dot followed by two digits as a decimal point', () => {
    expect(parseAmount('1.250')).toBe(1250)
    expect(parseAmount('1.250,5')).toBe(1250.5)
    expect(parseAmount('1250,50')).toBe(1250.5)
    expect(parseAmount('1500.50')).toBe(1500.5)
    expect(parseAmount('1.250.000')).toBe(1250000)
    expect(parseAmount(' ₺ 980 ')).toBe(980)
    expect(parseAmount('')).toBeNull()
    expect(parseAmount('abc')).toBeNull()
  })
})

const group = (overrides: Partial<PayoutGroup>): PayoutGroup => ({ key: 'due:abc:2026-11-05:TR_GULF', kind: 'DUE_DAY', connectionId: 'abc', platformCode: 'TRENDYOL', storeName: 'Ravencia',
  region: 'TR_GULF', regionName: null, day: '2026-11-05', amount: 100, linesAmount: null, records: 1, status: 'EXPECTED', ...overrides })

describe('payout lines query', () => {
  it('asks a due group by its store, day and region', () => {
    expect(payoutLinesQuery({ platforms: ['TRENDYOL'], group: group({}) })).toBe('platformCodes=TRENDYOL&connectionId=abc&day=2026-11-05&region=TR_GULF&kind=DUE_DAY')
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
