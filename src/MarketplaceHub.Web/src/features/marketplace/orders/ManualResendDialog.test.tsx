import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { queryKeys } from '../../../shared/api/query-keys'

// Owner, 23 Nov 2026 ("Platform seçtirsin ve sipariş no yazdırsın"): the Yeniden Gönderim gear's window picks a marketplace,
// takes an order number and lists the hand-added records with Kaldır; an old typed count shows once as "Eski Hatırlatma".
const records = [{ id: 'r1', orderId: 'o1', orderNumber: '1000000101', connectionId: 'c1', platformCode: 'TRENDYOL', connectionName: 'Trendyol', createdAt: '2026-11-23T10:00:00Z', shippedAt: null, version: 1 }]
const added = { id: 'r2', orderId: 'o2', orderNumber: '1000000102', connectionId: 'c1', platformCode: 'TRENDYOL', connectionName: 'Trendyol', createdAt: '2026-11-23T11:00:00Z', shippedAt: null, version: 1 }

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string, init?: RequestInit) => {
    if (path === '/manual-resends' && init?.method === 'POST') {
      const body = JSON.parse(init.body as string) as { orderNumber: string }
      if (body.orderNumber === '1000009999') throw new Error('Trendyol bağlantısında 1000009999 numaralı sipariş panelde yok.')
      return added
    }
    if (path === '/manual-resends') return { items: records }
    if (path.startsWith('/manual-resends/') && init?.method === 'DELETE') return undefined
    if (path.startsWith('/connections')) return { items: [{ id: 'c1', platformCode: 'TRENDYOL', displayName: 'Trendyol', status: 'ACTIVE' }, { id: 'e1', platformCode: 'TRENDYOL_EFATURAM', displayName: 'E-Faturam', status: 'ACTIVE' }], nextCursor: null, hasMore: false }
    return null
  }
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: vi.fn(async (path: string) => answer(path)) }
})

import { MANUAL_RESEND_NOTE, ManualResendDialog } from './ManualResendDialog'
import { normalizeManualResendNumber } from './manual-resends'

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
  vi.clearAllMocks()
})

async function settle(until: () => boolean) {
  for (let index = 0; index < 30 && !until(); index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
}

async function open(role: string, legacy = 0, onClearLegacy = vi.fn(async () => undefined)) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(queryKeys.me(), { role })
  await act(async () => { root.render(<QueryClientProvider client={client}><ManualResendDialog legacyCount={legacy} onClearLegacy={onClearLegacy} onClose={() => undefined} /></QueryClientProvider>) })
  await settle(() => document.body.textContent?.includes('#1000000101') ?? false)
  return onClearLegacy
}

const field = () => document.querySelector<HTMLInputElement>('.rr-mresend-number input')!
const typeInto = async (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  await act(async () => { setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
}
const submit = async () => { await act(async () => { document.querySelector<HTMLFormElement>('.rr-mresend-form')!.requestSubmit() }) }

describe('ManualResendDialog', () => {
  it('adds an order by the only marketplace and a typed number, and lists the records with their state', async () => {
    const { hubApi } = await import('../../../shared/api')
    await open('OWNER')
    const list = document.querySelector('.rr-mresend-list')!
    expect(list.textContent).toContain('#1000000101')
    expect(list.textContent).toContain('Kargoya Verilmesi Bekleniyor')
    // Only marketplace connections are offered; the only one is picked.
    expect(document.querySelector('.rr-mresend-store .rr-trigger')!.textContent).toContain('Trendyol')
    // The explanation sits in the title's hint, not in the body.
    expect(document.querySelector('.rr-mresend-note')).toBeNull()
    expect(document.querySelector('.rr-mresend')!.textContent).not.toContain('Kayıt pazaryerine hiçbir şey göndermez')
    const hint = document.querySelector('.rr-m-head .title-hint')!
    expect(hint).not.toBeNull()
    await act(async () => { (hint as HTMLElement).click() })
    expect(document.body.textContent).toContain(MANUAL_RESEND_NOTE)
    await typeInto(field(), ' #1000000102 ')
    await submit()
    await settle(() => document.querySelector('.rr-mresend-msg')?.textContent?.includes('eklendi') ?? false)
    const call = vi.mocked(hubApi).mock.calls.find(([path, init]) => path === '/manual-resends' && (init as RequestInit | undefined)?.method === 'POST')!
    const init = call[1] as RequestInit & { headers: Record<string, string> }
    expect(JSON.parse(init.body as string)).toEqual({ connectionId: 'c1', orderNumber: '1000000102' })
    expect(init.headers['Idempotency-Key']).toBeTruthy()
    expect(document.querySelector('.rr-mresend-msg')!.textContent).toBe('#1000000102 Yeniden Gönderimler\'e eklendi.')
    expect(field().value).toBe('')
  })

  it('says an unknown number is unknown and asks for a number before sending', async () => {
    const { hubApi } = await import('../../../shared/api')
    await open('ADMINISTRATOR')
    await submit()
    expect(document.querySelector('.rr-mresend-msg')!.textContent).toBe('Sipariş numarasını yazın.')
    expect(vi.mocked(hubApi).mock.calls.some(([path, init]) => path === '/manual-resends' && (init as RequestInit | undefined)?.method === 'POST')).toBe(false)
    await typeInto(field(), '1000009999')
    await submit()
    await settle(() => document.querySelector('.rr-mresend-msg.is-error')?.textContent?.includes('panelde yok') ?? false)
    expect(document.querySelector('.rr-mresend-msg.is-error')!.getAttribute('role')).toBe('alert')
  })

  it('removes a record and clears an old count once', async () => {
    const { hubApi } = await import('../../../shared/api')
    const clear = await open('OWNER', 3)
    expect(document.querySelector('.rr-mresend-legacy')!.textContent).toContain('Eski Hatırlatma: 3')
    await act(async () => { [...document.querySelectorAll<HTMLButtonElement>('.rr-mresend-legacy button')].find(button => button.textContent === 'Temizle')!.click() })
    expect(clear).toHaveBeenCalledTimes(1)
    await act(async () => { document.querySelector<HTMLButtonElement>('.rr-mresend-remove')!.click() })
    await settle(() => document.querySelector('.rr-mresend-msg')?.textContent?.includes('kaldırıldı') ?? false)
    const call = vi.mocked(hubApi).mock.calls.find(([path]) => path === '/manual-resends/r1')!
    expect((call[1] as RequestInit).method).toBe('DELETE')
  })

  it('shows the list only to roles that may not add or remove', async () => {
    await open('OPERATIONS', 2)
    expect(document.querySelector('.rr-mresend-form')).toBeNull()
    expect(document.querySelector('.rr-mresend-remove')).toBeNull()
    expect(document.querySelector('.rr-mresend-legacy button')).toBeNull()
    expect(document.querySelector('.rr-mresend-list')!.textContent).toContain('#1000000101')
  })

  it('drops spaces and a leading # from the typed number', () => {
    expect(['  1000000101 ', '#1000000101', '## 1000000101', ' # '].map(normalizeManualResendNumber)).toEqual(['1000000101', '1000000101', '1000000101', ''])
  })
})
