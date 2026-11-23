import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { JobChanges } from './JobChanges'
import {
  jobChangeCountItems, jobChangeDifference, jobChangeResult, jobChangeSourceLabel, jobChangesOutcomeText, jobChangesQueryString, jobHasChanges,
  type JobChangeLine, type JobChangesSummary
} from './job-changes'

const answers = vi.hoisted(() => ({ map: new Map<string, unknown>(), calls: [] as string[] }))

vi.mock('../../shared/api', () => {
  const answer = async (path: string) => {
    answers.calls.push(path)
    const key = [...answers.map.keys()].find(prefix => path.startsWith(prefix))
    if (!key) throw new Error(`unexpected ${path}`)
    return answers.map.get(key)
  }
  const hubFile = async (path: string) => { answers.calls.push(path); return { blob: new Blob(['x']), fileName: 'degisiklikler.xlsx', headers: new Headers() } }
  class ApiRequestError extends Error { code?: string }
  return { hubApi: answer, hubPoll: answer, hubFile, ApiRequestError }
})

const counts = { scanned: null, sent: null, applied: null, unchanged: null, pending: null, rejected: null, uncertain: null, superseded: null, priceChanged: null, stockChanged: null }

function line(patch: Partial<JobChangeLine>): JobChangeLine {
  return {
    id: crypto.randomUUID(), occurredAt: '2026-11-18T09:15:00Z', kind: 'STOCK_PRICE', productId: null, productTitle: 'Keten Gömlek', imageUrl: null, modelCode: 'KG-01',
    barcode: '8690001', stockCode: null, optionText: 'Renk: Beyaz | Beden: M', previousQuantity: 6, newQuantity: 5, previousSalePrice: 130, newSalePrice: 120,
    previousListPrice: 150, newListPrice: 150, currency: 'TRY', source: 'ORDER', result: 'APPLIED', errorCode: null, errorMessage: null, connectionId: null,
    platformCode: 'N11', storeName: 'N11 Mağaza', jobId: null, ...patch
  } as JobChangeLine
}

let container: HTMLDivElement
let root: Root
let client: QueryClient

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  answers.map.clear()
  answers.calls.length = 0
  container = document.createElement('div')
  container.className = 'jobs-reference-drawer'
  document.body.append(container)
  root = createRoot(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  client.clear()
})

async function show(jobType: string, onOpenJob?: (id: string) => void) {
  await act(async () => { root.render(<QueryClientProvider client={client}><JobChanges jobId="job-1" jobType={jobType} onOpenJob={onOpenJob} /></QueryClientProvider>) })
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
  return container.textContent ?? ''
}

