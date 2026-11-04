import { useRef } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../test/render'
import { TabSlidePanel, tabSlideDirection, useTabSlide, useTabSlideInPlace, type TabSlide } from './tab-slide'

const order = ['a', 'b', 'c'] as const

function Probe({ active, seen }: { active: string; seen: TabSlide[] }) {
  seen.push(useTabSlide(active, order))
  return null
}

function track(initial: string) {
  const seen: TabSlide[] = []
  const view = render(<Probe active={initial} seen={seen} />)
  return { last: () => seen[seen.length - 1], show: (active: string) => view.rerender(<Probe active={active} seen={seen} />) }
}

const motion = (reduce: boolean) => vi.stubGlobal('matchMedia', (query: string) => ({ matches: reduce && query.includes('reduce'), media: query, addEventListener: () => undefined, removeEventListener: () => undefined }))

// The test setup asks for reduced motion; these tests want the slide unless they say otherwise.
beforeEach(() => { motion(false) })
afterEach(() => { cleanup(); vi.unstubAllGlobals(); document.body.innerHTML = '' })

describe('useTabSlide', () => {
  it('does not animate on the first render', () => {
    const probe = track('b')
    expect(probe.last().className).toBe('')
    expect(probe.last().sliding).toBe(false)
  })

  it('slides in from the right for a tab to the right and from the left for one to the left', () => {
    const probe = track('b')
    const first = probe.last().key
    probe.show('c')
    expect(probe.last().className).toBe('rv-tab-in-next')
    const second = probe.last().key
    expect(second).not.toBe(first)
    probe.show('a')
    expect(probe.last().className).toBe('rv-tab-in-prev')
    expect(probe.last().key).not.toBe(second)
  })

  it('keeps the class and key when the same tab renders again, as on a data refetch', () => {
    const probe = track('a')
    probe.show('a')
    expect(probe.last().className).toBe('')
    probe.show('b')
    const { key } = probe.last()
    probe.show('b')
    expect(probe.last().key).toBe(key)
    expect(probe.last().className).toBe('rv-tab-in-next')
  })

  it('switches at once under reduced motion', () => {
    motion(true)
    const probe = track('a')
    const { key } = probe.last()
    probe.show('c')
    expect(probe.last().className).toBe('')
    expect(probe.last().key).toBe(key)
  })

  it('starts the page from its top on a tab change, not on the first render or a refetch', () => {
    const shell = document.createElement('div')
    shell.className = 'app-shell'
    shell.innerHTML = '<main><section class="content"></section></main>'
    document.body.append(shell)
    const scroller = shell.querySelector<HTMLElement>('.content')!
    scroller.scrollTop = 300
    const probe = track('a')
    expect(scroller.scrollTop).toBe(300)
    probe.show('a')
    expect(scroller.scrollTop).toBe(300)
    probe.show('b')
    expect(scroller.scrollTop).toBe(0)
    scroller.scrollTop = 200
    motion(true)
    probe.show('a')
    expect(scroller.scrollTop).toBe(0)
  })

  it('reads the direction from the tab order', () => {
    expect(tabSlideDirection('a', 'a', order)).toBe('')
    expect(tabSlideDirection('a', 'c', order)).toBe('rv-tab-in-next')
    expect(tabSlideDirection('c', 'b', order)).toBe('rv-tab-in-prev')
  })
})

describe('useTabSlideInPlace', () => {
  function Pane({ active }: { active: string }) {
    const ref = useRef<HTMLDivElement>(null)
    useTabSlideInPlace(ref, active, order)
    return <div ref={ref}>{active}</div>
  }

  it('plays the slide on the same element, only on a tab change', () => {
    const animate = vi.fn(() => ({ cancel: () => undefined }))
    const original = HTMLElement.prototype.animate
    HTMLElement.prototype.animate = animate as unknown as typeof HTMLElement.prototype.animate
    try {
      const view = render(<Pane active="b" />)
      const element = view.host.firstElementChild
      view.rerender(<Pane active="b" />)
      expect(animate).not.toHaveBeenCalled()
      view.rerender(<Pane active="a" />)
      expect(view.host.firstElementChild).toBe(element)
      expect(animate).toHaveBeenCalledTimes(1)
      expect(animate.mock.calls[0]).toEqual([[{ opacity: 0, translate: 'calc(28px * -1) 0' }, { opacity: 1, translate: '0 0' }], expect.objectContaining({ fill: 'backwards' })])
    } finally {
      HTMLElement.prototype.animate = original
    }
  })
})

describe('TabSlidePanel', () => {
  it('remounts its content only when the tab changes', () => {
    const view = render(<TabSlidePanel active="a" order={order} className="body">A</TabSlidePanel>)
    const panel = () => view.host.firstElementChild as HTMLElement
    const first = panel()
    expect(first.className).toBe('body')
    expect(first.hasAttribute('data-motion')).toBe(false)
    view.rerender(<TabSlidePanel active="a" order={order} className="body">A again</TabSlidePanel>)
    expect(panel()).toBe(first)
    view.rerender(<TabSlidePanel active="b" order={order} className="body">B</TabSlidePanel>)
    expect(panel()).not.toBe(first)
    expect(panel().className).toBe('body rv-tab-in-next')
    expect(panel().getAttribute('data-motion')).toBe('block')
  })
})
