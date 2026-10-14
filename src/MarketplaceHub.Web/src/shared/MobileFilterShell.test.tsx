import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MobileFilterShell } from './MobileFilterShell'

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  document.body.classList.remove('mobile-filters-open')
})

const render = (props: { activeCount?: number; onClear?: () => void; onApply?: () => void } = {}) =>
  act(() => root.render(<MobileFilterShell className="demo-filters" aria-label="Demo filtreleri" {...props}><label data-mfs-search>Ara<input aria-label="Ara" /></label><select aria-label="Durum" /></MobileFilterShell>))

describe('MobileFilterShell', () => {
  it('keeps the original fields and class on the wrapper', () => {
    render()
    const shell = container.querySelector('.mfs')!
    expect(shell.classList.contains('demo-filters')).toBe(true)
    expect(shell.getAttribute('aria-label')).toBe('Demo filtreleri')
    expect(shell.querySelector('[data-mfs-search] input')).not.toBeNull()
    expect(shell.querySelector('select[aria-label="Durum"]')).not.toBeNull()
  })

  it('shows the active filter count on the filter button', () => {
    render({ activeCount: 3 })
    const button = container.querySelector<HTMLButtonElement>('.mfs-open')!
    expect(button.getAttribute('aria-label')).toBe('Filtreler, 3 etkin')
    expect(button.querySelector('b')?.textContent).toBe('3')
  })

  it('opens as a dialog, locks page scroll, clears and applies, then closes', () => {
    const onClear = vi.fn()
    const onApply = vi.fn()
    render({ onClear, onApply })
    act(() => container.querySelector<HTMLButtonElement>('.mfs-open')!.click())
    const shell = container.querySelector('.mfs')!
    expect(shell.classList.contains('is-open')).toBe(true)
    expect(shell.getAttribute('role')).toBe('dialog')
    expect(document.body.classList.contains('mobile-filters-open')).toBe(true)

    const [clear, apply] = Array.from(container.querySelectorAll<HTMLButtonElement>('.mfs-footer button'))
    act(() => clear.click())
    expect(onClear).toHaveBeenCalledTimes(1)
    expect(shell.classList.contains('is-open')).toBe(true)

    act(() => apply.click())
    expect(onApply).toHaveBeenCalledTimes(1)
    expect(shell.classList.contains('is-open')).toBe(false)
    expect(document.body.classList.contains('mobile-filters-open')).toBe(false)
  })

  it('closes with Escape and the back button', () => {
    render()
    act(() => container.querySelector<HTMLButtonElement>('.mfs-open')!.click())
    act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })) })
    expect(container.querySelector('.mfs')!.classList.contains('is-open')).toBe(false)
    act(() => container.querySelector<HTMLButtonElement>('.mfs-open')!.click())
    act(() => container.querySelector<HTMLButtonElement>('.mfs-back')!.click())
    expect(container.querySelector('.mfs')!.classList.contains('is-open')).toBe(false)
  })
})
