import type { ReactNode } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { expect, test, vi } from 'vitest'
import { ProductDetailPage } from './F2Pages'
import { OrdersPage, ReturnDetailPage, ShipmentDetailPage } from './F3Pages'

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const renderAt = (path: string, route: string, page: ReactNode) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><Routes><Route path={route} element={page} /></Routes></MemoryRouter></QueryClientProvider>)
}

test('shows order information directly in the operational list without a detail expander', async () => {
  globalThis.fetch = vi.fn(input => {
    const url = String(input)
    if (url.includes('/api/v1/orders?')) return json({ items: [{ id: 'order-1', orderNumber: 'T-1001', derivedStatus: 'PROCESSING', currency: 'TRY', grossAmount: 599.9, discountAmount: 50, netAmount: 549.9, orderedAt: '2026-08-08T10:00:00Z', lineCount: 1, packageCount: 1, version: 2, connectionId: 'connection-1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol Mağaza', customerName: 'Ayşe Yılmaz', customerEmail: 'ayse@example.com', customerTaxOrIdentityNumber: '11111111111', orderType: 'MIKRO_IHRACAT', isMicroExport: true, shipmentAddressJson: '{}', invoiceAddressJson: '{}', shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'FATURA_BEKLIYOR', cargoProviderName: 'Yurtiçi Kargo', cargoTrackingNumber: 'TRK-1', primaryImageUrl: null, productQuantity: 2, lines: [{ id: 'line-1', sku: 'BLZ-M', barcode: '8690001', title: 'Kadın Desenli Bluz', orderedQuantity: 2, cancelledQuantity: 0, shippedQuantity: 0, deliveredQuantity: 0, returnedQuantity: 0, unitPrice: 274.95, vatRate: 10, rawStatus: 'Created', variantId: 'variant-1', modelCode: 'BLZ-1', optionSignature: 'Renk: Lacivert | Beden: M', imageUrl: null }], packages: [{ id: 'shipment-1', orderId: 'order-1', orderNumber: 'T-1001', externalPackageId: 'PKG-1', status: 'CREATED', rawStatus: 'Created', cargoTrackingNumber: 'TRK-1', cargoProviderName: 'Yurtiçi Kargo', statusOccurredAt: '2026-08-08T10:00:00Z', version: 1 }] }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'billing-1', publicId: 'public-1', platformCode: 'TRENDYOL_EFATURAM', environment: 'STAGE', displayName: 'E-Faturam', externalStoreId: 'ravencia', status: 'ACTIVE', apiVersion: '1', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.endsWith('/api/v1/orders/order-1')) return json({ id: 'order-1', orderNumber: 'T-1001', derivedStatus: 'PROCESSING', currency: 'TRY', grossAmount: 599.9, discountAmount: 50, netAmount: 549.9, orderedAt: '2026-08-08T10:00:00Z', connectionId: 'connection-1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol Mağaza', customerName: 'Ayşe Yılmaz', customerEmail: 'ayse@example.com', customerPhone: '05000000000', customerTaxOrIdentityNumber: '11111111111', orderType: 'MIKRO_IHRACAT', isMicroExport: true, isEInvoiceAvailable: false, shipmentAddressJson: '{}', invoiceAddressJson: '{"invoiceAddress":{"fullAddress":"Kadıköy İstanbul"}}', shipmentDueAt: null, invoiceStatus: 'FATURA_BEKLIYOR', invoiceDocumentUrl: null, lines: [{ id: 'line-1', sku: 'BLZ-M', barcode: '8690001', title: 'Kadın Desenli Bluz', orderedQuantity: 2, cancelledQuantity: 0, shippedQuantity: 0, deliveredQuantity: 0, returnedQuantity: 0, unitPrice: 274.95, vatRate: 10, rawStatus: 'Created', variantId: 'variant-1', modelCode: 'BLZ-1', optionSignature: 'Renk: Lacivert | Beden: M', imageUrl: null }], packages: [{ id: 'shipment-1', orderId: 'order-1', orderNumber: 'T-1001', externalPackageId: 'PKG-1', status: 'CREATED', rawStatus: 'Created', cargoTrackingNumber: 'TRK-1', cargoProviderName: 'Yurtiçi Kargo', statusOccurredAt: '2026-08-08T10:00:00Z', version: 1 }], version: 2 })
    return json({}, 404)
  }) as typeof fetch

  renderAt('/orders', '/orders', <OrdersPage />)
  fireEvent.click(screen.getByRole('tab', { name: /İşleme Alınanlar/ }))
  expect(await screen.findByText('Sipariş Bilgileri')).toBeInTheDocument()
  expect(screen.getByText(/Ayşe Yılmaz/)).toBeInTheDocument()
  expect(screen.getByText('Kadın Desenli Bluz')).toBeInTheDocument()
  expect(screen.getByText('Model Kodu: BLZ-1')).toBeInTheDocument()
  expect(screen.getByText('Renk: Lacivert')).toBeInTheDocument()
  expect(screen.getByText('Beden: M')).toBeInTheDocument()
  expect(screen.getAllByText('Yurtiçi Kargo')).toHaveLength(1)
  expect(screen.getByText('Mikro İhracat Faturası')).toBeInTheDocument()
  expect(screen.getByText('Trendyol termin bilgisi göndermedi')).toBeInTheDocument()
  expect(screen.getByText('Mikro İhracat Faturası').closest('.order-reference-invoice')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Fatura işlemleri' }))
  expect(screen.getByRole('menuitem', { name: 'Fatura & Adres Bilgileri' })).toBeInTheDocument()
  expect(screen.getByRole('menu')).toHaveClass('opens-down')
  fireEvent.click(screen.getByRole('menuitem', { name: 'Fatura Oluştur' }))
  expect(await screen.findByRole('dialog', { name: 'Fatura Oluştur' })).toBeInTheDocument()
  expect(await screen.findByText('Kadıköy İstanbul')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Devam Et ve Taslağı Oluştur' })).toBeEnabled()
  fireEvent.click(screen.getByRole('button', { name: 'Pencereyi kapat' }))
  fireEvent.click(screen.getByRole('checkbox', { name: 'Sipariş T-1001 seç' }))
  fireEvent.click(screen.getByRole('button', { name: 'Toplu işlemler⌄' }))
  expect(screen.getByRole('menuitem', { name: /İşleme Al/ })).toBeInTheDocument()
  expect(screen.getByRole('menuitem', { name: /Kargo Firmasını Değiştir/ })).toBeInTheDocument()
  expect(screen.getByRole('menuitem', { name: /Toplu Fatura Kes/ })).toBeInTheDocument()
  expect(screen.getByRole('menuitem', { name: /Kargo Stickerlarını Yazdır/ })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: /Gelişmiş Filtreler/ }))
  expect(screen.getByLabelText('Kargo')).toBeInTheDocument()
  expect(screen.getByLabelText('Fatura')).toBeInTheDocument()
  expect(screen.getByText(/599,90/)).toBeInTheDocument()
  expect(screen.queryByText('Sipariş detaylarını göster')).not.toBeInTheDocument()
})

test('queues Trendyol product update from the product publication workspace', async () => {
  let posted = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-product' })
    if (url.endsWith('/api/v1/products/product-1')) return json({ id: 'product-1', title: 'Ürün', description: 'Açıklama', status: 'ACTIVE', version: 1, variants: [{ id: 'variant-1', sku: 'SKU-1', barcode: 'BC-1', version: 1 }] })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL', displayName: 'Trendyol', externalStoreId: 'seller-1', status: 'ACTIVE' }], nextCursor: null, hasMore: false })
    if (url.includes('/publication-status/')) return json({ productId: 'product-1', connectionId: 'connection-1', profileId: 'profile-1', actualStatus: 'LIVE', desiredStatus: 'LIVE', lastJobStatus: 'COMPLETED', lastRejectionCode: null, lines: [] })
    if (url.endsWith('/api/v1/products/product-1/update-jobs') && init?.method === 'POST') { posted = String(init.body); return json('job-1', 202) }
    return json({}, 404)
  }) as typeof fetch

  renderAt('/products/product-1', '/products/:id', <ProductDetailPage />)
  const select = await screen.findByLabelText('Ürün Trendyol bağlantısı')
  await within(select).findByRole('option', { name: 'Trendyol · seller-1' })
  fireEvent.change(select, { target: { value: 'connection-1' } })
  fireEvent.click(screen.getByRole('button', { name: 'Trendyol ürününü güncelle' }))
  await waitFor(() => expect(JSON.parse(posted)).toEqual({ connectionId: 'connection-1' }))
  expect(await screen.findByRole('status')).toHaveTextContent('job-1')
})

