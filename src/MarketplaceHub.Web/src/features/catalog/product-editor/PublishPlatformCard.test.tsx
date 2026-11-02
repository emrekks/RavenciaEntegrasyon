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
    if (path.includes('/publication-status/') || path.startsWith('/mappings/categories/')) return new Promise(resolve => apiState.pending.set(path, resolve))
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

async function render(props: { familyProductIds?: string[]; settling?: boolean; selected?: boolean; onDeselect?: () => void; onBlockedChange?: (gaps: number) => void; productChecks?: Array<{ title: string; detail: string; ok: boolean }>; categoryId?: string } = {}) {
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter>
      <PublishPlatformCard card={card} selected={props.selected ?? false} settling={props.settling} productId="p1" familyProductIds={props.familyProductIds} categoryId={props.categoryId ?? ''} productChecks={props.productChecks ?? []} selectedAttributes={[]} onSelect={vi.fn()} onDeselect={props.onDeselect ?? vi.fn()} onBlockedChange={props.onBlockedChange} onFeedback={vi.fn()} />
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
    expect(container.textContent).toContain('1/2 renk yayında')
  })

  it('stays masked until the mapping check is in, then shows its state once', async () => {
    await render({ categoryId: 'c1' })
    await answer('p1', { actualStatus: 'UNKNOWN' })
    // The status is in but the mapping check is not: no provisional state ("Yayın için seçilmedi") to change later.
    expect(article().classList.contains('is-settling')).toBe(true)
    expect(subtitle()).toBe('Yayın durumu yükleniyor')
    const mappingKey = [...apiState.pending.keys()].find(path => path.startsWith('/mappings/categories/c1'))!
    await act(async () => { apiState.pending.get(mappingKey)!(null); apiState.pending.delete(mappingKey); await new Promise(resolve => setTimeout(resolve, 10)) })
    expect(article().classList.contains('is-settling')).toBe(false)
    expect(subtitle()).toBe('Zorunlu eksikler giderilmeden seçilemez')
    expect(container.querySelector('.publish-issue-trigger')?.getAttribute('title')).toBe('Yayın için zorunlu eksik')
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

describe('PublishPlatformCard required gaps', () => {
  it('turns a picked card off once its gap is known and tells the page how many gaps it has', async () => {
    const onDeselect = vi.fn()
    const onBlockedChange = vi.fn()
    await render({ selected: true, onDeselect, onBlockedChange, productChecks: [{ title: 'Temel Ürün Verileri', detail: 'Eksik alanlar: açıklama.', ok: false }] })
    // Still reading the status: nothing is said yet.
    expect(onDeselect).not.toHaveBeenCalled()
    expect(onBlockedChange).not.toHaveBeenCalledWith(1)
    await answer('p1', { actualStatus: 'UNKNOWN' })
    expect(onDeselect).toHaveBeenCalled()
    expect(onBlockedChange).toHaveBeenLastCalledWith(1)
  })

  it('reports no gap for a card that has none', async () => {
    const onBlockedChange = vi.fn()
    await render({ onBlockedChange, productChecks: [{ title: 'Temel Ürün Verileri', detail: 'Eksiksiz.', ok: true }] })
    await answer('p1', { actualStatus: 'UNKNOWN' })
    expect(onBlockedChange).toHaveBeenLastCalledWith(0)
  })
})

describe('PublishPlatformCard partly live', () => {
  const fact = () => container.querySelector('.publish-platform-fact-publication')!
  const status = () => fact().querySelector('dd')!
  const pill = () => status().querySelector<HTMLElement>('.publish-platform-status')!

  it('shows one pill with the live share and a line saying the rest is ready', async () => {
    await render({ familyProductIds: ['p1', 'p2'], productChecks: [{ title: 'Temel Ürün Verileri', detail: 'Eksiksiz.', ok: true }] })
    await answer('p1', { actualStatus: 'LIVE' })
    await answer('p2', { actualStatus: 'UNKNOWN' })
    expect(pill().textContent).toBe('1/2 renk yayında')
    expect(pill().querySelector<HTMLElement>('.publish-partial-progress > b')?.style.width).toBe('50%')
    // One pill only: no second status and no separate "hazır" chip in the status row.
    expect(status().querySelectorAll('.publish-platform-status')).toHaveLength(1)
    expect(status().querySelector('.publish-issue-trigger')).toBeNull()
    const note = fact().querySelector('.publish-partial-note.is-ready')!
    expect(note.textContent).toBe('Kalan 1 renk yayına hazır')
    expect(note.querySelector('svg')).not.toBeNull()
  })

  it('says how many gaps the rest has in a line that opens the same gap chips', async () => {
    await render({ familyProductIds: ['p1', 'p2'], productChecks: [{ title: 'Temel Ürün Verileri', detail: 'Eksik alanlar: açıklama.', ok: false }] })
    await answer('p1', { actualStatus: 'LIVE' })
    await answer('p2', { actualStatus: 'UNKNOWN' })
    expect(pill().textContent).toBe('1/2 renk yayında')
    expect(status().querySelectorAll('.publish-platform-status')).toHaveLength(1)
    expect(fact().querySelector('.publish-partial-note.is-ready')).toBeNull()
    const line = fact().querySelector<HTMLButtonElement>('.publish-partial-note.is-blocked .publish-issue-note-trigger')!
    expect(line.textContent).toBe('Kalan 1 renkte 1 eksik')
    await act(async () => { line.click() })
    expect(fact().querySelector('.publish-partial-note .publish-issue-tooltip.is-open .publish-issue-required .publish-issue-chip')?.textContent).toBe('Temel Ürün Verileri')
  })

  it('leaves a card with nothing live as it was', async () => {
    await render({ familyProductIds: ['p1', 'p2'], productChecks: [{ title: 'Temel Ürün Verileri', detail: 'Eksik alanlar: açıklama.', ok: false }] })
    await answer('p1', { actualStatus: 'UNKNOWN' })
    await answer('p2', { actualStatus: 'UNKNOWN' })
    expect(status().querySelector('.publish-issue-trigger')?.getAttribute('title')).toBe('Yayın için zorunlu eksik')
    expect(fact().querySelector('.publish-partial-note, .publish-partial-progress')).toBeNull()
  })

  it('leaves a fully live card as it was', async () => {
    await render({ familyProductIds: ['p1', 'p2'] })
    await answer('p1', { actualStatus: 'LIVE' })
    await answer('p2', { actualStatus: 'LIVE' })
    expect(subtitle()).toBe('Zaten yayında · yeniden seçilemez')
    expect(fact().querySelector('.publish-partial-note, .publish-partial-progress')).toBeNull()
  })
})
