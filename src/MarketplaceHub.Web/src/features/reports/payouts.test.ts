import { describe, expect, it } from 'vitest'
import { emptyPayoutFilters, estimateNote, filterGroups, isRolledToPayoutDay, lastPayout, lineMoney, nextPayout, otherCurrenciesText, payoutGap, payoutLinesQuery, rolledPayoutNote, type PayoutGroup } from './payouts'

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
  it('notes a Trendyol payout day as the next payout day of sales whose payout day passed, nothing else', () => {
    expect(isRolledToPayoutDay({ kind: 'PAYOUT_DAY', platformCode: 'HEPSIBURADA' })).toBe(false)
    expect(isRolledToPayoutDay({ kind: 'PAYOUT_DAY', platformCode: 'trendyol' })).toBe(true)
    expect(isRolledToPayoutDay({ kind: 'DUE_DAY', platformCode: 'TRENDYOL' })).toBe(false)
    expect(isRolledToPayoutDay({ kind: 'PAYOUT', platformCode: 'HEPSIBURADA' })).toBe(false)
    expect(isRolledToPayoutDay({ kind: 'PAYOUT_DAY', platformCode: 'N11' })).toBe(false)
    expect(rolledPayoutNote).toBe('Ödeme günü geçmiş, vadesi henüz dolmamış satışlar sonraki ödeme gününe aktarıldı.')
  })
})

const group = (overrides: Partial<PayoutGroup>): PayoutGroup => ({ key: 'due:abc:2026-11-05:TR_GULF', kind: 'DUE_DAY', connectionId: 'abc', platformCode: 'TRENDYOL', storeName: 'Ravencia',
  region: 'TR_GULF', regionName: null, day: '2026-11-05', amount: 100, linesAmount: null, records: 1, status: 'EXPECTED', estimates: null, carried: null, ...overrides })

describe('payout lines query', () => {
  it('asks a due group by its store, day and region', () => {
    expect(payoutLinesQuery({ platforms: ['TRENDYOL'], group: group({}) })).toBe('platforms=TRENDYOL&connectionId=abc&day=2026-11-05&region=TR_GULF&kind=DUE_DAY&limit=100')
  })
  it('asks a payout by its id, a search alone by its number', () => {
    expect(payoutLinesQuery({ platforms: null, group: group({ kind: 'PAYOUT', key: 'payout:abc:78500001', status: 'PAID' }) })).toBe('connectionId=abc&payoutId=78500001&limit=100')
    expect(payoutLinesQuery({ platforms: null, group: group({}), search: ' 10234 ', page: 2 })).toBe('q=10234&limit=100&after=bzIwMA')
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

describe('next payout of a picked region', () => {
  const group = (day: string, kind: PayoutGroup['kind'], amount: number): PayoutGroup => ({ key: `${day}:${kind}`, kind, connectionId: 'c', platformCode: 'TRENDYOL', storeName: 'Trendyol',
    region: 'TR_GULF', regionName: null, day, amount, linesAmount: null, records: 1, status: 'EXPECTED', estimates: null, carried: null })
  const data = (upcoming: PayoutGroup[]) => ({ next: group('2026-11-09', 'PAYOUT_DAY', 500), regions: [{ region: 'TR_GULF' as const, expected: 0, records: 0,
    nextDay: '2026-11-16', nextAmount: 900, unknown: 0, pendingDelivery: 0, pendingDeliveryOrders: 0 }], upcoming })
  it('takes the server region figure, skipping a day that nets to nothing or less, as the region card does', () => {
    // A deduction-only payout day comes first: the card must not say "Sıradaki −120 ₺ on the 12th".
    const payouts = data([group('2026-11-12', 'PAYOUT_DAY', -120), group('2026-11-16', 'PAYOUT_DAY', 900)])
    expect(nextPayout(payouts, 'TR_GULF')).toEqual({ day: '2026-11-16', amount: 900 })
  })
  it('takes the overall next payout without a region, and none for a region not in view', () => {
    expect(nextPayout(data([]), 'ALL')).toEqual({ day: '2026-11-09', amount: 500 })
    expect(nextPayout(data([]), 'EUROPE')).toEqual({ day: null, amount: null })
    expect(nextPayout(undefined, 'ALL')).toEqual({ day: null, amount: null })
  })
})

describe('other currencies', () => {
  it('says the balance in another currency apart and shows a record in its own currency', () => {
    expect(otherCurrenciesText(null)).toBe('')
    const text = otherCurrenciesText([{ currency: 'USD', expected: 100, records: 1 }])
    expect(text.startsWith('Diğer para birimleri (bakiyeye eklenmedi): ')).toBe(true)
    expect(text).toContain('100,00')
    expect(text).toContain('(1 kayıt)')
    expect(lineMoney(100, 'USD')).toContain('$')
    expect(lineMoney(100, undefined)).toContain('₺')
  })
})

// C-11 (25 Nov 2026): Son Ödeme of a picked region passes over a payout that paid nothing, as the server does for all.
describe('last payout', () => {
  it('takes the newest past payout that paid something in the picked region', () => {
    const zero = group({ key: 'z', day: '2026-11-20', amount: 0, status: 'PAID' })
    const paid = group({ key: 'p', day: '2026-11-13', amount: 250, status: 'PAID' })
    expect(lastPayout({ lastPaid: null }, [zero, paid], 'TR_GULF')).toBe(paid)
    expect(lastPayout({ lastPaid: null }, [zero], 'TR_GULF')).toBe(zero)
    expect(lastPayout({ lastPaid: null }, [], 'TR_GULF')).toBeNull()
    expect(lastPayout({ lastPaid: paid }, [zero], 'ALL')).toBe(paid)
  })
})

describe('payout amount filter', () => {
  const groups = [group({ key: 'a', amount: 12.5 }), group({ key: 'b', amount: 1250 }), group({ key: 'c', amount: 980 })]
  const kept = (min: string, max = '') => filterGroups(groups, { ...emptyPayoutFilters, min, max }).map(item => item.key)
  it('reads Turkish thousands and decimals', () => {
    expect(kept('1.250')).toEqual(['b'])
    expect(kept('12,5', '12.5')).toEqual(['a'])
    expect(kept('₺ 980', '980')).toEqual(['c'])
  })
  it('filters nothing on a bound it cannot read, never another number', () => {
    expect(kept('1,250.50')).toEqual(['a', 'b', 'c'])
    expect(kept('0.125')).toEqual(['a', 'b', 'c'])
    expect(kept('abc')).toEqual(['a', 'b', 'c'])
  })
})
