import { afterEach, describe, expect, it, vi } from 'vitest'
import { followNotificationBell, notificationHistoryPlacement } from './notification-history-placement'

describe('notificationHistoryPlacement', () => {
  it('sits under the bell with right edges aligned, 440 px wide on a wide window', () => {
    expect(notificationHistoryPlacement({ right: 1200, bottom: 50 }, 1300)).toEqual({ top: 60, left: 760, width: 440 })
  })

  it('stays 16 px inside a narrow window', () => {
    expect(notificationHistoryPlacement({ right: 100, bottom: 40 }, 375)).toEqual({ top: 50, left: 16, width: 343 })
  })
})

describe('followNotificationBell', () => {
  afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = '' })

  function setup() {
    const bell = document.createElement('div')
    const panel = document.createElement('div')
    document.body.append(bell, panel)
    let rect = { right: 1200, bottom: 50 }
    vi.spyOn(bell, 'getBoundingClientRect').mockImplementation(() => rect as DOMRect)
    const frames: FrameRequestCallback[] = []
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => undefined)
    const runFrames = () => frames.splice(0).forEach(callback => callback(0))
    return { bell, panel, runFrames, move: (next: typeof rect) => { rect = next } }
  }

  it('places the panel at once, then once per frame however many scroll events arrive', () => {
    const { bell, panel, runFrames, move } = setup()
    const stop = followNotificationBell(() => bell, () => panel)
    expect(panel.style.top).toBe('60px')
    expect(panel.style.width).toBe(`${Math.min(440, window.innerWidth - 32)}px`)

    move({ right: 1200, bottom: 80 })
    for (let i = 0; i < 5; i += 1) document.dispatchEvent(new Event('scroll'))
    expect(window.requestAnimationFrame).toHaveBeenCalledTimes(1)
    runFrames()
    expect(panel.style.top).toBe('90px')

    stop()
    move({ right: 1200, bottom: 10 })
    document.dispatchEvent(new Event('scroll'))
    runFrames()
    expect(panel.style.top).toBe('90px')
  })

  it('leaves the style alone while the bell has not moved', () => {
    const { bell, panel, runFrames } = setup()
    const stop = followNotificationBell(() => bell, () => panel)
    panel.style.top = '1px'
    window.dispatchEvent(new Event('resize'))
    runFrames()
    expect(panel.style.top).toBe('1px')
    stop()
  })
})
