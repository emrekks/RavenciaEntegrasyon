import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PlatformMultiSelect } from './platform-multi-select'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | undefined
let host: HTMLDivElement | undefined

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  root = undefined
  host = undefined
})

describe('compact platform multi-select', () => {
  it('opens from the table-header filter and reports a multi-platform selection', () => {
    const onChange = vi.fn()
    host = document.createElement('div')
    document.body.append(host)
    root = createRoot(host)

    act(() => root?.render(<PlatformMultiSelect
      compact
      label="Platform filtresi"
      options={[{ value: 'TRENDYOL', label: 'Trendyol' }, { value: 'SHOPIFY', label: 'Shopify' }]}
      selectedCodes={null}
      onChange={onChange}
    />))

    const trigger = host.querySelector<HTMLButtonElement>('[aria-label="Platform filtresi: Tüm platformlar"]')
    expect(trigger).not.toBeNull()
    act(() => trigger?.click())

    const menu = document.body.querySelector('[role="group"]')
    expect(menu).not.toBeNull()
    expect(menu?.parentElement).toBe(document.body)
    const checkboxes = menu?.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')
    expect(checkboxes).toHaveLength(2)
    act(() => checkboxes?.[1]?.click())

    expect(onChange).toHaveBeenCalledWith(['TRENDYOL'])
  })
})
