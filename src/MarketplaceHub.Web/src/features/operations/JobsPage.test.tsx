import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../../shared/api'

const apiState = vi.hoisted(() => ({ calls: [] as string[], retryGate: Promise.resolve() as Promise<void>, failNextPage: false, extraCountRows: [] as Array<{ status: string; jobType: string; count: number }>, sendLog: { items: [] as unknown[], nextCursor: null as string | null, hasMore: false }, sendLogCounts: [] as Array<{ result: string; count: number }>, activity: { items: [] as unknown[], nextCursor: null as string | null, hasMore: false }, activityCounts: [] as Array<{ kind: string; count: number }> }))

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
    if (path.startsWith('/price-stock-send-log/counts')) return apiState.sendLogCounts
    if (path.startsWith('/product-activity?')) return apiState.activity
    if (path.startsWith('/product-activity/counts')) return apiState.activityCounts
    if (path === '/price-stock-monitor') return { generatedAt: hoursAgo(0), connections: [{
      connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Ravencia Trendyol', status: 'ACTIVE', masterSwitchOn: true, priceWriteEnabled: true, stockWriteEnabled: true,
      automaticWritesOpen: true, lastSuccessfulPushAt: hoursAgo(1), queuedJobs: 2, waitingForOrderRead: 0, failedPushes7d: 0, manualReviewPushes: 0, invalidOffers: 0,
      notFoundLinks: 0, rejectedLinks: 0, latestFailure: null, invalidOfferSamples: [], orderSource: 'WEBHOOK', lastNotificationAt: hoursAgo(1), orderReadIntervalSeconds: 900
    }] }
    if (path === '/price-stock-monitor/emergency-stop') return { connectionsStopped: 1, policiesDisabled: 2, jobsCancelled: 3 }
    if (path === '/jobs/job-1/retry') { await apiState.retryGate; return { job: job('job-1', 'TRENDYOL_ORDER_SYNC', 'PENDING', hoursAgo(1)), attempts: [], order: null, change: null, relatedOrders: [] } }
    if (path === '/jobs/job-1' || path === '/jobs/job-2') { const id = path.slice(6); return { job: job(id, 'TRENDYOL_ORDER_SYNC', 'DEAD', hoursAgo(1)), attempts: [], order: null, change: null, relatedOrders: [] } }
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
  apiState.sendLogCounts = []
  apiState.activity = { items: [], nextCursor: null, hasMore: false }
  apiState.activityCounts = []
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
  vi.restoreAllMocks()
})

