import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { expect, test, vi } from 'vitest'
import { AttributeMappingPage, BrandMappingPage, IntegrationsPage, MappingPage } from './F3Pages'

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

test('shows only the three ADR-015 platforms in active connection UI', async () => {
  const connection = (id: string, platformCode: string, displayName: string) => ({ id, publicId: `public-${id}`, platformCode, environment: 'STAGE', displayName, externalStoreId: `store-${id}`, status: 'VERIFIED', apiVersion: 'v1', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, version: 1 })
  globalThis.fetch = vi.fn(() => json({ items: [connection('1', 'TRENDYOL', 'Trendyol Aktif'), connection('2', 'HEPSIBURADA', 'Hepsiburada Aktif'), connection('3', 'TRENDYOL_EFATURAM', 'E-Faturam Aktif'), connection('4', 'SHOPIFY', 'Shopify Ertelenmiş')], nextCursor: null, hasMore: false })) as typeof fetch
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><IntegrationsPage /></MemoryRouter></QueryClientProvider>)

  expect(await screen.findByText('Trendyol Aktif')).toBeInTheDocument()
  expect(screen.getByText('Hepsiburada Aktif')).toBeInTheDocument()
  expect(screen.getByText('E-Faturam Aktif')).toBeInTheDocument()
  expect(screen.queryByText('Shopify Ertelenmiş')).not.toBeInTheDocument()
  expect(screen.queryByRole('option', { name: /Shopify/i })).not.toBeInTheDocument()
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

test('maps a local attribute inside the verified Trendyol category scope', async () => {
  let savedBody = ''; let mappingUrl = ''
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-attribute' })
    if (url.includes('/api/v1/connections?')) return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL', displayName: 'Trendyol Stage', externalStoreId: 'seller-1', status: 'ACTIVE' }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/categories?')) return json({ items: [{ id: 'local-category-1', path: 'Giyim / Elbise', isLeaf: true, isActive: true }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/catalog/attributes?')) return json({ items: [{ id: 'local-attribute-1', code: 'SIZE', name: 'Beden', dataType: 'SINGLE_SELECT', isActive: true }], nextCursor: null, hasMore: false })
    if (url.includes('/api/v1/mappings/categories/local-category-1')) return json({ id: 'category-mapping-1', connectionId: 'connection-1', snapshotId: 'category-snapshot-1', localId: 'local-category-1', scopeExternalId: '', externalId: '14609', status: 'VERIFIED', version: 1 })
    if (url.includes('/api/v1/reference-data/categories/14609/attributes?')) return json({ snapshotId: 'attribute-snapshot-1', resourceType: 'CATEGORY_ATTRIBUTES', fetchedAt: '2026-08-04T00:00:00Z', items: [{ externalId: '293', parentExternalId: '14609', name: 'Beden', path: 'Beden', depth: 0, isLeaf: true, isActive: true }] })
    if (url.includes('/api/v1/mappings/attributes/local-attribute-1') && init?.method === 'PUT') { mappingUrl = url; savedBody = String(init.body); return json({ id: 'attribute-mapping-1', connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', localId: 'local-attribute-1', scopeExternalId: '14609', externalId: '293', status: 'VERIFIED', version: 1 }) }
    if (url.includes('/api/v1/mappings/attributes/local-attribute-1')) return json(null)
    return Promise.resolve(new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }))
  }) as typeof fetch
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><AttributeMappingPage /></MemoryRouter></QueryClientProvider>)

  const attributeSection = (await screen.findByRole('heading', { name: 'Özellik eşlemeleri', level: 1 })).closest('section')!; const page = within(attributeSection)
  const connection = page.getByLabelText('Özellik için aktif Trendyol bağlantısı'); await within(connection).findByRole('option', { name: 'Trendyol Stage · seller-1' }); fireEvent.change(connection, { target: { value: 'connection-1' } })
  const category = page.getByLabelText('Özellik kapsamı panel kategorisi'); await within(category).findByRole('option', { name: 'Giyim / Elbise' }); fireEvent.change(category, { target: { value: 'local-category-1' } })
  const local = await page.findByLabelText('Panel özelliği'); await within(local).findByRole('option', { name: 'Beden · SINGLE_SELECT' }); fireEvent.change(local, { target: { value: 'local-attribute-1' } })
  const external = await page.findByLabelText('Trendyol kategori özelliği'); await within(external).findByRole('option', { name: 'Beden' }); fireEvent.change(external, { target: { value: '293' } })
  const save = page.getByRole('button', { name: 'Eşlemeyi doğrula ve kaydet' }); await waitFor(() => expect(save).toBeEnabled()); fireEvent.click(save)

  expect(await page.findByRole('status')).toHaveTextContent('Özellik eşlemesi doğrulandı')
  expect(mappingUrl).toContain('/mappings/attributes/local-attribute-1')
  await waitFor(() => expect(JSON.parse(savedBody)).toEqual({ connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', externalId: '293', status: 'VERIFIED' }))
})
