import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const invoiceFixtures = [
  { id: 'inv-1', orderId: 'order-1', packageId: 'shopify-package-1', orderNumber: 'SH-1001', customerName: 'DSM IGA', orderedAt: '2026-10-04T10:00:00Z', shipmentStatus: 'NEW', deliveredAt: null, invoiceDueAt: null, isDueSoon: true, currency: 'TRY', amount: 899, productCount: 1, primaryImageUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: '7330037879585', invoiceId: 'invoice-1', invoiceStatus: 'FATURA_BEKLIYOR', invoiceNumber: null, canCreateInvoice: true, shipmentAddressJson: '{"addressLine1":"Yakacık mahallesi çeltik sokak 3/27","district":"Keçiören","city":"Ankara","postalCode":"06000"}', invoiceAddressJson: null, lines: [{ sku: 'SKU-1', barcode: '0012345678901', description: 'Ravencia test ürün', quantity: 1, unitPrice: 899, vatRate: 10, imageUrl: 'https://images.example.test/pants.jpg' }], invoiceErrorCode: null, invoiceDeliveryStatus: null, invoiceDeliveryReference: null, invoiceDocumentAvailable: false, marketplaceInvoiceUrl: 'https://documents.example.test/current-invoice.pdf', platformInvoiceStatus: 'INVOICED', platformCode: 'SHOPIFY', platformDisplayName: 'Shopify', invoiceCreationEnabled: true, marketplaceConnectionId: 'shopify-connection', externalOrderId: 'gid://shopify/Order/1001' },
  { id: 'inv-2', orderId: 'order-2', packageId: 'shopify-package-2', orderNumber: 'SH-1002', customerName: 'Mehmet Kaya', orderedAt: '2026-10-04T11:00:00Z', shipmentStatus: 'DELIVERED', deliveredAt: '2026-10-04T12:30:00Z', invoiceDueAt: null, isDueSoon: true, currency: 'TRY', amount: 1250, productCount: 2, primaryImageUrl: null, cargoProviderName: 'Yurtiçi', cargoTrackingNumber: 'TRK-2', invoiceId: 'invoice-2', invoiceStatus: 'FATURA_BEKLIYOR', invoiceNumber: null, canCreateInvoice: true, shipmentAddressJson: null, invoiceAddressJson: null, lines: [], invoiceErrorCode: null, invoiceDeliveryStatus: null, invoiceDeliveryReference: null, invoiceDocumentAvailable: false, marketplaceInvoiceUrl: 'https://documents.example.test/stale-invoice.pdf', platformInvoiceStatus: 'NOT_INVOICED', platformCode: 'SHOPIFY', platformDisplayName: 'Shopify', invoiceCreationEnabled: true, marketplaceConnectionId: 'shopify-connection', externalOrderId: 'gid://shopify/Order/1002', manualInvoiceDocumentId: 'manual-doc-2', manualInvoiceIssuedAt: '2026-10-04T11:00:00Z', manualInvoiceDeliveryStatus: null },
  { id: 'inv-3', orderId: 'order-3', packageId: 'trendyol-package-3', orderNumber: 'TY-1003', customerName: 'Zeynep Demir', orderedAt: '2026-10-04T12:00:00Z', shipmentStatus: 'NEW', deliveredAt: null, invoiceDueAt: null, isDueSoon: true, currency: 'TRY', amount: 499, productCount: 1, primaryImageUrl: null, cargoProviderName: 'Aras', cargoTrackingNumber: 'TRK-3', invoiceId: null, invoiceStatus: 'FATURA_BEKLIYOR', invoiceNumber: null, canCreateInvoice: true, shipmentAddressJson: null, invoiceAddressJson: null, lines: [], invoiceErrorCode: null, invoiceDeliveryStatus: null, invoiceDeliveryReference: null, invoiceDocumentAvailable: false, marketplaceInvoiceUrl: null, platformInvoiceStatus: 'NOT_INVOICED', allowedActions: ['CHECK_PLATFORM'], platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', invoiceCreationEnabled: true, marketplaceConnectionId: 'trendyol-connection', externalOrderId: 'external-ty-1003', externalPackageNumber: 'package-ty-3' },
]

const apiState = vi.hoisted(() => ({ uploads: [] as Array<{ path: string; fileName: string | null }>, statusUpdates: [] as Array<{ path: string; status: string; packageIds: string[] }>, pageRequests: [] as string[], orderSyncRequests: [] as Array<{ path: string; body: unknown }>, hiddenRequests: [] as Array<{ packageIds: string[]; hidden: boolean }> }))

vi.mock('../../shared/api', () => ({
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    if (path.startsWith('/invoice-workspace/page?')) {
      apiState.pageRequests.push(path)
      return { items: invoiceFixtures, totalCount: invoiceFixtures.length, pageNumber: 1, pageSize: 20, totalPages: 1, uninvoicedCount: 3, invoicedCount: 0, dueSoonCount: 3, totalPackageCount: 3, hasPendingMarketplaceInvoices: true, shipmentStatuses: ['DELIVERED'], cargoProviders: ['Aras', 'Yurtiçi'], invoiceStatuses: ['FATURA_BEKLIYOR'] }
    }
    if (path === '/invoice-workspace/manual-status') {
      const body = JSON.parse(String(init?.body)) as { status: string; packageIds: string[] }
      apiState.statusUpdates.push({ path, status: body.status, packageIds: body.packageIds })
      return { updatedCount: body.packageIds.length }
    }
    if (path === '/invoice-workspace/hidden') {
      const body = JSON.parse(String(init?.body)) as { packageIds: string[]; hidden: boolean }
      apiState.hiddenRequests.push(body)
      return { updatedCount: body.packageIds.length }
    }
    if (path.endsWith('/order-sync-jobs')) {
      apiState.orderSyncRequests.push({ path, body: init?.body ? JSON.parse(String(init.body)) : null })
      return { jobId: 'order-sync-job' }
    }
    if (path === '/invoice-workspace/manual-documents/manual-doc-2/delivery-jobs') return { jobIds: ['manual-delivery-job'], queuedPackageCount: 1, inspectionOnlyPackageCount: 0 }
    throw new Error(`Test API fixture does not handle ${init?.method ?? 'GET'} ${path}`)
  }),
  loadAllPages: vi.fn(async () => ({ items: [{ id: 'shopify-connection', platformCode: 'SHOPIFY', displayName: 'Shopify', status: 'ACTIVE', environment: 'LIVE', hasCredential: false }, { id: 'trendyol-connection', platformCode: 'TRENDYOL', displayName: 'Trendyol', status: 'ACTIVE', environment: 'LIVE', hasCredential: false }], nextCursor: null, hasMore: false })),
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
  apiState.pageRequests = []
  apiState.orderSyncRequests = []
  apiState.hiddenRequests = []
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/invoices?tab=DUE_SOON']}><InvoicesPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
})

