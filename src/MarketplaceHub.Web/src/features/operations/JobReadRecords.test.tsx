import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { JobReadRecords } from './JobReadRecords'
import { jobProgressPercent, keepsReadRecords, readProgressCounts, type JobReadSummary } from './job-read-summary'

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

const read: JobReadSummary = {
  kind: 'RETURN',
  counts: { received: 3, inserted: 1, updated: 0, unchanged: 1, skipped: 0, failed: 1 },
  omitted: 4,
  records: [
    { r: 'C-1', s: '1001', st: 'WaitingInAction', o: 'FAILED', why: 'Bağlı sipariş panelde bulunamadı; talep kaydedilmedi.' },
    { r: 'C-2', s: '1002', st: 'Accepted', o: 'NEW' },
    { r: 'C-3', s: '1003', st: 'Accepted', o: 'UNCHANGED' }
  ],
  sentence: "Trendyol'dan 3 iade talebi okundu: 1 yeni eklendi, 1 değişmedi, 1 işlenemedi."
}

describe('JobReadRecords', () => {
  it('lists what the read pulled with its numbers, outcome and reason, and the records beyond the cap', () => {
    act(() => root.render(<JobReadRecords read={read} recordsKept />))
    const text = container.textContent ?? ''
    expect(text).toContain('Çekilen Kayıtlar')
    expect(text).toContain('Talep No')
    expect(text).toContain('C-2')
    expect(text).toContain('1002')
    expect(text).toContain('Yeni')
    expect(text).toContain('Değişmedi')
    expect(text).toContain('Bağlı sipariş panelde bulunamadı')
    expect(text).toContain('3 iade talebi okundu')
    expect(text).toContain('+4 kayıt daha')
  })

  it('says plainly when an older job did not record its records, and stays out of types that never record', () => {
    act(() => root.render(<JobReadRecords read={null} recordsKept />))
    expect(container.textContent).toContain('kayıt bazında ayrıntı tutulmadan önce çalıştı')
    act(() => root.render(<JobReadRecords read={null} recordsKept={false} />))
    expect(container.textContent).toBe('')
  })
})

describe('read counts', () => {
  const job = { progressReceived: 4, progressCurrent: 4, progressProcessed: 0, progressSkipped: 0, progressFailed: 0 }

  it('counts the unchanged records instead of letting them vanish', () => {
    const counts = readProgressCounts(job, null)
    expect(counts.unchanged).toBe(4)
    expect(counts.processed + counts.unchanged + counts.skipped + counts.failed).toBe(counts.received)
  })

  it('uses the recorded counts when the job kept them', () => {
    const counts = readProgressCounts(job, read)
    expect(counts).toMatchObject({ received: 3, inserted: 1, updated: 0, unchanged: 1, failed: 1 })
  })

  it('shows a finished job as complete', () => {
    expect(jobProgressPercent({ status: 'SUCCEEDED', progressPercent: null, progressTotal: null, progressCurrent: 4 })).toBe(100)
    expect(jobProgressPercent({ status: 'SUCCEEDED', progressPercent: 0, progressTotal: null, progressCurrent: 0 })).toBe(100)
    expect(jobProgressPercent({ status: 'LEASED', progressPercent: null, progressTotal: 10, progressCurrent: 5 })).toBe(50)
    expect(jobProgressPercent({ status: 'LEASED', progressPercent: null, progressTotal: null, progressCurrent: 5 })).toBeNull()
  })

  it('knows which reads keep their records', () => {
    expect(keepsReadRecords('RETURN_SYNC')).toBe(true)
    expect(keepsReadRecords('TRENDYOL_ORDER_SYNC')).toBe(true)
    expect(keepsReadRecords('PAZARAMA_ORDER_STATUS_SYNC')).toBe(true)
    expect(keepsReadRecords('PRODUCT_SYNC')).toBe(true)
    expect(keepsReadRecords('N11_SETTLEMENT_SYNC')).toBe(true)
    expect(keepsReadRecords('MARKETPLACE_QUESTION_SYNC')).toBe(true)
    expect(keepsReadRecords('PRICE_INVENTORY_SYNC')).toBe(false)
    expect(keepsReadRecords('STOCK_RECONCILIATION')).toBe(false)
  })
})

describe('read status labels', () => {
  it('names invoice states in Turkish and keeps counts as they came', async () => {
    const { readStatusLabel } = await import('./job-read-summary')
    expect(readStatusLabel('Invoiced, NotInvoiced')).toBe('Faturalı, Faturasız')
    expect(readStatusLabel('3 varyant')).toBe('3 varyant')
    expect(readStatusLabel(null)).toBe('—')
  })
})
