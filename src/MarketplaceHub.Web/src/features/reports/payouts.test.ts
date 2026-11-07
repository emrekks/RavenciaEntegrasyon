import { describe, expect, it } from 'vitest'
import { estimateNote, isRolledToPayoutDay, parseAmount, payoutGap, payoutLinesQuery, rolledPayoutNote, type PayoutGroup } from './payouts'

describe('estimate note', () => {
  it('explains the deductions taken off a Trendyol payout day to come', () => {
    // Azerbaycan, Monday 9 Nov 2026: Trendyol's Ödeme Özeti said 858.08.
    const note = estimateNote({ linesAmount: 1165.85, estimates: [
      { kind: 'CARGO', count: 2, amount: 196.68, rate: 98.34, missing: 0 },
      { kind: 'PLATFORM_FEE', count: 2, amount: 21.98, rate: 10.99, missing: 0 },
      { kind: 'INTERNATIONAL_FEE', count: 2, amount: 89.11, rate: 0.06, missing: 0 }] })
    expect(note).toContain('Kayıtların toplamı 1.165,85 ₺')
    expect(note).toContain('tahminen düşüldü (307,77 ₺)')
    expect(note).toContain('Kargo 2 × 98,34 = 196,68 ₺')
    expect(note).toContain('desisini')
  })
  it('says when the cargo tariff is not known and is silent without estimates', () => {
    expect(estimateNote({ linesAmount: 100, estimates: [{ kind: 'CARGO', count: 3, amount: 0, rate: null, missing: 0 }] })).toContain('3 paket, tarife bilinmediği için düşülmedi')
    expect(estimateNote({ linesAmount: null, estimates: null })).toBeNull()
  })
})

describe('payout day roll', () => {
  it('notes a Trendyol or Hepsiburada payout day as the next payout day of records already due, nothing else', () => {
    expect(isRolledToPayoutDay({ kind: 'PAYOUT_DAY', platformCode: 'HEPSIBURADA' })).toBe(true)
    expect(isRolledToPayoutDay({ kind: 'PAYOUT_DAY', platformCode: 'trendyol' })).toBe(true)
    expect(isRolledToPayoutDay({ kind: 'DUE_DAY', platformCode: 'TRENDYOL' })).toBe(false)
    expect(isRolledToPayoutDay({ kind: 'PAYOUT', platformCode: 'HEPSIBURADA' })).toBe(false)
    expect(isRolledToPayoutDay({ kind: 'PAYOUT_DAY', platformCode: 'N11' })).toBe(false)
    expect(rolledPayoutNote).toBe('Vadesi dolan kayıtlar sonraki ödeme gününe aktarıldı.')
  })
})

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