test('queues only capability-provided shipment action with optimistic concurrency', async () => {
  let actionHeaders: Headers | undefined; let actionBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-shipment' })
    if (url.endsWith('/api/v1/shipments/shipment-1') && (!init?.method || init.method === 'GET')) return json({ package: { id: 'shipment-1', orderId: 'order-1', orderNumber: 'O-1', externalPackageId: 'P-1', status: 'PROCESSING', rawStatus: 'Picking', cargoTrackingNumber: 'T-1', statusOccurredAt: '2026-08-05T00:00:00Z', version: 4 }, allowedActions: ['INVOICED'], supportedLabelFormats: ['ZPL'], documents: [] })
    if (url.endsWith('/api/v1/shipments/shipment-1/actions') && init?.method === 'POST') { actionHeaders = new Headers(init.headers); actionBody = String(init.body); return json('job-2', 202) }
    return json({}, 404)
  }) as typeof fetch

  renderAt('/shipments/shipment-1', '/shipments/:id', <ShipmentDetailPage />)
  expect(await screen.findByRole('option', { name: 'INVOICED' })).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Resmî aksiyon payload JSON'), { target: { value: '{"invoiceNumber":"INV-1"}' } })
  fireEvent.click(screen.getByRole('button', { name: 'Aksiyonu kuyruğa al' }))
  await waitFor(() => expect(JSON.parse(actionBody)).toEqual({ action: 'INVOICED', payloadJson: '{"invoiceNumber":"INV-1"}' }))
  expect(actionHeaders?.get('If-Match')).toBe('"v4"')
})

