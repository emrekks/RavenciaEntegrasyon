import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { expect, test, vi } from 'vitest'
import { BrandMappingPage, IntegrationDetailPage, IntegrationsPage, MappingPage } from './F3Pages'

const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } }))
async function chooseSearchable(label: string, option: string) { const input = await screen.findByRole('combobox', { name: label }); await waitFor(() => expect(input).toBeEnabled()); fireEvent.focus(input); fireEvent.change(input, { target: { value: option } }); await waitFor(() => expect(screen.getAllByRole('option').length).toBeGreaterThan(0)); fireEvent.keyDown(input, { key: 'Enter' }); return input }

test('maps a local leaf category to the verified Trendyol snapshot', async () => {
  let savedBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-test' })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'connection-1', publicId: 'public-1', platformCode: 'TRENDYOL', environment: 'STAGE', displayName: 'Trendyol Stage', externalStoreId: 'seller-1', status: 'ACTIVE', apiVersion: 'V2', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/categories?')) return json({ items: [{ id: 'local-1', name: 'Elbise', path: 'Giyim / Elbise', depth: 1, isLeaf: true, isActive: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/reference-data/categories?')) return json({ snapshotId: 'snapshot-1', resourceType: 'CATEGORIES', fetchedAt: '2026-08-04T00:00:00Z', items: [{ externalId: 'external-1', parentExternalId: 'parent-1', name: 'Elbise', path: 'Kadın / Giyim / Elbise', depth: 2, isLeaf: true, isActive: true }] })
    if (url.includes('/api/v1/mappings/categories/local-1') && init?.method === 'PUT') {
      savedBody = String(init.body)
      return json({ id: 'mapping-1', connectionId: 'connection-1', snapshotId: 'snapshot-1', localId: 'local-1', externalId: 'external-1', status: 'VERIFIED', verifiedAt: '2026-08-04T00:01:00Z', version: 1 })
    }
    if (url.includes('/api/v1/mappings/categories/local-1')) return json(null)
    return Promise.resolve(new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }))
  }) as typeof fetch
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><MappingPage kind="categories" /></MemoryRouter></QueryClientProvider>)

  const local = await chooseSearchable('Panel kategorisi', 'Giyim / Elbise')
  await waitFor(() => expect(local).toHaveValue('Giyim / Elbise'))
  await chooseSearchable('Trendyol yaprak kategorisi', 'Kadın / Giyim / Elbise')
  fireEvent.click(await screen.findByRole('button', { name: 'Eşle' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Kategori eşleşti')
  await waitFor(() => expect(JSON.parse(savedBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'snapshot-1', externalId: 'external-1', status: 'VERIFIED' }))
})

test('creates and selects a panel category from the mapping workspace', async () => {
  let createdBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-category-create' })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL', environment: 'STAGE', displayName: 'Trendyol Stage', externalStoreId: '2738', status: 'ACTIVE' }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/categories?')) return json({ items: [{ id: 'parent-1', name: 'Giyim', path: 'Giyim', depth: 0, isLeaf: false, isActive: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/attributes?')) return json({ items: [], nextCursor: null, hasMore: false })
    if (url.includes('/attribute-requirements')) return json([])
    if (url.endsWith('/api/v1/catalog/categories') && init?.method === 'POST') { createdBody = String(init.body); return json({ id: 'new-1', name: 'Anne Bluz', path: 'Giyim / Anne Bluz', depth: 1, isLeaf: true, isActive: true, version: 1 }) }
    if (url.includes('/api/v1/reference-data/categories?')) return json({ snapshotId: 'snapshot-1', resourceType: 'CATEGORIES', fetchedAt: '2026-08-04T00:00:00Z', items: [] })
    return json({})
  }) as typeof fetch
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><MappingPage kind="categories" /></MemoryRouter></QueryClientProvider>)

  fireEvent.change(screen.getByLabelText('Yeni panel kategorisi adı'), { target: { value: 'Anne Bluz' } })
  fireEvent.click(screen.getByRole('button', { name: '+ Kategori ekle' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Anne Bluz')
  expect(JSON.parse(createdBody)).toEqual({ name: 'Anne Bluz', parentId: null })
})

test('shows only the two ADR-016 integrations in active connection UI', async () => {
  const connection = (id: string, platformCode: string, displayName: string) => ({ id, publicId: `public-${id}`, platformCode, environment: 'STAGE', displayName, externalStoreId: `store-${id}`, status: 'VERIFIED', apiVersion: 'v1', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 1 })
  globalThis.fetch = vi.fn(() => json({ items: [connection('1', 'TRENDYOL', 'Trendyol Aktif'), connection('2', 'TRENDYOL_EFATURAM', 'E-Faturam Aktif'), connection('3', 'LEGACY_PLATFORM_A', 'Eski Platform A'), connection('4', 'LEGACY_PLATFORM_B', 'Eski Platform B')], nextCursor: null, hasMore: false })) as typeof fetch
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><IntegrationsPage /></MemoryRouter></QueryClientProvider>)

  expect(await screen.findByText('Trendyol Aktif')).toBeInTheDocument()
  expect(screen.queryByText('Eski Platform A')).not.toBeInTheDocument()
  expect(screen.getByText('E-Faturam Aktif')).toBeInTheDocument()
  expect(screen.queryByText('Eski Platform B')).not.toBeInTheDocument()
  expect(screen.queryByRole('option', { name: /Eski Platform/i })).not.toBeInTheDocument()
})

