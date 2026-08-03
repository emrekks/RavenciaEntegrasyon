import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { expect, test, vi } from 'vitest'
import { MappingPage } from './F3Pages'

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
