import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ paths: [] as string[], total: (() => 120) as (query: URLSearchParams) => number }))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string) => {
    apiState.paths.push(path)
    const query = new URLSearchParams(path.split('?')[1])
    return { items: [], totalCount: apiState.total(query), page: Number(query.get('page')), pageSize: 50, amount: 0, profit: null, missingCost: 0, profitAmount: 0, profitOrders: 0, margin: null, missingCargo: 0 }
  }) }
})

import { OrderProfitability } from './OrderProfitability'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

async function render(from: string, platforms: string[] | null = null) {
  await act(async () => { root.render(<QueryClientProvider client={client}><OrderProfitability from={from} to="2026-10-31" platforms={platforms} /></QueryClientProvider>) })
  await settle()
}

const lastPage = () => new URLSearchParams(apiState.paths.at(-1)!.split('?')[1]).get('page')
const next = () => Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Sonraki')!

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.paths = []
  apiState.total = () => 120
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  client.clear()
  container.remove()
})

describe('OrderProfitability paging', () => {
  it('opens another period or platform choice on its first page', async () => {
    await render('2026-10-01')
    await act(async () => { next().click() })
    await settle()
    await act(async () => { next().click() })
    await settle()
    expect(lastPage()).toBe('2')

    await render('2026-09-01')
    expect(lastPage()).toBe('0')
    expect(apiState.paths.some(path => path.includes('from=2026-09-01') && path.includes('page=2'))).toBe(false)

    await act(async () => { next().click() })
    await settle()
    await render('2026-09-01', ['TRENDYOL'])
    expect(lastPage()).toBe('0')
  })

  it('moves a page past the end to the last page there is', async () => {
    await render('2026-10-01')
    await act(async () => { next().click() })
    await settle()
    await act(async () => { next().click() })
    await settle()
    // The list shrank to two pages while the third was asked for.
    apiState.total = () => 60
    await act(async () => { await client.invalidateQueries() })
    await settle()
    expect(lastPage()).toBe('1')
    expect(container.textContent).toContain('Sayfa 2 / 2')
  })
})