test('maps a local brand to the verified Trendyol brand snapshot', async () => {
  let savedBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-brand' })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL', environment: 'STAGE', displayName: 'Trendyol Stage', externalStoreId: 'seller-1', status: 'ACTIVE', apiVersion: 'V2', hasCredential: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/brands?')) return json({ items: [{ id: 'local-brand-1', name: 'Ravencia', isActive: true, version: 1 }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/reference-data/brands?')) return json({ snapshotId: 'brand-snapshot-1', resourceType: 'BRANDS', fetchedAt: '2026-08-04T00:00:00Z', items: [{ externalId: 'external-brand-1', parentExternalId: null, name: 'Ravencia', path: 'Ravencia', depth: 0, isLeaf: true, isActive: true }] })
    if (url.includes('/api/v1/mappings/brands/local-brand-1') && init?.method === 'PUT') { savedBody = String(init.body); return json({ id: 'brand-mapping-1', connectionId: 'connection-1', snapshotId: 'brand-snapshot-1', localId: 'local-brand-1', externalId: 'external-brand-1', status: 'VERIFIED', verifiedAt: '2026-08-04T00:01:00Z', version: 1 }) }
    if (url.includes('/api/v1/mappings/brands/local-brand-1')) return json(null)
    return Promise.resolve(new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }))
  }) as typeof fetch
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><BrandMappingPage /></MemoryRouter></QueryClientProvider>)

  const brandSection = (await screen.findByRole('heading', { name: 'Eşleştirme Merkezi' })).closest('section')!; const page = within(brandSection)
  await chooseSearchable('Panel markası', 'Ravencia')
  await chooseSearchable('Trendyol markası', 'Ravencia')
  fireEvent.click(page.getByRole('button', { name: 'Eşleştirmeyi kaydet' }))

  expect(await page.findByRole('status')).toHaveTextContent('Marka eşlemesi doğrulandı')
  await waitFor(() => expect(JSON.parse(savedBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'brand-snapshot-1', externalId: 'external-brand-1', status: 'VERIFIED' }))
})

