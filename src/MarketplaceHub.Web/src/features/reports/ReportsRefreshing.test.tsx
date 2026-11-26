import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'

// The loading line over the filters runs until the new period's figures are drawn, not only while they are fetched: the
// tabs draw a new answer a moment after it arrives (useDeferredValue), and meanwhile they still show the old period.
// Here the drawing is held back on purpose: the answer has come, the fetch is over, the old figures are still shown.

type Fixture = [string, unknown]
const state = vi.hoisted(() => ({ fixtures: [] as Fixture[], hold: false, held: undefined as unknown }))

vi.mock('react', async importOriginal => {
  const react = await importOriginal<typeof import('react')>()
  // The report (the only deferred value with a summary) stays at the one drawn before the hold.
  const useDeferredValue = <T,>(value: T): T => {
    const deferred = react.useDeferredValue(value)
    const report = typeof value === 'object' && value !== null && 'summary' in value
    if (!report) return deferred
    if (state.hold && state.held !== undefined) return state.held as T
    state.held = value
    return value
  }
  return { ...react, default: { ...react, useDeferredValue }, useDeferredValue }
})

vi.mock('../../shared/api', () => ({
  loadAllPages: vi.fn(async () => { throw new Error('connections not read') }),
  hubApi: vi.fn(async (path: string) => {
    const [pathname] = path.split('?')
    const found = state.fixtures.find(([matcher]) => matcher.replace(/^GET /, '') === pathname)
    if (!found) throw new Error(`No fixture for ${path}`)
    return structuredClone(found[1])
  })
}))

import { ReportsPage } from './ReportsPage'

const fixturePath = '../../../tests/visual/fixtures/reports.mjs'
let container: HTMLDivElement | null = null
let root: Root | null = null
let client: QueryClient | null = null

beforeAll(async () => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-10-20T09:30:00+03:00'))
  state.fixtures = ((await import(/* @vite-ignore */ fixturePath)) as { reports: Fixture[] }).reports
})
afterAll(() => { vi.useRealTimers() })
afterEach(() => { act(() => root?.unmount()); client?.clear(); container?.remove(); state.hold = false; state.held = undefined })

const settle = async () => { for (let step = 0; step < 4; step++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) }) }
const line = (page: HTMLElement) => page.querySelector('.reports-refreshing')!

describe('Raporlar loading line', () => {
  it('stays on after the new period is fetched until its figures are drawn', async () => {
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    root = createRoot(container)
    await act(async () => { root!.render(<QueryClientProvider client={client!}><MemoryRouter initialEntries={['/reports']}><ReportsPage /></MemoryRouter></QueryClientProvider>) })
    await settle()
    const page = container
    expect(line(page).classList.contains('is-active')).toBe(false)
    state.hold = true
    const week = [...page.querySelectorAll<HTMLButtonElement>('.reports-segmented button')].find(button => button.textContent === 'Bu Hafta')!
    await act(async () => { week.click() })
    await settle()
    expect(client.isFetching()).toBe(0)
    expect(line(page).classList.contains('is-active')).toBe(true)
    expect(line(page).textContent).toBe('Güncelleniyor')
  })
})
