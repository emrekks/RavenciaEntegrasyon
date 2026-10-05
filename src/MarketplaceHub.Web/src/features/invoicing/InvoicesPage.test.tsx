import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const invoiceFixtures = [
  { id: 'inv-1', orderId: 'order-1', packageId: 'shopify-package-1', orderNumber: 'SH-1001', customerName: 'Ayşe Yılmaz', orderedAt: '2026-10-04T10:00:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-04T12:00:00Z', invoiceDueAt: null, isDueSoon: true, currency: 'TRY', amount: 899, productCount: 1, primaryImageUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: 'TRK-1', invoiceId: 'invoice-1', invoiceStatus: 'FATURA_BEKLIYOR', invoiceNumber: null, canCreateInvoice: true, shipmentAddressJson: null, invoiceAddressJson: null, lines: [], invoiceErrorCode: null, invoiceDeliveryStatus: null, invoiceDeliveryReference: null, invoiceDocumentAvailable: false, platformCode: 'SHOPIFY', platformDisplayName: 'Shopify', invoiceCreationEnabled: true },
  { id: 'inv-2', orderId: 'order-2', packageId: 'shopify-package-2', orderNumber: 'SH-1002', customerName: 'Mehmet Kaya', orderedAt: '2026-10-04T11:00:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-04T12:30:00Z', invoiceDueAt: null, isDueSoon: true, currency: 'TRY', amount: 1250, productCount: 2, primaryImageUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: 'TRK-2', invoiceId: 'invoice-2', invoiceStatus: 'FATURA_BEKLIYOR', invoiceNumber: null, canCreateInvoice: true, shipmentAddressJson: null, invoiceAddressJson: null, lines: [], invoiceErrorCode: null, invoiceDeliveryStatus: null, invoiceDeliveryReference: null, invoiceDocumentAvailable: false, platformCode: 'SHOPIFY', platformDisplayName: 'Shopify', invoiceCreationEnabled: true },
  { id: 'inv-3', orderId: 'order-3', packageId: 'trendyol-package-3', orderNumber: 'TY-1003', customerName: 'Zeynep Demir', orderedAt: '2026-10-04T12:00:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-04T13:00:00Z', invoiceDueAt: null, isDueSoon: true, currency: 'TRY', amount: 499, productCount: 1, primaryImageUrl: null, cargoProviderName: 'Aras', cargoTrackingNumber: 'TRK-3', invoiceId: null, invoiceStatus: 'FATURA_BEKLIYOR', invoiceNumber: null, canCreateInvoice: true, shipmentAddressJson: null, invoiceAddressJson: null, lines: [], invoiceErrorCode: null, invoiceDeliveryStatus: null, invoiceDeliveryReference: null, invoiceDocumentAvailable: false, platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', invoiceCreationEnabled: true },
]

const apiState = vi.hoisted(() => ({ uploads: [] as Array<{ path: string; fileName: string | null }>, statusUpdates: [] as Array<{ path: string; status: string; packageIds: string[] }> }))

vi.mock('../../shared/api', () => ({
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    if (path === '/invoice-workspace') return invoiceFixtures
    if (path === '/invoice-workspace/manual-status') {
      const body = JSON.parse(String(init?.body)) as { status: string; packageIds: string[] }
      apiState.statusUpdates.push({ path, status: body.status, packageIds: body.packageIds })
      return { updatedCount: body.packageIds.length }
    }
    throw new Error(`Test API fixture does not handle ${init?.method ?? 'GET'} ${path}`)
  }),
  loadAllPages: vi.fn(async () => ({ items: [{ id: 'shopify-connection', platformCode: 'SHOPIFY', displayName: 'Shopify', status: 'ACTIVE', hasCredential: false }, { id: 'trendyol-connection', platformCode: 'TRENDYOL', displayName: 'Trendyol', status: 'ACTIVE', hasCredential: false }], nextCursor: null, hasMore: false })),
}))
vi.mock('../../shared/notifications', () => ({ appendNotification: vi.fn() }))

import { InvoicesPage } from './pages'

let container: HTMLDivElement
let root: Root
let client: QueryClient

function button(label: string) {
  const found = Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent?.trim().includes(label))
  if (!found) throw new Error(`Could not find button containing “${label}”`)
  return found
}
async function settle() { await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) }) }

beforeEach(async () => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.uploads = []
  apiState.statusUpdates = []
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/invoices?tab=DUE_SOON']}><InvoicesPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
})

afterEach(() => { act(() => root?.unmount()); client?.clear(); container.remove() })

describe('InvoicesPage Shopify bulk invoice status', () => {
  it('renders selection controls for every platform order and selects the whole page', async () => {
    expect(container.querySelector('[aria-label="Shopify #SH-1001 siparişini seç"]')).not.toBeNull()
    expect(container.querySelector('[aria-label="Shopify #SH-1002 siparişini seç"]')).not.toBeNull()
    expect(container.querySelector('[aria-label="Trendyol #TY-1003 siparişini seç"]')).not.toBeNull()

    const selectAll = container.querySelector<HTMLInputElement>('[aria-label="Bu sayfadaki tüm siparişleri seç"]')!
    act(() => selectAll.click())
    await settle()
    for (const item of invoiceFixtures) {
      expect(container.querySelector<HTMLInputElement>(`[aria-label$="#${item.orderNumber} siparişini seç"]`)?.checked).toBe(true)
    }
    expect(container.querySelector('.invoice-reference-bulk-toolbar')?.textContent).toContain('3 sipariş seçildi')
  })

  it('changes selected cross-platform invoice statuses without uploading files', async () => {
    const selectAll = container.querySelector<HTMLInputElement>('[aria-label="Bu sayfadaki tüm siparişleri seç"]')!
    act(() => selectAll.click())
    await settle()
    act(() => button('Seçilenlerin fatura durumunu değiştir').click())
    await settle()
    expect(container.querySelector('[role="dialog"]')?.textContent).toContain('Fatura dosyası yüklenmez')
    const status = container.querySelector<HTMLSelectElement>('[aria-label="Yeni fatura durumu"]')!
    act(() => { status.value = 'UPLOADED'; status.dispatchEvent(new Event('change', { bubbles: true })) })
    act(() => button('3 siparişin durumunu güncelle').click())
    await settle()

    expect(apiState.statusUpdates).toEqual([{ path: '/invoice-workspace/manual-status', status: 'UPLOADED', packageIds: ['shopify-package-1', 'shopify-package-2', 'trendyol-package-3'] }])
    expect(apiState.uploads).toEqual([])
    expect(container.querySelector('[role="dialog"]')).toBeNull()
    expect(container.textContent).toContain('3 siparişin fatura durumu')
  })
})
