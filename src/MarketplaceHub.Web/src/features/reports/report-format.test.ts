import { afterEach, describe, expect, it, vi } from 'vitest'
import { change, periodPresets, desiSourcesNote, formatPercent, moneyOrMissing, profitCost, reportQuery, sortRows, toCsv, type ReportSales } from './report-format'

describe('report formatting', () => {
  // The same rule as the server's ReportProfitCost (owner, 13 Nov 2026): a profit from 95 % of the kept units with a cost,
  // the rest priced by their share of the sales.
  const cost = (costed: number, extra: Partial<ReportSales['cost']> = {}): ReportSales['cost'] => ({
    keptQuantity: 1000, costedQuantity: costed, coverage: costed / 1000, costTotal: costed * 40, costedRevenue: costed * 100, keptRevenue: 100_000,
    grossProfit: 0, grossMargin: null, profitQuantity: 0, profitCoverage: null, profitRevenue: 0, profitCost: 0, profitCommission: 0, netProfit: 0, netMargin: null, costedVariants: 1, ...extra
  })
  it('works the profit out from 95 % of the kept units with a cost, the missing cost by its sales share', () => {
    expect(profitCost(cost(949)).usable).toBe(false)
    expect(profitCost(cost(950))).toEqual({ usable: true, known: 38_000, estimated: 2_000, missing: 50, total: 40_000 })
    expect(profitCost(cost(1000))).toEqual({ usable: true, known: 40_000, estimated: 0, missing: 0, total: 40_000 })
    // The 50 units without a cost sold for 1,000 only: 40 % of it, not 50 × 40.
    expect(profitCost(cost(950, { keptRevenue: 96_000 })).estimated).toBe(400)
    // An older report without the kept revenue: the average cost per unit.
    expect(profitCost(cost(950, { keptRevenue: undefined })).estimated).toBe(2_000)
    expect(profitCost(cost(0)).usable).toBe(false)
  })

  it('says where the parcels\' desi came from, leaving out the empty sources', () => {
    expect(desiSourcesNote({ marketplace: 5, manual: 0, measured: 30, default: 2, measuredParcels: 1320 }))
      .toBe(' Desi: 5 siparişte pazaryerinin siparişte bildirdiği, 30 siparişte Trendyol kargo faturalarından ölçülen (1.320 koli), 2 siparişte mağaza ortalaması.')
    expect(desiSourcesNote({ marketplace: 0, manual: 0, measured: 0, default: 4, measuredParcels: 0 })).toBe(' Desi: 4 siparişte varsayılan 1 desi.')
    expect(desiSourcesNote(null)).toBe('')
  })

  it('compares counts as a percentage, rates by their difference written in per cent, and not at all from an empty previous period', () => {
    expect(change(120, 100)).toEqual({ text: '+%20', direction: 'up' })
    expect(change(80, 100)).toEqual({ text: '−%20', direction: 'down' })
    // Something against nothing is new, nothing against something is a full fall, nothing against nothing no change.
    expect(change(5, 0)).toEqual({ text: 'Yeni', direction: 'up' })
    expect(change(0, 5)).toEqual({ text: '−%100', direction: 'down' })
    expect(change(0, 0)).toBeNull()
    expect(change(0.05, 0.03, 'rate')).toEqual({ text: '+%2,0', direction: 'up' })
    expect(change(null, 3)).toBeNull()
  })

  it('shows a missing amount as missing, not as zero lira', () => {
    expect(moneyOrMissing(0, 0)).toBe('—')
    expect(moneyOrMissing(1250.5, 3)).toContain('1.250,50')
    expect(formatPercent(0.0525)).toBe('%5,3')
    expect(formatPercent(null)).toBe('—')
  })

  it('asks for the chosen days, platforms and buckets only', () => {
    expect(reportQuery({ from: '2026-10-01', to: '2026-10-22', platforms: null, granularity: '', includeTest: false })).toBe('from=2026-10-01&to=2026-10-22')
    expect(reportQuery({ from: '2026-10-01', to: '2026-10-22', platforms: ['TRENDYOL', 'N11'], granularity: 'week', includeTest: true }))
      .toBe('from=2026-10-01&to=2026-10-22&platformCodes=TRENDYOL%2CN11&granularity=week&includeTest=true')
  })

  it('writes an Excel-safe CSV: formulas are quoted out and numbers keep the Turkish decimal comma', () => {
    const csv = toCsv([{ name: '=SUM(A1)', value: 12.5 }, { name: 'Ankara "Merkez"', value: 3 }], [
      { header: 'Şehir', value: row => row.name },
      { header: 'Adet', value: row => row.value }
    ])
    expect(csv.startsWith('﻿"Şehir";"Adet"')).toBe(true)
    expect(csv).toContain(`"'=SUM(A1)";"12,5"`)
    expect(csv).toContain('"Ankara ""Merkez""";"3"')
  })

  it('keeps the unknown bucket last whichever way a column is sorted', () => {
    const rows = [{ label: 'Bilinmiyor', value: 99, known: false }, { label: 'Ankara', value: 1, known: true }, { label: 'İzmir', value: 5, known: true }]
    expect(sortRows(rows, row => row.value, 'desc', row => !row.known).map(row => row.label)).toEqual(['İzmir', 'Ankara', 'Bilinmiyor'])
    expect(sortRows(rows, row => row.value, 'asc', row => !row.known).map(row => row.label)).toEqual(['Ankara', 'İzmir', 'Bilinmiyor'])
  })

  afterEach(() => { vi.useRealTimers() })

  // 9.1.6 of the operating guide: the period buttons followed the browser clock. At 00:30 on 1 November in Istanbul it is
  // still 31 October in London or New York; every button follows Istanbul's day, as Genel Bakış does.
  it('works the period buttons out from today in Istanbul, not from the browser clock', () => {
    // 21:30Z: still 31 October on a UTC clock (the CI runners'), 1 November in Istanbul.
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-10-31T21:30:00Z'))
    const ranges = Object.fromEntries(periodPresets().map(preset => [preset.key, preset.range]))
    expect(ranges.today).toEqual({ from: '2026-11-01', to: '2026-11-01' })
    expect(ranges.yesterday).toEqual({ from: '2026-10-31', to: '2026-10-31' })
    expect(ranges.month).toEqual({ from: '2026-11-01', to: '2026-11-01' })
    expect(ranges['last-month']).toEqual({ from: '2026-10-01', to: '2026-10-31' })
    expect(ranges['30']).toEqual({ from: '2026-10-03', to: '2026-11-01' })
    expect(ranges.week).toEqual({ from: '2026-10-26', to: '2026-11-01' })
    expect(periodPresets().map(preset => preset.label)).toEqual(['Bugün', 'Dün', 'Bu Hafta', 'Bu Ay', 'Geçen Ay', 'Son 30 Gün', 'Bu Yıl'])
  })
})
