import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'

const apiState = vi.hoisted(() => ({ calls: [] as string[] }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const brands = [{ externalId: '2216217', name: 'Polo Garage', isActive: true }, { externalId: '101', name: 'Polo', isActive: true }, { externalId: '303', name: 'Apolo', isActive: true }]
  const respond = async (path: string) => {
    apiState.calls.push(path)
    const url = new URL(path, 'http://hub.test')
    const slice = (items: typeof brands) => ({ snapshotId: 'snap-1', resourceType: 'BRANDS', fetchedAt: url.searchParams.get('connectionId') === 'conn-new' ? '2026-10-29T12:00:00Z' : '2026-10-28T12:00:00Z', itemCount: 808409, items })
    if (url.pathname === '/reference-data/brands/search') {
      const query = (url.searchParams.get('q') ?? '').toLocaleUpperCase('tr-TR')
      return slice(brands.filter(item => !query || item.name.toLocaleUpperCase('tr-TR').includes(query) || item.externalId === query))
    }
    if (url.pathname === '/reference-data/brands/lookup') {
      const ids = (url.searchParams.get('ids') ?? '').split(',')
      return slice(brands.filter(item => ids.includes(item.externalId)))
    }
    throw new Error(`Test API fixture does not handle ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { BrandReferencePicker, brandLookupBatchSize, lookupBrandReferences, type BrandReferenceSlice } from './brand-reference'

const snapshot: BrandReferenceSlice = { snapshotId: 'snap-1', resourceType: 'BRANDS', fetchedAt: '2026-10-28T12:00:00Z', itemCount: 808409, items: [] }
const wait = (ms: number) => act(async () => { await new Promise(resolve => setTimeout(resolve, ms)) })
const searches = () => apiState.calls.filter(path => path.startsWith('/reference-data/brands/search'))

function input(host: HTMLElement) {
  return host.querySelector<HTMLInputElement>('.brand-reference-picker input')!
}

function type(element: HTMLInputElement, value: string) {
  act(() => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

describe('brand reference picker', () => {
  let client: QueryClient
  beforeEach(() => {
    apiState.calls = []
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  })
  afterEach(() => cleanup())

  it('searches the server as the user types, debounced, and never downloads the whole list', async () => {
    const onChange = vi.fn()
    const { host } = render(<QueryClientProvider client={client}><BrandReferencePicker connectionId="conn-1" snapshot={snapshot} value="" placeholder="Platform Markası Seçin..." onChange={onChange} /></QueryClientProvider>)
    expect(apiState.calls).toEqual([])
    act(() => host.querySelector<HTMLButtonElement>('.panel-category-picker-trigger')!.click())
    await wait(20)
    expect(searches()).toEqual(['/reference-data/brands/search?connectionId=conn-1&q=&limit=50'])

    type(input(host), 'p')
    type(input(host), 'po')
    type(input(host), 'polo')
    await wait(50)
    // Still inside the debounce: no request per keystroke.
    expect(searches()).toHaveLength(1)
    await wait(350)
    expect(searches()).toEqual(['/reference-data/brands/search?connectionId=conn-1&q=&limit=50', '/reference-data/brands/search?connectionId=conn-1&q=polo&limit=50'])
    const options = [...host.querySelectorAll<HTMLButtonElement>('[role="option"]')]
    expect(options.map(option => option.querySelector('span')?.textContent)).toEqual(['Polo Garage', 'Polo', 'Apolo'])

    act(() => options[0].click())
    expect(onChange).toHaveBeenCalledWith('2216217', { externalId: '2216217', name: 'Polo Garage', isActive: true })
    expect(host.querySelector('.brand-reference-picker')).toBeNull()
    expect(apiState.calls.some(path => path.startsWith('/reference-data/brands?'))).toBe(false)
  })

  it('answers a repeated search from the cache until the brand list changes', async () => {
    const view = (current: BrandReferenceSlice) => <QueryClientProvider client={client}><BrandReferencePicker connectionId="conn-1" snapshot={current} value="2216217" selectedName="Polo Garage" placeholder="Seçin" onChange={() => undefined} /></QueryClientProvider>
    const { host, rerender } = render(view(snapshot))
    expect(host.querySelector('.panel-category-picker-trigger')?.textContent).toContain('Polo Garage')
    const open = async () => { act(() => host.querySelector<HTMLButtonElement>('.panel-category-picker-trigger')!.click()); await wait(20) }
    const close = () => act(() => host.querySelector<HTMLButtonElement>('[aria-label="Platform marka menüsünü kapat"]')!.click())

    await open()
    // The chosen brand leads the list.
    expect(host.querySelector('[role="option"][aria-selected="true"] span')?.textContent).toBe('Polo Garage')
    close()
    await open()
    expect(searches()).toHaveLength(1)
    close()

    // The worker's daily read produced a newer list: the search is asked again.
    rerender(view({ ...snapshot, fetchedAt: '2026-10-29T12:00:00Z' }))
    await open()
    expect(searches()).toHaveLength(2)
  })

  it('looks saved brand ids up in batches and leaves out ids the list no longer has', async () => {
    const ids = [...Array.from({ length: brandLookupBatchSize + 50 }, (_, index) => `missing-${index}`), '101', '303', '101']
    const found = await lookupBrandReferences('conn-1', ids)
    const lookups = apiState.calls.filter(path => path.startsWith('/reference-data/brands/lookup'))
    expect(lookups).toHaveLength(2)
    expect(new URL(lookups[0], 'http://hub.test').searchParams.get('ids')!.split(',')).toHaveLength(brandLookupBatchSize)
    expect(found.map(item => item.name).sort()).toEqual(['Apolo', 'Polo'])
  })
})