describe('JobsPage detail actions', () => {
  it('keeps another opened job in the drawer when a retry answers, and locks only the job it was for', async () => {
    let release = () => undefined as void
    apiState.retryGate = new Promise<void>(resolve => { release = resolve })
    await renderPage()
    // The list shows the newest first: job-1, then job-2.
    const row = (id: 'job-1' | 'job-2') => container.querySelectorAll<HTMLElement>('.jobs-reference-row')[id === 'job-1' ? 0 : 1]
    const correlation = () => container.querySelector('.job-detail-facts')?.textContent ?? ''
    await act(async () => { row('job-1').click() })
    await settle()
    expect(correlation()).toContain('corr-job-1')
    await act(async () => { button('Manuel Yeniden Dene')!.click() })
    await settle()
    expect(button('Manuel Yeniden Dene')!.disabled).toBe(true)

    await act(async () => { row('job-2').click() })
    await settle()
    expect(correlation()).toContain('corr-job-2')
    expect(button('Manuel Yeniden Dene')!.disabled).toBe(false)

    await act(async () => { release() })
    await settle()
    expect(correlation()).toContain('corr-job-2')
  })
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

describe('JobsPage API Sağlık tab (price/stock sending health)', () => {
  it('shows the sending health per connection, its explanation behind the help mark, and keeps the job list unread', async () => {
    await renderPage('/jobs?kategori=siparis')
    const jobReads = pageRequests().length
    const tab = Array.from(container.querySelectorAll<HTMLButtonElement>('[aria-label="İşlem Takibi görünümü"] [role="tab"]')).find(item => item.textContent === 'API Sağlık')!
    await act(async () => { tab.click() })
    for (let tick = 0; tick < 200 && !container.querySelector('.psm-row:not(.is-head)'); tick++) await settle()

    expect(currentSearch).toBe('?kategori=siparis&sekme=api-saglik')
    expect(container.querySelector('#psm-title')!.textContent).toContain('Fiyat ve Stok Gönderimi')
    expect(container.querySelector('.psm-head p')).toBeNull()
    const row = container.querySelector('.psm-row:not(.is-head)')!
    expect(row.textContent).toContain('Ravencia Trendyol')
    // The platform shows as its logo; a name that already says it is not followed by the platform name again.
    expect(row.querySelector('img.psm-logo')).not.toBeNull()
    expect(row.querySelector('.psm-conn small')).toBeNull()
    expect(row.querySelector('[data-label="Son Başarılı"] time')!.textContent).toBe('1 sa önce')
    expect(row.querySelector('[data-label="Kuyruk"]')!.className).toContain('is-warn')
    expect(row.querySelector('[data-label="Başarısız"]')!.className).toContain('is-muted')
    expect(container.querySelector('.psm-summary')!.textContent).toContain('Tümü Açık')
    expect(container.querySelector('.jobs-category-segment, [aria-label="İşlem kategorileri"]')).toBeNull()
    expect(pageRequests().length).toBe(jobReads)
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

  async function activityViewLoaded() {
    for (let tick = 0; tick < 200 && !container.querySelector('.pal .psl-empty, .pal-row'); tick++) await settle()
    await settle()
  }

  it('opens from the tab, keeps the job list unread and explains the empty log', async () => {
    await renderPage()
    const jobReads = pageRequests().length
    await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent?.includes('Loglar'))!.click() })
    await activityViewLoaded()
    // Loglar opens on Ürün Hareketleri; Pazaryeri Gönderimleri is the segment beside it.
    expect(currentSearch).toBe('?sekme=loglar')
    const views = container.querySelector('[role="tablist"][aria-label="Log görünümü"]')!
    expect(Array.from(views.querySelectorAll('[role="tab"]')).map(tab => [tab.textContent, tab.getAttribute('aria-selected')])).toEqual([['Ürün Hareketleri', 'true'], ['Pazaryeri Gönderimleri', 'false']])
    await act(async () => { Array.from(views.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent === 'Pazaryeri Gönderimleri')!.click() })
    await logViewLoaded()

    expect(currentSearch).toBe('?sekme=loglar&kayit=gonderimler')
    expect(logRequests().at(-1)!.get('limit')).toBe('50')
    expect(pageRequests().length).toBe(jobReads)
    expect(container.textContent).toContain('Henüz gönderim kaydı yok.')
    // The empty log does not claim whether automatic writes are on: it cannot know, API Sağlık does.
    expect(container.textContent).not.toContain('şu an kapalı')
    expect(container.textContent).toContain('API Sağlık gösterir')
    // The job list's counts give way to the log's own result summary.
    expect(container.querySelector('[aria-label="İşlem durum özeti"]')).toBeNull()
    expect(container.querySelector('[aria-label="Gönderim sonuç özeti"]')).not.toBeNull()
  })

  it('has the job list heading controls of its own: range, Filtrele and a result summary that filters', async () => {
    apiState.sendLogCounts = [{ result: 'APPLIED', count: 3 }, { result: 'UNCHANGED', count: 1 }, { result: 'REJECTED', count: 2 }, { result: 'SUBMITTED', count: 1 }]
    await renderPage('/jobs?sekme=loglar&kayit=gonderimler&aralik=7g')
    await logViewLoaded()
    const countRequests = () => apiState.calls.filter(path => path.startsWith('/price-stock-send-log/counts'))

    // The log opens on its whole history whatever range the job list holds.
    const range = container.querySelector<HTMLButtonElement>('.jobs-reference-heading-actions .jobs-reference-range')!
    expect(range.textContent).toBe('Tüm Zamanlar')
    expect(logRequests().at(-1)!.get('from')).toBeNull()
    const summary = container.querySelector('[aria-label="Gönderim sonuç özeti"]')!
    expect(Array.from(summary.querySelectorAll('[role="button"]')).map(chip => chip.textContent)).toEqual(['Başarılı 4', 'Sorunlu 2', 'Bekleyen 1', 'Yeni Sürüm 0'])

    await act(async () => { range.click() })
    await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>('.jobs-reference-range-menu button')).find(item => item.textContent === 'Son 24 Saat')!.click() })
    await settle()
    const since = Date.parse(logRequests().at(-1)!.get('from')!)
    expect(Math.abs(Date.now() - 24 * 60 * 60 * 1000 - since)).toBeLessThan(60_000)
    // The counts and the list each work out the range start when they are built; a millisecond apart is the same range.
    expect(Math.abs(Date.parse(new URLSearchParams(countRequests().at(-1)!.split('?')[1]).get('from')!) - since)).toBeLessThan(1_000)
    expect(container.querySelector('.jobs-reference-heading-actions .jobs-reference-range')!.textContent).toBe('Son 24 Saat')
    // The job list's range in the address is untouched.
    expect(currentSearch).toContain('aralik=7g')

    // Sorunlu picks its whole group; the counts are read without the result filter.
    const problems = Array.from(summary.querySelectorAll<HTMLElement>('[role="button"]')).find(chip => chip.textContent?.startsWith('Sorunlu'))!
    await act(async () => { problems.click() })
    await settle()
    expect(logRequests().at(-1)!.get('results')).toBe('REJECTED,FAILED,UNKNOWN')
    expect(countRequests().at(-1)).not.toContain('results=')
    expect(problems.getAttribute('aria-pressed')).toBe('true')
    await act(async () => { container.querySelector<HTMLButtonElement>('.jobs-reference-filter-toggle')!.click() })
    expect(container.querySelector('button[aria-label="Sonuç: 3 Sonuç"]')).not.toBeNull()
    expect(container.querySelector('.jobs-reference-filter-toggle .psl-clear-count')!.textContent).toBe('1')
  })

  it('is a main tab beside İş Takibi, above the category tabs, and İş Takibi brings the category back', async () => {
    await renderPage('/jobs?kategori=siparis')
    const main = container.querySelector('[role="tablist"][aria-label="İşlem Takibi görünümü"]')!
    expect(Array.from(main.querySelectorAll('[role="tab"]')).map(tab => tab.textContent)).toEqual(['İş Takibi', 'Loglar', 'API Sağlık'])
    const categories = container.querySelector('[role="tablist"][aria-label="İşlem kategorileri"]')!
    expect(categories.textContent).not.toContain('Loglar')
    // The second level is a segment with every category in it: no Diğer menu, no icons.
    expect(categories.className).toBe('jobs-category-segment')
    expect(Array.from(categories.querySelectorAll('.jobs-category-segment-label')).map(label => label.textContent)).toEqual(['Tüm İşlemler', 'Sipariş/Kargo', 'Fiyat/Stok', 'Ürün/Yayın', 'Faturalar', 'İadeler'])
    expect(categories.querySelector('[aria-haspopup], svg')).toBeNull()
    expect(main.compareDocumentPosition(categories) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()

    await act(async () => { Array.from(main.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent === 'Loglar')!.click() })
    await activityViewLoaded()
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
    await renderPage('/jobs?sekme=loglar&kayit=gonderimler')
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
    await renderPage('/jobs?sekme=loglar&kayit=gonderimler')
    await logViewLoaded()
    // The filters sit in the Filtrele panel of the page heading, like the job list's.
    expect(container.querySelector('button[aria-label="Sonuç: Tüm Sonuçlar"]')).toBeNull()
    await act(async () => { container.querySelector<HTMLButtonElement>('.jobs-reference-heading-actions .jobs-reference-filter-toggle')!.click() })
    expect(container.querySelector('[role="dialog"][aria-label="Gönderim kaydı filtreleri"]')).not.toBeNull()
    const clearButton = () => Array.from(container.querySelectorAll<HTMLButtonElement>('.psl-clear'))[0]
    expect(clearButton().disabled).toBe(true)
    await act(async () => { container.querySelector<HTMLButtonElement>('button[aria-label="Sonuç: Tüm Sonuçlar"]')!.click() })
    const menu = container.querySelector('[role="group"][aria-label="Sonuç"]')!
    expect(Array.from(menu.querySelectorAll('.psl-menu-heading')).map(heading => heading.textContent)).toEqual(['Başarılı', 'Sorunlu', 'Bekleyen', 'Diğer'])
    // The Sorunlu heading picks its whole group.
    const problems = Array.from(menu.querySelectorAll<HTMLLabelElement>('.psl-menu-heading')).find(heading => heading.textContent === 'Sorunlu')!
    await act(async () => { problems.querySelector('input')!.click() })
    await settle()
    expect(logRequests().at(-1)!.get('results')).toBe('REJECTED,FAILED,UNKNOWN')
    expect(container.querySelector('button[aria-label="Sonuç: 3 Sonuç"]')).not.toBeNull()
    expect(container.textContent).toContain('Seçili filtrelerle eşleşen gönderim kaydı bulunamadı.')
    expect(clearButton().disabled).toBe(false)
    expect(clearButton().textContent).toBe('Temizle1')

    await act(async () => { clearButton().click() })
    await settle()
    expect(logRequests().at(-1)!.get('results')).toBeNull()
  })

  it('orders by Zaman, newest first by default and oldest first on a click', async () => {
    await renderPage('/jobs?sekme=loglar&kayit=gonderimler')
    await logViewLoaded()
    const header = container.querySelector('.psl-table th[aria-sort]')!
    expect(header.getAttribute('aria-sort')).toBe('descending')
    expect(logRequests().at(-1)!.get('order')).toBeNull()

    await act(async () => { header.querySelector<HTMLButtonElement>('button')!.click() })
    await settle()
    expect(container.querySelector('.psl-table th[aria-sort]')!.getAttribute('aria-sort')).toBe('ascending')
    expect(logRequests().at(-1)!.get('order')).toBe('asc')
  })
})

