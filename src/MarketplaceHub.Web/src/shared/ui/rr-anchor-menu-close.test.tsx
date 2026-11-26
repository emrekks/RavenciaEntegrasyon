import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { RrAnchor } from './rr-components'

// An action menu closes when an item is chosen, even when the caller's menu never calls the close callback.
const q = (selector: string) => document.querySelector<HTMLElement>(selector)
const items = () => document.querySelectorAll<HTMLElement>('[role="menuitem"]')
// A pointer click has detail 1; element.click() (what Enter or Space on a button does) has detail 0.
const pointerClick = (element: Element) => act(() => { element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 })) })
const keyClick = (element: HTMLElement) => act(() => element.click())

function Menu({ onPick, onStay }: { onPick: () => void; onStay?: () => void }) {
  return <RrAnchor className="rr-dropdown" trigger={(open, toggle) => <button type="button" className="rr-trigger" aria-haspopup="menu" aria-expanded={open} onClick={toggle}>İşlemler</button>}>
    {() => <div className="rr-menu" role="menu"><button type="button" role="menuitem" onClick={onPick}>Seç</button><button type="button" role="menuitem" data-keep-open onClick={onStay}>Kal</button></div>}
  </RrAnchor>
}

describe('RrAnchor menu item choice', () => {
  afterEach(cleanup)

  it('closes on a click and runs the handler once', () => {
    const onPick = vi.fn()
    render(<Menu onPick={onPick} />)
    pointerClick(q('.rr-trigger')!)
    expect(q('[role="menu"]')).not.toBeNull()
    pointerClick(items()[0])
    expect(onPick).toHaveBeenCalledTimes(1)
    expect(q('[role="menu"]')).toBeNull()
  })

  it('closes on Enter and gives focus back to the trigger', () => {
    const onPick = vi.fn()
    render(<Menu onPick={onPick} />)
    pointerClick(q('.rr-trigger')!)
    items()[0].focus()
    keyClick(items()[0])
    expect(onPick).toHaveBeenCalledTimes(1)
    expect(q('[role="menu"]')).toBeNull()
    expect(document.activeElement).toBe(q('.rr-trigger'))
  })

  it('stays open for an item marked data-keep-open and closes on Escape', () => {
    const onStay = vi.fn()
    render(<Menu onPick={() => undefined} onStay={onStay} />)
    pointerClick(q('.rr-trigger')!)
    pointerClick(items()[1])
    expect(onStay).toHaveBeenCalledTimes(1)
    expect(q('[role="menu"]')).not.toBeNull()
    act(() => { (document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    expect(q('[role="menu"]')).toBeNull()
  })
})
