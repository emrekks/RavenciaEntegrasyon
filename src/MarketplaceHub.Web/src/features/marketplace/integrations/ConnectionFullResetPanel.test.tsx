import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ConnectionFullResetView } from '../connection-data-reset'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string; ifMatch?: string }>, view: null as unknown }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const headers = (init?.headers ?? {}) as Record<string, string>
    apiState.calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : undefined, ifMatch: headers['If-Match'] })
    if (method === 'POST') {
      apiState.view = { ...(apiState.view as ConnectionFullResetView), job: { jobId: 'job-1', status: 'PENDING', progressLabel: 'Sıfırlama sırada bekliyor', progressPercent: null, createdAt: '2026-10-30T09:00:00Z' } }
    }
    return apiState.view
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { ConnectionFullResetPanel, fullResetStatusText } from './ConnectionFullResetPanel'
import type { Connection } from '../shared'

const connection: Connection = { id: 'c1', publicId: 'c1', platformCode: 'TRENDYOL', environment: 'STAGE', displayName: 'Trendyol Test', externalStoreId: '9999', status: 'ACTIVE', apiVersion: 'V2', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, externalWritesEnabled: false, invoiceCreationEnabled: true, version: 7 }

let container: HTMLDivElement
let root: Root
let client: QueryClient
const feedback = vi.fn()

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

function setValue(element: HTMLInputElement, value: string) {
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(element, value)
  element.dispatchEvent(new Event('input', { bubbles: true }))
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.view = {
    items: [
      { scope: 'ORDERS', count: 1200 }, { scope: 'RETURNS', count: 30 }, { scope: 'INVOICES', count: 410 }, { scope: 'QUESTIONS', count: 5 },
      { scope: 'PRODUCTS', count: 80 }, { scope: 'PRODUCT_LINKS', count: 95 }, { scope: 'FINANCE', count: 900 }, { scope: 'REFERENCES', count: 10000 },
      { scope: 'MAPPINGS', count: 12 }, { scope: 'COMMISSION_TARIFFS', count: 1 }, { scope: 'PROMOTION_OFFERS', count: 2 }, { scope: 'JOBS', count: 40 }
    ],
    keptProducts: 15,
    job: null
  } satisfies ConnectionFullResetView
  feedback.mockReset()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

async function render() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><ConnectionFullResetPanel item={connection} onFeedback={feedback} onFinished={() => undefined} onCancel={() => undefined} /></QueryClientProvider>) })
  await settle()
}

describe('ConnectionFullResetPanel', () => {
  it('shows what goes, says that issued invoices go too and that they stay in E-Faturam', async () => {
    await render()
    const text = container.textContent ?? ''
    expect(text).toContain('Kesilmiş faturalar da silinir')
    expect(text).toContain('E-Faturam’da ve GİB’de geçerli kalır')
    expect(text).toContain('15 ürün başka bir bağlantıda kullanıldığı ya da panelde oluşturulduğu için korunur')
    expect(text).not.toContain('Mali Arşiv')
    // The product links name products already counted; the total leaves them out.
    expect(container.querySelector('.drl-total')!.textContent).toContain('12.680 kayıt')
  })

  it('starts only after the connection name is typed, with the connection version', async () => {
    await render()
    const go = () => Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Tam Sıfırla')
    expect(go()!.disabled).toBe(true)
    act(() => setValue(container.querySelector<HTMLInputElement>('#full-reset-c1-confirm')!, 'Trendyol Test'))
    expect(go()!.disabled).toBe(false)
    await act(async () => { go()!.click() })
    await settle()

    const post = apiState.calls.find(call => call.method === 'POST')!
    expect(post.path).toBe('/connections/c1/full-reset')
    expect(JSON.parse(post.body!)).toEqual({ confirmation: 'Trendyol Test' })
    expect(post.ifMatch).toBe('"v7"')
    expect(container.querySelector('.connection-full-reset-status')!.textContent).toContain('Sıfırlama sürüyor: Sıfırlama sırada bekliyor')
    expect(Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Sıfırlama Sürüyor…')!.disabled).toBe(true)
    expect(feedback).toHaveBeenCalledWith(expect.objectContaining({ kind: 'success', title: 'Tam Sıfırlama Başladı' }))
  })

  it('names how far a running reset is and why a finished one failed', () => {
    expect(fullResetStatusText({ jobId: 'j', status: 'LEASED', progressLabel: 'Finans hareketleri siliniyor', progressPercent: 50, createdAt: '2026-10-30T09:00:00Z' })).toBe('Sıfırlama sürüyor · %50: Finans hareketleri siliniyor')
    expect(fullResetStatusText({ jobId: 'j', status: 'DEAD', progressLabel: 'Bağlantı bulunamadı.', progressPercent: null, createdAt: '2026-10-30T09:00:00Z' })).toBe('Son tam sıfırlama tamamlanamadı: Bağlantı bulunamadı.')
    expect(fullResetStatusText(null)).toBeNull()
  })
})
