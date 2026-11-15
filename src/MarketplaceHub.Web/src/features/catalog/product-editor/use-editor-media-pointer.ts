// The picture strip's pointer handling: Escape leaves size chart picking, touch screens hold a picture to move it.
import { useCallback, useEffect, useRef } from 'react'
import { useMediaQuery } from '../../../shared/hooks/useMediaQuery'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'

export function useEditorMediaPointer(c: EditorProps & EditorStateResult) {
  const { sizeChartPicking, setSizeChartPicking } = c
  useEffect(() => {
    if (!sizeChartPicking) return
    const leave = (event: KeyboardEvent) => { if (event.key === 'Escape') setSizeChartPicking(false) }
    document.addEventListener('keydown', leave)
    return () => document.removeEventListener('keydown', leave)
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sizeChartPicking])
  // Touch screens have no drag and drop: holding a picture for a moment picks it up, sliding moves it and lifting the
  // finger drops it on the picture underneath. Moving before the hold completes scrolls the page as usual; while a
  // picture is held the page does not scroll (a non-passive touchmove stops it).
  // On touch screens a long press on a picture would start the browser's own drag (which scrolls the page towards the
  // edge it nears); the pictures are reordered by the press-and-hold handling below instead.
  const touchPointer = useMediaQuery('(pointer: coarse)')
  const mediaTouch = useRef<{ index: number; target: number; x: number; y: number; lastY: number; timer: number; dragging: boolean; scroller: Element | null } | null>(null)
  const suppressMediaClick = useRef(false)
  const mediaStripCleanup = useRef<(() => void) | null>(null)
  const mediaStripRef = useCallback((node: HTMLDivElement | null) => {
    mediaStripCleanup.current?.()
    mediaStripCleanup.current = null
    if (!node) return
    const hold = (event: TouchEvent) => { if (mediaTouch.current?.dragging) event.preventDefault() }
    node.addEventListener('touchmove', hold, { passive: false })
    mediaStripCleanup.current = () => node.removeEventListener('touchmove', hold)
  }, [])
  return { touchPointer, mediaTouch, suppressMediaClick, mediaStripCleanup, mediaStripRef }
}
export type EditorMediaPointerResult = ReturnType<typeof useEditorMediaPointer>