describe('JobChanges', () => {
  it('lists what a send carried, old → new, with each answer and the counts it knows', async () => {
    answers.map.set('/jobs/job-1/changes/summary', { mode: 'SEND_LOG', outcome: null, counts: { ...counts, sent: 3, applied: 1, unchanged: 0, pending: 0, rejected: 1, uncertain: 1 }, spawnedJobs: [], sourceTrigger: 'ORDER', sourceOrderNumber: '10045' })
    answers.map.set('/jobs/job-1/changes', { items: [
      line({ result: 'APPLIED' }),
      line({ result: 'REJECTED', errorMessage: 'Fiyat aralık dışında', barcode: '8690002' }),
      line({ result: 'UNKNOWN', barcode: '8690003' })
    ], nextCursor: null, hasMore: false, page: 1, pageSize: 25, totalCount: 3 })

    const text = await show('PRICE_INVENTORY_SYNC')

    expect(text).toContain('Gidenler ve Değişenler')
    expect(text).toContain('Gönderilen3')
    // Not recorded: a dash, never a zero.
    expect(text).toContain('Taranan—')
    expect(text).toContain('6 → 5')
    expect(text).toContain('−1')
    expect(text).toContain('Gönderildi · Kabul Edildi')
    expect(text).toContain('Reddedildi')
    expect(text).toContain('Fiyat aralık dışında')
    expect(text).toContain('Belirsiz · Yeniden Denenecek')
    expect(text).toContain('Sipariş #10045')
    expect(text).toContain('Varyant: Beyaz / M')
    expect(text).toContain('Toplam 3 adet')
  })

  it('says why a scan sent nothing and links the send jobs it queued', async () => {
    answers.map.set('/jobs/job-1/changes/summary', { mode: 'SEND_LOG', outcome: 'AUTOMATIC_WRITES_OFF', counts: { ...counts, sent: 0 }, spawnedJobs: [], sourceTrigger: 'RECONCILE', sourceOrderNumber: null })
    answers.map.set('/jobs/job-1/changes', { items: [], nextCursor: null, hasMore: false })

    const text = await show('STOCK_RECONCILIATION')

    expect(text).toContain('Otomatik yazma kapalı olduğu için gönderilmedi.')
    expect(text).toContain('Bu işte listelenecek satır yok.')
    expect(text).not.toContain('Doğan Gönderim İşleri')
  })

  it('opens a send job a scan queued', async () => {
    const opened: string[] = []
    answers.map.set('/jobs/job-1/changes/summary', { mode: 'SEND_LOG', outcome: null, counts: { ...counts, sent: 1, applied: 1 }, spawnedJobs: [{ id: 'send-9', jobType: 'PRICE_INVENTORY_SYNC', status: 'SUCCEEDED', createdAt: '2026-11-18T09:00:00Z', completedAt: null, lastErrorCode: null }], sourceTrigger: 'RECONCILE', sourceOrderNumber: null })
    answers.map.set('/jobs/job-1/changes', { items: [line({ source: 'RECONCILE', jobId: 'send-9' })], nextCursor: null, hasMore: false })

    const text = await show('STOCK_RECONCILIATION', id => opened.push(id))
    expect(text).toContain('Doğan Gönderim İşleri')
    expect(text).toContain('Uzlaştırma')
    const link = [...container.querySelectorAll('button')].find(button => button.textContent?.includes('Fiyat-Stok Gönderimi'))!
    await act(async () => { link.click() })
    expect(opened).toEqual(['send-9'])
  })

  it('shows a product import’s changed variants and an empty import in words', async () => {
    answers.map.set('/jobs/job-1/changes/summary', { mode: 'PRODUCT_IMPORT', outcome: 'NO_CHANGES', counts: { ...counts, priceChanged: 0, stockChanged: 0 }, spawnedJobs: [], sourceTrigger: null, sourceOrderNumber: null })
    answers.map.set('/jobs/job-1/changes', { items: [], nextCursor: null, hasMore: false })

    const text = await show('PRODUCT_SYNC')

    expect(text).toContain('Panelde Değişenler')
    expect(text).toContain('Fiyatı Değişen0')
    expect(text).toContain('Bu çekimde panelde fiyatı veya stoğu değişen varyant bulunmadı.')
  })

  it('pages the list 25 at a time, filters by a count chip and goes back to page 1 when the filter changes', async () => {
    answers.map.set('/jobs/job-1/changes/summary', { mode: 'SEND_LOG', outcome: null, counts: { ...counts, sent: 51, rejected: 7 }, spawnedJobs: [], sourceTrigger: null, sourceOrderNumber: null })
    answers.map.set('/jobs/job-1/changes', { items: [line({})], nextCursor: null, hasMore: false, page: 1, pageSize: 25, totalCount: 51 })

    const text = await show('PRICE_INVENTORY_SYNC')
    expect(text).toContain('Toplam 51 adet')
    expect(answers.calls).toContain('/jobs/job-1/changes?page=1&pageSize=25')
    const page2 = [...container.querySelectorAll<HTMLButtonElement>('.rr-pages button')].find(button => button.textContent === '2')!
    await act(async () => { page2.click() })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    expect(answers.calls).toContain('/jobs/job-1/changes?page=2&pageSize=25')
    const chip = [...container.querySelectorAll<HTMLButtonElement>('.jc-chip')].find(button => button.textContent?.includes('Reddedilen'))!
    await act(async () => { chip.click() })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    expect(answers.calls).toContain('/jobs/job-1/changes?page=1&pageSize=25&groups=rejected')
  })

  it('downloads the Excel file of the current search and filter', async () => {
    URL.createObjectURL = () => 'blob:x'; URL.revokeObjectURL = () => undefined
    answers.map.set('/jobs/job-1/changes/summary', { mode: 'SEND_LOG', outcome: null, counts: { ...counts, sent: 1, rejected: 1 }, spawnedJobs: [], sourceTrigger: null, sourceOrderNumber: null })
    answers.map.set('/jobs/job-1/changes', { items: [line({})], nextCursor: null, hasMore: false, page: 1, pageSize: 25, totalCount: 1 })
    await show('PRICE_INVENTORY_SYNC')
    const chip = [...container.querySelectorAll<HTMLButtonElement>('.jc-chip')].find(button => button.textContent?.includes('Reddedilen'))!
    await act(async () => { chip.click() })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    const button = [...container.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.includes('Excel’e Aktar'))!
    await act(async () => { button.click() })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    expect(answers.calls).toContain('/jobs/job-1/changes/export.xlsx?groups=rejected')
  })
})

