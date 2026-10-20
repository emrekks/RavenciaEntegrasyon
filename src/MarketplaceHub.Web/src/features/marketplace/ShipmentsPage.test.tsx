import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, render, waitFor } from '../../test/render'

const api = vi.hoisted(() => ({ paths: [] as string[] }))
const shipment = (id: string) => ({ id, orderId: `o-${id}`, orderNumber: `#${id}`, externalPackageId: `PK-${id}`, status: 'SHIPPED', rawStatus: 'Shipped', cargoTrackingNumber: null, cargoProviderName: null, statusOccurredAt: '2026-10-19T06:00:00Z', version: 1, isResend: false })

vi.mock('../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../shared/api')>()),
  hubApi: vi.fn(async (path: string) => {
    api.paths.push(path)
    const params = new URLSearchParams(path.split('?')[1])
    if (path.startsWith('/shipments?')) return params.get('after') === 'c1'
      ? { items: [shipment('3')], nextCursor: null, hasMore: false }
      : { items: [shipment('1'), shipment('2')], nextCursor: 'c1', hasMore: true }
    return []
  }),
}))

import { ShipmentsPage } from './pages'

beforeEach(() => { api.paths = [] })
afterEach(cleanup)

describe('shipments list', () => {
  it('reads one server page at a time and steps back without reading again', async () => {
    // the app keeps pages fresh for a minute (main.tsx)
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 60_000 } } })
    const { host } = render(<QueryClientProvider client={client}><MemoryRouter><ShipmentsPage /></MemoryRouter></QueryClientProvider>)
    await waitFor(() => host.textContent?.includes('#2'))
    expect(api.paths).toEqual(['/shipments?limit=20'])
    act(() => { byText<HTMLButtonElement>(host, 'button', 'Sonraki').click() })
    await waitFor(() => host.textContent?.includes('#3'))
    expect(api.paths.at(-1)).toBe('/shipments?limit=20&after=c1')
    expect(byText<HTMLButtonElement>(host, 'button', 'Sonraki').disabled).toBe(true)
    act(() => { byText<HTMLButtonElement>(host, 'button', 'Önceki').click() })
    await waitFor(() => host.textContent?.includes('#1'))
    expect(api.paths).toHaveLength(2)
  })
})
