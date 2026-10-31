import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, click, render } from '../../../test/render'
import { VariantMediaBulkSelect } from './VariantMediaBulkSelect'

afterEach(() => cleanup())

const values = [{ id: 'haki', label: 'Haki', assigned: true }, { id: 'siyah', label: 'Siyah' }, { id: 'vizon', label: 'Vizon', assigned: false }]
const key = (element: Element, name: string) => act(() => { element.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true })) })
const options = (host: HTMLElement) => [...host.querySelectorAll<HTMLButtonElement>('[role="option"]')]

describe('VariantMediaBulkSelect', () => {
  it('marks values that already have images with the round check icon instead of a text tick', () => {
    const { host } = render(<VariantMediaBulkSelect label="Seçenek Değeri" placeholder="Değer Seçin" value="haki" options={values} onChange={vi.fn()} />)
    const trigger = host.querySelector<HTMLButtonElement>('.category-attribute-select-trigger')!
    expect(trigger.textContent).toContain('Haki')
    expect(trigger.querySelector('.variant-media-value-check .ui-icon')).not.toBeNull()
    click(trigger)
    const list = options(host)
    expect(list.map(option => option.querySelector('span')!.firstChild!.textContent)).toEqual(['Değer Seçin', 'Haki', 'Siyah', 'Vizon'])
    expect(list.map(option => Boolean(option.querySelector('.variant-media-value-check')))).toEqual([false, true, false, false])
    expect(host.textContent).not.toContain('✓')
    // Screen readers hear the mark the eye sees.
    expect(list[1].textContent).toContain('görsel atandı')
    expect(list[1].getAttribute('aria-selected')).toBe('true')
  })

  it('opens from the keyboard, moves with the arrows and gives the chosen id back', () => {
    const onChange = vi.fn()
    const { host } = render(<VariantMediaBulkSelect label="Seçenek Değeri" placeholder="Değer Seçin" value="" options={values} onChange={onChange} />)
    const trigger = host.querySelector<HTMLButtonElement>('.category-attribute-select-trigger')!
    expect(trigger.textContent).toContain('Değer Seçin')
    expect(trigger.getAttribute('aria-haspopup')).toBe('listbox')
    trigger.focus()
    key(trigger, 'ArrowDown')
    expect(trigger.getAttribute('aria-expanded')).toBe('true')
    expect(document.activeElement).toBe(options(host)[0])
    key(document.activeElement!, 'ArrowDown')
    key(document.activeElement!, 'ArrowDown')
    expect(document.activeElement).toBe(options(host)[2])
    click(document.activeElement as HTMLButtonElement)
    expect(onChange).toHaveBeenCalledWith('siyah')
    expect(host.querySelector('[role="listbox"]')).toBeNull()
    expect(document.activeElement).toBe(trigger)
  })

  it('closes on Escape with focus back on the trigger, and stays shut while disabled', () => {
    const onChange = vi.fn()
    const { host, rerender } = render(<VariantMediaBulkSelect label="Seçenek Değeri" placeholder="Değer Seçin" value="haki" options={values} onChange={onChange} />)
    const trigger = host.querySelector<HTMLButtonElement>('.category-attribute-select-trigger')!
    click(trigger)
    expect(document.activeElement).toBe(options(host)[1])
    key(document.activeElement!, 'Escape')
    expect(host.querySelector('[role="listbox"]')).toBeNull()
    expect(document.activeElement).toBe(trigger)
    expect(onChange).not.toHaveBeenCalled()

    rerender(<VariantMediaBulkSelect label="Seçenek Değeri" placeholder="Değer Seçin" value="" options={[]} disabled onChange={onChange} />)
    expect(trigger.disabled).toBe(true)
  })
})
