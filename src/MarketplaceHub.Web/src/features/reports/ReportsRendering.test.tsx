import { act, createElement, memo, type ComponentProps } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'

// How much of Raporlar is drawn, and when (performance audit 26 Nov 2026): a block below the first screen waits until it
// is near the screen or the browser is idle, only the active tab is mounted, and a period change redraws a tab once. No
// clock is measured: these count what is in the page and how often a tab renders.

type Fixture = [string, unknown]
const state = vi.hoisted(() => ({ fixtures: [] as Fixture[], requests: [] as string[], overviewRenders: 0 }))

vi.mock('../../shared/api', () => ({
  loadAllPages: vi.fn(async () => { throw new Error('connections not read') }),
  hubApi: vi.fn(async (path: string) => {
    state.requests.push(path)
    const [pathname] = path.split('?')
    const found = state.fixtures.find(([matcher]) => matcher.replace(/^GET /, '') === pathname)
    if (!found) throw new Error(`No fixture for ${path}`)
    return structuredClone(found[1])
  })
}))

// Genel Bakış wrapped in a counter: how often the page hands it new props (it is memoised like the original).
vi.mock('./reports-page/OverviewTab', async importOriginal => {
  const original = await importOriginal<typeof import('./reports-page/OverviewTab')>()
  return { ...original, Overview: memo((props: ComponentProps<typeof original.Overview>) => { state.overviewRenders++; return createElement(original.Overview, props) }) }
})

import { ReportsPage } from './ReportsPage'

const fixturePath = '../../../tests/visual/fixtures/reports.mjs'

// The browser stand-ins: an IntersectionObserver that never reports (a block mounts only by the layout check or when
// idle), idle callbacks run by the test, and the placeholders placed below the screen when `belowFold` is set.
const idle: Array<() => void> = []
let belowFold = true
let visibility: DocumentVisibilityState = 'visible'
const realRect = HTMLElement.prototype.getBoundingClientRect
class StillObserver { observe() {} unobserve() {} disconnect() {} takeRecords() { return [] } }

let container: HTMLDivElement | null = null
let root: Root | null = null
let client: QueryClient | null = null

beforeAll(async () => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-10-20T09:30:00+03:00'))
  state.fixtures = ((await import(/* @vite-ignore */ fixturePath)) as { reports: Fixture[] }).reports
})
afterAll(() => { vi.useRealTimers() })
beforeEach(() => {
  vi.stubGlobal('IntersectionObserver', StillObserver)
  vi.stubGlobal('requestIdleCallback', (run: () => void) => { idle.push(run); return idle.length })
  HTMLElement.prototype.getBoundingClientRect = function rect(this: HTMLElement) {
    return belowFold && this.hasAttribute('data-deferred') ? new DOMRect(0, 5000, 300, 300) : realRect.call(this)
  }
  // A shown page (jsdom's own document reads as not shown, where every block would mount at once).
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility })
  visibility = 'visible'
  belowFold = true
  state.requests.length = 0
  state.overviewRenders = 0
})
afterEach(() => {
  act(() => root?.unmount()); client?.clear(); container?.remove()
  // Whatever is still scheduled runs against nothing, so the next test starts with an empty queue.
  while (idle.length) idle.shift()!()
  HTMLElement.prototype.getBoundingClientRect = realRect
  Reflect.deleteProperty(document, 'visibilityState')
  vi.unstubAllGlobals()
})

