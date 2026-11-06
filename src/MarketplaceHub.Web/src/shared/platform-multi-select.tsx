import { useEffect, useEffectEvent, useId, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './components'
import { useDismissable } from './hooks/useDismissable'

export type PlatformFilterOption = { value: string; label: string }

type PlatformMultiSelectProps = {
  label: string
  options: PlatformFilterOption[]
  selectedCodes: string[] | null
  onChange: (selectedCodes: string[]) => void
  compact?: boolean
}

export function PlatformMultiSelect({ label, options, selectedCodes, onChange, compact = false }: PlatformMultiSelectProps) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  const [compactMenuPosition, setCompactMenuPosition] = useState<{ top: number; left: number } | null>(null)
  const id = useId()
  const selectedOptions = options.filter(option => selectedCodes?.includes(option.value))
  const allSelected = options.length > 0 && selectedOptions.length === options.length
  const hasSelection = selectedOptions.length > 0 && !allSelected
  const triggerLabel = !hasSelection
    ? 'Tüm Platformlar'
    : selectedOptions.length === 1
        ? selectedOptions[0].label
        : `${selectedOptions.length} Platform Seçili`

  function updateCompactMenuPosition() {
    if (!menu.current || !trigger.current) return
    const triggerRect = trigger.current.getBoundingClientRect()
    // Layout size: the menu grows in from .92 as it opens (styles/motion.css).
    const menuRect = { width: menu.current.offsetWidth, height: menu.current.offsetHeight }
    const top = triggerRect.bottom + menuRect.height + 8 <= window.innerHeight - 12
      ? triggerRect.bottom + 8
      : Math.max(12, triggerRect.top - menuRect.height - 8)
    // A compact (icon) trigger opens from its left edge, a field trigger from its right edge; either way the menu is
    // clamped into the window. It is portalled to the body, so no ancestor's overflow or stacking can cut or cover it.
    const wanted = compact ? triggerRect.left : triggerRect.right - menuRect.width
    const left = Math.max(12, Math.min(wanted, window.innerWidth - menuRect.width - 12))
    setCompactMenuPosition(current => current?.top === top && current.left === left ? current : { top, left })
  }

  useDismissable(open, [root, menu], () => setOpen(false), { triggerRef: trigger })

  const repositionCompactMenu = useEffectEvent(() => updateCompactMenuPosition())
  useEffect(() => {
    if (!open) return
    const repositionOnViewportChange = () => repositionCompactMenu()
    window.addEventListener('scroll', repositionOnViewportChange, { passive: true, capture: true })
    window.addEventListener('resize', repositionOnViewportChange)
    return () => {
      window.removeEventListener('scroll', repositionOnViewportChange, true)
      window.removeEventListener('resize', repositionOnViewportChange)
    }
  }, [open, compact])

  useLayoutEffect(() => {
    if (open) repositionCompactMenu()
  }, [open, compact, selectedCodes])

  function toggleOption(value: string, checked: boolean) {
    const current = selectedCodes ?? []
    const next = checked ? Array.from(new Set([...current, value])) : current.filter(item => item !== value)
    onChange(next)
  }

  function toggleMenu() {
    if (open) {
      setOpen(false)
      return
    }
    if (trigger.current) {
      const rect = trigger.current.getBoundingClientRect()
      const width = Math.min(300, window.innerWidth - 24)
      setCompactMenuPosition({ top: rect.bottom + 8, left: Math.max(12, Math.min(compact ? rect.left : rect.right - width, window.innerWidth - width - 12)) })
    }
    setOpen(true)
  }

  const menuContent = open && <div ref={menu} className={`platform-multi-select-menu is-compact`} id={`${id}-menu`} role="group" aria-labelledby={`${id}-label`} style={{ position: 'fixed', ...(compactMenuPosition ?? {}) }}>
    <div className="platform-multi-select-menu-header"><strong>Platformlar</strong><button type="button" onClick={() => onChange(selectedOptions.length ? [] : options.map(option => option.value))}>{selectedOptions.length ? 'Tümünü Kaldır' : 'Tümünü Seç'}</button></div>
    {options.map(option => <label className="platform-multi-select-option" key={option.value}>
      <input type="checkbox" checked={Boolean(selectedCodes?.includes(option.value))} onChange={event => toggleOption(option.value, event.target.checked)} />
      <span>{option.label}</span>
    </label>)}
  </div>

  return <div className={`platform-multi-select-field${compact ? ' is-compact' : ''}`}>
    <span className="platform-multi-select-label" id={`${id}-label`}>{label}</span>
    <div className="platform-multi-select" ref={root}>
      <button ref={trigger} type="button" className={`platform-multi-select-trigger${compact ? ' is-compact' : ''}${open ? ' is-open' : ''}${hasSelection ? ' has-selection' : ''}`} aria-label={compact ? `${label}: ${triggerLabel}` : undefined} aria-labelledby={!compact ? `${id}-label ${id}-value` : undefined} title={compact ? `${label}: ${triggerLabel}` : undefined} aria-expanded={open} aria-controls={open ? `${id}-menu` : undefined} onClick={toggleMenu} disabled={!options.length}>
        {compact ? <><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg>{hasSelection && <span className="platform-multi-select-selection-count" aria-hidden="true">{selectedCodes?.length ?? 0}</span>}</> : <><span id={`${id}-value`}>{triggerLabel}</span><UiIcon name="chevronDown" /></>}
      </button>
    </div>
    {menuContent && createPortal(menuContent, document.body)}
  </div>
}
