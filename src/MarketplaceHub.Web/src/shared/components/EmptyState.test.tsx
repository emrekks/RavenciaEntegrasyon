import { act } from 'react'
import type { ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { EmptyState, ListEmptyState } from './EmptyState'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

function render(node: ReactNode) {
  const container = document.createElement('div')
  document.body.appendChild(container)
  const root = createRoot(container)
  act(() => root.render(node))
  return { container, unmount: () => { act(() => root.unmount()); container.remove() } }
}

describe('EmptyState', () => {
  afterEach(() => { document.body.innerHTML = '' })

  it('draws icon, title, lead and an optional action in the one shared markup', () => {
    const { container, unmount } = render(<EmptyState title="Veri Yok" icon="chart" action={<button type="button">Yenile</button>}>Bu dönemde veri yok.</EmptyState>)
    expect(container.querySelector('.rv-empty-state-icon')).not.toBeNull()
    expect(container.querySelector('b')?.textContent).toBe('Veri Yok')
    expect(container.querySelector('.rv-empty-state-lead')?.textContent).toBe('Bu dönemde veri yok.')
    expect(container.querySelector('button')?.textContent).toBe('Yenile')
    unmount()
  })

  it('ListEmptyState without a filter is the plain empty state and has no Temizle', () => {
    const { container, unmount } = render(<ListEmptyState noun="Ürün" hasActiveFilters={false} onClear={vi.fn()} filteredHint="Filtrelerle eşleşen ürün yok." emptyHint="Ürünler pazaryerlerinden gelir." />)
    expect(container.querySelector('b')?.textContent).toBe('Henüz Ürün Yok')
    expect(container.textContent).toContain('Ürünler pazaryerlerinden gelir.')
    expect(container.textContent).not.toContain('Filtrelerle')
    expect(container.querySelector('button')).toBeNull()
    unmount()
  })

  it('ListEmptyState with a filter says Bulunamadı and Temizle clears', () => {
    const onClear = vi.fn()
    const { container, unmount } = render(<ListEmptyState noun="Ürün" hasActiveFilters onClear={onClear} filteredHint="Filtrelerle eşleşen ürün yok." emptyHint="x" />)
    expect(container.querySelector('b')?.textContent).toBe('Ürün Bulunamadı')
    expect(container.textContent).toContain('Filtrelerle eşleşen ürün yok.')
    const button = container.querySelector('button')
    expect(button?.textContent).toBe('Temizle')
    act(() => button?.click())
    expect(onClear).toHaveBeenCalledTimes(1)
    unmount()
  })
})
