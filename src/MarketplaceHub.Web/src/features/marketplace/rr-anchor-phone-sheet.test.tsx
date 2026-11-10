import { act } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { RrAnchor } from './returns-ui'

// On phones an RrAnchor menu is a bottom sheet. Inside a window (translate, will-change, backdrop blur) a fixed sheet
// was placed in the window instead of on the screen, so sheet and scrim open in the top layer, while staying in the
// anchor's DOM: the scrim tap, Escape and an option still work and the window under it stays open.

const q = (selector: string) => document.querySelector<HTMLElement>(selector)
const click = (element: Element | null) => act(() => (element as HTMLElement).click())
const escape = () => act(() => { (document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })

let shown: Element[] = []

function Window({ onPick }: { onPick: (value: string) => void }) {
  return <div className="rr-layer"><div className="rr-overlay" style={{ backdropFilter: 'blur(6px)' }}><div className="rr-modal window" style={{ willChange: 'translate' }}>
    <RrAnchor trigger={(open, toggle) => <button type="button" className="sheet-trigger" aria-expanded={open} onClick={toggle}>Sıralama</button>}>
      {close => <div className="rr-pop sheet" role="listbox" style={{ position: 'fixed' }}><button type="button" role="option" aria-selected={false} className="sheet-opt" onClick={() => { onPick('NAME'); close() }}>Model Kodu (A-Z)</button></div>}
    </RrAnchor>
  </div></div></div>
}

describe('RrAnchor bottom sheet on phones', () => {
  beforeEach(() => {
    shown = []
    vi.spyOn(window, 'matchMedia').mockImplementation(query => ({ matches: query.includes('max-width: 760px'), media: query, onchange: null, addListener: () => undefined, removeListener: () => undefined, addEventListener: () => undefined, removeEventListener: () => undefined, dispatchEvent: () => false }))
    Object.defineProperty(HTMLElement.prototype, 'showPopover', { configurable: true, writable: true, value: function (this: HTMLElement) { shown.push(this) } })
  })
  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
    delete (HTMLElement.prototype as Partial<HTMLElement>).showPopover
  })

  it('opens sheet and scrim in the top layer, scrim first, so neither is placed inside the window', () => {
    render(<Window onPick={() => undefined} />)
    click(q('.sheet-trigger'))
    const sheet = q('.sheet')!
    const scrim = q('.rr-anchor-scrim')!
    expect(sheet.getAttribute('popover')).toBe('manual')
    expect(scrim.getAttribute('popover')).toBe('manual')
    expect(shown).toEqual([scrim, sheet])
    expect(q('.window')!.contains(sheet)).toBe(true)
  })

  it('closes on a scrim tap and on Escape; the window stays', () => {
    render(<Window onPick={() => undefined} />)
    click(q('.sheet-trigger'))
    click(q('.rr-anchor-scrim'))
    expect(q('.sheet')).toBeNull()
    expect(q('.window')).not.toBeNull()

    click(q('.sheet-trigger'))
    act(() => q('.sheet-opt')!.focus())
    escape()
    expect(q('.sheet')).toBeNull()
    expect(q('.window')).not.toBeNull()
  })

  it('an option picks its value and closes the sheet', () => {
    const onPick = vi.fn()
    render(<Window onPick={onPick} />)
    click(q('.sheet-trigger'))
    click(q('.sheet-opt'))
    expect(onPick).toHaveBeenCalledWith('NAME')
    expect(q('.sheet')).toBeNull()
  })

  it('on a desktop the popover stays where the stylesheet puts it', () => {
    vi.mocked(window.matchMedia).mockImplementation(query => ({ matches: false, media: query, onchange: null, addListener: () => undefined, removeListener: () => undefined, addEventListener: () => undefined, removeEventListener: () => undefined, dispatchEvent: () => false }))
    render(<Window onPick={() => undefined} />)
    click(q('.sheet-trigger'))
    expect(q('.sheet')!.hasAttribute('popover')).toBe(false)
    expect(q('.rr-anchor-scrim')!.hasAttribute('popover')).toBe(false)
  })
})
