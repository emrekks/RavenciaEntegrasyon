import type { ReactNode } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { expect, test, vi } from 'vitest'
import { ProductDetailPage } from './F2Pages'
import { ReturnDetailPage, ShipmentDetailPage } from './F3Pages'

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const renderAt = (path: string, route: string, page: ReactNode) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><Routes><Route path={route} element={page} /></Routes></MemoryRouter></QueryClientProvider>)
}

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
    if (url.endsWith('/api/v1/returns/return-1') && (!init?.method || init.method === 'GET')) return json({ id: 'return-1', externalClaimId: 'C-1', orderNumber: 'O-1', status: 'ACTION_REQUIRED', rawStatus: 'Created', reasonCode: 'R1', reasonText: 'Neden', actionDueAt: null, allowedActions: ['APPROVE'], version: 3 })
    if (url.endsWith('/api/v1/returns/return-1/actions') && init?.method === 'POST') { decisionBody = String(init.body); return json('decision-1', 202) }
    return json({}, 404)
  }) as typeof fetch

  renderAt('/returns/return-1', '/returns/:id', <ReturnDetailPage />)
  expect(await screen.findByRole('option', { name: 'APPROVE' })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Kararı kuyruğa al' }))
  await waitFor(() => expect(JSON.parse(decisionBody)).toEqual({ action: 'APPROVE', reasonCode: null, explanation: null, evidenceAssetIds: [] }))
  expect(screen.queryByRole('option', { name: 'REJECT' })).not.toBeInTheDocument()
})
