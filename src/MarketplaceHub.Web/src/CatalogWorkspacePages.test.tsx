import type { ReactNode } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { expect, test, vi } from 'vitest'
import { NewProductPage } from './F2Pages'
import { MappingPage } from './F3Pages'

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const page = <T,>(items: T[]) => ({ items, nextCursor: null, hasMore: false })
const renderPage = (node: ReactNode) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter>{node}</MemoryRouter></QueryClientProvider>)
}

test('creates cartesian variants with variant-scoped attributes and global multi-select values', async () => {
  let productBody: any
  globalThis.fetch = vi.fn((input, init) => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/csrf')) return json({ token: 'csrf-catalog' })
    if (url.includes('/api/v1/catalog/categories?')) return json(page([{ id: 'category-1', name: 'Bluz', path: 'Giyim / Bluz', depth: 1, isLeaf: true, isActive: true, version: 4 }]))
    if (url.includes('/api/v1/catalog/brands?')) return json(page([{ id: 'brand-1', name: 'Ravencia', isActive: true, version: 1 }]))
    if (url.includes('/api/v1/connections?')) return json(page([]))
    if (url.endsWith('/api/v1/catalog/categories/category-1/attribute-requirements')) return json([
      { attributeId: 'size-attribute', isRequired: true, allowsCustomValue: false, displayOrder: 0, attribute: { id: 'size-attribute', code: 'SIZE', name: 'Beden', dataType: 'SINGLE_SELECT', values: [{ id: 'size-s', value: 'S' }, { id: 'size-m', value: 'M' }] } },
      { attributeId: 'color-attribute', isRequired: true, allowsCustomValue: false, displayOrder: 1, attribute: { id: 'color-attribute', code: 'COLOR', name: 'Renk', dataType: 'SINGLE_SELECT', values: [{ id: 'color-white', value: 'Beyaz' }] } },
      { attributeId: 'material-attribute', isRequired: false, allowsCustomValue: false, displayOrder: 2, attribute: { id: 'material-attribute', code: 'MATERIAL', name: 'Materyal', dataType: 'MULTI_SELECT', values: [{ id: 'material-cotton', value: 'Pamuk' }, { id: 'material-viscose', value: 'Viskon' }] } },
    ])
    if (url.endsWith('/api/v1/products') && init?.method === 'POST') {
      productBody = JSON.parse(String(init.body))
      return json({ id: 'product-1', variants: [{ id: 'variant-1' }, { id: 'variant-2' }] }, 201)
    }
    return json({}, 404)
  }) as typeof fetch

  renderPage(<NewProductPage />)
  expect(screen.queryByPlaceholderText('Kategorilerde ara')).not.toBeInTheDocument()
  expect(screen.getByLabelText('Barkod')).toBeInTheDocument()
  expect(screen.getByLabelText('Desi')).toHaveValue(1)
  fireEvent.click(screen.getByRole('checkbox', { name: /Desiyi ölçülerden hesapla/ }))
  expect(screen.getByLabelText('Ağırlık (kg)')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('checkbox', { name: /Desiyi ölçülerden hesapla/ }))
  fireEvent.change(await screen.findByLabelText('Ürün adı'), { target: { value: 'Kadın Desenli Bluz' } })
  fireEvent.change(screen.getByLabelText('Açıklama'), { target: { value: 'Ürün açıklaması' } })
  fireEvent.change(screen.getByRole('combobox', { name: 'Panel kategorisi' }), { target: { value: 'category-1' } })
  fireEvent.change(screen.getByLabelText('Marka'), { target: { value: 'brand-1' } })
  fireEvent.change(screen.getByLabelText('Model kodu'), { target: { value: 'RAV-100' } })
  fireEvent.change(screen.getByLabelText('Temel SKU'), { target: { value: 'RAV-BLUZ' } })

  const sizeToggle = await screen.findByLabelText(/Beden/)
  const colorToggle = screen.getByLabelText(/Renk/)
  fireEvent.click(sizeToggle)
  fireEvent.click(colorToggle)
  fireEvent.click(screen.getByRole('button', { name: 'S' }))
  fireEvent.click(screen.getByRole('button', { name: 'M' }))
  fireEvent.click(screen.getByRole('button', { name: 'Beyaz' }))
  fireEvent.click(screen.getByRole('button', { name: 'Pamuk' }))
  fireEvent.click(screen.getByRole('button', { name: 'Viskon' }))
  fireEvent.click(screen.getByRole('button', { name: 'Ürünleri ekle' }))

  expect(screen.getAllByPlaceholderText('Varyant SKU')).toHaveLength(2)
  fireEvent.click(screen.getByRole('button', { name: 'Ürünü kaydet' }))

  await waitFor(() => expect(productBody).toBeDefined())
  expect(productBody.attributes).toEqual([
    expect.objectContaining({ attributeId: 'material-attribute', valueId: 'material-cotton' }),
    expect.objectContaining({ attributeId: 'material-attribute', valueId: 'material-viscose' }),
  ])
  expect(productBody.variants).toHaveLength(2)
  expect(productBody.variants[0]).toEqual(expect.objectContaining({ desi: 1, weight: null, width: null, height: null, length: null }))
  expect(productBody.variants[0].attributes).toEqual(expect.arrayContaining([
    expect.objectContaining({ attributeId: 'size-attribute' }),
    expect.objectContaining({ attributeId: 'color-attribute', valueId: 'color-white' }),
  ]))
  expect(productBody.variants[1].attributes).toEqual(expect.arrayContaining([
    expect.objectContaining({ attributeId: 'size-attribute' }),
    expect.objectContaining({ attributeId: 'color-attribute', valueId: 'color-white' }),
  ]))
})

