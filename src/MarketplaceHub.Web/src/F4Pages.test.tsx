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
  documents: [], attempts: [], deliveries: [], allowedActions: ['VALIDATE', 'SUBMIT'], requiresSensitiveConfirmation: true
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

test('queues the Stage financial canary without password only for its separately scoped endpoint', async () => {
  let request: RequestInit | undefined
  const canary = { ...invoiceDetail, allowedActions: ['STAGE_CAPABILITY_PROBE'], requiresSensitiveConfirmation: false }
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-f4-canary' })
    if (url.endsWith('/api/v1/invoices/invoice-1') && (!init?.method || init.method === 'GET')) return json(canary)
    if (url.endsWith('/api/v1/invoices/invoice-1/stage-capability-probe-jobs') && init?.method === 'POST') { request = init; return json({ jobId: 'job-canary' }, 202) }
    return json({ title: 'Bulunamadı' }, 404)
  }) as typeof fetch
  render(<QueryClientProvider client={client()}><MemoryRouter initialEntries={['/invoices/invoice-1']}><Routes><Route path="/invoices/:id" element={<InvoiceDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)
  const run = await screen.findByRole('button', { name: 'Stage mali canary çalıştır' })
  expect(run).toBeEnabled()
  fireEvent.click(run)
  await waitFor(() => expect(request).toBeDefined())
  expect(new Headers(request?.headers).get('If-Match')).toBe('"v3"')
  expect(request?.body).toBeUndefined()
})

test('queues a manual Stage E-Faturam submission without password confirmation', async () => {
  let request: RequestInit | undefined
  const stageInvoice = { ...invoiceDetail, allowedActions: ['SUBMIT'], requiresSensitiveConfirmation: false }
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-f4-stage-submit' })
    if (url.endsWith('/api/v1/invoices/invoice-1') && (!init?.method || init.method === 'GET')) return json(stageInvoice)
    if (url.endsWith('/api/v1/invoices/invoice-1/submit-jobs') && init?.method === 'POST') { request = init; return json({ jobId: 'job-stage-submit' }, 202) }
    return json({ title: 'Not found' }, 404)
  }) as typeof fetch
  render(<QueryClientProvider client={client()}><MemoryRouter initialEntries={['/invoices/invoice-1']}><Routes><Route path="/invoices/:id" element={<InvoiceDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)
  const submit = await screen.findByRole('button', { name: /Faturam/ })
  expect(submit).toBeEnabled()
  fireEvent.click(submit)
  await waitFor(() => expect(request).toBeDefined())
  expect(new Headers(request?.headers).get('If-Match')).toBe('"v3"')
  expect(new Headers(request?.headers).get('Idempotency-Key')).toBeTruthy()
  expect(JSON.parse(String(request?.body))).toEqual({ password: '', confirmed: false })
})

test('uploads a manual invoice document only to the private invoice archive', async () => {
  let request: RequestInit | undefined
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-invoice-upload' })
    if (url.endsWith('/api/v1/invoices/invoice-1') && (!init?.method || init.method === 'GET')) return json(invoiceDetail)
    if (url.endsWith('/api/v1/invoices/invoice-1/documents/manual') && init?.method === 'POST') { request = init; return json({ duplicate: false }, 201) }
    return json({ title: 'Bulunamadı' }, 404)
  }) as typeof fetch

  render(<QueryClientProvider client={client()}><MemoryRouter initialEntries={['/invoices/invoice-1?upload=1']}><Routes><Route path="/invoices/:id" element={<InvoiceDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)
  const input = await screen.findByLabelText('Fatura belgesi seç')
  fireEvent.change(input, { target: { files: [new File(['%PDF-1.7'], 'fatura.pdf', { type: 'application/pdf' })] } })
  fireEvent.click(screen.getByRole('button', { name: 'Belgeyi yükle' }))
  await waitFor(() => expect(request).toBeDefined())
  expect(new Headers(request?.headers).get('Idempotency-Key')).toContain('invoice-document:invoice-1')
  expect(new Headers(request?.headers).get('X-CSRF-TOKEN')).toBeTruthy()
  expect(request?.body).toBeInstanceOf(FormData)
  expect(await screen.findByRole('status')).toHaveTextContent('güvenli özel arşive yüklendi')
})

test('shows automatic invoice routing and saves only the manual package policy', async () => {
  let policyRequest: RequestInit | undefined
  let policyLoaded = false
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-f4-policy' })
    if (url.includes('/api/v1/connections')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL_EFATURAM', displayName: 'E-Faturam Stage', status: 'ACTIVE', hasCredential: true }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/billing/invoice-policies/connection-1') && init?.method === 'PUT') {
      policyRequest = init
      return json({ id: 'policy-1', providerConnectionId: 'connection-1', triggerState: 'MANUAL_CONFIRMED', packageScope: 'SHIPMENT_PACKAGE', dueRule: 'IMMEDIATE', roundingRule: 'LINE_HALF_AWAY_FROM_ZERO', adjustmentRule: 'REJECT_OVER_ONE_KURUS', autoSubmit: false, version: 2 })
    }
    if (url.includes('/api/v1/billing/invoice-policies/connection-1')) { policyLoaded = true; return json({ id: 'policy-1', providerConnectionId: 'connection-1', triggerState: 'MANUAL_CONFIRMED', packageScope: 'SHIPMENT_PACKAGE', dueRule: 'IMMEDIATE', roundingRule: 'LINE_HALF_AWAY_FROM_ZERO', adjustmentRule: 'REJECT_OVER_ONE_KURUS', autoSubmit: false, version: 1 }) }
    return json({ title: 'Bulunamadı' }, 404)
  }) as typeof fetch

  render(<QueryClientProvider client={client()}><MemoryRouter><BillingSettingsPage /></MemoryRouter></QueryClientProvider>)

  expect(await screen.findByRole('heading', { name: 'Otomatik fatura yönlendirmesi' })).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Belge türü otomatik seçilir' })).toBeInTheDocument()
  expect(screen.getByText('TEMELFATURA')).toBeInTheDocument()
  expect(screen.getByText('EARSIVFATURA')).toBeInTheDocument()
  expect(screen.queryByLabelText('VKN/TCKN')).not.toBeInTheDocument()
  expect(screen.getByText(/Ödeme ve taşıyıcı alanları ekranda ayar değildir/)).toBeInTheDocument()

  const provider = screen.getByLabelText('Provider')
  await within(provider).findByRole('option', { name: 'E-Faturam Stage' })
  fireEvent.change(provider, { target: { value: 'connection-1' } })
  await waitFor(() => expect(policyLoaded).toBe(true))
  const save = screen.getByRole('button', { name: 'Manuel paket politikasını kaydet' })
  await waitFor(() => expect(save).toBeEnabled())
  fireEvent.click(save)

  expect(await screen.findByRole('status')).toHaveTextContent('Manuel paket faturası politikası kaydedildi')
  await waitFor(() => expect(policyRequest).toBeDefined())
  expect(new Headers(policyRequest?.headers).get('If-Match')).toBe('"v1"')
  expect(JSON.parse(String(policyRequest?.body))).toEqual({
    triggerState: 'MANUAL_CONFIRMED',
    packageScope: 'SHIPMENT_PACKAGE',
    dueRule: 'IMMEDIATE',
    roundingRule: 'LINE_HALF_AWAY_FROM_ZERO',
    adjustmentRule: 'REJECT_OVER_ONE_KURUS',
    autoSubmit: false
  })
})