describe('JobsPage Loglar › Ürün Hareketleri', () => {
  const activityRequests = () => apiState.calls.filter(path => path.startsWith('/product-activity?')).map(path => new URLSearchParams(path.slice(path.indexOf('?') + 1)))
  const countRequests = () => apiState.calls.filter(path => path.startsWith('/product-activity/counts'))

  async function loaded() {
    for (let tick = 0; tick < 200 && !container.querySelector('.pal .psl-empty, .pal-row'); tick++) await settle()
    await settle()
  }

  function entry(patch: Record<string, unknown>) {
    return {
      id: 'a-1', occurredAt: hoursAgo(1), kind: 'STOCK', type: 'MANUAL_ADJUSTMENT', productId: 'p-1', productTitle: 'Keten Gömlek', imageUrl: null, variantId: 'v-1',
      sku: 'KG-1', barcode: '8680001', modelCode: 'KG', optionText: 'Renk: Siyah | Beden: M', quantityDelta: 2, previousSalePrice: null, newSalePrice: null,
      previousListPrice: null, newListPrice: null, currency: null, previousStatus: null, newStatus: null, source: 'BULK_EDIT', note: 'Toplu ürün stok düzenleme',
      connectionId: null, platformCode: null, storeName: null, orderId: null, orderNumber: null, returnId: null, returnNumber: null, actorUserId: 'u-1', actorName: 'Emre',
      ...patch
    }
  }

  it('lists stock, return, price and status changes with their source, link and user', async () => {
    apiState.activityCounts = [{ kind: 'STOCK', count: 5 }, { kind: 'RETURN', count: 1 }, { kind: 'PRICE', count: 2 }]
    apiState.activity = {
      items: [
        entry({ id: 'a-1', kind: 'RETURN', type: 'RETURN_PASS', quantityDelta: 1, source: 'RETURN', note: null, connectionId: 'ty', platformCode: 'TRENDYOL', storeName: 'Ravencia Trendyol', orderId: 'o-1', orderNumber: '10931', returnId: 'r-1', returnNumber: 'R-77' }),
        entry({ id: 'a-2', type: 'ORDER_RECEIVED', quantityDelta: -1, stockBefore: 5, stockAfter: 4, source: 'ORDER', note: null, connectionId: 'ty', platformCode: 'TRENDYOL', storeName: 'Ravencia Trendyol', orderId: 'o-2', orderNumber: '10932', actorUserId: null, actorName: null }),
        entry({ id: 'a-3', kind: 'PRICE', type: 'OFFER_PRICE', quantityDelta: null, previousSalePrice: 899, newSalePrice: 950, previousListPrice: 1200, newListPrice: 1200, currency: 'TRY', source: 'BULK_EDIT', note: null, connectionId: 'ty', platformCode: 'TRENDYOL', storeName: 'Ravencia Trendyol' }),
        entry({ id: 'a-4', kind: 'STATUS', type: 'STATUS', variantId: null, sku: null, barcode: null, optionText: null, quantityDelta: null, previousStatus: 'ACTIVE', newStatus: 'ARCHIVED', source: 'EDITOR', note: null })
      ],
      nextCursor: null,
      hasMore: false
    }
    await renderPage('/jobs?sekme=loglar')
    await loaded()

    const rows = Array.from(container.querySelectorAll('.pal-row'))
    expect(rows).toHaveLength(4)
    expect(rows[0].textContent).toContain('+1 Stok')
    expect(rows[0].textContent).toContain('İade #R-77')
    expect(rows[0].querySelector<HTMLAnchorElement>('.pal-source a')!.getAttribute('href')).toBe('/returns/r-1')
    expect(rows[0].textContent).toContain('Ravencia Trendyol')
    expect(rows[0].textContent).toContain('Emre')
    expect(rows[1].textContent).toContain('−1 · 5 → 4')
    expect(rows[1].querySelector<HTMLAnchorElement>('.pal-source a')!.getAttribute('href')).toBe('/orders?search=10932')
    expect(rows[1].textContent).toContain('Sistem')
    expect(rows[2].textContent).toContain('₺899 → ₺950')
    expect(rows[2].textContent).toContain('Mağaza Fiyatı')
    expect(rows[3].textContent).toContain('Satışa Açık → Satışa Kapalı')
    expect(rows[3].textContent).toContain('Panel')
    expect(rows[3].querySelector<HTMLAnchorElement>('.pal-product-link')!.getAttribute('href')).toBe('/products/p-1')
    const summary = container.querySelector('[aria-label="Ürün hareketi özeti"]')!
    expect(Array.from(summary.querySelectorAll('[role="button"]')).map(chip => chip.textContent)).toEqual(['Stok 5', 'Fiyat 2', 'Durum 0', 'İade 1'])
  })

  it('filters by a kind from the summary and reads the counts without it', async () => {
    await renderPage('/jobs?sekme=loglar')
    await loaded()
    expect(activityRequests().at(-1)!.get('kinds')).toBeNull()
    const prices = Array.from(container.querySelectorAll<HTMLElement>('[aria-label="Ürün hareketi özeti"] [role="button"]')).find(chip => chip.textContent?.startsWith('Fiyat'))!
    await act(async () => { prices.click() })
    await settle()
    expect(activityRequests().at(-1)!.get('kinds')).toBe('PRICE')
    expect(prices.getAttribute('aria-pressed')).toBe('true')
    expect(countRequests().at(-1)).not.toContain('kinds=')
    expect(container.textContent).toContain('Seçili filtrelerle eşleşen ürün hareketi bulunamadı.')
    await act(async () => { container.querySelector<HTMLButtonElement>('.jobs-reference-heading-actions .jobs-reference-filter-toggle')!.click() })
    expect(container.querySelector('button[aria-label="Tür: Fiyat"]')).not.toBeNull()
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
