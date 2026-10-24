import { describe, expect, it } from 'vitest'
import { change, desiSourcesNote, formatPercent, moneyOrMissing, reportQuery, sortRows, toCsv } from './report-format'

describe('report formatting', () => {
  it('says where the parcels\' desi came from, leaving out the empty sources', () => {
    expect(desiSourcesNote({ marketplace: 5, manual: 0, measured: 30, default: 2, measuredParcels: 1320 }))
      .toBe(' Desi: 5 siparişte pazaryerinin siparişte bildirdiği, 30 siparişte Trendyol kargo faturalarından ölçülen (1.320 koli), 2 siparişte mağaza ortalaması.')
    expect(desiSourcesNote({ marketplace: 0, manual: 0, measured: 0, default: 4, measuredParcels: 0 })).toBe(' Desi: 4 siparişte varsayılan 1 desi.')
    expect(desiSourcesNote(null)).toBe('')
  })

  it('compares counts as a percentage, rates by their difference written in per cent, and not at all from an empty previous period', () => {
    expect(change(120, 100)).toEqual({ text: '+%20', direction: 'up' })
    expect(change(80, 100)).toEqual({ text: '−%20', direction: 'down' })
    expect(change(5, 0)).toBeNull()
    expect(change(0, 0)).toEqual({ text: '%0', direction: 'flat' })
    expect(change(0.05, 0.03, 'rate')).toEqual({ text: '+%2,0', direction: 'up' })
    expect(change(null, 3)).toBeNull()
  })

  it('shows a missing amount as missing, not as zero lira', () => {
    expect(moneyOrMissing(0, 0)).toBe('Tutar yok')
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
})
