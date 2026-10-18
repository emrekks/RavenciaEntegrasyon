import { describe, expect, it } from 'vitest'
import { freshnessAge, freshnessText, initialImportSummary } from './data-freshness'

describe('data freshness', () => {
  const now = Date.parse('2026-10-18T12:00:00Z')

  it('says how long ago the stores were read', () => {
    expect(freshnessAge('2026-10-18T11:59:25Z', now)).toBe('35 sn önce')
    expect(freshnessAge('2026-10-18T11:56:00Z', now)).toBe('4 dk önce')
    expect(freshnessAge('2026-10-18T09:00:00Z', now)).toBe('3 sa önce')
  })

  it('mentions order notifications only when they are on', () => {
    expect(freshnessText({ kind: 'ORDERS', lastSuccessAt: '2026-10-18T11:59:25Z', instant: true, connections: 2 }, now)).toBe('Son güncelleme 35 sn önce · Anlık bildirim açık')
    expect(freshnessText({ kind: 'RETURNS', lastSuccessAt: '2026-10-18T11:59:25Z', instant: false, connections: 2 }, now)).toBe('Son güncelleme 35 sn önce')
    expect(freshnessText({ kind: 'QUESTIONS', lastSuccessAt: null, instant: false, connections: 2 }, now)).toBeNull()
  })

  it('summarises a first import per kind of data', () => {
    expect(initialImportSummary([
      { kind: 'ORDERS', state: 'RUNNING', percent: 60 },
      { kind: 'RETURNS', state: 'DONE', percent: null },
      { kind: 'QUESTIONS', state: 'QUEUED', percent: null }
    ])).toBe('İlk aktarım: Siparişler %60 · İadeler tamam · Sorular sırada')
  })
})
