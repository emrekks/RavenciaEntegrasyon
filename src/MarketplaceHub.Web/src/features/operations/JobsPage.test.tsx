import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../../shared/api'

const apiState = vi.hoisted(() => ({ calls: [] as string[], failNextPage: false, extraCountRows: [] as Array<{ status: string; jobType: string; count: number }>, sendLog: { items: [] as unknown[], nextCursor: null as string | null, hasMore: false } }))

// The page shows the last 24 hours by default, so the fixture's jobs are dated relative to the run.
function hoursAgo(hours: number) {
  return new Date(Date.now() - hours * 3_600_000).toISOString()
}

function job(id: string, jobType: string, status: string, createdAt: string) {
  return {
    id, connectionId: null, jobType, status, attemptCount: 1, maxAttempts: 3, availableAt: createdAt, lastErrorCode: null, lastErrorSummary: null,
    correlationId: `corr-${id}`, createdAt, startedAt: createdAt, completedAt: createdAt, marketplace: 'Trendyol', externalId: null, firstFailedAt: null,
    lastFailedAt: null, nextRetryAt: null, batchCount: 1, progressCurrent: 0, progressTotal: null, progressPercent: null, progressLabel: null,
    progressReceived: 0, progressProcessed: 0, progressSkipped: 0, progressFailed: 0, lastAttemptStartedAt: createdAt, heartbeatAt: null
  }
}

vi.mock('../../shared/api', () => {
  const mocked = {
  loadAllPages: vi.fn(async () => ({ items: [
    { id: 'ty', platformCode: 'TRENDYOL', status: 'ACTIVE' },
    { id: 'ef', platformCode: 'TRENDYOL_EFATURAM', status: 'ACTIVE' },
    { id: 'n11', platformCode: 'N11', status: 'DISABLED' }
  ], nextCursor: null, hasMore: false })),
  hubApi: vi.fn(async (path: string) => {
    apiState.calls.push(path)
    if (path.startsWith('/jobs/counts') && path.includes('platform=TRENDYOL_EFATURAM')) return [{ status: 'DEAD', jobType: 'INVOICE_SUBMIT', count: 4 }]
    if (path.startsWith('/jobs/counts')) return [
      { status: 'SUCCEEDED', jobType: 'TRENDYOL_ORDER_SYNC', count: 2 },
      { status: 'DEAD', jobType: 'TRENDYOL_ORDER_SYNC', count: 1 },
      { status: 'SUCCEEDED', jobType: 'PRICE_INVENTORY_SYNC', count: 1 },
      ...apiState.extraCountRows
    ]
    if (path.startsWith('/jobs/metrics')) return []
    if (path.startsWith('/price-stock-send-log?')) return apiState.sendLog
    if (path === '/jobs/job-sent') return { job: job('job-sent', 'PRICE_INVENTORY_SYNC', 'SUCCEEDED', hoursAgo(1)), attempts: [], order: null, change: null, relatedOrders: [] }
    if (path.startsWith('/jobs/page?')) {
      const params = new URLSearchParams(path.slice(path.indexOf('?') + 1))
      if (params.get('after') === 'cursor-1') {
        if (apiState.failNextPage) throw new Error('Listenin devamı okunamadı. Listeyi yenileyip tekrar deneyin.')
        return { items: [job('job-3', 'TRENDYOL_ORDER_SYNC', 'DEAD', hoursAgo(3))], nextCursor: null, hasMore: false }
      }
      return {
        items: [job('job-1', 'TRENDYOL_ORDER_SYNC', 'SUCCEEDED', hoursAgo(1)), job('job-2', 'TRENDYOL_ORDER_SYNC', 'SUCCEEDED', hoursAgo(2))],
        nextCursor: 'cursor-1',
        hasMore: true
      }
    }
    throw new Error(`Test API fixture does not handle ${path}`)
  })
  }
  // Polled reads go through hubPoll; the fixture answers them the same way.
  return { ...mocked, hubPoll: mocked.hubApi }
})

import { formatJobTime, JobsPage } from './pages'

const me = { id: 'user-1', email: 'ops@example.test', displayName: 'Operasyon', role: 'OWNER', state: 'ACTIVE', tenantId: 'tenant-1' } as unknown as Me

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

function button(label: string) {
  return Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent?.trim() === label) ?? null
}

function pageRequests() {
  return apiState.calls.filter(path => path.startsWith('/jobs/page?')).map(path => new URLSearchParams(path.slice(path.indexOf('?') + 1)))
}

function rowCount() {
  return container.querySelectorAll('.jobs-reference-row').length
}

