import { afterEach, describe, expect, it } from 'vitest'
import { clampIntoViewport, unclampViewport } from './viewport-clamp'

function menu({ left, width, cssLeft = '6px', cssRight = 'auto' }: { left: number; width: number; cssLeft?: string; cssRight?: string }) {
  // The menu's own offsets come from a stylesheet, as in the app; the clamp writes inline ones over them.
  const sheet = document.createElement('style')
  sheet.textContent = `.menu { position: absolute; left: ${cssLeft}; right: ${cssRight}; }`
  const element = document.createElement('div')
  element.className = 'menu'
  document.body.append(sheet, element)
  Object.defineProperty(element, 'offsetParent', { configurable: true, get: () => null })
  Object.defineProperty(element, 'offsetLeft', { configurable: true, get: () => left })
  Object.defineProperty(element, 'offsetWidth', { configurable: true, get: () => width })
  Object.defineProperty(element, 'offsetHeight', { configurable: true, get: () => 40 })
  return element
}

describe('clampIntoViewport', () => {
  const view = 402
  Object.defineProperty(document.documentElement, 'clientWidth', { configurable: true, get: () => view })
  afterEach(() => { document.body.innerHTML = '' })

  it('leaves a menu that fits where it is', () => {
    const element = menu({ left: 36, width: 200 })
    clampIntoViewport(element)
    expect(element.style.left).toBe('')
    expect(element.dataset.viewportClamp).toBeUndefined()
  })

  it('moves a menu that runs past the right edge back inside, 12 px from it', () => {
    const element = menu({ left: 271, width: 200 })
    clampIntoViewport(element)
    // 271 + 200 = 471; the right edge may reach 402 - 12 = 390, so it moves 81 px left.
    expect(element.style.left).toBe('-75px')
    unclampViewport(element)
    expect(element.style.left).toBe('')
    expect(getComputedStyle(element).left).toBe('6px')
  })

  it('moves a menu that starts left of the window to the right, and moves both offsets when both are set', () => {
    const element = menu({ left: -40, width: 200, cssLeft: '-20px', cssRight: '10px' })
    clampIntoViewport(element)
    expect(element.style.left).toBe('32px')
    expect(element.style.right).toBe('-42px')
  })
})
