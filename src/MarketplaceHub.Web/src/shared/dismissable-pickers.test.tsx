import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../test/render'
import { PlatformMultiSelect } from './platform-multi-select'
import { DateRangePicker } from './DateRangePicker'
import { ViewTabsWithMore } from './view-tabs'
import { closeAllDismissables } from './hooks/useDismissable'
import { InvoiceSortSelect } from '../features/invoicing/InvoiceSortSelect'
import { RrAnchor } from '../features/marketplace/returns-ui'

// Every picker closes on a press outside (also on an element that stops the event), on Escape with focus back on its
// trigger, on a route change, and when another picker opens (shared/hooks/useDismissable).

const press = (element: Element) => act(() => {
  element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }))
  element.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }))
})
const click = (element: Element | null) => act(() => (element as HTMLElement).click())
const escape = () => act(() => { (document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
const q = (selector: string) => document.querySelector(selector)

function Page() {
  return <>
    <PlatformMultiSelect label="Platformlar" options={[{ value: 'TRENDYOL', label: 'Trendyol' }]} selectedCodes={null} onChange={() => undefined} />
    <DateRangePicker from="" to="" onChange={() => undefined} />
    <InvoiceSortSelect value="ORDER_DESC" onChange={() => undefined} />
    <ViewTabsWithMore ariaLabel="Görünüm" items={[{ value: 'a', label: 'A' }, { value: 'b', label: 'B' }, { value: 'c', label: 'C' }]} more={['c']} value="a" onChange={() => undefined} />
    <RrAnchor trigger={(open, toggle) => <button type="button" className="anchor-trigger" aria-expanded={open} onClick={toggle}>Menü</button>}>{close => <div className="rr-menu anchor-menu"><button type="button" onClick={close}>Seç</button></div>}</RrAnchor>
    <div className="stopper" role="presentation" onPointerDown={event => event.stopPropagation()} onMouseDown={event => event.stopPropagation()}>modal backdrop</div>
  </>
}

const pickers = [
  { name: 'platform multi-select', trigger: '.platform-multi-select-trigger', panel: '.platform-multi-select-menu' },
  { name: 'date range picker', trigger: '.date-range-picker-trigger', panel: '.date-range-picker-popover' },
  { name: 'invoice sort', trigger: '.invoice-sort-trigger', panel: '.invoice-sort-list' },
  { name: 'view tabs Diğer menu', trigger: '.rv-view-tabs-more', panel: '[role="menu"]' },
  { name: 'row action menu (RrAnchor)', trigger: '.anchor-trigger', panel: '.anchor-menu' },
]

describe('dropdowns and pickers close like one another', () => {
  afterEach(() => { cleanup(); vi.restoreAllMocks() })

  for (const picker of pickers) {
    it(`${picker.name}: outside press (also one that stops the event), Escape and route change`, () => {
      render(<Page />)
      click(q(picker.trigger))
      expect(q(picker.panel)).not.toBeNull()
      press(q('.stopper')!)
      expect(q(picker.panel)).toBeNull()

      click(q(picker.trigger))
      expect(q(picker.panel)).not.toBeNull()
      act(() => (q(picker.panel)!.querySelector('button, input, [tabindex]') as HTMLElement | null)?.focus())
      escape()
      expect(q(picker.panel)).toBeNull()
      expect(q(picker.trigger)!.contains(document.activeElement) || document.activeElement === q(picker.trigger)).toBe(true)

      click(q(picker.trigger))
      act(() => closeAllDismissables('route'))
      expect(q(picker.panel)).toBeNull()
    })
  }

  it('opening one picker closes the one that was open', () => {
    render(<Page />)
    for (let index = 0; index < pickers.length; index += 1) {
      const current = pickers[index]
      const next = pickers[(index + 1) % pickers.length]
      press(q(current.trigger)!); click(q(current.trigger))
      expect(q(current.panel)).not.toBeNull()
      press(q(next.trigger)!); click(q(next.trigger))
      expect(q(current.panel)).toBeNull()
      expect(q(next.panel)).not.toBeNull()
      press(q('.stopper')!)
    }
  })
})