describe('job-changes helpers', () => {
  it('applies to the price/stock sends, the stock scan and dispatch and the product imports only', () => {
    expect(['PRICE_INVENTORY_SYNC', 'STOCK_RECONCILIATION', 'STOCK_PROJECTION_DISPATCH', 'PRODUCT_SYNC', 'HEPSIBURADA_PRODUCT_SYNC'].every(jobHasChanges)).toBe(true)
    expect(jobHasChanges('TRENDYOL_ORDER_SYNC')).toBe(false)
  })

  it('keeps an unknown count unknown', () => {
    const items = jobChangeCountItems({ mode: 'SEND_LOG', counts: { ...counts, sent: 0 } } as Pick<JobChangesSummary, 'mode' | 'counts'>)
    expect(items.find(item => item.key === 'sent')?.value).toBe(0)
    expect(items.find(item => item.key === 'scanned')?.value).toBeNull()
  })

  it('words the outcomes and the sources', () => {
    expect(jobChangesOutcomeText('ORDER_READ_PENDING')).toContain('sipariş okuması bekleniyor')
    expect(jobChangesOutcomeText('NO_CHANGES')).toBe('Bu taramada gönderilecek değişiklik bulunmadı.')
    expect(jobChangesOutcomeText(null)).toBeNull()
    expect(jobChangeSourceLabel({ source: 'MANUAL_STOCK', result: 'APPLIED' })).toBe('Elle Stok')
    expect(jobChangeSourceLabel({ source: 'ORDER', result: 'APPLIED' })).toBe('Sipariş')
    expect(jobChangeSourceLabel({ source: 'MARKETPLACE_IMPORT', result: 'CHANGED' })).toBe('Pazaryerinden Çekim')
  })

  it('shows a refusal with the marketplace’s reason and the difference with its sign', () => {
    expect(jobChangeResult({ result: 'REJECTED', errorCode: 'X', errorMessage: 'Stok negatif' })).toMatchObject({ label: 'Reddedildi', tone: 'danger', detail: 'Stok negatif' })
    expect(jobChangeResult({ result: 'APPLIED', errorCode: null, errorMessage: null }).detail).toBeNull()
    expect(jobChangeDifference(130, 120, true)).toBe('−₺10')
    expect(jobChangeDifference(799, 749.9, true)).toBe('−₺49,10')
    expect(jobChangeDifference(null, 5)).toBeNull()
    expect(jobChangesQueryString('  869 ', null)).toBe('?limit=50&search=869')
  })
})
