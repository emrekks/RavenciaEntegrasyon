import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// The order list's "Etiket Durumu" filter is the server's (labelPrinted, kept per package): an address opened with it
// reads one page, not the whole order history first.
const requested: string[] = []

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const answer = async (path: string) => {
    requested.push(path)
    if (path.startsWith('/orders?')) return { items: [], nextCursor: null, hasMore: false, totalCount: 0, supportedFilters: ['labelPrinted'] }
    if (path.startsWith('/orders/facets')) return { platforms: {}, cargos: {} }
    if (path.startsWith('/orders/summary')) return {}
    if (path.startsWith('/dashboard/cancellations')) return { stores: [] }
    if (path.startsWith('/dashboard/bootstrap')) return { channels: [] }
    if (path.startsWith('/settings/shipping-label')) return { settings: null, version: 0 }
    return null
  }
  return { ...original, hubApi: vi.fn(answer), hubPoll: vi.fn(answer), loadAllPages: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }
})

import { OrdersPage } from './OrdersPage'

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  requested.length = 0
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('OrdersPage label filter', () => {
  it('asks the server for one filtered page when opened with the filter', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders?label=NOT_PRINTED']}><OrdersPage /></MemoryRouter></QueryClientProvider>) })
    for (let index = 0; index < 10; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
    const lists = requested.filter(path => path.startsWith('/orders?'))
    expect(lists).toHaveLength(1)
    const params = new URLSearchParams(lists[0].split('?')[1])
    expect(params.get('labelPrinted')).toBe('false')
    expect(params.get('limit')).toBe('50')
    // The tab counters count under the same filter from the first read.
    expect(requested.filter(path => path.startsWith('/orders/summary?') && !path.includes('resentNotShipped') && !path.includes('micro')).every(path => path.includes('labelPrinted=false'))).toBe(true)
  })
})
