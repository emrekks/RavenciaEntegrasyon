import { useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

export function VariantHeaderActionMenu({ anchorRef, label, children }: { anchorRef: { current: HTMLDivElement | null }; label: string; children: ReactNode }) {
  const menuRef = useRef<HTMLDivElement>(null)
  const [position, setPosition] = useState<{ top: number; left: number; visibility: 'hidden' | 'visible' }>({ top: 0, left: 0, visibility: 'hidden' })
  useLayoutEffect(() => {
    const place = () => {
      const anchor = anchorRef.current?.querySelector('button')
      const menu = menuRef.current
      if (!anchor || !menu) return
      const bounds = anchor.getBoundingClientRect()
      // Layout size: the menu grows in from .92 as it opens (styles/motion.css).
      const menuBounds = { width: menu.offsetWidth, height: menu.offsetHeight }
      const left = Math.max(12, Math.min(window.innerWidth - menuBounds.width - 12, bounds.right - menuBounds.width))
      const below = bounds.bottom + 8
      const top = below + menuBounds.height <= window.innerHeight - 12 ? below : Math.max(12, bounds.top - menuBounds.height - 8)
      setPosition({ top, left, visibility: 'visible' })
    }
    place()
    window.addEventListener('resize', place)
    window.addEventListener('scroll', place, true)
    return () => {
      window.removeEventListener('resize', place)
      window.removeEventListener('scroll', place, true)
    }
  }, [anchorRef])
  return createPortal(<div ref={menuRef} className="variant-header-action-menu variant-header-action-menu-portal" role="dialog" aria-label={label} data-variant-header-popover="true" style={position}>{children}</div>, document.body)
}