let currentSearch = ''
function LocationProbe() {
  currentSearch = useLocation().search
  return null
}

async function renderPage(url = '/jobs') {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[url]}><JobsPage me={me} /><LocationProbe /></MemoryRouter></QueryClientProvider>) })
  await settle()
  await settle()
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.failNextPage = false
  apiState.extraCountRows = []
  apiState.sendLog = { items: [], nextCursor: null, hasMore: false }
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
  vi.restoreAllMocks()
})

describe('JobsPage cursor paging', () => {
  it('loads the first server page and the next one on demand', async () => {
    await renderPage()

    expect(rowCount()).toBe(2)
    expect(pageRequests().every(params => params.get('after') === null && params.get('limit') === '200')).toBe(true)
    expect(container.textContent).toContain('2 kayıt yüklendi, daha eskileri var')

    await act(async () => { button('Daha Fazla Yükle')!.click() })
    await settle()

    expect(pageRequests().some(params => params.get('after') === 'cursor-1')).toBe(true)
    expect(rowCount()).toBe(3)
    expect(button('Daha Fazla Yükle')).toBeNull()
    expect(container.textContent).toContain('Toplam 3 kayıt')
  })

  it('sends the status chip and the tab job types to the server with every page', async () => {
    await renderPage()

    await act(async () => { container.querySelector<HTMLElement>('.jobs-reference-status-summary .error')!.click() })
    await settle()
    const statusRequest = pageRequests().at(-1)!
    expect(statusRequest.get('status')).toBe('BLOCKED,MANUAL_REVIEW,DEAD')
    expect(statusRequest.get('after')).toBeNull()

    await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent?.includes('Sipariş/Kargo'))!.click() })
    await settle()
    const jobTypes = pageRequests().at(-1)!.get('jobTypes')!.split(',')
    expect(jobTypes).toContain('TRENDYOL_ORDER_SYNC')
    expect(jobTypes).toContain('SHIPMENT_ACTION')
    expect(jobTypes).not.toContain('PRICE_INVENTORY_SYNC')
    expect(currentSearch).toBe('?durum=hata&kategori=siparis')
  })

  it('keeps the loaded pages when a count refresh brings a new job type (G22)', async () => {
    await renderPage('/jobs?kategori=siparis')
    await act(async () => { button('Daha Fazla Yükle')!.click() })
    await settle()
    expect(rowCount()).toBe(3)
    const firstPageReads = pageRequests().filter(params => params.get('after') === null).length

    apiState.extraCountRows = [{ status: 'SUCCEEDED', jobType: 'SHIPMENT_ACTION', count: 5 }]
    await act(async () => { await client.invalidateQueries({ queryKey: ['jobs', 'counts'] }) })
    await settle()

    expect(rowCount()).toBe(3)
    expect(pageRequests().filter(params => params.get('after') === null).length).toBe(firstPageReads)
  })

  it('polls the list only while its first page is loaded (W22, G24)', async () => {
    await renderPage()
    const listQuery = () => client.getQueryCache().findAll({ queryKey: ['jobs', 'page'] }).at(-1)!
    const interval = () => ((listQuery().options as { refetchInterval: (query: unknown) => unknown }).refetchInterval)(listQuery())
    expect(interval()).toBe(15_000)

    await act(async () => { button('Daha Fazla Yükle')!.click() })
    await settle()
    expect(interval()).toBe(false)
  })
})

describe('JobsPage address state (W16)', () => {
  it('opens with the filters and page size from the address', async () => {
    await renderPage('/jobs?durum=hata&adet=50&aralik=7g')

    const request = pageRequests().at(-1)!
    expect(request.get('status')).toBe('BLOCKED,MANUAL_REVIEW,DEAD')
    expect(request.get('since')).not.toBeNull()
    expect(container.querySelector('.jobs-reference-range')!.textContent).toContain('Son 7 Gün')
    act(() => { container.querySelector<HTMLButtonElement>('.jobs-reference-filter-toggle')!.click() })
    expect(container.querySelector<HTMLSelectElement>('select[aria-label="Sayfa başına işlem"]')!.value).toBe('50')
  })

  it('writes the search into the address and drops the page number on a filter change', async () => {
    await renderPage('/jobs?sayfa=2')
    const search = container.querySelector<HTMLInputElement>('input[aria-label="Correlation ID ile işlem ara"]')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(search, 'corr-job-1')
      search.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await settle()
    expect(currentSearch).toBe('?ara=corr-job-1')
    expect(rowCount()).toBe(1)
  })

  it('keeps the loaded rows and says so when the next page fails', async () => {
    apiState.failNextPage = true
    await renderPage()

    await act(async () => { button('Daha Fazla Yükle')!.click() })
    await settle()

    expect(rowCount()).toBe(2)
    expect(container.textContent).toContain('Listenin devamı okunamadı.')
  })
})

