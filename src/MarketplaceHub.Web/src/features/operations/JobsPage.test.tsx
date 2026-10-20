import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../../shared/api'

const apiState = vi.hoisted(() => ({ calls: [] as string[], failNextPage: false }))

function job(id: string, jobType: string, status: string, createdAt: string) {
  return {
    id, connectionId: null, jobType, status, attemptCount: 1, maxAttempts: 3, availableAt: createdAt, lastErrorCode: null, lastErrorSummary: null,
    correlationId: `corr-${id}`, createdAt, startedAt: createdAt, completedAt: createdAt, marketplace: 'Trendyol', externalId: null, firstFailedAt: null,
    lastFailedAt: null, nextRetryAt: null, batchCount: 1, progressCurrent: 0, progressTotal: null, progressPercent: null, progressLabel: null,
    progressReceived: 0, progressProcessed: 0, progressSkipped: 0, progressFailed: 0, lastAttemptStartedAt: createdAt, heartbeatAt: null
  }
}

vi.mock('../../shared/api', () => ({
  hubApi: vi.fn(async (path: string) => {
    apiState.calls.push(path)
    if (path.startsWith('/jobs/counts')) return [
      { status: 'SUCCEEDED', jobType: 'TRENDYOL_ORDER_SYNC', count: 2 },
      { status: 'DEAD', jobType: 'TRENDYOL_ORDER_SYNC', count: 1 },
      { status: 'SUCCEEDED', jobType: 'PRICE_INVENTORY_SYNC', count: 1 }
    ]
    if (path.startsWith('/jobs/metrics')) return []
    if (path.startsWith('/jobs/page?')) {
      const params = new URLSearchParams(path.slice(path.indexOf('?') + 1))
      if (params.get('after') === 'cursor-1') {
        if (apiState.failNextPage) throw new Error('Listenin devamı okunamadı. Listeyi yenileyip tekrar deneyin.')
        return { items: [job('job-3', 'TRENDYOL_ORDER_SYNC', 'DEAD', '2026-10-20T07:00:00Z')], nextCursor: null, hasMore: false }
      }
      return {
        items: [job('job-1', 'TRENDYOL_ORDER_SYNC', 'SUCCEEDED', '2026-10-20T09:00:00Z'), job('job-2', 'TRENDYOL_ORDER_SYNC', 'SUCCEEDED', '2026-10-20T08:00:00Z')],
        nextCursor: 'cursor-1',
        hasMore: true
      }
    }
    throw new Error(`Test API fixture does not handle ${path}`)
  })
}))

import { JobsPage } from './pages'

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

async function renderPage() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><JobsPage me={me} /></MemoryRouter></QueryClientProvider>) })
  await settle()
  await settle()
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.failNextPage = false
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
    expect(pageRequests().at(-1)!.get('jobTypes')).toBe('TRENDYOL_ORDER_SYNC')
  })

  it('asks nothing for a tab without jobs in the range', async () => {
    await renderPage()
    const before = pageRequests().length

    await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent?.includes('Faturalar'))!.click() })
    await settle()

    expect(pageRequests().length).toBe(before)
    expect(container.textContent).toContain('Seçili kategori ve filtrelerle eşleşen kayıt bulunamadı.')
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
