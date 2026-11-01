import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushUnitPriceAlignment, queueUnitPriceAlignment } from './unit-price-alignment'

function row(heights: number[], log: string[]) {
  const tr = document.createElement('tr')
  heights.forEach((height, index) => {
    const line = document.createElement('div')
    line.className = 'rr-pline'
    line.getBoundingClientRect = () => { log.push(`read ${height}`); return { height } as DOMRect }
    const price = document.createElement('div')
    price.className = 'rr-pr'
    const style = price.style
    Object.defineProperty(style, 'height', { configurable: true, get: () => style.getPropertyValue('height'), set: (value: string) => { log.push(`write ${index}`); style.setProperty('height', value) } })
    tr.append(line, price)
  })
  document.body.append(tr)
  return tr
}

afterEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks() })

describe('unit price alignment', () => {
  it('measures every queued row before writing any height, in one frame', () => {
    const frames: FrameRequestCallback[] = []
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1440 })
    const log: string[] = []
    const first = row([40], log), second = row([64], log)
    queueUnitPriceAlignment(first)
    queueUnitPriceAlignment(second)
    expect(frames).toHaveLength(1)
    frames[0](0)
    expect(log).toEqual(['read 40', 'read 64', 'write 0', 'write 0'])
    expect([first, second].map(tr => tr.querySelector<HTMLElement>('.rr-pr')!.style.height)).toEqual(['40px', '64px'])
  })

  it('leaves an unchanged height alone and clears it on narrow screens', () => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(() => 1)
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1440 })
    const log: string[] = []
    const tr = row([40], log)
    queueUnitPriceAlignment(tr); flushUnitPriceAlignment()
    log.length = 0
    queueUnitPriceAlignment(tr); flushUnitPriceAlignment()
    expect(log).toEqual(['read 40'])
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 800 })
    queueUnitPriceAlignment(tr); flushUnitPriceAlignment()
    expect(tr.querySelector<HTMLElement>('.rr-pr')!.style.height).toBe('')
  })
})
