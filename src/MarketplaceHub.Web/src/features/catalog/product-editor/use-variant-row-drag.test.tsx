import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { describe, expect, it, vi } from 'vitest'
import { useVariantRowDrag } from './use-variant-row-drag'

// The keyboard way to reorder the variant table: Alt+Up / Alt+Down on the handle moves one row; the first and the last stay.
describe('moveVariantRow', () => {
  function setup() {
    let rows = [{ key: 'a' }, { key: 'b' }, { key: 'c' }]
    const setVariantRows = vi.fn((update: (current: typeof rows) => typeof rows) => { rows = update(rows) })
    let api: ReturnType<typeof useVariantRowDrag> | undefined
    const noop = () => undefined
    function Probe() {
      api = useVariantRowDrag({ setVariantRows, setDraggedVariantKey: noop, setDragOverVariantKey: noop, pointerDraggedVariantKey: null, setPointerDraggedVariantKey: noop, pointerDraggedVariantRef: { current: null }, pointerDragSourceRef: { current: null }, pointerDragIdRef: { current: null } } as never)
      return null
    }
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    const host = document.createElement('div'); document.body.append(host)
    act(() => createRoot(host).render(<Probe />))
    return { move: (key: string, delta: -1 | 1) => act(() => api!.moveVariantRow(key, delta)), order: () => rows.map(row => row.key).join('') }
  }
  it('moves a row one place up or down and leaves the ends where they are', () => {
    const { move, order } = setup()
    move('b', -1); expect(order()).toBe('bac')
    move('b', -1); expect(order()).toBe('bac')
    move('a', 1); expect(order()).toBe('bca')
    move('a', 1); expect(order()).toBe('bca')
  })
})
