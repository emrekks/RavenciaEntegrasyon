import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DateRangePicker } from './DateRangePicker'

let container: HTMLDivElement
let root: Root

beforeEach(() => {
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
    expect(presetButtons.map(button => button.textContent)).toContain('Bu ay')
    expect(presetButtons.map(button => button.textContent)).toContain('Bu sene')
    expect(presetButtons.map(button => button.textContent)).not.toContain('Son 60 gün')
    expect(presetButtons.map(button => button.textContent)).not.toContain('Son 90 gün')
    expect(presetButtons.map(button => button.textContent)).not.toContain('Son 180 gün')

    act(() => presetButtons.find(button => button.textContent === 'Bu ay')!.click())
    expect(onChange).toHaveBeenCalledWith({ from: '2026-10-01', to: '2026-10-11' })
  })

  it('sets the year preset from January 1 through today', () => {
    const onChange = vi.fn()
    act(() => root.render(<DateRangePicker from="" to="" onChange={onChange} />))
    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    const yearPreset = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.date-range-picker-presets button')).find(button => button.textContent === 'Bu sene')!

    act(() => yearPreset.click())
    expect(onChange).toHaveBeenCalledWith({ from: '2026-01-01', to: '2026-10-11' })
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

  it('opens above the trigger when the viewport has more space there', () => {
    act(() => root.render(<DateRangePicker from="" to="" onChange={() => undefined} />))
    const picker = container.querySelector<HTMLDivElement>('.date-range-picker')!
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 768 })
    Object.defineProperty(picker, 'getBoundingClientRect', { configurable: true, value: () => ({ x: 100, y: 600, top: 600, right: 300, bottom: 630, left: 100, width: 200, height: 30, toJSON: () => ({}) }) })

    act(() => container.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    const popover = document.body.querySelector<HTMLElement>('.date-range-picker-popover')!
    expect(Number.parseFloat(popover.style.top)).toBeLessThan(600)
    expect(popover.style.maxHeight).toBe('430px')
  })
})
