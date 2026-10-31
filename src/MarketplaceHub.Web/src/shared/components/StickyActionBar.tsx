import { useEffect, useRef, useState, type ReactNode } from 'react'
import './sticky-action-bar.css'

const exitMs = 160

// The bottom bar of a page's selection or save actions. It docks to the bottom edge of the content column with no
// gap (rows never scroll by under it) and stays in the page flow, so at the end of the list it sits after the last
// row instead of covering it. It slides in when it opens and out when it closes, showing what it showed last while
// it leaves (a cleared selection does not flash "0 seçildi").
export function StickyActionBar({ open = true, label, className, children }: { open?: boolean; label: string; className?: string; children: ReactNode }) {
  const [present, setPresent] = useState(open)
  if (open && !present) setPresent(true)
  const lastChildren = useRef<ReactNode>(children)
  useEffect(() => { if (open) lastChildren.current = children })
  useEffect(() => {
    if (open || !present) return undefined
    const timer = window.setTimeout(() => setPresent(false), exitMs)
    return () => window.clearTimeout(timer)
  }, [open, present])
  if (!open && !present) return null
  return <div className={`rv-action-bar${open ? '' : ' is-leaving'}${className ? ` ${className}` : ''}`} role="group" aria-label={label} inert={!open || undefined}>{open ? children : lastChildren.current}</div>
}
