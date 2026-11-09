import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { pendingVisibleImages, scheduleDeferredPrefetch } from './deferred-prefetch'

function image(complete: boolean, top = 10) {
  const element = document.createElement('img')
  Object.defineProperty(element, 'complete', { value: complete, configurable: true })
  element.getBoundingClientRect = () => ({ top, bottom: top + 82, left: 10, right: 64, width: 54, height: 82, x: 10, y: top, toJSON: () => ({}) })
  return element
}

describe('scheduleDeferredPrefetch', () => {
  let root: HTMLDivElement
  beforeEach(() => {
    vi.useFakeTimers()
    root = document.createElement('div')
    document.body.append(root)
  })
  afterEach(() => {
    root.remove()
    vi.useRealTimers()
  })

  it('waits for the pictures on screen, then for an idle moment', () => {
    const shown = image(false)
    root.append(shown, image(true), image(false, 5000))
    const run = vi.fn()
    scheduleDeferredPrefetch(run, { root, maxWaitMs: 10_000 })
    vi.advanceTimersByTime(3000)
    expect(run).not.toHaveBeenCalled()
    shown.dispatchEvent(new Event('load'))
    vi.advanceTimersByTime(1600)
    expect(run).toHaveBeenCalledTimes(1)
  })

  it('does not wait for a lazy picture below the fold', () => {
    const shown = image(false)
    root.append(image(false, 5000), shown, image(true))
    expect(pendingVisibleImages(root, { innerHeight: 900, innerWidth: 1440 })).toEqual([shown])
  })

  it('starts after maxWaitMs when a picture never ends', () => {
    root.append(image(false))
    const run = vi.fn()
    scheduleDeferredPrefetch(run, { root, maxWaitMs: 5000 })
    vi.advanceTimersByTime(4999)
    expect(run).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(run).toHaveBeenCalledTimes(1)
    // A picture loading afterwards does not read the page twice.
    root.querySelector('img')!.dispatchEvent(new Event('load'))
    vi.advanceTimersByTime(10_000)
    expect(run).toHaveBeenCalledTimes(1)
  })

  it('starts at once when the end of the list comes into view', () => {
    let notify: ((entries: Array<{ isIntersecting: boolean }>) => void) | null = null
    const original = globalThis.IntersectionObserver
    globalThis.IntersectionObserver = class { constructor(callback: typeof notify) { notify = callback } observe() {} disconnect() {} } as unknown as typeof IntersectionObserver
    try {
      root.append(image(false))
      const run = vi.fn()
      scheduleDeferredPrefetch(run, { root, nearEnd: document.createElement('div'), maxWaitMs: 10_000 })
      expect(run).not.toHaveBeenCalled()
      notify!([{ isIntersecting: true }])
      expect(run).toHaveBeenCalledTimes(1)
    } finally {
      globalThis.IntersectionObserver = original
    }
  })

  it('never runs once cancelled', () => {
    const run = vi.fn()
    const cancel = scheduleDeferredPrefetch(run, { root, maxWaitMs: 1000 })
    cancel()
    vi.advanceTimersByTime(5000)
    expect(run).not.toHaveBeenCalled()
  })
})
