import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { expect, test, vi } from 'vitest'
import { BrandMappingPage, IntegrationDetailPage, IntegrationsPage, MappingPage } from './F3Pages'

const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } }))

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

  await screen.findByRole('option', { name: 'Trendyol Stage · seller-1' })
  fireEvent.change(screen.getByLabelText('Aktif Trendyol bağlantısı'), { target: { value: 'connection-1' } })
  const local = await screen.findByLabelText('Panel yaprak kategorisi')
  await waitFor(() => expect(local).toBeEnabled())
  fireEvent.change(local, { target: { value: 'local-1' } })
  const external = await screen.findByLabelText('Trendyol yaprak kategorisi')
  await waitFor(() => expect(screen.getByRole('option', { name: 'Kadın / Giyim / Elbise' })).toBeInTheDocument())
  fireEvent.change(external, { target: { value: 'external-1' } })
  fireEvent.click(await screen.findByRole('button', { name: 'Eşlemeyi doğrula ve kaydet' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Kategori eşlemesi doğrulandı')
  await waitFor(() => expect(JSON.parse(savedBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'snapshot-1', externalId: 'external-1', status: 'VERIFIED' }))
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

  const brandSection = (await screen.findByRole('heading', { name: 'Marka eşlemeleri' })).closest('section')!; const page = within(brandSection)
  const connection = await page.findByLabelText('Marka için aktif Trendyol bağlantısı'); await within(connection).findByRole('option', { name: 'Trendyol Stage · seller-1' }); fireEvent.change(connection, { target: { value: 'connection-1' } })
  const local = await page.findByLabelText('Panel markası'); await waitFor(() => expect(local).toBeEnabled()); fireEvent.change(local, { target: { value: 'local-brand-1' } })
  const external = await page.findByLabelText('Trendyol markası'); await within(external).findByRole('option', { name: 'Ravencia' }); fireEvent.change(external, { target: { value: 'external-brand-1' } })
  fireEvent.click(page.getByRole('button', { name: 'Eşlemeyi doğrula ve kaydet' }))

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
    if (url.includes('/api/v1/mappings/attribute-values/local-value-1')) return json(valueSaved ? { id: 'value-mapping-1', connectionId: 'connection-1', snapshotId: 'value-snapshot-1', localId: 'local-value-1', scopeExternalId: '14609/293', externalId: 'value-2', status: 'VERIFIED', version: 1 } : null)
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
  fireEvent.change(remoteAttribute, { target: { value: '293' } })
  fireEvent.click(screen.getByRole('button', { name: 'Eşlemeyi doğrula ve kaydet' }))

  expect(await screen.findByText('Özellik eşlemesi doğrulandı ve kategori kapsamında kaydedildi.')).toBeInTheDocument()
  await waitFor(() => expect(JSON.parse(attributeBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', externalId: '293', status: 'VERIFIED' }))

  expect(await screen.findByRole('heading', { name: 'Özellik değeri eşlemesi' })).toBeInTheDocument()
  const localValue = screen.getByLabelText('Panel özellik değeri')
  fireEvent.change(localValue, { target: { value: 'local-value-1' } })
  const remoteValue = screen.getByLabelText('Trendyol özellik değeri')
  await within(remoteValue).findByRole('option', { name: 'M' })
  fireEvent.change(remoteValue, { target: { value: 'value-2' } })
  fireEvent.click(screen.getByRole('button', { name: 'Değer eşlemesini kaydet' }))

  expect(await screen.findByText('Özellik değeri eşlemesi doğrulandı.')).toBeInTheDocument()
  expect(valueMappingUrl).toContain('/mappings/attribute-values/local-value-1')
  await waitFor(() => expect(JSON.parse(valueBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'value-snapshot-1', externalId: 'value-2', status: 'VERIFIED' }))
})


test('loads secret-free E-Faturam settings and submits all carrier mappings with ETag', async () => {
  let patch: RequestInit | undefined
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-efaturam-settings' })
    if (url.endsWith('/api/v1/connections/connection-ef/efaturam-settings')) return json({ integrationModel: 'MARKETPLACE', companyId: 10, userId: 20, prefix: 'RVN', carriers: [{ providerName: 'ANON-CARGO-A', taxId: '1111111111', legalName: 'Anonim Kargo A A.Ş.' }, { providerName: 'ANON-CARGO-B', taxId: '22222222222', legalName: 'Anonim Kargo B A.Ş.' }], purchaseUrl: 'https://www.trendyol.com', paymentAgentName: 'Trendyol', paymentType: 'PAZARYERI', paymentMeans: 'MEDIATOR', eInvoiceType: 'TEMELFATURA', externalWritesEnabled: false, version: 4 })
    if (url.endsWith('/api/v1/connections/connection-ef/capabilities')) return json([])
    if (url.endsWith('/api/v1/connections/connection-ef') && init?.method === 'PATCH') { patch = init; return json({ id: 'connection-ef', publicId: 'public-ef', platformCode: 'TRENDYOL_EFATURAM', environment: 'STAGE', displayName: 'E-Faturam Stage', externalStoreId: '100001', status: 'VERIFIED', apiVersion: '1.0.0', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 5 }) }
    if (url.endsWith('/api/v1/connections/connection-ef')) return json({ id: 'connection-ef', publicId: 'public-ef', platformCode: 'TRENDYOL_EFATURAM', environment: 'STAGE', displayName: 'E-Faturam Stage', externalStoreId: '100001', status: 'VERIFIED', apiVersion: '1.0.0', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 4 })
    return Promise.resolve(new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }))
  }) as typeof fetch

  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/integrations/connection-ef']}><Routes><Route path="/integrations/:id" element={<IntegrationDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>)

  expect(await screen.findByRole('heading', { name: 'E-Faturam mali hesap ayarları' })).toBeInTheDocument()
  const carriers = screen.getByLabelText('Kargo tüzel kimlik eşlemeleri')
  expect(carriers).toHaveValue('ANON-CARGO-A | 1111111111 | Anonim Kargo A A.Ş.\nANON-CARGO-B | 22222222222 | Anonim Kargo B A.Ş.')
  fireEvent.change(screen.getByLabelText('E-Fatura senaryosu'), { target: { value: 'TICARIFATURA' } })
  fireEvent.change(carriers, { target: { value: 'ANON-CARGO-A | 1111111111 | Anonim Kargo A A.Ş.\nANON-CARGO-C | 3333333333 | Anonim Kargo C A.Ş.' } })
  fireEvent.click(screen.getByRole('button', { name: 'Mali hesap ayarlarını kaydet' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Bağlantı ayarları güncellendi.')
  await waitFor(() => expect(patch).toBeDefined())
  expect(new Headers(patch?.headers).get('If-Match')).toBe('"v4"')
  expect(JSON.parse(String(patch?.body))).toMatchObject({
    displayName: 'E-Faturam Stage',
    efaturamIntegrationModel: 'MARKETPLACE',
    efaturamCompanyId: 10,
    efaturamUserId: 20,
    efaturamPrefix: 'RVN',
    efaturamEInvoiceType: 'TICARIFATURA',
    efaturamCarriers: [
      { providerName: 'ANON-CARGO-A', taxId: '1111111111', legalName: 'Anonim Kargo A A.Ş.' },
      { providerName: 'ANON-CARGO-C', taxId: '3333333333', legalName: 'Anonim Kargo C A.Ş.' }
    ]
  })
})
