import { act, useRef, useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { isRevealCandidate, revealClass, usePageEntryReveal } from './usePageEntryReveal'

afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks() })

function Shell({ late }: { late: boolean }) {
  const ref = useRef<HTMLElement>(null)
  usePageEntryReveal(ref)
  return <main ref={ref}><section className="content"><header id="head">Başlık</header><div id="grid">{late && <section id="late" className="rr-panel"><table><tbody><tr><td>1</td></tr></tbody></table></section>}</div><div className="rv-action-bar" id="bar" /></section></main>
}

describe('usePageEntryReveal', () => {
  it('plays the entry on every block of the page once, and on a panel that mounts with its data later', async () => {
    vi.useFakeTimers()
    // Motion is allowed here (the test setup asks for reduced motion) and every block is on screen (nothing is laid out).
    vi.spyOn(window, 'matchMedia').mockImplementation(query => ({ matches: false, media: query, onchange: null, addListener: () => undefined, removeListener: () => undefined, addEventListener: () => undefined, removeEventListener: () => undefined, dispatchEvent: () => false }))
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({ top: 10, bottom: 50, left: 0, right: 100, width: 100, height: 40, x: 0, y: 10, toJSON: () => ({}) } as DOMRect)
    let setLate: (value: boolean) => void = () => undefined
    function Host() { const [late, set] = useState(false); setLate = set; return <Shell late={late} /> }
    const { host } = render(<Host />)
    expect(host.querySelector('#head')!.classList.contains(revealClass)).toBe(true)
    expect(host.querySelector('#grid')!.classList.contains(revealClass)).toBe(true)
    expect(host.querySelector('#bar')!.classList.contains(revealClass)).toBe(false)
    act(() => { vi.advanceTimersByTime(3000) })
    expect(host.querySelector('#head')!.classList.contains(revealClass)).toBe(false)
    await act(async () => { setLate(true); await Promise.resolve() })
    expect(host.querySelector('#late')!.classList.contains(revealClass)).toBe(true)
  })

  it('plays nothing when the device asks for less motion', () => {
    const { host } = render(<Shell late />)
    expect(host.querySelector('.' + revealClass)).toBeNull()
  })

  it('treats the action bar, dialogs and toasts as not part of the entry', () => {
    const bar = document.createElement('div'); bar.className = 'rv-action-bar'
    const main = document.createElement('main'); const content = document.createElement('section'); content.className = 'content'
    main.append(content); content.append(bar); document.body.append(main)
    expect(isRevealCandidate(bar)).toBe(false)
    const block = document.createElement('div'); content.append(block)
    expect(isRevealCandidate(block)).toBe(true)
    main.remove()
  })
})
