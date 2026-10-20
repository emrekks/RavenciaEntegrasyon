import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; headers: Record<string, string> }>, jobAnswers: [] as Array<number | 'ok'>, status: 'CREATED' }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    apiState.calls.push({ path, method, headers: Object.fromEntries(new Headers(init?.headers).entries()) })
    if (path === '/imports/imp-1' && method === 'GET') return { id: 'imp-1', version: 3, sourceType: 'CSV', status: apiState.status, totalRows: 10, validRows: 10, errorRows: 0, reviewRows: 0, sourceAssetId: 'asset-1' }
    if (path === '/imports/imp-1/preview-jobs') {
      const answer = apiState.jobAnswers.shift() ?? 'ok'
      if (answer !== 'ok') throw new original.ApiRequestError('Pazaryeri geçici olarak yanıt vermiyor.', answer, 'MARKETPLACE_UNAVAILABLE')
      return { jobId: 'job-1' }
    }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond), loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }
})

import { ImportDetailPage } from './ImportPages'
import { resetAnsweredIdempotencyKeys } from '../../../shared/idempotency'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

async function renderPage() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/imports/imp-1']}><Routes><Route path="/imports/:id" element={<ImportDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>) })
  await settle()
}

function previewKeys() {
  return apiState.calls.filter(call => call.path === '/imports/imp-1/preview-jobs').map(call => call.headers['idempotency-key'])
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.jobAnswers = []
  apiState.status = 'CREATED'
  resetAnsweredIdempotencyKeys()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

describe('ImportDetailPage', () => {
  it('retries a refused preview job with a fresh key instead of the answered versioned one (G4)', async () => {
    apiState.jobAnswers = [503, 'ok']
    await renderPage()
    const preview = () => Array.from(container.querySelectorAll('button')).find(button => button.textContent === 'Önizleme Oluştur')!

    await act(async () => { preview().click() })
    await settle()
    await act(async () => { preview().click() })
    await settle()

    const [first, second] = previewKeys()
    expect(first).toBe('import-preview:imp-1:v3')
    expect(second).toMatch(/^import-preview:[0-9a-f-]{36}$/)
  })

  it('does not schedule another read of a finished import (W32)', async () => {
    apiState.status = 'COMPLETED'
    await renderPage()
    const query = client.getQueryCache().find({ queryKey: ['import', 'imp-1'] })!
    const interval = (query.options as { refetchInterval?: unknown }).refetchInterval as (query: unknown) => unknown
    expect(interval(query)).toBe(false)
    expect(container.textContent).toContain('İçe Aktarım imp-1')
  })
})