describe('JobsPage platform filter', () => {
  function countRequests() {
    return apiState.calls.filter(path => path.startsWith('/jobs/counts')).map(path => new URLSearchParams(path.slice(path.indexOf('?') + 1)))
  }

  function openFilters() {
    act(() => { container.querySelector<HTMLButtonElement>('.jobs-reference-filter-toggle')!.click() })
    return container.querySelector<HTMLSelectElement>('select[aria-label="Platform"]')!
  }

  it('offers Tümü and every connected platform, and sends the choice to the list and the counts', async () => {
    await renderPage()
    const select = openFilters()

    expect(Array.from(select.options).map(option => option.textContent)).toEqual(['Tümü', 'Trendyol', 'N11', 'E-Faturam'])
    expect(pageRequests().every(params => params.get('platform') === null)).toBe(true)

    await act(async () => { select.value = 'TRENDYOL_EFATURAM'; select.dispatchEvent(new Event('change', { bubbles: true })) })
    await settle()
    await settle()

    expect(currentSearch).toBe('?platform=trendyol_efaturam')
    expect(pageRequests().at(-1)!.get('platform')).toBe('TRENDYOL_EFATURAM')
    expect(countRequests().at(-1)!.get('platform')).toBe('TRENDYOL_EFATURAM')
    // The status row and the tab counts come from the platform's counts.
    const summary = container.querySelector('.jobs-reference-status-summary')!.textContent
    expect(summary).toContain('Hata 4')
    expect(summary).toContain('Başarılı 0')
    const invoiceTab = Array.from(container.querySelectorAll('[role="tab"]')).find(tab => tab.textContent?.includes('Faturalar'))!
    expect(invoiceTab.textContent).toContain('4')
  })

  it('keeps the platform from the URL on load and through the next page', async () => {
    await renderPage('/jobs?platform=n11')

    expect(pageRequests().at(-1)!.get('platform')).toBe('N11')
    expect(countRequests().at(-1)!.get('platform')).toBe('N11')
    expect(container.querySelector('.jobs-reference-filter-toggle')!.textContent).toContain('N11')

    await act(async () => { button('Daha Fazla Yükle')!.click() })
    await settle()
    const next = pageRequests().find(params => params.get('after') === 'cursor-1')!
    expect(next.get('platform')).toBe('N11')

    const select = openFilters()
    await act(async () => { select.value = ''; select.dispatchEvent(new Event('change', { bubbles: true })) })
    await settle()
    expect(currentSearch).toBe('')
    expect(pageRequests().at(-1)!.get('platform')).toBeNull()
  })
})

