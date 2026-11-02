import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { StickyActionBar } from './StickyActionBar'

type Callback = (entries: Array<Partial<IntersectionObserverEntry>>) => void
let callback: Callback | null = null

afterEach(() => { cleanup(); vi.unstubAllGlobals(); callback = null })

function stubObserver() {
  vi.stubGlobal('IntersectionObserver', class { constructor(cb: Callback) { callback = cb } observe() {} disconnect() {} })
}

describe('useStuckBottom through StickyActionBar', () => {
  it('starts stuck (square) and without an observer stays that way', () => {
    const { host } = render(<StickyActionBar label="Toplu işlemler">2 seçildi</StickyActionBar>)
    expect(host.querySelector('.rv-action-bar')?.getAttribute('data-stuck')).toBe('true')
  })

  it('turns rounded once the bar rests fully inside the scroller, and square again when it sticks', () => {
    stubObserver()
    const { host } = render(<StickyActionBar label="Toplu işlemler">2 seçildi</StickyActionBar>)
    const bar = () => host.querySelector('.rv-action-bar')
    act(() => callback?.([{ intersectionRatio: 1, boundingClientRect: { bottom: 700 } as DOMRectReadOnly, rootBounds: { bottom: 799 } as DOMRectReadOnly }]))
    expect(bar()?.getAttribute('data-stuck')).toBe('false')
    expect(bar()?.getAttribute('data-docked')).toBe('true')
    act(() => callback?.([{ intersectionRatio: 0.98, boundingClientRect: { bottom: 800 } as DOMRectReadOnly, rootBounds: { bottom: 799 } as DOMRectReadOnly }]))
    expect(bar()?.getAttribute('data-stuck')).toBe('true')
  })
})
