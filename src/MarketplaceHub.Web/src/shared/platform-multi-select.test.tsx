import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PlatformMultiSelect } from './platform-multi-select'

describe('PlatformMultiSelect', () => {
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

  it('starts with no boxes checked, shows all platforms, and toggles select all into clear all', () => {
    const onChange = vi.fn()
    const options = [
      { value: 'HEPSIBURADA', label: 'Hepsiburada' },
      { value: 'SHOPIFY', label: 'Shopify' }
    ]

    act(() => root.render(<PlatformMultiSelect label="Platformlar" options={options} selectedCodes={[]} onChange={onChange} />))
    expect(container.querySelector('.platform-multi-select-trigger')?.textContent).toContain('Tüm platformlar')
    act(() => container.querySelector<HTMLButtonElement>('.platform-multi-select-trigger')?.click())
    expect(Array.from(container.querySelectorAll<HTMLInputElement>('.platform-multi-select-option input')).map(input => input.checked)).toEqual([false, false])

    act(() => container.querySelector<HTMLButtonElement>('.platform-multi-select-menu-header button')?.click())
    expect(onChange).toHaveBeenLastCalledWith(['HEPSIBURADA', 'SHOPIFY'])

    act(() => root.render(<PlatformMultiSelect label="Platformlar" options={options} selectedCodes={['HEPSIBURADA', 'SHOPIFY']} onChange={onChange} />))
    expect(container.querySelector('.platform-multi-select-menu-header button')?.textContent).toBe('Tümünü kaldır')
    expect(Array.from(container.querySelectorAll<HTMLInputElement>('.platform-multi-select-option input')).map(input => input.checked)).toEqual([true, true])

    act(() => container.querySelector<HTMLButtonElement>('.platform-multi-select-menu-header button')?.click())
    expect(onChange).toHaveBeenLastCalledWith([])
  })
})
