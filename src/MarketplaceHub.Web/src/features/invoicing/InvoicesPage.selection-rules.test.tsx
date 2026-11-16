import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { listSelectionRules, selectionEvents, type SelectionEvent } from '../../test/list-selection-rules'

// WEB-SAYFALAR-11: Faturalar's row selection against the shared table (src/test/list-selection-rules.ts).
const item = (index: number) => ({ id: `inv-${index}`, orderId: `order-${index}`, packageId: `package-${index}`, orderNumber: `SH-100${index}`, customerName: 'Mehmet Kaya', orderedAt: '2026-10-04T11:00:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-04T12:30:00Z', invoiceDueAt: null, isDueSoon: true, currency: 'TRY', amount: 1250, productCount: 1, primaryImageUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: `TRK-${index}`, invoiceId: null, invoiceStatus: 'FATURA_BEKLIYOR', invoiceNumber: null, canCreateInvoice: true, shipmentAddressJson: null, invoiceAddressJson: null, lines: [], invoiceErrorCode: null, invoiceDeliveryStatus: null, invoiceDeliveryReference: null, invoiceDocumentAvailable: false, marketplaceInvoiceUrl: null, platformInvoiceStatus: 'NOT_INVOICED', platformCode: 'SHOPIFY', platformDisplayName: 'Shopify', invoiceCreationEnabled: true, marketplaceConnectionId: 'shopify-connection', externalOrderId: `gid://shopify/Order/100${index}`, manualInvoiceDocumentId: null, manualInvoiceIssuedAt: null, manualInvoiceDeliveryStatus: null })
const apiState = vi.hoisted(() => ({ pageRequests: [] as string[], withSecond: true }))

vi.mock('../../shared/api', () => ({
  get hubPoll() { return (this as unknown as { hubApi: unknown }).hubApi },
  hubApi: vi.fn(async (path: string) => {
    if (path.startsWith('/invoice-workspace/page?')) {
      apiState.pageRequests.push(path)
      // The page asks in the cursor form: limit, and from the second page on the offset cursor ("o" + offset, base64url).
      const asked = new URLSearchParams(path.split('?')[1])
      const after = asked.get('after')
      const requestedPage = Math.min(3, after ? Number(atob(after.replace(/-/g, '+').replace(/_/g, '/')).slice(1)) / Number(asked.get('limit') ?? 20) + 1 : 1)
      const items = requestedPage > 1 ? [item(9)] : [item(1), ...(apiState.withSecond ? [item(2)] : [])]
      return { items, totalCount: 60, pageNumber: requestedPage, pageSize: 20, totalPages: 3, uninvoicedCount: 3, invoicedCount: 0, dueSoonCount: 3, totalPackageCount: 3, hasPendingMarketplaceInvoices: true, shipmentStatuses: ['DELIVERED'], cargoProviders: ['Aras', 'Yurtiçi'], invoiceStatuses: ['FATURA_BEKLIYOR'] }
    }
    throw new Error(`Test API fixture does not handle ${path}`)
  }),
  loadAllPages: vi.fn(async () => ({ items: [{ id: 'shopify-connection', platformCode: 'SHOPIFY', displayName: 'Shopify', status: 'ACTIVE', environment: 'LIVE', hasCredential: false }], nextCursor: null, hasMore: false }))
}))
vi.mock('../../shared/notifications', async importOriginal => ({ ...(await importOriginal<typeof import('../../shared/notifications')>()), appendNotification: vi.fn() }))

import { InvoicesPage } from './pages'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() { for (let index = 0; index < 4; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) }) }

beforeEach(async () => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.pageRequests = []
  apiState.withSecond = true
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } })
  client.setQueryData(['me'], { id: 'user-1', email: 'u@example.invalid', displayName: 'U', role: 'ADMINISTRATOR', state: 'ACTIVE', tenantId: 'tenant-1' })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/invoices?tab=DUE_SOON']}><InvoicesPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
})

