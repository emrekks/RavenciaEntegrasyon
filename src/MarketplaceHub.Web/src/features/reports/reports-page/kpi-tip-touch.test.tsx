import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { KpiStrip } from './report-parts'

class FakeObserver { observe() {} disconnect() {} }

const items = [
  { key: 'a', label: 'Net Ciro', value: 100, tip: 'Net ciro açıklaması' },
  { key: 'b', label: 'Sipariş', value: 5, tip: 'Sipariş açıklaması' }
]

async function render() {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  vi.stubGlobal('IntersectionObserver', FakeObserver)
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(hover: none)', media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }))
  const host = document.createElement('div')
  document.body.append(host)
  const root = createRoot(host)
  await act(async () => { root.render(<KpiStrip label="Özet" items={items} />) })
  return { host, unmount: () => act(async () => root.unmount()) }
}

afterEach(() => { vi.unstubAllGlobals(); document.body.innerHTML = '' })

describe('KpiStrip tips on a touch screen', () => {
  it('opens only from the (i) mark, closes on the mark again, outside, and keeps one open', async () => {
    const { host, unmount } = await render()
    const cards = () => [...host.querySelectorAll('.reports-kpis > div')]
    const tipOpen = () => host.querySelectorAll('.reports-hover-tip.is-floating').length
    const click = (element: Element) => act(async () => { element.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
    // The card body, a figure and the label text never open it.
    await click(cards()[0]!)
    await click(cards()[0]!.querySelector('strong')!)
    await click(cards()[0]!.querySelector('.reports-kpi-label')!)
    expect(tipOpen()).toBe(0)
    // The mark opens it; the mark again closes it.
    await click(cards()[0]!.querySelector('.reports-qmark')!)
    expect(tipOpen()).toBe(1)
    await click(cards()[0]!.querySelector('.reports-qmark')!)
    expect(tipOpen()).toBe(0)
    // Another mark replaces the open tip (one at a time); a tap elsewhere closes it.
    await click(cards()[0]!.querySelector('.reports-qmark')!)
    await act(async () => { cards()[1]!.querySelector('.reports-qmark')!.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })) })
    await click(cards()[1]!.querySelector('.reports-qmark')!)
    expect(tipOpen()).toBe(1)
    expect(cards()[1]!.classList.contains('is-open')).toBe(true)
    await act(async () => { document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })) })
    expect(tipOpen()).toBe(0)
    await unmount()
  })
})
