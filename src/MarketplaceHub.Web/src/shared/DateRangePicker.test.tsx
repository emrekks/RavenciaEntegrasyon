import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DateRangePicker } from './DateRangePicker'
import { istanbulDateKey } from './format-date'

vi.mock('./format-date', async importOriginal => {
  const actual = await importOriginal<typeof import('./format-date')>()
  return { ...actual, istanbulDateKey: vi.fn(actual.istanbulDateKey) }
})

let container: HTMLDivElement
let root: Root

beforeEach(async () => {
  const actual = await vi.importActual<typeof import('./format-date')>('./format-date')
  vi.mocked(istanbulDateKey).mockImplementation(actual.istanbulDateKey)
  vi.useFakeTimers()
  vi.setSystemTime(new Date(2026, 9, 11, 12))
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  vi.useRealTimers()
})

describe('DateRangePicker presets', () => {
  it('offers the current month and current year instead of rolling day ranges', () => {
    const onChange = vi.fn()
    act(() => root.render(<DateRangePicker from="" to="" onChange={onChange} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())

    const presetButtons = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-presets button'))
    expect(presetButtons.map(button => button.textContent)).toContain('Bu Ay')
    expect(presetButtons.map(button => button.textContent)).toContain('Bu Yıl')
    expect(presetButtons.map(button => button.textContent)).not.toContain('Son 60 gün')
    expect(presetButtons.map(button => button.textContent)).not.toContain('Son 90 gün')
    expect(presetButtons.map(button => button.textContent)).not.toContain('Son 180 gün')

    act(() => presetButtons.find(button => button.textContent === 'Bu Ay')!.click())
    expect(onChange).toHaveBeenCalledWith({ from: '2026-10-01', to: '2026-10-11' })
  })

  it('sets the year preset from January 1 through today', () => {
    const onChange = vi.fn()
    act(() => root.render(<DateRangePicker from="" to="" onChange={onChange} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    const yearPreset = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-presets button')).find(button => button.textContent === 'Bu Yıl')!

    act(() => yearPreset.click())
    expect(onChange).toHaveBeenCalledWith({ from: '2026-01-01', to: '2026-10-11' })
  })

  it('takes today and the ready ranges from the Istanbul calendar on a device in another time zone', () => {
    // The device clock still says 31 Oct 18:30 (New York) while Istanbul is already at 1 Nov 01:30. The worker threads
    // cannot switch their own time zone, so the Istanbul day is set on istanbulDateKey instead.
    vi.setSystemTime(new Date(2026, 9, 31, 18, 30))
    vi.mocked(istanbulDateKey).mockReturnValue('2026-11-01')
    const onChange = vi.fn()
    act(() => root.render(<DateRangePicker from="" to="" onChange={onChange} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    const preset = (label: string) => Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-presets button')).find(button => button.textContent === label)!

    act(() => preset('Bugün').click())
    expect(onChange).toHaveBeenLastCalledWith({ from: '2026-11-01', to: '2026-11-01' })
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    act(() => preset('Bu Ay').click())
    expect(onChange).toHaveBeenLastCalledWith({ from: '2026-11-01', to: '2026-11-01' })
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    act(() => preset('Geçen Ay').click())
    expect(onChange).toHaveBeenLastCalledWith({ from: '2026-10-01', to: '2026-10-31' })
  })

  it('disables days after today and stops forward month navigation', () => {
    const onChange = vi.fn()
    act(() => root.render(<DateRangePicker from="" to="" onChange={onChange} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())

    const days = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-grid button'))
    expect(days.find(button => button.getAttribute('aria-label') === '11 Ekim 2026')!.disabled).toBe(false)
    expect(days.find(button => button.getAttribute('aria-label') === '12 Ekim 2026')!.disabled).toBe(true)
    act(() => days.find(button => button.getAttribute('aria-label') === '12 Ekim 2026')!.click())
    expect(onChange).not.toHaveBeenCalled()
    expect(document.body.querySelector<HTMLButtonElement>('button[aria-label="Sonraki ay"]')!.disabled).toBe(true)
  })

  it('shows the month before and this month when the range is in this month', () => {
    act(() => root.render(<DateRangePicker from="2026-10-05" to="2026-10-11" onChange={vi.fn()} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    expect(Array.from(document.body.querySelectorAll('.date-range-picker-month > strong')).map(label => label.textContent)).toEqual(['Eylül 2026', 'Ekim 2026'])
  })

  it('moves focus to the chosen day on opening, and Escape gives it back to the trigger', () => {
    act(() => root.render(<DateRangePicker from="2026-10-05" to="2026-10-08" onChange={vi.fn()} />))
    const trigger = container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!
    act(() => trigger.click())
    expect(document.activeElement?.getAttribute('aria-label')).toBe('5 Ekim 2026')
    act(() => { document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    expect(document.body.querySelector('.date-range-picker-popover')).toBeNull()
    expect(document.activeElement).toBe(trigger)
  })

  it('focuses the first ready range when no day is chosen', () => {
    act(() => root.render(<DateRangePicker from="" to="" onChange={vi.fn()} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    expect(document.activeElement).toBe(document.body.querySelector('.date-range-picker-presets button'))
  })

  it('walks the days with the arrow keys, turning the month, and keeps one day in the Tab order', () => {
    const onChange = vi.fn()
    act(() => root.render(<DateRangePicker from="2026-09-01" to="2026-09-03" onChange={onChange} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    const press = (key: string) => act(() => { document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })) })
    expect(document.activeElement?.getAttribute('aria-label')).toBe('1 Eylül 2026')
    press('ArrowRight')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('2 Eylül 2026')
    press('ArrowDown')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('9 Eylül 2026')
    press('End')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('13 Eylül 2026')
    press('ArrowLeft'); press('Home')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('7 Eylül 2026')
    // Back past the first calendar: the months turn so the day stays on screen.
    press('ArrowUp'); press('ArrowUp')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('24 Ağustos 2026')
    expect(Array.from(document.body.querySelectorAll('.date-range-picker-month > strong')).map(label => label.textContent)).toEqual(['Ağustos 2026', 'Eylül 2026'])
    expect(Array.from(document.body.querySelectorAll('[role="gridcell"]')).filter(cell => (cell as HTMLElement).tabIndex === 0)).toHaveLength(1)
    // A day after today stays out of reach.
    press('ArrowRight')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('25 Ağustos 2026')
    act(() => (document.activeElement as HTMLButtonElement).click())
    expect(onChange).toHaveBeenCalled()
  })

  it('does not move to a day after today', () => {
    act(() => root.render(<DateRangePicker from="2026-10-11" to="2026-10-11" onChange={vi.fn()} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    expect(document.activeElement?.getAttribute('aria-label')).toBe('11 Ekim 2026')
    act(() => { document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true })) })
    expect(document.activeElement?.getAttribute('aria-label')).toBe('11 Ekim 2026')
  })

  it('keeps Tab inside the open calendar', () => {
    act(() => root.render(<DateRangePicker from="" to="" onChange={vi.fn()} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    const buttons = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-popover button:not(:disabled)')).filter(button => button.tabIndex >= 0)
    const last = buttons[buttons.length - 1]
    act(() => last.focus())
    const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true })
    act(() => { last.dispatchEvent(tab) })
    expect(tab.defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(buttons[0])
    const back = new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true })
    act(() => { buttons[0].dispatchEvent(back) })
    expect(document.activeElement).toBe(last)
  })

  it('opens above the trigger when the viewport has more space there', () => {
    act(() => root.render(<DateRangePicker from="" to="" onChange={() => undefined} />))
    const picker = container.querySelector<HTMLDivElement>('.date-range-picker')!
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 768 })
    Object.defineProperty(picker, 'getBoundingClientRect', { configurable: true, value: () => ({ x: 100, y: 600, top: 600, right: 300, bottom: 630, left: 100, width: 200, height: 30, toJSON: () => ({}) }) })

    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    const popover = document.body.querySelector<HTMLElement>('.date-range-picker-popover')!
    expect(Number.parseFloat(popover.style.top)).toBeLessThan(600)
    expect(popover.style.maxHeight).toBe('330px')
  })
})