afterEach(() => { act(() => root?.unmount()); client?.clear(); container.remove() })

const box = (orderNumber: string) => container.querySelector<HTMLInputElement>(`[aria-label$="#${orderNumber} siparişini seç"]`)
// The bar shows what it showed last while it slides away; a leaving bar counts nothing.
const barText = () => container.querySelector('.invoice-reference-bulk-toolbar:not(.is-leaving)')?.textContent ?? ''
const lastPage = () => apiState.pageRequests.at(-1) ?? ''
const field = (label: string) => Array.from(container.querySelectorAll('.invoice-filter-scope .rr-fld')).find(element => element.querySelector('small')?.textContent === label)!
const apply = () => act(() => [...container.querySelectorAll<HTMLButtonElement>('.invoice-filter-scope .rr-fbtns button')].find(button => button.textContent === 'Filtrele')!.click())

const events: Record<SelectionEvent, () => Promise<void>> = {
  async filter() {
    act(() => field('Kargo Firması').querySelector<HTMLButtonElement>('button.rr-trigger')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="radio"], [role="option"]')).find(option => option.textContent?.includes('Aras'))!.click())
    await settle()
    expect(lastPage()).toContain('cargoProviderName=')
  },
  async search() {
    const input = container.querySelector<HTMLInputElement>('input[aria-label="Fatura Ara"]')!
    act(() => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(input, 'SH-1002')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    apply()
    await settle()
    expect(lastPage()).toContain('search=SH-1002')
  },
  async tab() {
    act(() => Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent?.includes('Tümü'))!.click())
    await settle()
    expect(lastPage()).toContain('tab=ALL')
  },
  async sort() {
    act(() => container.querySelector<HTMLButtonElement>('button[aria-label^="Sıralama:"]')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="option"]')).find(option => option.textContent === 'En Eski Siparişler')!.click())
    apply()
    await settle()
    expect(lastPage()).toContain('sort=ORDER_ASC')
  },
  async pageSize() {
    act(() => container.querySelector<HTMLButtonElement>('button.rr-psize-btn')!.click())
    act(() => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="option"]')).find(option => option.textContent === '50')!.click())
    await settle()
    expect(lastPage()).toContain('limit=50')
  },
  async page() {
    act(() => container.querySelector<HTMLButtonElement>('button[aria-label="Sonraki"]')!.click())
    await settle()
    expect(lastPage()).toContain(`after=${btoa('o20')}`)
    act(() => container.querySelector<HTMLButtonElement>('button[aria-label="Önceki"]')!.click())
    await settle()
  },
  async refetch() {
    await act(async () => { await client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }) })
    await settle()
  },
  async rowGone() {
    apiState.withSecond = false
    await act(async () => { await client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }) })
    await settle()
    expect(box('SH-1002')).toBeNull()
  }
}

describe('Faturalar row selection (WEB-SAYFALAR-11 table)', () => {
  it.each(selectionEvents)('%s', async event => {
    act(() => box('SH-1002')!.click())
    await settle()
    expect(barText()).toContain('1 sipariş seçildi')
    await events[event]()
    const outcome = listSelectionRules.invoices[event]
    if (outcome === 'clears') {
      expect(barText()).toBe('')
      expect(box('SH-1002')?.checked ?? false).toBe(false)
    }
    if (outcome === 'keeps') {
      expect(barText()).toContain('1 sipariş seçildi')
      expect(box('SH-1002')!.checked).toBe(true)
    }
    if (outcome === 'counted') expect(barText()).toContain('1 sipariş seçildi')
    if (outcome === 'uncounted') {
      expect(barText()).toBe('')
      apiState.withSecond = true
      await act(async () => { await client.invalidateQueries({ queryKey: ['invoice-workspace-page'] }) })
      await settle()
      expect(box('SH-1002')!.checked).toBe(true)
      expect(barText()).toContain('1 sipariş seçildi')
    }
  })
})
