import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { useProductLines } from './product-lines'

let container: HTMLDivElement
let root: Root
let originalMatchMedia: typeof window.matchMedia

function Harness({ total }: { total: number }) {
  const view = useProductLines(total)
  return <div>
    <div className={`rr-plines${view.scrolls ? ' rr-plines-scroll' : ''}`} ref={view.linesRef} data-more={view.moreHint}>{Array.from({ length: view.limit }, (_, index) => <p key={index} className="line" />)}</div>
    {view.phone && view.hiddenOnPhone > 0 && <button type="button" id="more" onClick={view.toggleExpanded}>{view.expanded ? 'Daha Az Göster' : `${view.hiddenOnPhone} Ürün Daha`}</button>}
  </div>
}

async function show(total: number, phone: boolean) {
  window.matchMedia = ((query: string) => ({ matches: phone && query === '(max-width: 760px)', media: query, addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined, removeListener: () => undefined, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia
  root = createRoot(container)
  await act(async () => { root.render(<Harness total={total} />) })
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  originalMatchMedia = window.matchMedia
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  window.matchMedia = originalMatchMedia
  container.remove()
})

describe('useProductLines', () => {
  it('lets a desktop row with up to four products grow with them', async () => {
    await show(4, false)
    expect(container.querySelector('.rr-plines-scroll')).toBeNull()
    expect(container.querySelectorAll('.line')).toHaveLength(4)
  })

  it('scrolls a desktop row from the fifth product on, with every product still drawn and a +N hint', async () => {
    await show(7, false)
    const list = container.querySelector('.rr-plines-scroll')!
    expect(list).not.toBeNull()
    expect(container.querySelectorAll('.line')).toHaveLength(7)
    expect(list.getAttribute('data-more')).toBe('+3 ürün')
  })

  it('shows three products on a phone card and opens the rest with a button, without a scroll box', async () => {
    await show(6, true)
    expect(container.querySelector('.rr-plines-scroll')).toBeNull()
    expect(container.querySelectorAll('.line')).toHaveLength(3)
    const button = container.querySelector<HTMLButtonElement>('#more')!
    expect(button.textContent).toBe('3 Ürün Daha')
    await act(async () => { button.click() })
    expect(container.querySelectorAll('.line')).toHaveLength(6)
    expect(container.querySelector<HTMLButtonElement>('#more')!.textContent).toBe('Daha Az Göster')
  })

  it('needs no button when a phone card has three products or fewer', async () => {
    await show(3, true)
    expect(container.querySelector('#more')).toBeNull()
    expect(container.querySelectorAll('.line')).toHaveLength(3)
  })
})
