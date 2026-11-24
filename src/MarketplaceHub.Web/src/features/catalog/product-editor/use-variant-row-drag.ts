// Reordering the variant table's rows by dragging a row's handle (pointer events, also on touch screens).
import { useEffect } from 'react'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'
import type { VariantCodeMenusResult } from './use-variant-code-menus'
import type { EditorDataResult } from './use-editor-data'
import type { EditorSeedResult } from './use-editor-seed'

export function useVariantRowDrag(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult & VariantCodeMenusResult & EditorDataResult & EditorSeedResult) {
  const { setVariantRows, setDraggedVariantKey, setDragOverVariantKey, pointerDraggedVariantKey, setPointerDraggedVariantKey, pointerDraggedVariantRef, pointerDragSourceRef, pointerDragIdRef } = c
  function reorderVariants(sourceKey: string, targetKey: string) {
    if (sourceKey === targetKey) return
    setVariantRows(rows => {
      const sourceIndex = rows.findIndex(row => row.key === sourceKey); const targetIndex = rows.findIndex(row => row.key === targetKey)
      if (sourceIndex < 0 || targetIndex < 0) return rows
      const next = [...rows]; const [moved] = next.splice(sourceIndex, 1)
      const targetIndexAfterRemoval = next.findIndex(row => row.key === targetKey)
      const insertionIndex = sourceIndex < targetIndex ? targetIndexAfterRemoval + 1 : targetIndexAfterRemoval
      next.splice(insertionIndex, 0, moved)
      return next
    })
  }
  // The keyboard way (Alt+Up / Alt+Down on the focused handle): one row up or down.
  function moveVariantRow(key: string, delta: -1 | 1) {
    setVariantRows(rows => {
      const from = rows.findIndex(row => row.key === key); const to = from + delta
      if (from < 0 || to < 0 || to >= rows.length) return rows
      const next = [...rows]; const [moved] = next.splice(from, 1); next.splice(to, 0, moved)
      return next
    })
    window.requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-variant-row-key="${key}"] .variant-row-lead`)?.focus())
  }
  useEffect(() => {
    if (!pointerDraggedVariantKey) return
    const rowKeyAtPoint = (clientX: number, clientY: number) => document.elementFromPoint(clientX, clientY)?.closest<HTMLElement>('[data-variant-row-key]')?.dataset.variantRowKey ?? null
    const clearPointerDrag = () => {
      const source = pointerDragSourceRef.current
      if (source && pointerDragIdRef.current !== null) {
        try { source.releasePointerCapture(pointerDragIdRef.current) } catch { /* pointer capture may already be released */ }
      }
      pointerDraggedVariantRef.current = null
      pointerDragSourceRef.current = null
      pointerDragIdRef.current = null
      setPointerDraggedVariantKey(null)
      setDraggedVariantKey(null)
      setDragOverVariantKey(null)
    }
    const handlePointerMove = (event: PointerEvent) => {
      event.preventDefault()
      // The table scrolls inside its box: near the box's top or bottom edge the drag scrolls it, so a row can be carried past the visible part.
      const box = document.querySelector<HTMLElement>('.variant-table-editor')
      if (box) { const rect = box.getBoundingClientRect(); if (event.clientY > rect.bottom - 48) box.scrollTop += 14; else if (event.clientY < rect.top + 48 + 46) box.scrollTop -= 14 }
      const targetKey = rowKeyAtPoint(event.clientX, event.clientY)
      if (targetKey && targetKey !== pointerDraggedVariantKey) setDragOverVariantKey(targetKey)
    }
    const handlePointerUp = (event: PointerEvent) => {
      const sourceKey = pointerDraggedVariantRef.current
      const targetKey = rowKeyAtPoint(event.clientX, event.clientY)
      if (sourceKey && targetKey) reorderVariants(sourceKey, targetKey)
      clearPointerDrag()
    }
    document.addEventListener('pointermove', handlePointerMove, { passive: false })
    document.addEventListener('pointerup', handlePointerUp)
    document.addEventListener('pointercancel', clearPointerDrag)
    return () => {
      document.removeEventListener('pointermove', handlePointerMove)
      document.removeEventListener('pointerup', handlePointerUp)
      document.removeEventListener('pointercancel', clearPointerDrag)
    }
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs,
  // and reorderVariants, which only writes the rows through their setter.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pointerDraggedVariantKey])
  function beginVariantPointerDrag(event: React.PointerEvent<HTMLDivElement>, keyValue: string) {
    if (event.pointerType === 'mouse' && event.button !== 0) return
    event.preventDefault()
    pointerDraggedVariantRef.current = keyValue
    pointerDragSourceRef.current = event.currentTarget
    pointerDragIdRef.current = event.pointerId
    try { event.currentTarget.setPointerCapture(event.pointerId) } catch { /* capture is not available in every browser */ }
    setPointerDraggedVariantKey(keyValue)
    setDraggedVariantKey(keyValue)
    setDragOverVariantKey(null)
  }
  return { reorderVariants, moveVariantRow, beginVariantPointerDrag }
}
export type VariantRowDragResult = ReturnType<typeof useVariantRowDrag>
