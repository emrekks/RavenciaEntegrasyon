import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { SearchableSelect } from './MappingPages'

// The brand mapping list had no outside close: it stayed open over the page until an option was chosen.
const press = (element: Element) => act(() => {
  element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }))
  element.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }))
})
const options = [{ value: 'b1', label: 'Ravencia' }, { value: 'b2', label: 'Moda' }]

describe('SearchableSelect', () => {
  afterEach(() => cleanup())

  it('closes on a press outside, on Escape (focus back on the trigger) and after a choice', () => {
    const onChange = vi.fn()
    const { host } = render(<><SearchableSelect label="Trendyol Markası" value="" options={options} placeholder="Marka Seçin" onChange={onChange} /><p className="outside">sayfa</p></>)
    const trigger = host.querySelector<HTMLButtonElement>('.searchable-select-trigger')!
    act(() => trigger.click())
    expect(host.querySelector('.searchable-select-menu')).not.toBeNull()
    press(host.querySelector('.outside')!)
    expect(host.querySelector('.searchable-select-menu')).toBeNull()

    act(() => trigger.click())
    act(() => host.querySelector<HTMLInputElement>('.searchable-select-menu input')?.focus())
    act(() => { document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    expect(host.querySelector('.searchable-select-menu')).toBeNull()
    expect(document.activeElement).toBe(trigger)

    act(() => trigger.click())
    const option = [...host.querySelectorAll<HTMLElement>('.searchable-select-menu [role="option"], .searchable-select-menu button')].find(element => element.textContent?.includes('Moda'))!
    press(option)
    expect(onChange).toHaveBeenCalledWith('b2')
    expect(host.querySelector('.searchable-select-menu')).toBeNull()
  })
})