test('loads category-scoped attribute mappings in one bulk request', async () => {
  const calls: string[] = []
  globalThis.fetch = vi.fn((input) => {
    const url = String(input); calls.push(url)
    if (url.includes('/api/v1/connections?')) return json(page([{ id: 'connection-1', platformCode: 'TRENDYOL', displayName: 'Trendyol', externalStoreId: '2738', status: 'ACTIVE' }]))
    if (url.includes('/api/v1/catalog/categories?')) return json(page([{ id: 'category-1', name: 'Bluz', path: 'Giyim / Bluz', depth: 1, isLeaf: true, isActive: true, version: 4 }]))
    if (url.includes('/api/v1/catalog/attributes?')) return json(page([{ id: 'attribute-1', code: 'SIZE', name: 'Beden', dataType: 'SINGLE_SELECT', isActive: true, version: 1, values: [{ id: 'size-s', value: 'S', isActive: true }] }]))
    if (url.endsWith('/api/v1/catalog/categories/category-1')) return json({ id: 'category-1', name: 'Bluz', path: 'Giyim / Bluz', depth: 1, isLeaf: true, isActive: true, version: 4 })
    if (url.endsWith('/api/v1/catalog/categories/category-1/attribute-requirements')) return json([{ attributeId: 'attribute-1', isRequired: true, allowsCustomValue: false, displayOrder: 0, attribute: { id: 'attribute-1', code: 'SIZE', name: 'Beden', dataType: 'SINGLE_SELECT', isActive: true, version: 1, values: [{ id: 'size-s', value: 'S', isActive: true }] } }])
    if (url.includes('/api/v1/reference-data/categories?')) return json({ snapshotId: 'category-snapshot', resourceType: 'CATEGORIES', fetchedAt: '2026-08-06T00:00:00Z', items: [{ externalId: '14609', name: 'Bluz', path: 'Giyim / Bluz', isLeaf: true, isActive: true }] })
    if (url.includes('/api/v1/mappings/categories/category-1')) return json({ id: 'category-mapping', connectionId: 'connection-1', snapshotId: 'category-snapshot', localId: 'category-1', scopeExternalId: '', externalId: '14609', status: 'VERIFIED', version: 1 })
    if (url.includes('/api/v1/reference-data/categories/14609/attributes?')) return json({ snapshotId: 'attribute-snapshot', resourceType: 'CATEGORY_ATTRIBUTES', fetchedAt: '2026-08-06T00:00:00Z', items: [{ externalId: '293', name: 'Beden', path: 'Beden', isLeaf: true, isActive: true, isRequired: true, allowsCustomValue: false, allowsMultipleValues: false }] })
    if (url.includes('/api/v1/mappings/attributes?')) return json([{ id: 'attribute-mapping', connectionId: 'connection-1', snapshotId: 'attribute-snapshot', localId: 'attribute-1', scopeExternalId: '14609', externalId: '293', status: 'VERIFIED', version: 2 }])
    if (url.includes('/api/v1/reference-data/categories/14609/attributes/293/values?')) return json({ snapshotId: 'value-snapshot', resourceType: 'ATTRIBUTE_VALUES', fetchedAt: '2026-08-06T00:00:00Z', items: [{ externalId: '1', name: 'S', path: 'S', isLeaf: true, isActive: true }] })
    if (url.includes('/api/v1/mappings/attribute-values/size-s')) return json(null)
    return json({}, 404)
  }) as typeof fetch

  renderPage(<MappingPage kind="categories" />)
  const connection = await screen.findByRole('combobox', { name: 'Aktif Trendyol bağlantısı' })
  fireEvent.focus(connection); fireEvent.change(connection, { target: { value: 'Trendyol' } }); fireEvent.keyDown(connection, { key: 'Enter' })
  const category = await screen.findByRole('combobox', { name: 'Panel yaprak kategorisi' })
  fireEvent.focus(category); fireEvent.change(category, { target: { value: 'Giyim / Bluz' } }); fireEvent.keyDown(category, { key: 'Enter' })

  expect(await screen.findByText('1/1 zorunlu özellik eşlendi')).toBeInTheDocument()
  expect(calls.filter(url => url.includes('/api/v1/mappings/attributes?'))).toHaveLength(1)
  expect(calls.some(url => url.includes('/api/v1/mappings/attributes/attribute-1?'))).toBe(false)
})
