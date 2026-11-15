import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, flush, render } from '../test/render'

const reads = vi.hoisted(() => ({ count: 0 }))
vi.mock('./api', () => ({ hubPoll: vi.fn(async () => { reads.count++; return { new: 1, processing: 2, pending: 3 } }) }))

import { pendingOrdersQuery } from './order-navigation-count'

function Reader() {
  const summary = useQuery(pendingOrdersQuery)
  return <span>{summary.data?.pending ?? '-'}</span>
}

beforeEach(() => { reads.count = 0 })
afterEach(() => { cleanup(); vi.useRealTimers() })

// W2 (scan 2026-11-15): the menu badge and the overview card share the query; a screen that mounts it right after the
// other read it (the lazily loaded overview after the shell) asked for the same answer again.
describe('the one Bekleyen query on mount', () => {
  it('is read once when a second screen mounts it right after the first read it', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><Reader /></QueryClientProvider>)
    await flush()
    render(<QueryClientProvider client={client}><Reader /></QueryClientProvider>)
    await flush()
    expect(reads.count).toBe(1)
  })

  it('is read again when a screen mounts it after the answer has aged', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><Reader /></QueryClientProvider>)
    await flush()
    vi.setSystemTime(Date.now() + 10_000)
    render(<QueryClientProvider client={client}><Reader /></QueryClientProvider>)
    await flush()
    expect(reads.count).toBe(2)
  })
})
