import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { expect, test, vi } from 'vitest'
import { BillingSettingsPage, InvoiceDetailPage } from './F4Pages'

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const client = () => new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })

const invoiceDetail = {
  id: 'invoice-1', orderId: 'order-1', orderNumber: 'ORDER-1', packageId: 'package-1', providerConnectionId: 'connection-1',
  invoiceType: 'EARSIVFATURA', sequencePurpose: 'NORMAL', status: 'READY', currency: 'TRY', taxExclusiveTotal: 100,
  discountTotal: 0, taxTotal: 20, payableTotal: 120, note: 'YALNIZ: YÜZ YİRMİ TÜRK LİRASI', invoiceNumber: null,
  ettnUuid: null, dueAt: null, issuedAt: null, lastErrorCode: null, createdAt: '2026-08-05T12:00:00Z', version: 3,
  lines: [{ id: 'line-1', lineSequence: 1, description: 'Ürün', sku: 'SKU-1', unit: 'ADET', quantity: 1, unitPrice: 100, discountAmount: 0, vatRate: 20, vatAmount: 20, lineTotal: 120 }],
  documents: [], attempts: [], deliveries: [], allowedActions: ['VALIDATE', 'SUBMIT']
}

test('requires explicit password confirmation and queues E-Faturam submission with concurrency headers', async () => {
  let request: RequestInit | undefined
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-f4-submit' })
    if (url.endsWith('/api/v1/invoices/invoice-1') && (!init?.method || init.method === 'GET')) return json(invoiceDetail)
    if (url.endsWith('/api/v1/invoices/invoice-1/submit-jobs') && init?.method === 'POST') { request = init; return json({ jobId: 'job-1' }, 202) }
    return json({ title: 'Bulunamadı' }, 404)
  }) as typeof fetch

  render(<QueryClientProvider client={client()}><MemoryRouter initialEntries={['/invoices/invoice-1']}><Routes><Route path="/invoices/:id" element={<InvoiceDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)

  expect(await screen.findByRole('heading', { name: 'ORDER-1' })).toBeInTheDocument()
  const submit = screen.getByRole('button', { name: 'E-Faturam’a gönder' })
  expect(submit).toBeDisabled()
  fireEvent.change(screen.getByLabelText('Hesap parolası'), { target: { value: 'test-password' } })
  fireEvent.click(screen.getByLabelText('Bu dış mali işlemi açıkça onaylıyorum.'))
  expect(submit).toBeEnabled()
  fireEvent.click(submit)

  expect(await screen.findByRole('status')).toHaveTextContent('İş güvenli kuyruğa alındı.')
  await waitFor(() => expect(request).toBeDefined())
  const headers = new Headers(request?.headers)
  expect(headers.get('If-Match')).toBe('"v3"')
  expect(headers.get('Idempotency-Key')).toBeTruthy()
  expect(headers.get('X-CSRF-TOKEN')).toBeTruthy()
  expect(JSON.parse(String(request?.body))).toEqual({ password: 'test-password', confirmed: true })
})

test('queries and displays E-Fatura taxpayer applications for the selected provider connection', async () => {
  globalThis.fetch = vi.fn((input) => {
    const url = String(input)
    if (url.includes('/api/v1/connections')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL_EFATURAM', displayName: 'E-Faturam Stage', status: 'ACTIVE' }], nextCursor: null, hasMore: false })
    if (url.endsWith('/api/v1/billing/legal-entity-profile')) return json({ id: 'legal-1', title: 'Ravencia', maskedTaxId: '******7890', status: 'ACTIVE', version: 1 })
    if (url.includes('/api/v1/billing/invoice-policies/connection-1')) return json({ id: 'policy-1', providerConnectionId: 'connection-1', triggerState: 'MANUAL_CONFIRMED', packageScope: 'SHIPMENT_PACKAGE', dueRule: 'IMMEDIATE', roundingRule: 'LINE_HALF_AWAY_FROM_ZERO', adjustmentRule: 'REJECT_OVER_ONE_KURUS', autoSubmit: false, version: 1 })
    if (url.includes('/api/v1/billing/taxpayers/1234567890?connectionId=connection-1')) return json({ taxId: '1234567890', isRegistered: true, providerCustomerId: '100001', checkedAt: '2026-08-05T12:00:00Z', applications: [{ type: 1, serviceName: 'E-FATURA', gibStatus: 'AKTIF', activated: true, activationDate: '2025-01-01T00:00:00Z', deactivationDate: null }] })
    return json({ title: 'Bulunamadı' }, 404)
  }) as typeof fetch

  render(<QueryClientProvider client={client()}><MemoryRouter><BillingSettingsPage /></MemoryRouter></QueryClientProvider>)

  const provider = await screen.findByLabelText('Provider')
  await within(provider).findByRole('option', { name: 'E-Faturam Stage' })
  fireEvent.change(provider, { target: { value: 'connection-1' } })
  const taxpayerForm = screen.getByRole('heading', { name: 'E-Fatura mükellefiyet sorgusu' }).closest('form')!
  fireEvent.change(within(taxpayerForm).getByLabelText('VKN/TCKN'), { target: { value: '1234567890' } })
  fireEvent.click(within(taxpayerForm).getByRole('button', { name: 'Provider’dan sorgula' }))

  expect(await screen.findByText('Aktif e-belge başvurusu bulundu')).toBeInTheDocument()
  expect(screen.getByText('E-FATURA')).toBeInTheDocument()
  expect(screen.getByText('AKTIF')).toBeInTheDocument()
})
