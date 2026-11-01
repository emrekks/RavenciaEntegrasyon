import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { startViewTransitionSafely } from './view-transition'

// The document seen as a browser that may or may not offer view transitions.
const doc = document as unknown as { startViewTransition?: unknown }
const nodeProcess = (globalThis as unknown as { process: { on(event: string, listener: (reason: unknown) => void): void; off(event: string, listener: (reason: unknown) => void): void } }).process

// A transition as Chromium hands back one it skips: `ready` rejects, nothing else is awaited by the caller.
function skippedTransition(reason = new DOMException('Transition was aborted because of invalid state', 'InvalidStateError')) {
  return { ready: Promise.reject(reason), finished: Promise.resolve(), updateCallbackDone: Promise.resolve(), skipTransition: vi.fn() }
}

// The test setup asks for reduced motion; these tests need motion so the transition is started.
beforeEach(() => { vi.spyOn(window, 'matchMedia').mockImplementation(query => ({ matches: false, media: query }) as MediaQueryList) })
afterEach(() => { delete doc.startViewTransition; vi.restoreAllMocks() })

describe('startViewTransitionSafely', () => {
  it('runs the update and leaves no unhandled rejection when the browser skips the transition', async () => {
    const unhandled = vi.fn()
    nodeProcess.on('unhandledRejection', unhandled)
    const update = vi.fn()
    doc.startViewTransition = vi.fn((callback: () => void) => { callback(); return skippedTransition() })

    startViewTransitionSafely(update)
    await new Promise(resolve => setTimeout(resolve, 20))
    nodeProcess.off('unhandledRejection', unhandled)

    expect(update).toHaveBeenCalledTimes(1)
    expect(unhandled).not.toHaveBeenCalled()
  })

  it('skips the running transition when a second change comes before it ends', () => {
    const first = { ready: new Promise(() => undefined), finished: new Promise(() => undefined), updateCallbackDone: Promise.resolve(), skipTransition: vi.fn() }
    const second = skippedTransition()
    const start = vi.fn().mockReturnValueOnce(first).mockReturnValueOnce(second)
    doc.startViewTransition = start

    startViewTransitionSafely(() => undefined)
    startViewTransitionSafely(() => undefined)

    expect(first.skipTransition).toHaveBeenCalledTimes(1)
    expect(start).toHaveBeenCalledTimes(2)
  })

  it('simply applies the change where the browser has no view transitions or start throws', () => {
    const update = vi.fn()
    startViewTransitionSafely(update)
    doc.startViewTransition = () => { throw new DOMException('hidden', 'InvalidStateError') }
    startViewTransitionSafely(update)
    expect(update).toHaveBeenCalledTimes(2)
  })
})