const settle = async () => { for (let step = 0; step < 4; step++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }

async function open(search = '') {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root!.render(<QueryClientProvider client={client!}><MemoryRouter initialEntries={[`/reports${search}`]}><ReportsPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
  return container
}

// Runs the idle callbacks one at a time, as the browser would, until every waiting block is drawn.
async function drainIdle() {
  let rounds = 0
  while (idle.length && rounds++ < 100) await act(async () => { idle.shift()!() })
  await settle()
}

// The tab's markup, one element a line, without React's generated ids and with each element's attributes in name order
// (their order only says when an attribute was first set, e.g. a tone that arrives with a later answer).
function markup(page: HTMLElement) {
  const copy = page.querySelector('.reports-body')!.cloneNode(true) as HTMLElement
  for (const element of [copy, ...copy.querySelectorAll('*')]) {
    const attributes = [...element.attributes].filter(item => !['id', 'for', 'aria-describedby', 'aria-labelledby', 'aria-controls'].includes(item.name) && !/«|»|_r_|:r[0-9a-z]+:/.test(item.value))
      .map(item => [item.name, item.value]).sort(([left], [right]) => left.localeCompare(right))
    for (const item of [...element.attributes]) element.removeAttribute(item.name)
    for (const [name, value] of attributes) element.setAttribute(name, value)
  }
  return copy.innerHTML.replace(/></g, '>\n<')
}

describe('Raporlar rendering', () => {
  it('draws only the first screen of Genel Bakış at first, the rest when idle, one block at a time', async () => {
    const page = await open()
    expect(page.querySelectorAll('.reports-kpis > div')).toHaveLength(6)
    expect(page.querySelectorAll('.reports-hero-card')).toHaveLength(0)
    expect(page.querySelector('.reports-card-bridge')).toBeNull()
    const waiting = page.querySelectorAll('[data-deferred]').length
    expect(waiting).toBe(12)
    // One idle period draws one block.
    await act(async () => { idle.shift()!() })
    expect(page.querySelectorAll('[data-deferred]')).toHaveLength(waiting - 1)
    await drainIdle()
    expect(page.querySelectorAll('[data-deferred]')).toHaveLength(0)
    expect(page.querySelectorAll('.reports-hero-card')).toHaveLength(9)
  })

  it.each(['', '?view=profit', '?view=products', '?view=stock', '?view=payouts', '?view=customers', '?view=geo'])('ends with the same markup on /reports%s whether its blocks wait or not', async search => {
    belowFold = false
    const eager = markup(await open(search))
    act(() => root?.unmount()); container?.remove(); client?.clear()
    expect(eager).not.toContain('data-deferred')
    belowFold = true
    const page = await open(search)
    expect(page.querySelectorAll('[data-deferred]').length).toBeGreaterThan(0)
    await drainIdle()
    expect(markup(page)).toBe(eager)
  })

  it('draws every waiting block before printing', async () => {
    const page = await open()
    expect(page.querySelectorAll('[data-deferred]').length).toBeGreaterThan(0)
    act(() => { window.dispatchEvent(new Event('beforeprint')) })
    expect(page.querySelectorAll('[data-deferred]')).toHaveLength(0)
    expect(page.querySelectorAll('.reports-hero-card')).toHaveLength(9)
  })

  it('draws every waiting block at once while the page is hidden', async () => {
    const page = await open()
    expect(page.querySelectorAll('[data-deferred]').length).toBeGreaterThan(0)
    visibility = 'hidden'
    await act(async () => { document.dispatchEvent(new Event('visibilitychange')) })
    await settle()
    expect(page.querySelectorAll('[data-deferred]')).toHaveLength(0)
  })

  it('gives a waiting box the grid class of its block and, back on the tab, the height the block had', async () => {
    const page = await open('?view=stock')
    expect([...page.querySelectorAll('.reports-grid > [data-deferred]')].map(box => box.className)).toContain('is-wide')
    // jsdom lays nothing out, so the measured height is 0: what matters is that the box takes it instead of its estimate.
    await drainIdle()
    act(() => root?.unmount()); container?.remove(); client?.clear()
    const again = await open('?view=stock')
    expect([...again.querySelectorAll<HTMLElement>('[data-deferred]')].every(box => box.style.minHeight === '0px')).toBe(true)
  })

  it('mounts only the active tab: the other tabs\' content and data are not there', async () => {
    const page = await open()
    await drainIdle()
    expect(page.querySelector('.reports-overview')).not.toBeNull()
    for (const other of ['.reports-payouts', '.reports-order-profit', '.reports-map-panel', '.reports-lists']) expect(page.querySelector(other)).toBeNull()
    expect(state.requests.filter(path => /^\/reports\/(stock|payouts|order-profitability)/.test(path))).toEqual([])
  })

  it('redraws Genel Bakış once for a new period, not for the loading line or the click itself', async () => {
    const page = await open()
    await drainIdle()
    state.overviewRenders = 0
    const week = [...page.querySelectorAll<HTMLButtonElement>('.reports-segmented button')].find(button => button.textContent === 'Bu Hafta')!
    await act(async () => { week.click() })
    await settle()
    expect(state.requests.filter(path => path.startsWith('/reports/sales?'))).toHaveLength(2)
    expect(state.overviewRenders).toBe(1)
  })
})
