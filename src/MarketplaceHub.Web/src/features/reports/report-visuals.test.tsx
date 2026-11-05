import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useReveal } from './report-visuals'

// An IntersectionObserver that remembers what it watches and lets the test bring it into view.
const watched: Array<{ element: Element; callback: IntersectionObserverCallback }> = []
class FakeObserver {
  constructor(private readonly callback: IntersectionObserverCallback) {}
  observe(element: Element) { watched.push({ element, callback: this.callback }) }
  disconnect() {}
}

function Bars({ values }: { values: number[] }) {
  const [ref, shown] = useReveal<HTMLUListElement>()
  if (!values.length) return <p>Gösterilecek değer yok.</p>
  return <ul ref={ref} data-shown={shown}>{values.map(value => <li key={value}>{value}</li>)}</ul>
}

afterEach(() => { vi.unstubAllGlobals(); watched.length = 0 })

describe('useReveal', () => {
  it('watches bars drawn only after an empty state and shows them once in view', async () => {
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    vi.stubGlobal('IntersectionObserver', FakeObserver)
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {} }))
    const container = document.createElement('div')
    const root = createRoot(container)
    await act(async () => { root.render(<Bars values={[]} />) })
    expect(watched).toHaveLength(0)
    await act(async () => { root.render(<Bars values={[3, 5]} />) })
    const list = container.querySelector('ul')!
    expect(watched.map(item => item.element)).toEqual([list])
    expect(list.dataset.shown).toBe('false')
    await act(async () => { watched[0].callback([{ isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver) })
    expect(container.querySelector('ul')!.dataset.shown).toBe('true')
    act(() => root.unmount())
  })
})
