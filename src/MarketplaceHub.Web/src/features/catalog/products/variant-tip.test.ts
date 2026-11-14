import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CLOSE_DELAY, SHUT_ATTR, closeVariantTip, openVariantTipAnchor, variantTipBlur, variantTipFocus, variantTipPointerEnter, variantTipPointerLeave } from './variant-tip'

// jsdom has no popover API and no :focus-visible: the tips carry a fake one, and the focus-visible state is set by hand.
const shown = new WeakSet<Element>()
const focusVisible = new WeakSet<Element>()
const originalMatches = Element.prototype.matches
const originalMatchMedia = window.matchMedia
let touch = false

function row() {
  const td = document.createElement('td')
  td.innerHTML = '<span class="rr-vhover" tabindex="0"><span class="rr-varcell">3 seçenek</span><span class="rr-tip rr-vtip"><table><tbody><tr><td>S</td></tr></tbody></table></span></span>'
  document.body.append(td)
  const anchor = td.querySelector<HTMLElement>('.rr-vhover')!
  return { anchor, tip: anchor.querySelector<HTMLElement>('.rr-vtip')!, isOpen: () => shown.has(anchor.querySelector('.rr-vtip')!) }
}

beforeEach(() => {
  vi.useFakeTimers()
  touch = false
  Element.prototype.matches = function (this: Element, selector: string): boolean {
    if (selector === ':popover-open') return shown.has(this)
    if (selector === ':focus-visible') return focusVisible.has(this)
    return originalMatches.call(this, selector)
  } as typeof Element.prototype.matches
  Object.assign(HTMLElement.prototype, {
    showPopover(this: HTMLElement) { shown.add(this) },
    hidePopover(this: HTMLElement) { shown.delete(this) }
  })
  window.matchMedia = (query: string) => ({ matches: query === '(hover: none)' ? touch : false, media: query, onchange: null, addListener: () => undefined, removeListener: () => undefined, addEventListener: () => undefined, removeEventListener: () => undefined, dispatchEvent: () => false }) as MediaQueryList
})

afterEach(() => {
  closeVariantTip()
  vi.useRealTimers()
  document.body.innerHTML = ''
  Element.prototype.matches = originalMatches
  window.matchMedia = originalMatchMedia
  delete (HTMLElement.prototype as Partial<HTMLElement>).showPopover
  delete (HTMLElement.prototype as Partial<HTMLElement>).hidePopover
})

describe('variant stock table open and close', () => {
  it('opens on hover and closes a moment after the pointer leaves', () => {
    const a = row()
    variantTipPointerEnter(a.anchor)
    expect(a.isOpen()).toBe(true)
    variantTipPointerLeave(a.anchor)
    expect(a.isOpen()).toBe(true)
    vi.advanceTimersByTime(CLOSE_DELAY)
    expect(a.isOpen()).toBe(false)
  })

  it('stays open while the pointer moves from "N seçenek" onto the table, and closes when it leaves both', () => {
    const a = row()
    variantTipPointerEnter(a.anchor)
    variantTipPointerLeave(a.anchor)
    vi.advanceTimersByTime(CLOSE_DELAY / 2)
    variantTipPointerEnter(a.anchor)
    vi.advanceTimersByTime(CLOSE_DELAY * 2)
    expect(a.isOpen()).toBe(true)
    variantTipPointerLeave(a.anchor)
    vi.advanceTimersByTime(CLOSE_DELAY)
    expect(a.isOpen()).toBe(false)
  })

  it('keeps one table open: another row closes the first at once', () => {
    const a = row(), b = row()
    variantTipPointerEnter(a.anchor)
    variantTipPointerEnter(b.anchor)
    expect(a.isOpen()).toBe(false)
    expect(b.isOpen()).toBe(true)
    expect(openVariantTipAnchor()).toBe(b.anchor)
  })

  it('does not open on a focus that is not Tab (a dialog giving focus back, the window coming to the front)', () => {
    const a = row()
    focusVisible.add(a.anchor)
    variantTipFocus(a.anchor)
    expect(a.isOpen()).toBe(false)
  })

  it('opens on Tab focus and closes on blur', () => {
    const a = row()
    focusVisible.add(a.anchor)
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab' }))
    variantTipFocus(a.anchor)
    expect(a.isOpen()).toBe(true)
    focusVisible.delete(a.anchor)
    variantTipBlur(a.anchor)
    expect(a.isOpen()).toBe(false)
  })

  it('closes on Esc, a scroll, a press outside, a resize and the window losing focus, and hides the :hover copy', () => {
    const a = row()
    const events: Array<() => void> = [
      () => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })),
      () => document.body.dispatchEvent(new Event('scroll')),
      () => document.body.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true })),
      () => window.dispatchEvent(new Event('resize')),
      () => window.dispatchEvent(new Event('blur'))
    ]
    for (const fire of events) {
      variantTipPointerEnter(a.anchor)
      expect(a.isOpen()).toBe(true)
      expect(a.anchor.hasAttribute(SHUT_ATTR)).toBe(false)
      fire()
      expect(a.isOpen()).toBe(false)
      expect(a.anchor.hasAttribute(SHUT_ATTR)).toBe(true)
    }
  })

  it('stays open on a scroll inside the table and a press on its own cell', () => {
    const a = row()
    variantTipPointerEnter(a.anchor)
    a.tip.dispatchEvent(new Event('scroll'))
    a.anchor.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }))
    expect(a.isOpen()).toBe(true)
  })

  it('leaves touch screens to touch-tips (tap to toggle)', () => {
    touch = true
    const a = row()
    variantTipPointerEnter(a.anchor)
    expect(a.isOpen()).toBe(false)
  })
})
