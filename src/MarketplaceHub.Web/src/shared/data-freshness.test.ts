import { describe, expect, it } from 'vitest'
import { initialImportSummary } from './data-freshness'

describe('data freshness', () => {
  it('summarises a first import per kind of data', () => {
    expect(initialImportSummary([
      { kind: 'ORDERS', state: 'RUNNING', percent: 60 },
      { kind: 'RETURNS', state: 'DONE', percent: null },
      { kind: 'QUESTIONS', state: 'QUEUED', percent: null }
    ])).toBe('İlk aktarım: Siparişler %60 · İadeler tamam · Sorular sırada')
  })
})
