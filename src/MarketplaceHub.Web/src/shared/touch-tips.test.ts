import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { OPEN_ATTR, closeTouchTip, installTouchTips, placeBeside } from './touch-tips'

type Rect = { left: number; top: number; width: number; height: number }
const box = ({ left, top, width, height }: Rect) => ({ left, top, width, height, right: left + width, bottom: top + height, x: left, y: top, toJSON: () => ({}) }) as DOMRect
const setRect = (element: Element, rect: Rect | (() => Rect)) => { element.getBoundingClientRect = () => box(typeof rect === 'function' ? rect() : rect) }

let hover = false
const originalMatchMedia = window.matchMedia
const originalWidth = window.innerWidth, originalHeight = window.innerHeight
let uninstall = () => {}

function pointer(type: string, target: Element, x: number, y: number, pointerType = 'touch') {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y })
  Object.defineProperty(event, 'pointerType', { value: pointerType })
  Object.defineProperty(event, 'pointerId', { value: 1 })
  target.dispatchEvent(event)
}
const tap = (target: Element, pointerType = 'touch') => { pointer('pointerdown', target, 50, 50, pointerType); pointer('pointerup', target, 51, 52, pointerType) }
const bubble = () => document.getElementById('touch-tip-bubble')

beforeEach(() => {
  hover = false
  window.matchMedia = (query: string) => ({
    matches: query === '(hover: none)' ? !hover : query.includes('max-width: 760px') ? true : false,
    media: query, onchange: null, addListener: () => undefined, removeListener: () => undefined,
    addEventListener: () => undefined, removeEventListener: () => undefined, dispatchEvent: () => false
  }) as MediaQueryList
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: 375 })
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: 700 })
  Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, get() { return (this as HTMLElement).classList.contains('touch-tip-bubble') ? 240 : 0 } })
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', { configurable: true, get() { return (this as HTMLElement).classList.contains('touch-tip-bubble') ? 40 : 0 } })
  uninstall = installTouchTips()
})

afterEach(() => {
  uninstall()
  closeTouchTip()
  document.body.innerHTML = ''
  window.matchMedia = originalMatchMedia
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: originalWidth })
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: originalHeight })
  delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth
  delete (HTMLElement.prototype as { offsetHeight?: number }).offsetHeight
})

function platformMark(left = 4, top = 300) {
  document.body.innerHTML = '<div class="rr-pmarks"><span class="rr-pm rr-on" tabindex="0" data-tip="Trendyol\'da yayında: tüm varyantlar (28/28)"><i></i></span></div><p class="elsewhere">x</p>'
  const mark = document.querySelector('.rr-pm')!
  setRect(mark, { left, top, width: 24, height: 24 })
  return mark
}

