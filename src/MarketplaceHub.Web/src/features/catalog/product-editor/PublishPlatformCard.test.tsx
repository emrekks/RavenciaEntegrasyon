import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Each status request waits until the test answers it, so the first frames can be read before the data is in.
const apiState = vi.hoisted(() => ({ pending: new Map<string, (value: unknown) => void>() }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = (path: string) => {
    if (path.includes('/publication-status/')) return new Promise(resolve => apiState.pending.set(path, resolve))
    return Promise.resolve(null)
  }
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond) }
})

import { PublishPlatformCard } from './ProductEditorPages'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
let client: QueryClient

const card = { code: 'trendyol', name: 'Trendyol', initial: 'T', tone: 'trendyol', mode: 'PUBLISH' as const, connection: { id: 'c1', platformCode: 'TRENDYOL', displayName: 'Mağaza', externalStoreId: '930112', status: 'ACTIVE' } }

async function render(props: { familyProductIds?: string[]; settling?: boolean; onDeselect?: () => void } = {}) {
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter>
      <PublishPlatformCard card={card} selected={false} settling={props.settling} productId="p1" familyProductIds={props.familyProductIds} categoryId="" productChecks={[]} selectedAttributes={[]} onSelect={vi.fn()} onDeselect={props.onDeselect ?? vi.fn()} onFeedback={vi.fn()} />
    </MemoryRouter></QueryClientProvider>)
  })
}

async function answer(productId: string, status: Record<string, unknown>) {
  const key = [...apiState.pending.keys()].find(path => path.startsWith(`/products/${productId}/publication-status/`))
  expect(key, `no pending status request for ${productId}`).toBeDefined()
  await act(async () => { apiState.pending.get(key!)!(status); apiState.pending.delete(key!); await new Promise(resolve => setTimeout(resolve, 10)) })
}

const article = () => container.querySelector('article.publish-platform-card')!
const subtitle = () => container.querySelector('.publish-platform-card-head small')?.textContent

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  apiState.pending.clear()
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('PublishPlatformCard first frames', () => {
  it('does not show a status cached from an earlier open before this open has read it again', async () => {
    client.setQueryData(['publication-status', 'p1', 'c1'], { actualStatus: 'LIVE' })
    const onDeselect = vi.fn()
    await render({ onDeselect })
    expect(article().classList.contains('is-settling')).toBe(true)
    expect(container.textContent).not.toContain('Zaten yayında')
    expect(container.querySelector<HTMLButtonElement>('.publish-platform-card-head')!.disabled).toBe(true)
    expect(onDeselect).not.toHaveBeenCalled()
    await answer('p1', { actualStatus: 'UNKNOWN' })
    expect(article().classList.contains('is-settling')).toBe(false)
    expect(subtitle()).toBe('Yayın için seçilmedi')
  })

  it('waits for the colour family before saying the card is live', async () => {
    await render({ familyProductIds: ['p1', 'p2'] })
    await answer('p1', { actualStatus: 'LIVE' })
    // The sibling is still being read: not "Zaten yayında" (it may be only partly live).
    expect(article().classList.contains('is-settling')).toBe(true)
    expect(container.textContent).not.toContain('Zaten yayında')
    await answer('p2', { actualStatus: 'UNKNOWN' })
    expect(article().classList.contains('is-settling')).toBe(false)
    expect(container.textContent).toContain('Kısmen yayında · 1/2 renk')
  })

  it('stays masked while the product itself is loading', async () => {
    await render({ settling: true })
    await answer('p1', { actualStatus: 'LIVE' })
    expect(article().classList.contains('is-settling')).toBe(true)
    expect(container.querySelector('.publish-platform-toggle.on')).toBeNull()
    await render({ settling: false })
    expect(subtitle()).toBe('Zaten yayında · yeniden seçilemez')
  })
})
