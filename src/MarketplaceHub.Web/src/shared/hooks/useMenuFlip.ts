import { useLayoutEffect, type RefObject } from 'react'
import { clampIntoViewport } from '../viewport-clamp'

/**
 * A dropdown that hangs under its trigger (position: absolute; top: 100%) opens above it instead when the room under the
 * trigger is too small and there is more above (data-placement="up", styles/menus.css), and its height is held to the
 * room it gets, scrolling inside. Sideways it is kept inside the window (clampIntoViewport). `menu` finds the menu inside `root`; the trigger is the root's first button.
 */
export function useMenuFlip(open: boolean, root: RefObject<HTMLElement | null>, menu: string) {
  useLayoutEffect(() => {
    if (!open) return
    const place = () => {
      const host = root.current
      const list = host?.querySelector<HTMLElement>(menu)
      if (!host || !list) return
      list.removeAttribute('data-placement')
      list.style.removeProperty('max-height')
      list.style.removeProperty('overflow-y')
      const anchor = (host.querySelector('button') ?? host).getBoundingClientRect()
      const height = list.offsetHeight
      const below = window.innerHeight - anchor.bottom - 16
      const above = anchor.top - 16
      const up = height > below && above > below
      if (up) list.setAttribute('data-placement', 'up')
      const room = up ? above : below
      if (height > room) { list.style.setProperty('max-height', `${Math.max(160, Math.floor(room))}px`); list.style.setProperty('overflow-y', 'auto') }
      clampIntoViewport(list)
    }
    place()
    window.addEventListener('resize', place)
    return () => window.removeEventListener('resize', place)
  }, [open, root, menu])
}