describe('touch tips', () => {
  it('opens a logo tip on a tap, inside the window, and closes it on a second tap', () => {
    const mark = platformMark()
    tap(mark)
    expect(mark.hasAttribute(OPEN_ATTR)).toBe(true)
    const tip = bubble()!
    expect(tip.textContent).toBe("Trendyol'da yayında: tüm varyantlar (28/28)")
    // At the left edge of a 375 px phone the 240 px bubble starts 12 px in, not off the screen; above the mark.
    expect(parseFloat(tip.style.left)).toBe(12)
    expect(parseFloat(tip.style.left) + 240).toBeLessThanOrEqual(375 - 12)
    expect(parseFloat(tip.style.top)).toBe(300 - 6 - 40)
    tap(mark)
    expect(mark.hasAttribute(OPEN_ATTR)).toBe(false)
    expect(bubble()).toBeNull()
  })

  it('keeps the bubble inside the right edge and flips it below a mark at the top of the window', () => {
    const mark = platformMark(360, 20)
    tap(mark)
    const tip = bubble()!
    expect(parseFloat(tip.style.left) + 240).toBe(375 - 12)
    expect(parseFloat(tip.style.top)).toBe(20 + 24 + 6)
  })

  it('closes on a tap elsewhere, Esc and a scroll', () => {
    const mark = platformMark()
    tap(mark)
    tap(document.querySelector('.elsewhere')!)
    expect(bubble()).toBeNull()
    tap(mark)
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    expect(bubble()).toBeNull()
    tap(mark)
    window.dispatchEvent(new Event('scroll'))
    document.dispatchEvent(new Event('scroll'))
    expect(bubble()).toBeNull()
    expect(mark.hasAttribute(OPEN_ATTR)).toBe(false)
  })

  it('does not open when the finger moves (a scroll) or the pointer can hover', () => {
    const mark = platformMark()
    pointer('pointerdown', mark, 50, 50)
    pointer('pointerup', mark, 50, 80)
    expect(bubble()).toBeNull()
    tap(mark, 'mouse')
    expect(bubble()).toBeNull()
    hover = true
    tap(mark)
    expect(bubble()).toBeNull()
  })

  it('moves an element tip above its trigger when it runs past the bottom, and stays open on a tap inside it', () => {
    document.body.innerHTML = '<table class="rr-ptbl"><tbody><tr><td><span class="rr-vhover" tabindex="0"><span class="rr-varcell">3 seçenek</span><div class="rr-tip rr-vtip"><table><tbody><tr><td class="cell">S</td></tr></tbody></table></div></span></td></tr></tbody></table>'
    const host = document.querySelector<HTMLElement>('.rr-vhover')!
    const tip = document.querySelector<HTMLElement>('.rr-vtip')!
    setRect(host, { left: 16, top: 520, width: 160, height: 40 })
    // Shown in place under the trigger (CSS), card width, 200 px tall: its bottom (766) is past the 700 px window.
    setRect(tip, () => { const dy = parseFloat(tip.style.translate.split(' ')[1] ?? '0') || 0; return { left: 12, top: 566 + dy, width: 351, height: 200 } })
    tap(host.querySelector('.rr-varcell')!)
    expect(host.hasAttribute(OPEN_ATTR)).toBe(true)
    // Above the trigger: its bottom 6 px over the trigger's top (520 - 6 - 200 = 314).
    expect(tip.style.translate).toBe(`0px ${314 - 566}px`)
    tap(tip.querySelector('.cell')!)
    expect(host.hasAttribute(OPEN_ATTR)).toBe(true)
    tap(host.querySelector('.rr-varcell')!)
    expect(host.hasAttribute(OPEN_ATTR)).toBe(false)
    expect(tip.style.translate).toBe('')
  })

  it('opens one tip at a time and leaves a trigger inside a link to the link', () => {
    document.body.innerHTML = '<span class="rr-reason-tip a" tabindex="0"><span class="rr-reason-pop">A</span></span><span class="rr-reason-tip b" tabindex="0"><span class="rr-reason-pop">B</span></span><a href="/x"><span class="rr-reason-tip c"><span class="rr-reason-pop">C</span></span></a>'
    for (const element of document.querySelectorAll('.rr-reason-tip, .rr-reason-pop')) setRect(element, { left: 20, top: 200, width: 100, height: 30 })
    const [a, b, c] = Array.from(document.querySelectorAll('.rr-reason-tip'))
    tap(a)
    tap(b)
    expect(a.hasAttribute(OPEN_ATTR)).toBe(false)
    expect(b.hasAttribute(OPEN_ATTR)).toBe(true)
    tap(c)
    expect(c.hasAttribute(OPEN_ATTR)).toBe(false)
  })

  it('opens a report explanation from its trigger on its row', () => {
    document.body.innerHTML = '<div class="reports-tip-host"><dt><button type="button" class="reports-tip-trigger">En İyi Gün</button></dt><span role="tooltip" class="reports-hover-tip">Açıklama</span></div>'
    const host = document.querySelector('.reports-tip-host')!
    for (const element of document.querySelectorAll('.reports-tip-host, .reports-hover-tip')) setRect(element, { left: 20, top: 300, width: 200, height: 40 })
    tap(document.querySelector('.reports-tip-trigger')!)
    expect(host.hasAttribute(OPEN_ATTR)).toBe(true)
  })
})

describe('placeBeside', () => {
  const view = { width: 375, height: 700, top: 0 }
  const anchor = box({ left: 100, top: 400, width: 40, height: 20 })
  it('prefers its side when it fits and flips when it does not', () => {
    expect(placeBeside(anchor, 200, 100, 'below', view)).toMatchObject({ top: 426, below: true, maxHeight: null })
    expect(placeBeside(anchor, 200, 100, 'above', view)).toMatchObject({ top: 294, below: false })
    expect(placeBeside(anchor, 200, 320, 'below', view)).toMatchObject({ top: 400 - 6 - 320, below: false })
  })
  it('never opens under the sticky top bar: cut to the roomier side', () => {
    const near = box({ left: 100, top: 120, width: 40, height: 20 })
    const place = placeBeside(near, 200, 600, 'above', { ...view, top: 100 })
    expect(place.below).toBe(true)
    expect(place.top).toBe(146)
    expect(place.maxHeight).toBe(700 - 12 - 140 - 6)
  })
  it('keeps a wide tip 12 px from both sides', () => {
    const place = placeBeside(anchor, 500, 50, 'below', view)
    expect(place.left).toBe(12)
    expect(place.maxWidth).toBe(351)
  })
})