describe('JobsPage Loglar tab (price/stock send log)', () => {
  function logRequests() {
    return apiState.calls.filter(path => path.startsWith('/price-stock-send-log?')).map(path => new URLSearchParams(path.slice(path.indexOf('?') + 1)))
  }

  // The log view is a lazy chunk: under a full parallel run its import takes longer than a few ticks, so the test waits
  // for its first read and its rendered state instead.
  async function logViewLoaded() {
    for (let tick = 0; tick < 200 && (logRequests().length === 0 || !container.querySelector('.psl-empty, .psl-row')); tick++) await settle()
    await settle()
  }

  it('opens from the tab, keeps the job list unread and explains the empty log', async () => {
    await renderPage()
    const jobReads = pageRequests().length
    await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent?.includes('Loglar'))!.click() })
    await logViewLoaded()

    expect(currentSearch).toBe('?sekme=loglar')
    expect(logRequests().at(-1)!.get('limit')).toBe('50')
    expect(pageRequests().length).toBe(jobReads)
    expect(container.textContent).toContain('Henüz gönderim kaydı yok.')
    expect(container.textContent).toContain('Otomatik fiyat-stok yazması şu an kapalı')
    expect(container.querySelector('.jobs-reference-status-summary')).toBeNull()
  })

  it('is a main tab beside İş Takibi, above the category tabs, and İş Takibi brings the category back', async () => {
    await renderPage('/jobs?kategori=siparis')
    const main = container.querySelector('[role="tablist"][aria-label="İşlem Takibi görünümü"]')!
    expect(Array.from(main.querySelectorAll('[role="tab"]')).map(tab => tab.textContent)).toEqual(['İş Takibi', 'Loglar'])
    const categories = container.querySelector('[role="tablist"][aria-label="İşlem kategorileri"]')!
    expect(categories.textContent).not.toContain('Loglar')
    expect(main.compareDocumentPosition(categories) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()

    await act(async () => { Array.from(main.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent === 'Loglar')!.click() })
    await logViewLoaded()
    expect(container.querySelector('[aria-label="İşlem kategorileri"]')).toBeNull()

    await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>('[aria-label="İşlem Takibi görünümü"] [role="tab"]')).find(tab => tab.textContent === 'İş Takibi')!.click() })
    await settle()
    expect(currentSearch).toBe('?kategori=siparis')
    expect(container.querySelector('[aria-label="İşlem kategorileri"] [aria-selected="true"]')!.textContent).toContain('Sipariş')
  })

  it('shows a sent line and opens its job', async () => {
    apiState.sendLog = {
      items: [{
        id: 'log-1', createdAt: '2026-10-30T09:15:00Z', completedAt: null, connectionId: 'ty', platformCode: 'TRENDYOL', storeName: 'Ravencia Trendyol',
        jobId: 'job-sent', variantId: 'v-1', productId: 'p-1', productTitle: 'Keten Gömlek', imageUrl: null, barcode: '8680001', stockCode: 'KG-1', modelCode: 'KG',
        previousQuantity: 5, newQuantity: 4, previousSalePrice: 799, newSalePrice: 749, previousListPrice: 999, newListPrice: 999, currency: 'TRY',
        trigger: 'ORDER', result: 'REJECTED', errorCode: 'TY-400', errorMessage: 'Fiyat aralık dışı', externalRequestId: 'batch-1'
      }],
      nextCursor: null,
      hasMore: false
    }
    await renderPage('/jobs?sekme=loglar')
    await logViewLoaded()

    const row = container.querySelector('.psl-row')!
    expect(row.textContent).toContain('12:15:00')
    expect(row.textContent).toContain('Ravencia Trendyol')
    expect(row.textContent).toContain('Keten Gömlek')
    expect(row.textContent).toContain('5 → 4')
    expect(row.textContent).toContain('₺799 → ₺749')
    expect(row.textContent).not.toContain('Liste:')
    expect(row.textContent).toContain('Sipariş')
    expect(row.querySelector('.psl-result')!.getAttribute('title')).toBe('Pazaryeri satırı reddetti. — TY-400: Fiyat aralık dışı')

    await act(async () => { button('İşi Aç')!.click() })
    await settle()
    expect(apiState.calls).toContain('/jobs/job-sent')
  })

  it('sends the result filter and clears it with Temizle', async () => {
    await renderPage('/jobs?sekme=loglar')
    await logViewLoaded()
    const select = container.querySelector<HTMLSelectElement>('select[aria-label="Sonuç"]')!
    await act(async () => { select.value = 'FAILED'; select.dispatchEvent(new Event('change', { bubbles: true })) })
    await settle()
    expect(logRequests().at(-1)!.get('results')).toBe('FAILED')
    expect(container.textContent).toContain('Seçili filtrelerle eşleşen gönderim kaydı bulunamadı.')

    await act(async () => { button('Temizle')!.click() })
    await settle()
    expect(logRequests().at(-1)!.get('results')).toBeNull()
  })
})

// Audit 29 Oct 2026, W-5: "Bugün" is the Istanbul day even when the browser's clock is on another zone.
describe('JobsPage day label', () => {
  // A browser on UTC: its local clock reads are the UTC ones.
  const spies: Array<{ mockRestore: () => void }> = []
  beforeEach(() => {
    for (const [local, utc] of [['getFullYear', 'getUTCFullYear'], ['getMonth', 'getUTCMonth'], ['getDate', 'getUTCDate'], ['getHours', 'getUTCHours'], ['getMinutes', 'getUTCMinutes']] as const)
      spies.push(vi.spyOn(Date.prototype, local).mockImplementation(function (this: Date) { return this[utc]() }))
  })
  afterEach(() => { spies.splice(0).forEach(spy => spy.mockRestore()) })

  it('calls only a job of the Istanbul day today', () => {
    // 01:00 in Istanbul (22:00 UTC the day before): a job at 23:30 Istanbul time was yesterday.
    const now = new Date('2026-10-28T22:00:00Z')
    expect(formatJobTime('2026-10-28T20:30:00Z', now)).toEqual({ time: '23:30:00', day: '28.10.2026' })
    expect(formatJobTime('2026-10-28T21:30:00Z', now)).toEqual({ time: '00:30:00', day: 'Bugün' })
  })
})
