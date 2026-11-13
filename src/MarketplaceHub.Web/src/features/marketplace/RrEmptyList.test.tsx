import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { RrEmptyList } from './returns-ui'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

function render(props: Parameters<typeof RrEmptyList>[0]) {
  const container = document.createElement('div')
  document.body.appendChild(container)
  const root = createRoot(container)
  act(() => root.render(<RrEmptyList {...props} />))
  return { container, unmount: () => { act(() => root.unmount()); container.remove() } }
}

describe('RrEmptyList', () => {
  afterEach(() => { document.body.innerHTML = '' })

  it('shows the plain empty state without Temizle while no filter is active', () => {
    const onClear = vi.fn()
    const { container, unmount } = render({ noun: 'Ürün', hasActiveFilters: false, onClear, filteredHint: 'Filtrelerle eşleşen ürün yok.', emptyHint: 'Ürünler pazaryerlerinden gelir.' })
    expect(container.querySelector('b')?.textContent).toBe('Henüz Ürün Yok')
    expect(container.textContent).toContain('Ürünler pazaryerlerinden gelir.')
    expect(container.textContent).not.toContain('Filtrelerle')
    expect(container.querySelector('button')).toBeNull()
    unmount()
  })

  it('shows Ürün Bulunamadı with Temizle while a filter is active', () => {
    const onClear = vi.fn()
    const { container, unmount } = render({ noun: 'Ürün', hasActiveFilters: true, onClear, filteredHint: 'Filtrelerle eşleşen ürün yok.', emptyHint: 'x' })
    expect(container.querySelector('b')?.textContent).toBe('Ürün Bulunamadı')
    expect(container.textContent).toContain('Filtrelerle eşleşen ürün yok.')
    const button = container.querySelector('button')
    expect(button?.textContent).toBe('Temizle')
    act(() => button?.click())
    expect(onClear).toHaveBeenCalledTimes(1)
    unmount()
  })
})
