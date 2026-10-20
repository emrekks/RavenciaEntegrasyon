import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, render, waitFor } from '../../test/render'

const api = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; headers: Record<string, string>; body?: string }>, release: null as null | (() => void), platformCode: 'TRENDYOL', allowedActions: [] as string[] }))

vi.mock('../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../shared/api')>()),
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    api.calls.push({ path, method, headers: Object.fromEntries(new Headers(init?.headers).entries()), body: typeof init?.body === 'string' ? init.body : undefined })
    if (path === '/shipments/p1' && method === 'GET') return {
      platformCode: api.platformCode, allowedActions: api.allowedActions, supportedLabelFormats: ['A4'], isStageConnection: false, documents: [],
      package: { id: 'p1', orderId: 'o1', orderNumber: '1001', externalPackageId: 'PK-1', status: 'PROCESSING', rawStatus: 'Picking', cargoTrackingNumber: 'TR123', cargoProviderName: 'Yurtiçi', statusOccurredAt: '2026-10-19T06:00:00Z', version: 7, isResend: false },
    }
    if (method === 'POST') { await new Promise<void>(resolve => { api.release = resolve }); return { jobId: 'j1' } }
    return []
  }),
}))

import { ShipmentDetailPage } from './orders/ShipmentPages'

beforeEach(() => { api.calls = []; api.release = null; api.platformCode = 'TRENDYOL'; api.allowedActions = [] })
afterEach(cleanup)

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/shipments/p1']}><Routes><Route path="/shipments/:id" element={<ShipmentDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)
}

const submit = (form: HTMLFormElement) => act(() => { form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
const posts = () => api.calls.filter(call => call.method === 'POST')

describe('shipment package requests', () => {
  it('sends a label request once however often the form is sent, with a key tied to the package version', async () => {
    const { host } = mount()
    const button = await waitFor(() => byText<HTMLButtonElement>(host, 'button', 'Etiket Oluştur'))
    const form = button.closest('form')!
    submit(form)
    submit(form)
    await waitFor(() => posts().length === 1)
    expect(button.disabled).toBe(true)
    expect(posts()[0].headers['idempotency-key']).toMatch(/^shipment\/common-label-jobs:p1:v7:[0-9a-f]{8}$/)
    expect(posts()[0].headers['if-match']).toBe('"v7"')
    await act(async () => { api.release?.() })
    await waitFor(() => !button.disabled)
    expect(posts()).toHaveLength(1)
  })

  it('asks in the panel before a Hepsiburada package is opened and sends nothing when cancelled', async () => {
    api.platformCode = 'HEPSIBURADA'
    api.allowedActions = ['UNPACK']
    const { host } = mount()
    const unpack = await waitFor(() => byText<HTMLButtonElement>(host, 'button', 'Paketi Aç'))
    act(() => { unpack.click() })
    const dialog = await waitFor(() => document.querySelector('[role="dialog"]'))
    expect(dialog.textContent).toContain('Paket Açılsın mı?')
    act(() => { byText<HTMLButtonElement>(dialog, 'button', 'Vazgeç').click() })
    await waitFor(() => !document.querySelector('[role="dialog"]'))
    expect(posts()).toHaveLength(0)
  })
})
