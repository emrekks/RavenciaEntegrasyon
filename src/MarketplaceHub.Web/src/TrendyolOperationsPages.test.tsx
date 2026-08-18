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
  expect(screen.getByText(/E-Faturam’a gönderilir/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Faturayı Oluştur' })).toBeEnabled()
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

test('does not render an invented overdue duration for a missing provider due date', async () => {
  globalThis.fetch = vi.fn(input => {
    const url = String(input)
    if (url.includes('/api/v1/orders?')) return json({ items: [{ id: 'order-due-missing', orderNumber: 'T-1002', derivedStatus: 'PROCESSING', currency: 'TRY', grossAmount: 1, discountAmount: 0, netAmount: 1, orderedAt: '2026-08-08T10:00:00Z', lineCount: 1, packageCount: 0, version: 1, connectionId: 'connection-1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol Mağaza', customerName: 'Test Müşteri', customerEmail: null, customerTaxOrIdentityNumber: null, orderType: 'NORMAL', isMicroExport: false, shipmentAddressJson: '{}', invoiceAddressJson: '{}', shipmentDueAt: '0001-01-01T00:00:00+00:00', isDeadlineCritical: false, invoiceStatus: 'FATURA_BEKLIYOR', cargoProviderName: null, cargoTrackingNumber: null, primaryImageUrl: null, productQuantity: 1, lines: [], packages: [] }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/connections?')) return json({ items: [], nextCursor: null, hasMore: false })
    return json({}, 404)
  }) as typeof fetch

  renderAt('/orders', '/orders', <OrdersPage />)
  fireEvent.click(screen.getByRole('tab', { name: /İşleme Alınanlar/ }))
  expect(await screen.findByText('Termin zamanı bekleniyor')).toBeInTheDocument()
  expect(screen.queryByText(/gün gecikmiştir/)).not.toBeInTheDocument()
})

test('explains a missing invoice package and queues a single-order refresh', async () => {
  let refreshBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    const order = { id: 'order-masked', orderNumber: '1114396103', derivedStatus: 'PROCESSING', currency: 'TRY', grossAmount: 52.9, discountAmount: 0, netAmount: 52.9, orderedAt: '2026-08-18T10:00:00Z', lineCount: 1, packageCount: 0, version: 1, connectionId: 'connection-1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', customerName: '*** ***', customerEmail: null, customerPhone: null, customerTaxOrIdentityNumber: '11111111111', orderType: 'NORMAL', isMicroExport: false, isEInvoiceAvailable: false, shipmentAddressJson: '{}', invoiceAddressJson: '{"invoiceAddress":{"fullAddress":"***","neighborhood":"Caferağa"}}', shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'FATURA_BEKLIYOR', invoiceDocumentUrl: null, invoiceId: null, cargoProviderName: null, cargoTrackingNumber: null, primaryImageUrl: null, productQuantity: 1, lines: [{ id: 'line-1', sku: 'SKU-1', barcode: null, title: 'Köpek Ödülü', orderedQuantity: 1, cancelledQuantity: 0, shippedQuantity: 0, deliveredQuantity: 0, returnedQuantity: 0, unitPrice: 52.9, vatRate: 20, rawStatus: 'Created', variantId: null, modelCode: null, optionSignature: null, imageUrl: null }], packages: [] }
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-order-refresh' })
    if (url.includes('/api/v1/orders?')) return json({ items: [order], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'billing-1', publicId: 'public-1', platformCode: 'TRENDYOL_EFATURAM', environment: 'STAGE', displayName: 'E-Faturam', externalStoreId: 'ravencia', status: 'ACTIVE', apiVersion: '1', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.endsWith('/api/v1/orders/order-masked')) return json(order)
    if (url.endsWith('/api/v1/connections/connection-1/order-sync-jobs') && init?.method === 'POST') { refreshBody = String(init.body); return json({ id: 'job-1' }, 202) }
    return json({}, 404)
  }) as typeof fetch

  renderAt('/orders', '/orders', <OrdersPage />)
  fireEvent.click(screen.getByRole('tab', { name: /İşleme Alınanlar/ }))
  await screen.findByText('Sipariş Bilgileri')
  fireEvent.click(screen.getByRole('button', { name: 'Fatura işlemleri' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Fatura Oluştur' }))

  expect(await screen.findByText('Trendyol müşteri bilgisini maskeledi')).toBeInTheDocument()
  expect(screen.getByText('Caferağa')).toBeInTheDocument()
  expect(screen.getByText(/paket bilgisi henüz gelmedi/i)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Faturayı Oluştur' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Paket bilgisini yenile' }))
  await waitFor(() => expect(JSON.parse(refreshBody)).toEqual({ externalOrderId: '1114396103' }))
  expect(await screen.findByText(/yenileme kuyruğa alındı/i)).toBeInTheDocument()
})

test('retries a pre-provider E-Faturam authentication failure and waits for the provider invoice number', async () => {
  let submitted = false
  let submitIdempotency = ''
  let submitBody = ''
  const order = { id: 'order-invoice-retry', orderNumber: '1910028925', derivedStatus: 'PROCESSING', currency: 'TRY', grossAmount: 381.8, discountAmount: 0, netAmount: 381.8, orderedAt: '2026-08-18T10:00:00Z', lineCount: 1, packageCount: 1, version: 2, connectionId: 'connection-1', platformCode: 'TRENDYOL', platformDisplayName: 'Trendyol', customerName: 'Test Müşteri', customerEmail: null, customerPhone: null, customerTaxOrIdentityNumber: '11111111111', orderType: 'NORMAL', isMicroExport: false, isEInvoiceAvailable: false, shipmentAddressJson: '{}', invoiceAddressJson: '{"invoiceAddress":{"fullAddress":"Bornova İzmir"}}', shipmentDueAt: null, isDeadlineCritical: false, invoiceStatus: 'FATURA_ISLENIYOR', invoiceId: 'invoice-1', invoiceDocumentUrl: null, cargoProviderName: 'Yurtiçi Kargo', cargoTrackingNumber: 'TRK-1', primaryImageUrl: null, productQuantity: 1, lines: [{ id: 'line-1', sku: 'SKU-1', barcode: null, title: 'Ürün', orderedQuantity: 1, cancelledQuantity: 0, shippedQuantity: 0, deliveredQuantity: 0, returnedQuantity: 0, unitPrice: 381.8, vatRate: 20, rawStatus: 'Created', variantId: null, modelCode: null, optionSignature: null, imageUrl: null }], packages: [{ id: 'package-1', orderId: 'order-invoice-retry', orderNumber: '1910028925', externalPackageId: 'PKG-1', status: 'CREATED', rawStatus: 'Created', cargoTrackingNumber: 'TRK-1', cargoProviderName: 'Yurtiçi Kargo', statusOccurredAt: '2026-08-18T10:00:00Z', version: 1 }] }
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-invoice-retry' })
    if (url.includes('/api/v1/orders?')) return json({ items: [order], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'billing-1', publicId: 'public-1', platformCode: 'TRENDYOL_EFATURAM', environment: 'PRODUCTION', displayName: 'E-Faturam', externalStoreId: 'ravencia', status: 'ACTIVE', apiVersion: '1', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.endsWith('/api/v1/orders/order-invoice-retry')) return json(order)
    if (url.endsWith('/api/v1/invoices/invoice-1/submit-jobs') && init?.method === 'POST') { submitted = true; submitIdempotency = new Headers(init.headers).get('Idempotency-Key') ?? ''; submitBody = String(init.body); return json({ jobId: 'job-retry' }, 202) }
    if (url.endsWith('/api/v1/invoices/invoice-1')) return json(submitted
      ? { id: 'invoice-1', version: 8, status: 'SUBMITTED', invoiceNumber: 'RVN2026000000001', externalReference: 'provider-1', lastErrorCode: null }
      : { id: 'invoice-1', version: 7, status: 'SUBMITTING', invoiceNumber: null, externalReference: null, lastErrorCode: 'EFATURAM_ACCESS_TOKEN_REJECTED' })
    return json({}, 404)
  }) as typeof fetch

  renderAt('/orders', '/orders', <OrdersPage />)
  fireEvent.click(screen.getByRole('tab', { name: /İşleme Alınanlar/ }))
  await screen.findByText('Sipariş Bilgileri')
  fireEvent.click(screen.getByRole('button', { name: 'Fatura işlemleri' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Fatura Oluştur' }))
  await screen.findByRole('dialog', { name: 'Fatura Oluştur' })
  fireEvent.click(await screen.findByRole('button', { name: 'Faturayı Oluştur' }))

  expect(await screen.findByText('Fatura E-Faturam’da oluşturuldu: RVN2026000000001')).toBeInTheDocument()
  expect(JSON.parse(submitBody)).toEqual({ password: '', confirmed: false })
  expect(submitIdempotency).not.toBe('invoice-submit:invoice-1')
  expect(submitIdempotency).not.toBe('')
})

test('uses the exact product-creation workspace for product editing', async () => {
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-product' })
    if (url.endsWith('/api/v1/products/product-1')) return json({ id: 'product-1', title: 'Ürün', description: 'Açıklama', status: 'ACTIVE', version: 1, variants: [{ id: 'variant-1', sku: 'SKU-1', barcode: 'BC-1', version: 1 }] })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL', displayName: 'Trendyol', externalStoreId: 'seller-1', status: 'ACTIVE' }], nextCursor: null, hasMore: false })
    return json({}, 404)
  }) as typeof fetch

  renderAt('/products/product-1', '/products/:id', <ProductDetailPage />)
  expect(await screen.findByRole('heading', { name: 'Yeni Ürün Ekle' })).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Ürün seçenek grupları' })).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Yayınlanacak kanallar' })).toBeInTheDocument()
})

test('edits catalog fields and saved category attributes from the unified product workspace', async () => {
  let patchBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-product-edit' })
    if (url.endsWith('/api/v1/products/product-1') && (!init?.method || init.method === 'GET')) return json({ id: 'product-1', title: 'Eski ürün', description: '<p>Eski açıklama</p>', brandId: 'brand-1', categoryId: 'category-1', status: 'ACTIVE', version: 4, modelCode: 'MODEL-1', variants: [{ id: 'variant-1', sku: 'SKU-1', barcode: 'BC-1', version: 1, onHand: 3, available: 3 }], attributes: [{ attributeId: 'attribute-1', valueId: 'value-1', textValue: null, numberValue: null, booleanValue: null, sortOrder: 0 }] })
    if (url.endsWith('/api/v1/products/product-1') && init?.method === 'PATCH') { patchBody = String(init.body); return json({}) }
    if (url.includes('/api/v1/catalog/categories?')) return json({ items: [{ id: 'category-1', name: 'Elbise', path: 'Giyim / Elbise', depth: 1, isLeaf: true, isActive: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/brands?')) return json({ items: [{ id: 'brand-1', name: 'Ravencia', isActive: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/categories/category-1/attribute-requirements')) return json([{ attributeId: 'attribute-1', isRequired: true, allowsCustomValue: false, displayOrder: 0, attribute: { id: 'attribute-1', code: 'KUMAS', name: 'Kumaş', dataType: 'SINGLE_SELECT', values: [{ id: 'value-1', value: 'Pamuk' }] } }])
    if (url.includes('/api/v1/connections?')) return json({ items: [], nextCursor: null, hasMore: false })
    return json({}, 404)
  }) as typeof fetch

  renderAt('/products/product-1', '/products/:id', <ProductDetailPage />)
  const title = await screen.findByDisplayValue('Eski ürün')
  await screen.findByRole('button', { name: 'Pamuk' })
  fireEvent.change(title, { target: { value: 'Yeni ürün adı' } })
  fireEvent.click(screen.getByRole('button', { name: 'Ürünü kaydet' }))
  await waitFor(() => expect(JSON.parse(patchBody)).toEqual({ title: 'Yeni ürün adı', description: '<p>Eski açıklama</p>', brandId: 'brand-1', categoryId: 'category-1', attributes: [{ attributeId: 'attribute-1', valueId: 'value-1', textValue: null, numberValue: null, booleanValue: null, sortOrder: 0 }], variantsToCreate: [] }))
})

test('queues only capability-provided shipment action with optimistic concurrency', async () => {
  let actionHeaders: Headers | undefined; let actionBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-shipment' })
    if (url.endsWith('/api/v1/shipments/shipment-1') && (!init?.method || init.method === 'GET')) return json({ package: { id: 'shipment-1', orderId: 'order-1', orderNumber: 'O-1', externalPackageId: 'P-1', status: 'PROCESSING', rawStatus: 'Picking', cargoTrackingNumber: 'T-1', statusOccurredAt: '2026-08-05T00:00:00Z', version: 4 }, allowedActions: ['INVOICED'], supportedLabelFormats: ['ZPL'], isStageConnection: false, documents: [] })
    if (url.endsWith('/api/v1/shipments/shipment-1/actions') && init?.method === 'POST') { actionHeaders = new Headers(init.headers); actionBody = String(init.body); return json('job-2', 202) }
    return json({}, 404)
  }) as typeof fetch

  renderAt('/shipments/shipment-1', '/shipments/:id', <ShipmentDetailPage />)
  expect(await screen.findByRole('option', { name: 'INVOICED' })).toBeInTheDocument()
  expect(screen.queryByText('Stage etiket testi')).not.toBeInTheDocument()
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

test('explains when the provider return state has no available decision', async () => {
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-return' })
    if (url.endsWith('/api/v1/returns/return-pending') && (!init?.method || init.method === 'GET')) return json({ id: 'return-pending', externalClaimId: 'C-2', orderNumber: 'O-2', customerName: 'Test Müşteri', orderedAt: '2026-08-05T00:00:00Z', orderAmount: 120, currency: 'TRY', status: 'REQUESTED', rawStatus: 'Created', reasonCode: 'R1', reasonText: 'Neden', actionDueAt: null, cargoProviderName: null, cargoTrackingNumber: null, allowedActions: [], stockDispositionAvailable: false, lines: [], version: 1 })
    return json({}, 404)
  }) as typeof fetch

  renderAt('/returns/return-pending', '/returns/:id', <ReturnDetailPage />)
  expect(await screen.findByText('Sağlayıcının mevcut iade durumu bu kayıtta henüz onay veya ret işlemini desteklemiyor.')).toBeInTheDocument()
  expect(screen.queryByText(/production dış-yazma ayarları/i)).not.toBeInTheDocument()
})
