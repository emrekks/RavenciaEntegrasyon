import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Each status request waits until the test answers it, so the first frames can be read before the data is in.
// A job's detail is answered from `jobs` at once; every path asked is kept in `calls`.
const apiState = vi.hoisted(() => ({ pending: new Map<string, (value: unknown) => void>(), jobs: new Map<string, unknown>(), calls: [] as string[] }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = (path: string) => {
    apiState.calls.push(path)
    if (path.startsWith('/jobs/')) return Promise.resolve(apiState.jobs.get(path) ?? null)
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

async function render(props: { onSelect?: () => void; variantLinks?: { linked: number; total: number; missing?: number }; familyProductIds?: string[]; settling?: boolean; selected?: boolean; onDeselect?: () => void; onBlockedChange?: (gaps: number) => void; productChecks?: Array<{ title: string; detail: string; ok: boolean }>; categoryId?: string } = {}) {
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter>
      <PublishPlatformCard card={card} selected={props.selected ?? false} settling={props.settling} productId="p1" familyProductIds={props.familyProductIds} variantLinks={props.variantLinks} categoryId={props.categoryId ?? ''} productChecks={props.productChecks ?? []} selectedAttributes={[]} onSelect={props.onSelect ?? vi.fn()} onDeselect={props.onDeselect ?? vi.fn()} onBlockedChange={props.onBlockedChange} onFeedback={vi.fn()} />
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
const fact = () => container.querySelector('.publish-platform-fact-publication')!
const pill = () => fact().querySelector<HTMLButtonElement>('button.publish-steps-trigger')!
const steps = () => [...pill().querySelectorAll('.publish-step')].map(step => step.className.replace('publish-step is-', ''))
// Each corner badge as "<tone> <count> @<step index>".
const counts = () => [...pill().querySelectorAll('.publish-step')].flatMap((step, index) => [...step.querySelectorAll('.publish-step-badge')].map(badge => `${badge.className.match(/is-(red|yellow|green)/)![1]} ${badge.textContent} @${index}`))
const popover = () => fact().querySelector<HTMLElement>('.publish-issue-tooltip')!
const ok = [{ title: 'Temel Ürün Verileri', detail: 'Eksiksiz.', ok: true }]
const gap = [{ title: 'Temel Ürün Verileri', detail: 'Eksik alanlar: açıklama.', ok: false }]

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  apiState.pending.clear()
  apiState.jobs.clear()
  apiState.calls.length = 0
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
    expect(steps()).toEqual(['fail', 'idle', 'idle', 'idle'])
    expect(pill().getAttribute('aria-label')).toBe('Trendyol: Zorunlu Eksik; 1 zorunlu eksik')
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

describe('PublishPlatformCard status steps', () => {
  it('reads a never sent, ready card as Hazırlık done and Gönderim next, with no count', async () => {
    await render({ productChecks: ok })
    await answer('p1', { actualStatus: 'UNKNOWN' })
    expect(steps()).toEqual(['done', 'next', 'idle', 'idle'])
    expect(counts()).toEqual([])
    expect(pill().querySelector('.publish-steps-label')?.textContent).toBe('Yayına Hazır')
    expect(pill().getAttribute('aria-label')).toBe('Trendyol: Yayına Hazır')
    expect([...pill().querySelectorAll('.publish-step')].map(step => step.getAttribute('title'))).toEqual(['Hazırlık · Tamam', 'Gönderim · Sırada', 'Onay · Bekliyor', 'Yayında · Bekliyor'])
    // The pill is the cell's one control: no refresh icon beside it, no job line under it.
    expect(fact().querySelectorAll('button')).toHaveLength(1 + popover().querySelectorAll('button').length)
    expect(fact().querySelector('small')).toBeNull()
    expect(popover().querySelector('.publish-issue-empty')?.textContent).toBe('Eksik kalmadı.')
  })

  it('reads a fully live card as four done steps', async () => {
    await render({ familyProductIds: ['p1', 'p2'] })
    await answer('p1', { actualStatus: 'LIVE', profileId: 'pr1' })
    await answer('p2', { actualStatus: 'LIVE' })
    expect(subtitle()).toBe('Zaten yayında · yeniden seçilemez')
    expect(steps()).toEqual(['done', 'done', 'done', 'done'])
    expect(counts()).toEqual([])
    expect(popover().querySelector('.publish-issue-empty')).toBeNull()
  })

  it('shows the live share of a partly live card whose rest is ready, and says so in the popover', async () => {
    await render({ familyProductIds: ['p1', 'p2'], productChecks: ok })
    await answer('p1', { actualStatus: 'LIVE' })
    await answer('p2', { actualStatus: 'UNKNOWN' })
    expect(steps()).toEqual(['done', 'next', 'idle', 'half'])
    expect(counts()).toEqual(['green 1 @3'])
    expect(pill().querySelector<HTMLElement>('.publish-steps')?.style.getPropertyValue('--publish-step-share')).toBe('50')
    expect(pill().getAttribute('aria-label')).toBe('Trendyol: Kısmen Yayında, 1/2 renk yayında')
    expect(popover().querySelector('.publish-issue-lead')?.textContent).toBe('1/2 renk yayındaKalan 1 renk yayına hazır')
  })

  it('shows the gap count of a partly live card whose rest is blocked, and a press pins the gap chips', async () => {
    await render({ familyProductIds: ['p1', 'p2'], productChecks: gap })
    await answer('p1', { actualStatus: 'LIVE' })
    await answer('p2', { actualStatus: 'UNKNOWN' })
    expect(steps()).toEqual(['fail', 'idle', 'idle', 'half'])
    expect(counts()).toEqual(['red 1 @0', 'green 1 @3'])
    expect(pill().getAttribute('aria-label')).toBe('Trendyol: Kısmen Yayında, 1/2 renk yayında; 1 zorunlu eksik')
    await act(async () => { pill().click() })
    expect(pill().getAttribute('aria-expanded')).toBe('true')
    expect(popover().classList.contains('is-open')).toBe(true)
    expect(popover().querySelector('.publish-issue-lead')?.textContent).toContain('Kalan 1 renkte 1 eksik')
    expect(popover().querySelector('.publish-issue-required .publish-issue-chip')?.textContent).toBe('Temel Ürün Verileri')
  })

  it('reads a card with nothing live and a required gap as Hazırlık failed', async () => {
    await render({ familyProductIds: ['p1', 'p2'], productChecks: gap })
    await answer('p1', { actualStatus: 'UNKNOWN' })
    await answer('p2', { actualStatus: 'UNKNOWN' })
    expect(steps()).toEqual(['fail', 'idle', 'idle', 'idle'])
    expect(counts()).toEqual(['red 1 @0'])
    // The count is on the icon, not a chip beside the steps.
    expect(pill().querySelector('.publish-steps-counts')).toBeNull()
  })

  it('puts the refresh icon in the header beside the toggle and İş Ayrıntısı in the popover of a card awaiting approval', async () => {
    const onSelect = vi.fn()
    await render({ productChecks: ok, onSelect })
    await answer('p1', { actualStatus: 'APPROVAL_PENDING', profileId: 'pr1', lastJobId: 'j1', lastJobStatus: 'SUCCEEDED' })
    expect(steps()).toEqual(['done', 'done', 'now', 'idle'])
    expect([...popover().querySelectorAll('.publish-issue-actions > button')].map(action => action.textContent)).toEqual(['İş Ayrıntısı'])
    const head = container.querySelector<HTMLButtonElement>('button.publish-platform-card-head')!
    const refresh = container.querySelector<HTMLButtonElement>('.publish-platform-card-top > button.publish-platform-card-refresh')!
    // A sibling of the selection button, never inside it.
    expect(head.contains(refresh)).toBe(false)
    expect(head.querySelector('button')).toBeNull()
    expect(refresh.getAttribute('aria-label')).toBe('Trendyol yayın durumunu yenile')
    expect(refresh.title).toBe('Platformdaki mevcut durumu yeniden sorgula')
    await act(async () => { refresh.click(); await new Promise(resolve => setTimeout(resolve, 10)) })
    expect(apiState.calls).toContain('/products/p1/publication-status/c1/refresh')
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('marks the step a failed job stopped at and moves its reason into the popover', async () => {
    apiState.jobs.set('/jobs/j1', { job: { id: 'j1', jobType: 'PRODUCT_CREATE', status: 'DEAD', progressCurrent: 0, progressTotal: null, progressPercent: null, progressLabel: null, lastErrorCode: 'X', lastErrorSummary: 'Web Color değeri eşlenmemiş.' } })
    await render({ productChecks: ok })
    await answer('p1', { actualStatus: 'UNKNOWN', lastJobId: 'j1', lastJobStatus: 'DEAD' })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
    expect(steps()).toEqual(['done', 'fail', 'idle', 'idle'])
    expect(popover().querySelector('.publish-issue-lead span.is-danger')?.textContent).toContain('Web Color değeri eşlenmemiş.')
    expect(fact().querySelector('small')).toBeNull()
  })

  it('reads a refused card as Onay failed', async () => {
    await render({ productChecks: ok })
    await answer('p1', { actualStatus: 'CREATE_REJECTED', profileId: 'pr1' })
    expect(steps()).toEqual(['done', 'done', 'fail', 'idle'])
  })

  it('reads a card the marketplace no longer knows as Yayında failed, with the count of the vanished variants', async () => {
    await render({ productChecks: ok, variantLinks: { linked: 0, total: 2, missing: 2 } })
    await answer('p1', { actualStatus: 'LIVE', profileId: 'pr1' })
    expect(steps()).toEqual(['done', 'done', 'done', 'fail'])
    expect(counts()).toEqual(['red 2 @3'])
  })
})

