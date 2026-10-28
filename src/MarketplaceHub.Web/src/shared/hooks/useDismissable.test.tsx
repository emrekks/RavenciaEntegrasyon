import { act, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { closeAllDismissables, insideSelector, useDismissable } from './useDismissable'

function Popover({ id, children, initiallyOpen = false, isInside }: { id: string; children?: React.ReactNode; initiallyOpen?: boolean; isInside?: (node: Node) => boolean }) {
  const [open, setOpen] = useState(initiallyOpen)
  const trigger = useRef<HTMLButtonElement>(null)
  const panel = useRef<HTMLDivElement>(null)
  useDismissable(open, [trigger, panel], () => setOpen(false), { triggerRef: trigger, isInside })
  return <span data-popover={id}>
    <button ref={trigger} type="button" data-trigger={id} onClick={() => setOpen(value => !value)}>{id}</button>
    {open && createPortal(<div ref={panel} data-panel={id}><button type="button" data-item={id}>item</button>{children}</div>, document.body)}
  </span>
}

const press = (element: Element) => act(() => {
  element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }))
  element.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }))
})
const click = (element: Element) => act(() => (element as HTMLElement).click())
const escape = () => act(() => { document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
const panel = (id: string) => document.querySelector(`[data-panel="${id}"]`)
const trigger = (id: string) => document.querySelector(`[data-trigger="${id}"]`)!

describe('useDismissable', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
  })

  it('stays open on presses on its trigger and its portalled panel, and closes on a press anywhere else', () => {
    act(() => root.render(<><Popover id="a" /><p data-outside>page</p></>))
    click(trigger('a'))
    press(panel('a')!.querySelector('[data-item]')!)
    expect(panel('a')).not.toBeNull()
    press(container.querySelector('[data-outside]')!)
    expect(panel('a')).toBeNull()
  })

  it('closes even when the pressed element stops the event (a modal backdrop, another popover)', () => {
    act(() => root.render(<><Popover id="a" /><div data-stopper role="presentation" onPointerDown={event => event.stopPropagation()} onMouseDown={event => event.stopPropagation()}>stops</div></>))
    click(trigger('a'))
    press(container.querySelector('[data-stopper]')!)
    expect(panel('a')).toBeNull()
  })

  it('closes on Escape, gives focus back to its trigger and keeps the Escape from the window behind it', () => {
    const behind = vi.fn()
    document.addEventListener('keydown', behind)
    try {
      act(() => root.render(<Popover id="a" />))
      click(trigger('a'))
      act(() => (panel('a')!.querySelector('[data-item]') as HTMLElement).focus())
      escape()
      expect(panel('a')).toBeNull()
      expect(document.activeElement).toBe(trigger('a'))
      expect(behind).not.toHaveBeenCalled()
    } finally {
      document.removeEventListener('keydown', behind)
    }
  })

  it('closes the open one when another opens, but keeps a popover open while one nested inside it opens', () => {
    act(() => root.render(<><Popover id="a" /><Popover id="b" /><Popover id="outer"><Popover id="inner" /></Popover></>))
    click(trigger('a'))
    press(trigger('b')); click(trigger('b'))
    expect(panel('a')).toBeNull()
    expect(panel('b')).not.toBeNull()

    press(trigger('outer')); click(trigger('outer'))
    expect(panel('b')).toBeNull()
    press(trigger('inner')); click(trigger('inner'))
    expect(panel('outer')).not.toBeNull()
    expect(panel('inner')).not.toBeNull()
    // A press in the nested (portalled) panel belongs to the outer one as well.
    press(panel('inner')!.querySelector('[data-item]')!)
    expect(panel('outer')).not.toBeNull()
    // Escape closes the innermost first.
    escape()
    expect(panel('inner')).toBeNull()
    expect(panel('outer')).not.toBeNull()
  })

  it('opening one by keyboard or code closes the others too', () => {
    act(() => root.render(<><Popover id="a" initiallyOpen /><Popover id="b" /></>))
    expect(panel('a')).not.toBeNull()
    act(() => (trigger('b') as HTMLElement).click())
    expect(panel('a')).toBeNull()
    expect(panel('b')).not.toBeNull()
  })

  it('closes every open popover on a route change', () => {
    act(() => root.render(<Popover id="a" initiallyOpen />))
    expect(panel('a')).not.toBeNull()
    act(() => closeAllDismissables('route'))
    expect(panel('a')).toBeNull()
  })

  it('treats nodes matched by isInside as part of the popover', () => {
    act(() => root.render(<><Popover id="a" isInside={insideSelector('.calendar')} /><div className="calendar"><span data-day>12</span></div><p data-outside>x</p></>))
    click(trigger('a'))
    press(container.querySelector('[data-day]')!.firstChild!.parentElement!)
    expect(panel('a')).not.toBeNull()
    press(container.querySelector('[data-outside]')!)
    expect(panel('a')).toBeNull()
  })
})
