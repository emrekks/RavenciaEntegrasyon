import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'

// Every tab of Raporlar against the preview's realistic report (tests/visual/fixtures): the whole rendered body (text,
// figures, bar widths, labels, order), React's generated ids left out, must stay exactly as written in
// __snapshots__/ReportsFigures.test.tsx.snap. The snapshot was taken before the page's rendering was made lazier, so a
// change of how the page renders that changed a single figure, label or order fails here.

type Fixture = [string, unknown]
const fixtures = vi.hoisted(() => ({ list: [] as Fixture[] }))

vi.mock('../../shared/api', () => ({
  loadAllPages: vi.fn(async () => { throw new Error('connections not read') }),
  hubApi: vi.fn(async (path: string) => {
    const [pathname] = path.split('?')
    const found = fixtures.list.find(([matcher]) => matcher.replace(/^GET /, '') === pathname)
    if (!found) throw new Error(`No fixture for ${path}`)
    return structuredClone(found[1])
  })
}))

import { ReportsPage } from './ReportsPage'

// The same fixture files the browser checks use (tests/visual/fixtures/reports.mjs); imported by a path TypeScript does
// not follow, as they are plain JavaScript outside src.
const fixturePath = '../../../tests/visual/fixtures/reports.mjs'

let container: HTMLDivElement | null = null
let root: Root | null = null
let client: QueryClient | null = null

beforeAll(async () => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-10-20T09:30:00+03:00'))
  fixtures.list = ((await import(/* @vite-ignore */ fixturePath)) as { reports: Fixture[] }).reports
})
afterAll(() => { vi.useRealTimers() })
afterEach(() => { act(() => root?.unmount()); client?.clear(); container?.remove() })

async function open(search: string) {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root!.render(<QueryClientProvider client={client!}><MemoryRouter initialEntries={[`/reports${search}`]}><ReportsPage /></MemoryRouter></QueryClientProvider>) })
  for (let step = 0; step < 4; step++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
  return container
}

// The page as markup, without React's generated ids (they follow the component tree, not the content).
const generatedId = /«|»|_r_|:r[0-9a-z]+:/
export function figuresOf(page: HTMLElement) {
  const copy = page.querySelector('.reports-page')!.cloneNode(true) as HTMLElement
  for (const element of [copy, ...copy.querySelectorAll('*')]) {
    for (const attribute of [...element.attributes]) {
      if (['id', 'for', 'aria-describedby', 'aria-labelledby', 'aria-controls'].includes(attribute.name) || generatedId.test(attribute.value)) element.removeAttribute(attribute.name)
    }
  }
  return copy.innerHTML.replace(/></g, '>\n<')
}

export const reportViews = ['', '?view=profit', '?view=payouts', '?view=orders', '?view=products', '?view=products&sub=brands', '?view=stock',
  '?view=ops&sub=returns', '?view=ops&sub=cancellations', '?view=ops&sub=cargo', '?view=ops&sub=invoices', '?view=customers', '?view=geo']

describe('Raporlar figures', () => {
  it.each(reportViews)('renders every figure of /reports%s as before', async search => {
    const page = await open(search)
    expect(page.querySelector('.reports-body')).not.toBeNull()
    expect(figuresOf(page)).toMatchSnapshot()
  })
})
