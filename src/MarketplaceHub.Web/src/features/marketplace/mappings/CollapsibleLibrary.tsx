import { useEffect, useId, useState, type ReactNode } from 'react'
import { UiIcon } from '../../../shared/components'

export const libraryOpenStorageKey = 'rv-category-library-open'

function readOpen(storageKey: string): boolean {
  try { return localStorage.getItem(storageKey) === '1' } catch { return false }
}

function writeOpen(storageKey: string, open: boolean) {
  try { localStorage.setItem(storageKey, open ? '1' : '0') } catch { /* storage unavailable */ }
}

// A library block that stays folded to one 36 px row until opened. It is closed by default, remembers the choice, and
// opens by itself while a search is typed (forceOpen) or when openSignal changes (an entry was just added).
export function CollapsibleLibrary({ title, count, forceOpen = false, openSignal = 0, storageKey = libraryOpenStorageKey, children }: {
  title: string
  count: number
  forceOpen?: boolean
  openSignal?: number
  storageKey?: string
  children: ReactNode
}) {
  const [open, setOpen] = useState(() => readOpen(storageKey))
  const regionId = useId()
  useEffect(() => { if (forceOpen) setOpen(true) }, [forceOpen])
  useEffect(() => { if (openSignal > 0) setOpen(true) }, [openSignal])
  function toggle() {
    const next = !open
    setOpen(next)
    writeOpen(storageKey, next)
  }
  return <div className={open ? 'library-collapse is-open' : 'library-collapse'}>
    <button type="button" className="library-collapse-toggle" aria-expanded={open} aria-controls={regionId} onClick={toggle}>
      <span>{title} ({count.toLocaleString('tr-TR')})</span>
      <UiIcon name="chevronDown" size={16} />
    </button>
    <div className="library-collapse-region" id={regionId} role="region" aria-label={title} inert={!open}>
      <div className="library-collapse-inner">{children}</div>
    </div>
  </div>
}