test('queues an approved return decision without inventing unsupported actions', async () => {
  let decisionBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-return' })
    if (url.endsWith('/api/v1/returns/return-1') && (!init?.method || init.method === 'GET')) return json({ id: 'return-1', externalClaimId: 'C-1', orderNumber: 'O-1', customerName: 'Test Müşteri', orderedAt: '2026-08-05T00:00:00Z', orderAmount: 120, currency: 'TRY', status: 'ACTION_REQUIRED', rawStatus: 'Created', reasonCode: 'R1', reasonText: 'Neden', actionDueAt: null, cargoProviderName: null, cargoTrackingNumber: null, allowedActions: ['APPROVE'], stockDispositionAvailable: false, lines: [], version: 3 })
    if (url.endsWith('/api/v1/returns/return-1/actions') && init?.method === 'POST') { decisionBody = String(init.body); return json('decision-1', 202) }
    return json({}, 404)
  }) as typeof fetch

  renderAt('/returns/return-1', '/returns/:id', <ReturnDetailPage />)
  expect(await screen.findByRole('option', { name: 'APPROVE' })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Kararı kuyruğa al' }))
  await waitFor(() => expect(JSON.parse(decisionBody)).toEqual({ action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [] }))
  expect(screen.queryByRole('option', { name: 'REJECT' })).not.toBeInTheDocument()
})
