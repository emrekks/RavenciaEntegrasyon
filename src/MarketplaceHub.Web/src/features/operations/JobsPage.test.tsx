import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../../shared/api'

const apiState = vi.hoisted(() => ({ calls: [] as string[], retryGate: Promise.resolve() as Promise<void>, failNextPage: false, extraCountRows: [] as Array<{ status: string; jobType: string; count: number }>, sendLog: { items: [] as unknown[], nextCursor: null as string | null, hasMore: false }, sendLogCounts: [] as Array<{ result: string; count: number }>, activity: { items: [] as unknown[], nextCursor: null as string | null, hasMore: false }, activityCounts: [] as Array<{ kind: string; count: number }> }))

// A job older than every loaded page and the default range, found only by the server lookup.
const oldJobId = vi.hoisted(() => '01a1631c-aa8f-79f0-8ff6-9ccd6c21023e')

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
    if (path === '/api-health-signals') return { generatedAt: hoursAgo(0), connections: [{
      connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Ravencia Trendyol',
      auth: { state: 'valid', lastRejectedAt: null, lastRejectedCode: null, lastOkAt: hoursAgo(1) }, lag: { stream: 'ORDERS', lagSeconds: 2400, expectedSeconds: 120, level: 'fault' },
      errorRate: { attempts: 100, failed: 4, remoteSide: 3, ourSide: 1, rateLimited: 0, workflowWait: 0 }, rateLimit: { refused24h: 0, waitingJobs: 0, pausedUntil: null, lastRunRefusals: 0 },
      queue: null, webhook: { used: true, lastReceivedAt: hoursAgo(1), healthy: true, lastOrderReadAt: hoursAgo(1), fallbackReadSeconds: 900 }
    }] }
    if (path === '/dashboard/integration-matrix') return [{ connectionId: 'ty', platformCode: 'TRENDYOL', displayName: 'Ravencia Trendyol', externalStoreId: '123', status: 'ACTIVE', issue: 'Ürün güncelleme listing state kayıtları eksik.', cells: {
      orders: { state: 'ok', lastSuccessAt: hoursAgo(1), lastProblemAt: null }, products: { state: 'bad', lastSuccessAt: null, lastProblemAt: hoursAgo(2) }
    } }]
    if (path === '/dashboard/bootstrap') return { metrics: { pendingOrders: 0, lateOrders: 0, pendingReturns: 0, recentJobCount: 5987, deadJobCount: 12, manualReviewJobCount: 18 } }
    if (path === '/connections/ty/instant-notifications') return { state: 'ACTIVE', endpoint: null, registeredAt: null, lastReceivedAt: hoursAgo(1), receivedLast24Hours: 4, message: null }
    if (path === '/price-stock-monitor/emergency-stop') return { connectionsStopped: 1, policiesDisabled: 2, jobsCancelled: 3 }
    if (path === '/jobs/job-1/retry') { await apiState.retryGate; return { job: job('job-1', 'TRENDYOL_ORDER_SYNC', 'PENDING', hoursAgo(1)), attempts: [], order: null, change: null, relatedOrders: [] } }
    if (path === '/jobs/job-1' || path === '/jobs/job-2') { const id = path.slice(6); return { job: job(id, 'TRENDYOL_ORDER_SYNC', 'DEAD', hoursAgo(1)), attempts: [], order: null, change: null, relatedOrders: [] } }
    if (path === `/jobs/${oldJobId}`) return { job: job(oldJobId, 'TRENDYOL_ORDER_SYNC', 'DEAD', hoursAgo(24 * 60)), attempts: [], order: null, change: null, relatedOrders: [] }
    if (path === '/jobs/job-sent') return { job: job('job-sent', 'PRICE_INVENTORY_SYNC', 'SUCCEEDED', hoursAgo(1)), attempts: [], order: null, change: null, relatedOrders: [] }
    if (path.startsWith('/jobs/page?')) {
      const params = new URLSearchParams(path.slice(path.indexOf('?') + 1))
      // The server lookup (?search=): a job matched whole by its id or correlation id, at any age.
      const term = params.get('search')
      if (term) {
        const known = [job('job-1', 'TRENDYOL_ORDER_SYNC', 'SUCCEEDED', hoursAgo(1)), job('job-2', 'TRENDYOL_ORDER_SYNC', 'SUCCEEDED', hoursAgo(2)), job(oldJobId, 'TRENDYOL_ORDER_SYNC', 'DEAD', hoursAgo(24 * 60))]
        return { items: known.filter(item => item.id === term || item.correlationId === term), nextCursor: null, hasMore: false }
      }
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
  return { ...mocked, hubPoll: mocked.hubApi, backgroundPollHeaders: {} }
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

// The filter card's fields (filter-fields.tsx): a trigger named "<field>: <value>" opens an rr-pop named after the field.
function trigger(name: string) {
  return container.querySelector<HTMLButtonElement>(`.ops-filters button.rr-trigger[aria-label^="${name}: "]`)!
}

async function openField(name: string) {
  await act(async () => { trigger(name).click() })
  return container.querySelector<HTMLElement>(`.rr-pop[aria-label="${name}"]`)!
}

// A single-choice row (a button) by its text.
function optionNamed(pop: Element, label: string) {
  return Array.from(pop.querySelectorAll<HTMLButtonElement>('button.rr-opt')).find(item => item.querySelector('.rr-grow')?.textContent === label)!
}

// A multiple-choice row's checkbox (a value or a group heading) by its text.
function checkboxNamed(pop: Element, label: string) {
  return Array.from(pop.querySelectorAll('label.rr-opt')).find(item => item.querySelector('.rr-grow')?.textContent === label)!.querySelector<HTMLInputElement>('input')!
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
    // The filter card shows the range and the phone Filtre count; Sayfa Başına sits beside Yenile.
    expect(trigger('Zaman Aralığı').textContent).toBe('Son 7 Gün')
    expect(container.querySelector('.ops-filters .rr-ftoggle .rr-fcount')!.textContent).toBe('1')
    expect(container.querySelector('.jobs-reference-toolbar .rr-psize-btn')!.textContent).toBe('50')
  })

  it('picks Yön, Durum, the range and the page size from the shared dropdowns, and Temizle resets the card', async () => {
    await renderPage()
    const direction = await openField('Yön')
    expect(Array.from(direction.querySelectorAll('.rr-opt')).map(item => item.textContent)).toEqual(['Tüm Yönler', 'Okuma', 'Yazma', 'Bildirim', 'Panel İçi'])
    await act(async () => { optionNamed(direction, 'Yazma').click() })
    await settle()
    expect(currentSearch).toBe('?yon=yazma')
    expect(container.querySelector('.rr-pop[aria-label="Yön"]')).toBeNull()

    const state = await openField('Durum')
    await act(async () => { optionNamed(state, 'Manuel İnceleme').click() })
    await settle()
    expect(pageRequests().at(-1)!.get('status')).toBe('MANUAL_REVIEW')

    const range = await openField('Zaman Aralığı')
    await act(async () => { optionNamed(range, 'Tüm Zamanlar').click() })
    await settle()
    expect(new URLSearchParams(currentSearch).get('aralik')).toBe('tum')

    await act(async () => { container.querySelector<HTMLButtonElement>('.rr-psize-btn')!.click() })
    await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>('.rr-psize-pop .rr-opt')).find(item => item.textContent === '100')!.click() })
    expect(new URLSearchParams(currentSearch).get('adet')).toBe('100')

    const clear = container.querySelector<HTMLButtonElement>('.ops-filters .ops-clear')!
    expect(clear.textContent).toBe('Temizle')
    await act(async () => { clear.click() })
    await settle()
    // The card's filters go back to their defaults; the page size is not a filter and stays.
    expect(currentSearch).toBe('?adet=100')
    expect(clear.disabled).toBe(true)
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
    expect(pageRequests().some(request => request.get('search') === 'corr-job-1' && !request.has('since'))).toBe(true)
  })

  it('finds a job by its id however old it is and opens its detail (notification links)', async () => {
    await renderPage(`/jobs?ara=${oldJobId}`)
    await settle()
    expect(rowCount()).toBe(1)
    expect(apiState.calls).toContain(`/jobs/${oldJobId}`)
    expect(container.textContent).not.toContain('eşleşen işlem yok')
  })

  it('finds a job by its correlation id beyond the loaded pages without opening it', async () => {
    await renderPage(`/jobs?ara=corr-${oldJobId}`)
    expect(rowCount()).toBe(1)
    expect(apiState.calls).not.toContain(`/jobs/${oldJobId}`)
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

  it('offers every connected platform with its logo, and sends the choice to the list and the counts', async () => {
    await renderPage()
    const pop = await openField('Platform')

    expect(Array.from(pop.querySelectorAll('.rr-opt .rr-grow')).map(option => option.textContent)).toEqual(['Trendyol', 'N11', 'Trendyol E-Faturam'])
    expect(pop.querySelectorAll('.rr-opt .rr-logo')).toHaveLength(3)
    expect(trigger('Platform').textContent).toBe('Tüm Platformlar')
    expect(pageRequests().every(params => params.get('platform') === null)).toBe(true)

    await act(async () => { checkboxNamed(pop, 'Trendyol E-Faturam').click() })
    await settle()
    await settle()

    expect(currentSearch).toBe('?platform=trendyol_efaturam')
    expect(pageRequests().at(-1)!.get('platform')).toBe('TRENDYOL_EFATURAM')
    expect(countRequests().at(-1)!.get('platform')).toBe('TRENDYOL_EFATURAM')
    expect(trigger('Platform').textContent).toBe('Trendyol E-Faturam')
    // The status row and the tab counts come from the platform's counts.
    const summary = container.querySelector('.jobs-reference-status-summary')!.textContent
    expect(summary).toContain('Hata 4')
    expect(summary).toContain('Başarılı 0')
    const invoiceTab = Array.from(container.querySelectorAll('[role="tab"]')).find(tab => tab.textContent?.includes('Faturalar'))!
    expect(invoiceTab.textContent).toContain('4')

    // Several platforms go to the server as one comma list.
    await act(async () => { checkboxNamed(container.querySelector('.rr-pop[aria-label="Platform"]')!, 'N11').click() })
    await settle()
    expect(currentSearch).toBe('?platform=trendyol_efaturam%2Cn11')
    expect(pageRequests().at(-1)!.get('platform')).toBe('TRENDYOL_EFATURAM,N11')
    expect(trigger('Platform').textContent).toBe('2 Platform')
  })

  it('keeps the platform from the URL on load and through the next page', async () => {
    await renderPage('/jobs?platform=n11')

    expect(pageRequests().at(-1)!.get('platform')).toBe('N11')
    expect(countRequests().at(-1)!.get('platform')).toBe('N11')
    expect(trigger('Platform').textContent).toBe('N11')

    await act(async () => { button('Daha Fazla Yükle')!.click() })
    await settle()
    const next = pageRequests().find(params => params.get('after') === 'cursor-1')!
    expect(next.get('platform')).toBe('N11')

    const pop = await openField('Platform')
    await act(async () => { Array.from(pop.querySelectorAll<HTMLButtonElement>('.rr-ph button')).find(item => item.textContent === 'Tümünü Kaldır')!.click() })
    await settle()
    expect(currentSearch).toBe('')
    expect(pageRequests().at(-1)!.get('platform')).toBeNull()
  })
})

