import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Scan A-2: Fatura İptal Et (CANCEL) says in one plain sentence what it does, next to its button or, where a live
// provider asks for the confirmation tick, right above that tick.
const detail = vi.hoisted(() => ({ value: {} as Record<string, unknown> }))

vi.mock('../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../shared/api')>()),
  hubApi: vi.fn(async (path: string) => {
    if (path === '/invoices/inv-1') return detail.value
    throw new Error(`Test API fixture does not handle ${path}`)
  })
}))
vi.mock('../../shared/notifications', async importOriginal => ({ ...(await importOriginal<typeof import('../../shared/notifications')>()), appendNotification: vi.fn() }))

import { InvoiceDetailPage } from './pages'
import { invoiceCancellationWarning } from './invoice-copy'

const invoice = (overrides: Record<string, unknown>) => ({
  id: 'inv-1', orderNumber: 'TY-1001', invoiceType: 'EARSIVFATURA', status: 'MANUAL_REVIEW', currency: 'TRY', payableTotal: 120, invoiceNumber: 'GIB2026000000001',
  dueAt: null, createdAt: '2026-11-20T09:00:00Z', version: 3, orderId: 'order-1', packageId: 'package-1', providerConnectionId: 'provider-1', sequencePurpose: 'SALE',
  ettnUuid: '00000000-0000-4000-8000-000000000001', taxExclusiveTotal: 100, discountTotal: 0, taxTotal: 20, note: '', issuedAt: '2026-11-20T09:01:00Z', lastErrorCode: null,
  lines: [], documents: [], attempts: [], deliveries: [], allowedActions: ['CANCEL'], requiresSensitiveConfirmation: false, ...overrides
})

let container: HTMLDivElement
let root: Root

async function open(value: Record<string, unknown>) {
  detail.value = value
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['me'], { id: 'user-1', email: 'owner@example.invalid', displayName: 'Sahip', role: 'OWNER', state: 'ACTIVE', tenantId: 'tenant-1' })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/invoices/inv-1']}><Routes><Route path="/invoices/:id" element={<InvoiceDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>) })
  for (let index = 0; index < 4; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => { act(() => root.unmount()); container.remove() })

const warnings = () => [...container.querySelectorAll('.invoice-cancel-warning')].map(element => element.textContent)

describe('InvoiceDetailPage cancellation warning', () => {
  it('says the e-Arşiv is cancelled at the provider and cannot be undone, once, beside the button', async () => {
    await open(invoice({}))
    expect(invoiceCancellationWarning).toBe('Bu e-Arşiv fatura sağlayıcıda iptal edilecek. İşlem geri alınamaz.')
    expect(warnings()).toEqual([invoiceCancellationWarning])
    expect([...container.querySelectorAll('button')].some(button => button.textContent === 'E-Arşiv İptal İsteği Gönder')).toBe(true)
  })

  it('stands above the confirmation tick when a live provider asks for it', async () => {
    await open(invoice({ requiresSensitiveConfirmation: true }))
    expect(warnings()).toEqual([invoiceCancellationWarning])
    expect(container.querySelector('.form-panel .invoice-cancel-warning')).not.toBeNull()
  })

  it('is not shown when no cancellation is offered', async () => {
    await open(invoice({ allowedActions: ['RECONCILE'], status: 'SUBMITTED' }))
    expect(warnings()).toEqual([])
  })
})