afterEach(() => { act(() => root?.unmount()); client?.clear(); container.remove() })

describe('InvoicesPage Shopify bulk invoice status', () => {
  it('shows only the invoice package result count in the table summary', () => {
    const summary = container.querySelector('.invoice-reference-workspace > header p')
    expect(summary?.textContent).toBe('Filtreleme sonuçları: 3 paket')
    expect(summary?.textContent).not.toContain('Fatura işlemleri kayıt bazında yürütülür')
  })

  it('shows recognizable labels for structured address fields in invoice details', async () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('SH-1001'))!
    act(() => row.querySelector<HTMLButtonElement>('.invoice-reference-details-trigger')!.click())
    await settle()

    const addresses = container.querySelector('.invoice-detail-addresses')!
    expect(addresses.textContent).toContain('Teslimat Adresi')
    expect(addresses.textContent).toContain('Adres satırı: Yakacık mahallesi çeltik sokak 3/27')
    expect(addresses.textContent).toContain('İlçe: Keçiören')
    expect(addresses.textContent).toContain('İl: Ankara')
    expect(addresses.textContent).toContain('Posta kodu: 06000')
    expect(button('Manuel Fatura Belgesi Ekle')).toBeTruthy()
    expect(button('Siparişi Gizle')).toBeTruthy()
  })

  it('keeps the current marketplace invoice link inside the invoice details', async () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('SH-1001'))

    expect(row?.querySelector('.invoice-reference-document-link')).toBeNull()
    expect(row?.querySelector('.invoice-reference-status')?.textContent).toContain('Faturası Mevcut')
    expect(row?.querySelector('.invoice-reference-status .rv-badge-success')).not.toBeNull()
    expect(row?.querySelector('.invoice-reference-status')?.textContent).not.toContain('Fatura Bekliyor')
    expect(row?.querySelector('.invoice-reference-shipment')?.textContent).not.toContain('Henüz teslim edilmedi')

    act(() => button('Detayları Aç').click())
    await settle()
    const link = Array.from(container.querySelectorAll<HTMLAnchorElement>('.invoice-document-link'))
      .find(item => item.textContent?.includes('Pazaryerindeki güncel fatura bağlantısını aç'))
    expect(link?.href).toBe('https://documents.example.test/current-invoice.pdf')
    expect(link?.rel).toContain('noreferrer')
  })

  it('opens invoice detail product images in the shared image lightbox', async () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('SH-1001'))!
    act(() => row.querySelector<HTMLButtonElement>('.invoice-reference-details-trigger')!.click())
    await settle()

    const thumbnail = container.querySelector<HTMLButtonElement>('.invoice-detail-line-image-button')
    expect(thumbnail?.getAttribute('aria-label')).toBe('Ravencia test ürün görselini büyüt')
    act(() => thumbnail!.click())

    const dialog = document.getElementById('invoice-detail-image-modal-title')?.closest('[role="dialog"]')
    expect(dialog).not.toBeNull()
    expect(dialog?.querySelector('img')?.getAttribute('alt')).toBe('Ravencia test ürün büyük ürün görseli')
    act(() => dialog?.querySelector<HTMLButtonElement>('.modal-close')?.click())
    expect(document.getElementById('invoice-detail-image-modal-title')).toBeNull()
  })

  it('does not present a stale marketplace document link while the platform says invoice pending', () => {
    const rows = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
    const row = rows.find(item => item.textContent?.includes('SH-1002'))!
    expect(row.querySelector('.invoice-reference-document-link')).toBeNull()
    expect(row.querySelector('.invoice-reference-status')?.textContent).toContain('Fatura Bekliyor')
  })

  it('shows the marketplace refresh control inside the invoice details, not on the row', async () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('TY-1003'))!
    expect(row.querySelector('[aria-label="Pazaryeri fatura bilgisini yenile"]')).toBeNull()

    act(() => row.querySelector<HTMLButtonElement>('.invoice-reference-details-trigger')!.click())
    await settle()
    const refresh = container.querySelector<HTMLButtonElement>('.invoice-detail-platform-refresh')
    expect(refresh?.textContent).toBe('Platformdan Bilgileri Güncelle')
    expect(refresh?.querySelector('.ui-icon')).toBeNull()
    expect(refresh?.closest('.invoice-detail-header')).not.toBeNull()
  })

  it('shows every invoice operation and locks actions that are unavailable for the Trendyol order', () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('TY-1003'))!
    expect(row.querySelector('.invoice-reference-shipment')?.textContent).not.toContain('Henüz teslim edilmedi')
    act(() => row.querySelector<HTMLButtonElement>('.invoice-reference-actions-trigger')!.click())
    const actionButtons = Array.from(document.body.querySelectorAll<HTMLButtonElement>('[role="menu"] button'))
    const create = actionButtons.find(item => item.textContent?.includes('Fatura Oluştur'))
    const sync = actionButtons.find(item => item.textContent?.includes('Sipariş Durumunu Güncelle'))
    const manual = actionButtons.find(item => item.textContent?.includes('Manuel Fatura Ekle'))
    const send = actionButtons.find(item => item.textContent?.includes('Manuel Faturayı Pazaryerine Gönder'))
    expect(create).not.toBeUndefined()
    expect(create?.disabled).toBe(true)
    // Trendyol keeps the order status on the marketplace, so the row offers no status action.
    expect(sync).toBeUndefined()
    expect(manual?.disabled).toBe(false)
    expect(send?.disabled).toBe(true)
    expect(container.querySelector('[role="dialog"]')).toBeNull()
  })

  it('lets a Shopify order be hidden from its row menu instead of offering a status update', async () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('SH-1001'))!
    act(() => row.querySelector<HTMLButtonElement>('.invoice-reference-actions-trigger')!.click())
    await settle()
    const items = Array.from(document.body.querySelectorAll<HTMLButtonElement>('[role="menuitem"]'))
    expect(items.some(item => item.textContent?.includes('Sipariş Durumunu Güncelle'))).toBe(false)
    act(() => items.find(item => item.textContent?.includes('Siparişi Gizle'))!.click())
    await settle()
    expect(apiState.hiddenRequests).toEqual([{ packageIds: ['shopify-package-1'], hidden: true }])
    expect(apiState.orderSyncRequests).toEqual([])
  })

  it('opens manual invoice upload from the row actions menu without sending it', async () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('SH-1001'))!
    act(() => row.querySelector<HTMLButtonElement>('.invoice-reference-actions-trigger')!.click())
    await settle()
    act(() => Array.from(document.body.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')).find(item => item.textContent?.includes('Manuel Fatura Ekle'))!.click())
    await settle()
    expect(container.querySelector('[role="dialog"]')?.textContent).toContain('Siparişe manuel fatura belgesi ekle')
    expect(apiState.orderSyncRequests).toEqual([])
    act(() => button('Vazgeç').click())
  })

  it('offers marketplace delivery when a manual invoice document already exists', async () => {
    const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
      .find(item => item.textContent?.includes('SH-1002'))!
    act(() => row.querySelector<HTMLButtonElement>('.invoice-reference-actions-trigger')!.click())
    await settle()
    const send = Array.from(document.body.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')).find(item => item.textContent?.includes('Manuel Faturayı Pazaryerine Gönder'))
    expect(send).not.toBeUndefined()
    act(() => send!.click())
    await settle()
    expect(container.textContent).toContain('1 paket için taze durum kontrolü ve manuel fatura iletimi kuyruğa alındı.')
  })

  it('copies the raw invoice order number using the copy control', async () => {
    const originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard')
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    try {
      const row = Array.from(container.querySelectorAll<HTMLElement>('.invoice-reference-row'))
        .find(item => item.textContent?.includes('TY-1003'))!
      const copy = row.querySelector<HTMLButtonElement>('.invoice-reference-order .order-number-copy')!
      expect(copy.getAttribute('aria-label')).toBe('Sipariş numarası TY-1003 kopyala')
      act(() => copy.click())
      await settle()
      expect(writeText).toHaveBeenCalledWith('TY-1003')
      expect(copy.getAttribute('aria-label')).toBe('Sipariş numarası TY-1003 kopyalandı')
    } finally {
      if (originalClipboard) Object.defineProperty(navigator, 'clipboard', originalClipboard)
      else Reflect.deleteProperty(navigator, 'clipboard')
    }
  })

  it('renders selection controls for every platform order and selects the whole page', async () => {
    expect(apiState.pageRequests[0]).toContain('/invoice-workspace/page?')
    expect(apiState.pageRequests[0]).toContain('tab=DUE_SOON')
    expect(container.querySelector('[aria-label="Shopify #SH-1001 siparişini seç"]')).not.toBeNull()
    expect(container.querySelector('[aria-label="Shopify #SH-1002 siparişini seç"]')).not.toBeNull()
    expect(container.querySelector('[aria-label="Trendyol #TY-1003 siparişini seç"]')).not.toBeNull()
    expect(container.querySelector('.invoice-reference-buyer > strong')?.textContent).toBe('DSM IGA')

    const selectAll = container.querySelector<HTMLInputElement>('[aria-label="Bu sayfadaki tüm siparişleri seç"]')!
    act(() => selectAll.click())
    await settle()
    for (const item of invoiceFixtures) {
      expect(container.querySelector<HTMLInputElement>(`[aria-label$="#${item.orderNumber} siparişini seç"]`)?.checked).toBe(true)
    }
    expect(container.querySelector('.invoice-reference-bulk-toolbar')?.textContent).toContain('3 sipariş seçildi')
    expect(button('Siparişleri gizle').querySelector('.ui-icon-eyeOff')).not.toBeNull()
  })

  it('shows the all invoices tab and requests unhidden invoice records', async () => {
    expect(button('Tümü').textContent).toContain('3')
    act(() => button('Tümü').click())
    await settle()
    expect(apiState.pageRequests.at(-1)).toContain('tab=ALL')
  })

  it('changes only Shopify invoice statuses when mixed platforms are selected', async () => {
    const selectAll = container.querySelector<HTMLInputElement>('[aria-label="Bu sayfadaki tüm siparişleri seç"]')!
    act(() => selectAll.click())
    await settle()
    act(() => button('Shopify faturalarını güncelle').click())
    await settle()
    expect(container.querySelector('[role="dialog"]')?.textContent).toContain('Fatura dosyası yüklenmez')
    const status = container.querySelector<HTMLSelectElement>('[aria-label="Yeni fatura durumu"]')!
    act(() => { status.value = 'UPLOADED'; status.dispatchEvent(new Event('change', { bubbles: true })) })
    act(() => button('2 Shopify siparişini güncelle').click())
    await settle()

    expect(apiState.statusUpdates).toEqual([{ path: '/invoice-workspace/manual-status', status: 'UPLOADED', packageIds: ['shopify-package-1', 'shopify-package-2'] }])
    expect(apiState.uploads).toEqual([])
    expect(container.querySelector('[role="dialog"]')).toBeNull()
    expect(container.textContent).toContain('2 siparişin fatura durumu')
  })
})