describe('JobsPage API Sağlık tab (price/stock sending health)', () => {
  it('folds the sending health into the Entegrasyon Durumu row: one value per cell, details in tooltips, the stop in its header', async () => {
    await renderPage('/jobs?kategori=siparis')
    const jobReads = pageRequests().length
    const tab = Array.from(container.querySelectorAll<HTMLButtonElement>('[aria-label="İşlem Takibi görünümü"] [role="tab"]')).find(item => item.textContent === 'API Sağlık')!
    await act(async () => { tab.click() })
    for (let tick = 0; tick < 200 && !container.querySelector('.rr-mxt tbody tr td[data-l="Otomatik Yazma"] .psm-chip'); tick++) await settle()

    expect(currentSearch).toBe('?kategori=siparis&sekme=api-saglik')
    // The lower panel is gone: one table.
    expect(container.querySelector('.psm-panel, .psm-table')).toBeNull()
    const panel = container.querySelector('.api-health .rr-panel')!
    expect(Array.from(panel.querySelectorAll('thead th')).map(item => item.textContent)).toEqual(['Pazar Yeri', 'Sipariş', 'Ürün', 'Stok', 'Fiyat', 'İade', 'Fatura', 'Webhook', 'Otomatik Yazma', 'Sorun', ''])
    const row = panel.querySelector('tbody tr')!
    const write = row.querySelector('[data-l="Otomatik Yazma"] .psm-chip')!
    expect(write.textContent).toBe('Açık')
    expect(write.getAttribute('title')).toContain('Kuyruk: 2')
    expect(write.getAttribute('title')).toContain('Son başarılı gönderim: 1 sa önce')
    // A zero problem count shows nothing; the order source sits in the Webhook tooltip.
    expect(row.querySelector('[data-l="Sorun"]')!.textContent).toBe('')
    expect(row.querySelector('[data-l="Webhook"] .rr-mx')!.getAttribute('title')).toContain('yedek okuma 15 dakikada bir')
    expect(row.querySelectorAll('td small').length).toBe(1)
    // The emergency stop moved to the table's header and keeps its confirmation.
    const stop = panel.querySelector<HTMLButtonElement>('.rr-panel-h button.psm-stop')!
    expect(stop.textContent).toBe('Tüm Otomatik Yazmaları Durdur')
    expect(stop.disabled).toBe(false)
    expect(container.querySelector('.rr-mx-notice')).toBeNull()
    // Under it, API Sağlığı: one value per cell from stored data.
    for (let tick = 0; tick < 200 && !container.querySelector('.rr-sigt tbody td[data-l="Geride Kalma"]'); tick++) await settle()
    const signalRow = container.querySelector('.rr-sigt tbody tr')!
    expect(Array.from(container.querySelectorAll('.rr-sigt thead th')).map(item => item.textContent)).toEqual(['Pazar Yeri', 'Anahtar / Yetki', 'Geride Kalma', 'Hata Oranı (24 sa)', 'Hız Sınırı (429)', 'Kuyruk Yaşı', 'Webhook'])
    expect(Array.from(signalRow.querySelectorAll('td[data-l]')).map(item => item.textContent)).toEqual(['Geçerli', 'Sipariş 40 dk geride', '%4', '0', 'Yok', '1 sa önce'])
    expect(signalRow.querySelectorAll('small').length).toBe(0)
    expect(container.querySelector('.jobs-category-segment, [aria-label="İşlem kategorileri"]')).toBeNull()
    expect(pageRequests().length).toBe(jobReads)
  })

  it('draws the Genel Bakış figure cards as the summary and its Entegrasyon Durumu panel', async () => {
    await renderPage('/jobs?sekme=api-saglik')
    for (let tick = 0; tick < 200 && !container.querySelector('.rr-mxt tbody tr td[data-l]'); tick++) await settle()
    await settle()

    const panel = container.querySelector('.api-health .rr.rr-dashboard .rr-panel')!
    expect(panel.querySelector('h2')!.textContent).toBe('Entegrasyon Durumu')
    expect(panel.querySelector('.rr-mxt tbody tr')!.className).toBe('rr-attn')
    expect(panel.querySelector('.rr-mx-foot')!.textContent).toContain('5.987')
    const cards = Array.from(container.querySelectorAll<HTMLElement>('.api-health .rr-opgrid > .rr-opcard'))
    expect(cards.map(card => card.querySelector('small')!.textContent)).toEqual(['Sorunlu Bağlantı', 'Son 24 Saatte İstek', 'Tamamlanamayan İş', 'Karar Bekleyen İş'])
    expect(cards.map(card => card.querySelector('strong')!.textContent)).toEqual(['1', '5.987', '12', '18'])
    expect(cards.map(card => card.getAttribute('href'))).toEqual([null, '/jobs', '/jobs?statu=DEAD&aralik=tum', '/jobs?statu=MANUAL_REVIEW&aralik=tum'])
    expect(cards[0].className).toContain('rr-warn')
    expect(cards[1].className).toBe('rr-opcard')
    expect(cards[2].className).toContain('rr-bad')
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

  it('has a filter card of its own: range, the shared dropdowns and a result summary that filters', async () => {
    apiState.sendLogCounts = [{ result: 'APPLIED', count: 3 }, { result: 'UNCHANGED', count: 1 }, { result: 'REJECTED', count: 2 }, { result: 'SUBMITTED', count: 1 }]
    await renderPage('/jobs?sekme=loglar&kayit=gonderimler&aralik=7g')
    await logViewLoaded()
    const countRequests = () => apiState.calls.filter(path => path.startsWith('/price-stock-send-log/counts'))

    // The log opens on its whole history whatever range the job list holds.
    expect(trigger('Zaman Aralığı').textContent).toBe('Tüm Zamanlar')
    expect(logRequests().at(-1)!.get('from')).toBeNull()
    const summary = container.querySelector('[aria-label="Gönderim sonuç özeti"]')!
    expect(Array.from(summary.querySelectorAll('[role="button"]')).map(chip => chip.textContent)).toEqual(['Başarılı 4', 'Sorunlu 2', 'Bekleyen 1', 'Yeni Sürüm 0'])

    const range = await openField('Zaman Aralığı')
    await act(async () => { optionNamed(range, 'Son 24 Saat').click() })
    await settle()
    const since = Date.parse(logRequests().at(-1)!.get('from')!)
    expect(Math.abs(Date.now() - 24 * 60 * 60 * 1000 - since)).toBeLessThan(60_000)
    // The counts and the list each work out the range start when they are built; a millisecond apart is the same range.
    expect(Math.abs(Date.parse(new URLSearchParams(countRequests().at(-1)!.split('?')[1]).get('from')!) - since)).toBeLessThan(1_000)
    expect(trigger('Zaman Aralığı').textContent).toBe('Son 24 Saat')
    // The job list's range in the address is untouched.
    expect(currentSearch).toContain('aralik=7g')

    // Sorunlu picks its whole group; the counts are read without the result filter.
    const problems = Array.from(summary.querySelectorAll<HTMLElement>('[role="button"]')).find(chip => chip.textContent?.startsWith('Sorunlu'))!
    await act(async () => { problems.click() })
    await settle()
    expect(logRequests().at(-1)!.get('results')).toBe('REJECTED,FAILED,UNKNOWN')
    expect(countRequests().at(-1)).not.toContain('results=')
    expect(problems.getAttribute('aria-pressed')).toBe('true')
    expect(trigger('Sonuç').textContent).toBe('3 Sonuç')
    // The phone Filtre button counts the range and the result filter; Temizle counts the same.
    expect(container.querySelector('.ops-filters .rr-fcount')!.textContent).toBe('2')
    expect(container.querySelector('.ops-filters .ops-clear')!.textContent).toBe('Temizle')
    // Each result shows its count in the menu.
    const results = await openField('Sonuç')
    expect(checkboxNamed(results, 'Reddedildi').closest('label')!.querySelector('.rr-opt-count')!.textContent).toBe('2')
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
    // The filters sit in the log's own filter card, the shared one of the other pages.
    expect(container.querySelector('.ops-filters[aria-label="Gönderim kaydı filtreleri"] .rr-fcard')).not.toBeNull()
    const clearButton = () => container.querySelector<HTMLButtonElement>('.ops-filters .ops-clear')!
    expect(clearButton().disabled).toBe(true)
    const menu = await openField('Sonuç')
    expect(trigger('Sonuç').textContent).toBe('Tüm Sonuçlar')
    expect(Array.from(menu.querySelectorAll('.ops-opt-group')).map(heading => heading.textContent)).toEqual(['Başarılı', 'Sorunlu', 'Bekleyen', 'Diğer'])
    // Every option is one row: a checkbox beside its text.
    for (const row of Array.from(menu.querySelectorAll('label.rr-opt'))) expect(row.firstElementChild!.matches('input.rr-cb')).toBe(true)
    // The Sorunlu heading picks its whole group; the menu stays open for more picks.
    await act(async () => { checkboxNamed(menu, 'Sorunlu').click() })
    await settle()
    expect(logRequests().at(-1)!.get('results')).toBe('REJECTED,FAILED,UNKNOWN')
    expect(trigger('Sonuç').textContent).toBe('3 Sonuç')
    expect(checkboxNamed(menu, 'Sorunlu').checked).toBe(true)
    // One value of the group dropped leaves the heading half picked.
    await act(async () => { checkboxNamed(menu, 'Reddedildi').click() })
    await settle()
    expect(checkboxNamed(menu, 'Sorunlu').indeterminate).toBe(true)
    expect(trigger('Sonuç').textContent).toBe('2 Sonuç')
    expect(container.textContent).toContain('Seçili filtrelerle eşleşen gönderim kaydı bulunamadı.')
    expect(clearButton().disabled).toBe(false)
    expect(clearButton().textContent).toBe('Temizle')

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
    expect(rows[1].textContent).toContain('5 → 4−1')
    expect(rows[1].textContent).toContain('Varyant: Siyah / M')
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
    expect(trigger('Tür').textContent).toBe('Fiyat')
  })

  it('keeps İlk Fiyatları Göster in the Hareket Türü menu and sends the typed search on Filtrele', async () => {
    await renderPage('/jobs?sekme=loglar')
    await loaded()
    const movements = await openField('Hareket Türü')
    const rows = Array.from(movements.querySelectorAll('label.rr-opt .rr-grow')).map(item => item.textContent)
    expect(rows.at(-1)).toBe('İlk Fiyatları Göster')
    await act(async () => { checkboxNamed(movements, 'Satış').click() })
    await act(async () => { checkboxNamed(movements, 'İlk Fiyatları Göster').click() })
    await settle()
    expect(trigger('Hareket Türü').textContent).toBe('Satış')
    expect(container.querySelector('.ops-filters .ops-clear')!.textContent).toBe('Temizle')

    const search = container.querySelector<HTMLInputElement>('input[aria-label="Ürün hareketlerinde ara"]')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(search, 'BRK-1')
      search.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => { button('Filtrele')!.click() })
    await settle()
    expect(activityRequests().at(-1)!.get('search')).toBe('BRK-1')
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
