import { render } from '../test/render'
import { describe, expect, it } from 'vitest'
import { Tabs } from './components/DesignSystem'
import { revealActiveTab, scrollLeftForTab, updateStripFade } from './tab-strip-scroll'

function rect(left: number, right: number): DOMRect { return { left, right, top: 0, bottom: 48, width: right - left, height: 48, x: left, y: 0, toJSON: () => ({}) } }

function strip(scrollLeft: number, tabLeft: number, tabRight: number, width = 400, scrollWidth = 700) {
  const el = document.createElement('div')
  const tab = document.createElement('button')
  tab.setAttribute('aria-selected', 'true')
  el.append(tab)
  Object.defineProperty(el, 'clientWidth', { value: width, configurable: true })
  Object.defineProperty(el, 'scrollWidth', { value: scrollWidth, configurable: true })
  el.scrollLeft = scrollLeft
  el.getBoundingClientRect = () => rect(0, width)
  tab.getBoundingClientRect = () => rect(tabLeft, tabRight)
  return { el, tab }
}

describe('tab strip scroll', () => {
  it('leaves a strip that fits, and a tab already in view, alone', () => {
    const fit = strip(0, 500, 600, 400, 400)
    expect(scrollLeftForTab(fit.el, fit.tab)).toBeNull()
    const visible = strip(0, 100, 200)
    expect(scrollLeftForTab(visible.el, visible.tab)).toBeNull()
  })

  it('scrolls right to reveal a tab cut off at the end, with its side room', () => {
    const { el, tab } = strip(0, 320, 460)
    expect(scrollLeftForTab(el, tab)).toBe(68)
    revealActiveTab(el, false)
    expect(el.scrollLeft).toBe(68)
  })

  it('scrolls left to reveal a tab cut off at the start, never below 0', () => {
    const { el, tab } = strip(200, -30, 90)
    expect(scrollLeftForTab(el, tab)).toBe(162)
    const first = strip(5, -40, 60)
    expect(scrollLeftForTab(first.el, first.tab)).toBe(0)
  })

  it('shows the fade attributes only on the sides with hidden tabs', () => {
    const { el } = strip(0, 0, 10)
    updateStripFade(el)
    expect(el.hasAttribute('data-more-end')).toBe(true)
    expect(el.hasAttribute('data-more-start')).toBe(false)
    el.scrollLeft = 300
    updateStripFade(el)
    expect(el.hasAttribute('data-more-end')).toBe(false)
    expect(el.hasAttribute('data-more-start')).toBe(true)
  })

  it('Tabs renders in jsdom without errors and keeps its tablist', () => {
    render(<Tabs ariaLabel="Deneme" value="a" onChange={() => undefined} items={[{ value: 'a', label: 'A' }, { value: 'b', label: 'B' }]} />)
    expect(document.querySelector('[role="tablist"]')!.getAttribute('data-more-end')).toBeNull()
  })
})