test('maps a category-scoped attribute and its value in the unified mapping workspace', async () => {
  let attributeSaved = false
  let valueSaved = false
  let attributeBody = ''
  let valueBody = ''
  let valueMappingUrl = ''

  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-attribute-value' })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL', displayName: 'Trendyol Stage', externalStoreId: 'seller-1', status: 'ACTIVE' }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/categories?')) return json({ items: [{ id: 'local-category-1', path: 'Giyim / Elbise', isLeaf: true, isActive: true }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/attributes?')) return json({ items: [{ id: 'local-attribute-1', code: 'SIZE', name: 'Beden', dataType: 'SINGLE_SELECT', isActive: true, values: [{ id: 'local-value-1', value: 'M', isActive: true }] }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/mappings/categories/local-category-1')) return json({ id: 'category-mapping-1', connectionId: 'connection-1', snapshotId: 'category-snapshot-1', localId: 'local-category-1', scopeExternalId: '', externalId: '14609', status: 'VERIFIED', version: 1 })
    if (url.includes('/api/v1/reference-data/categories/14609/attributes/293/values?')) return json({ snapshotId: 'value-snapshot-1', resourceType: 'ATTRIBUTE_VALUES', fetchedAt: '2026-08-05T00:00:00Z', items: [{ externalId: 'value-2', parentExternalId: '293', name: 'M', path: 'M', depth: 0, isLeaf: true, isActive: true }] })
    if (url.includes('/api/v1/reference-data/categories/14609/attributes?')) return json({ snapshotId: 'attribute-snapshot-1', resourceType: 'CATEGORY_ATTRIBUTES', fetchedAt: '2026-08-05T00:00:00Z', items: [{ externalId: '293', parentExternalId: '14609', name: 'Beden', path: 'Beden', depth: 0, isLeaf: true, isActive: true, isRequired: true, allowsCustomValue: false, allowsMultipleValues: false }] })
    if (url.includes('/api/v1/mappings/attributes/local-attribute-1') && init?.method === 'PUT') {
      attributeBody = String(init.body)
      attributeSaved = true
      return json({ id: 'attribute-mapping-1', connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', localId: 'local-attribute-1', scopeExternalId: '14609', externalId: '293', status: 'VERIFIED', version: 1 })
    }
    if (url.includes('/api/v1/mappings/attributes/local-attribute-1')) return json(attributeSaved ? { id: 'attribute-mapping-1', connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', localId: 'local-attribute-1', scopeExternalId: '14609', externalId: '293', status: 'VERIFIED', version: 1 } : null)
    if (url.includes('/api/v1/mappings/attribute-values/local-value-1') && init?.method === 'PUT') {
      valueMappingUrl = url
      valueBody = String(init.body)
      valueSaved = true
      return json({ id: 'value-mapping-1', connectionId: 'connection-1', snapshotId: 'value-snapshot-1', localId: 'local-value-1', scopeExternalId: '14609/293', externalId: 'value-2', status: 'VERIFIED', version: 1 })
    }
    if (url.includes('/api/v1/mappings/attribute-values?')) return json(valueSaved ? [{ id: 'value-mapping-1', connectionId: 'connection-1', snapshotId: 'value-snapshot-1', localId: 'local-value-1', scopeExternalId: '14609/293', externalId: 'value-2', status: 'VERIFIED', version: 1 }] : [])
    return Promise.resolve(new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }))
  }) as typeof fetch

  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><MappingPage kind="attributes" /></MemoryRouter></QueryClientProvider>)

  expect(await screen.findByRole('heading', { name: 'Özellik eşlemeleri', level: 1 })).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Kategori kapsamını seçin' })).toBeInTheDocument()

  const connection = screen.getByLabelText('Özellik için aktif Trendyol bağlantısı')
  await within(connection).findByRole('option', { name: 'Trendyol Stage · seller-1' })
  fireEvent.change(connection, { target: { value: 'connection-1' } })

  const category = screen.getByLabelText('Özellik kapsamı panel kategorisi')
  await within(category).findByRole('option', { name: 'Giyim / Elbise' })
  fireEvent.change(category, { target: { value: 'local-category-1' } })

  const localAttribute = await screen.findByLabelText('Panel özelliği')
  await within(localAttribute).findByRole('option', { name: 'Beden · SINGLE_SELECT' })
  fireEvent.change(localAttribute, { target: { value: 'local-attribute-1' } })

  const remoteAttribute = screen.getByLabelText('Trendyol kategori özelliği')
  await within(remoteAttribute).findByRole('option', { name: 'Beden · zorunlu' })
  await waitFor(() => expect(remoteAttribute).toBeEnabled())
  fireEvent.change(remoteAttribute, { target: { value: '293' } })
  const saveAttribute = screen.getByRole('button', { name: 'Eşlemeyi doğrula ve kaydet' })
  await waitFor(() => expect(saveAttribute).toBeEnabled())
  fireEvent.click(saveAttribute)

  expect(await screen.findByText('Özellik eşlemesi doğrulandı ve kategori kapsamında kaydedildi.')).toBeInTheDocument()
  await waitFor(() => expect(JSON.parse(attributeBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', scopeExternalId: '14609', externalId: '293', status: 'VERIFIED' }))

  expect(await screen.findByRole('heading', { name: 'Değer eşleştirmeleri' })).toBeInTheDocument()
  const remoteValue = screen.getByLabelText('M Trendyol değeri')
  await within(remoteValue).findByRole('option', { name: 'M' })
  fireEvent.change(remoteValue, { target: { value: 'value-2' } })
  fireEvent.click(screen.getByRole('button', { name: 'Tüm eşlemeleri kaydet' }))

  expect(await screen.findByText('1 değer eşlemesi kaydedildi.')).toBeInTheDocument()
  expect(valueMappingUrl).toContain('/mappings/attribute-values/local-value-1')
  await waitFor(() => expect(JSON.parse(valueBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'value-snapshot-1', scopeExternalId: '14609/293', externalId: 'value-2', status: 'VERIFIED' }))
})


test('stores the provider-required E-Faturam partner and customer credentials without exposing fiscal settings', async () => {
  let credentialRequest: RequestInit | undefined
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-efaturam-direct' })
    if (url.endsWith('/api/v1/connections/connection-ef/capabilities')) return json([])
    if (url.endsWith('/api/v1/connections/connection-ef/credential') && init?.method === 'PUT') {
      credentialRequest = init
      return json({})
    }
    if (url.endsWith('/api/v1/connections/connection-ef')) return json({ id: 'connection-ef', publicId: 'public-ef', platformCode: 'TRENDYOL_EFATURAM', environment: 'STAGE', displayName: 'E-Faturam Stage', externalStoreId: '100001', status: 'VERIFIED', apiVersion: '1.0.0', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 4 })
    return Promise.resolve(new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }))
  }) as typeof fetch

  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/integrations/connection-ef']}><Routes><Route path="/integrations/:id" element={<IntegrationDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)

  expect(await screen.findByRole('heading', { name: 'E-Faturam müşteri kapsamı otomatik alınır' })).toBeInTheDocument()
  expect(screen.queryByText('E-Faturam mali hesap ayarları')).not.toBeInTheDocument()
  expect(screen.queryByLabelText('E-Fatura senaryosu')).not.toBeInTheDocument()
  expect(screen.queryByLabelText('Kargo tüzel kimlik eşlemeleri')).not.toBeInTheDocument()

  fireEvent.change(screen.getByLabelText('Partner e-postası'), { target: { value: 'partner@example.test' } })
  fireEvent.change(screen.getByLabelText('Partner parolası'), { target: { value: 'partner-password' } })
  fireEvent.change(screen.getByLabelText('Müşteri e-postası'), { target: { value: 'customer@example.test' } })
  fireEvent.change(screen.getByLabelText('Müşteri parolası'), { target: { value: 'customer-password' } })
  fireEvent.change(screen.getByLabelText('Müşteri VKN / TCKN'), { target: { value: '1234567890' } })
  fireEvent.click(screen.getByRole('button', { name: 'Şifreli kaydet' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Credential şifreli olarak yenilendi')
  await waitFor(() => expect(credentialRequest).toBeDefined())
  const headers = new Headers(credentialRequest?.headers)
  expect(headers.get('If-Match')).toBe('"v4"')
  expect(headers.get('Idempotency-Key')).toBeTruthy()
  expect(headers.get('X-CSRF-TOKEN')).toBeTruthy()
  expect(JSON.parse(String(credentialRequest?.body))).toEqual({ email: 'partner@example.test', password: 'partner-password', customerEmail: 'customer@example.test', customerPassword: 'customer-password', customerTaxId: '1234567890' })
})

test('queues a read-only refresh for one Trendyol order number', async () => {
  let refreshBody = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-order-refresh' })
    if (url.endsWith('/api/v1/connections/connection-trendyol/capabilities')) return json([{ code: 'ORDER_READ', supportLevel: 'SUPPORTED', sourceUrl: 'https://developers.trendyol.com', verifiedAt: '2026-08-09T00:00:00Z', constraintsJson: null, version: 1 }])
    if (url.endsWith('/api/v1/connections/connection-trendyol/order-sync-jobs') && init?.method === 'POST') { refreshBody = String(init.body); return json({ id: 'job-1' }) }
    if (url.endsWith('/api/v1/connections/connection-trendyol')) return json({ id: 'connection-trendyol', publicId: 'public-trendyol', platformCode: 'TRENDYOL', environment: 'STAGE', displayName: 'Trendyol Stage', externalStoreId: 'seller-1', status: 'ACTIVE', apiVersion: 'V2', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 1 })
    return Promise.resolve(new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }))
  }) as typeof fetch

  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/integrations/connection-trendyol']}><Routes><Route path="/integrations/:id" element={<IntegrationDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)

  const input = await screen.findByLabelText('Sipariş numarasıyla yenile')
  fireEvent.change(input, { target: { value: '1238693012' } })
  fireEvent.click(screen.getByRole('button', { name: 'Yalnız bu siparişi yenile' }))

  expect(await screen.findByRole('status')).toHaveTextContent('İş kuyruğa alındı')
  await waitFor(() => expect(JSON.parse(refreshBody)).toEqual({ externalOrderId: '1238693012' }))
})
