import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './components'

export type PlatformFilterOption = { value: string; label: string }

type PlatformMultiSelectProps = {
  label: string
  options: PlatformFilterOption[]
  selectedCodes: string[] | null
  onChange: (selectedCodes: string[] | null) => void
  compact?: boolean
}

export function PlatformMultiSelect({ label, options, selectedCodes, onChange, compact = false }: PlatformMultiSelectProps) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  const [compactMenuPosition, setCompactMenuPosition] = useState<{ top: number; left: number } | null>(null)
  const id = useId()
  const allSelected = selectedCodes === null
  const selectedOptions = options.filter(option => selectedCodes?.includes(option.value))
  const triggerLabel = allSelected || selectedCodes?.length === options.length
    ? 'Tüm platformlar'
    : selectedOptions.length === 0
      ? 'Platform seçilmedi'
      : selectedOptions.length === 1
        ? selectedOptions[0].label
        : `${selectedOptions.length} platform seçili`

  useEffect(() => {
    const closeOutside = (event: PointerEvent) => {
      if (event.target instanceof Node && !root.current?.contains(event.target) && !menu.current?.contains(event.target)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', closeOutside)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOutside)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [])

  useEffect(() => {
    if (!open || !compact) return
    const closeOnViewportChange = () => setOpen(false)
    window.addEventListener('scroll', closeOnViewportChange, { passive: true })
    window.addEventListener('resize', closeOnViewportChange)
    return () => {
      window.removeEventListener('scroll', closeOnViewportChange)
      window.removeEventListener('resize', closeOnViewportChange)
    }
  }, [open, compact])

  useLayoutEffect(() => {
    if (!open || !compact || !menu.current || !trigger.current) return
    const triggerRect = trigger.current.getBoundingClientRect()
    const menuRect = menu.current.getBoundingClientRect()
    const top = triggerRect.bottom + menuRect.height + 8 <= window.innerHeight - 12
      ? triggerRect.bottom + 8
      : Math.max(12, triggerRect.top - menuRect.height - 8)
    const left = Math.max(12, Math.min(triggerRect.right - menuRect.width, window.innerWidth - menuRect.width - 12))
    setCompactMenuPosition(current => current?.top === top && current.left === left ? current : { top, left })
  }, [open, compact])

  function toggleOption(value: string, checked: boolean) {
    const current = allSelected ? options.map(option => option.value) : selectedCodes ?? []
    const next = checked ? Array.from(new Set([...current, value])) : current.filter(item => item !== value)
    onChange(next.length === options.length ? null : next)
  }

  function toggleMenu() {
    if (open) {
      setOpen(false)
      return
    }
    if (compact && trigger.current) {
      const rect = trigger.current.getBoundingClientRect()
      const width = Math.min(300, window.innerWidth - 24)
      setCompactMenuPosition({ top: rect.bottom + 8, left: Math.max(12, Math.min(rect.right - width, window.innerWidth - width - 12)) })
    }
    setOpen(true)
  }

  const menuContent = open && <div ref={menu} className={`platform-multi-select-menu${compact ? ' is-compact' : ''}`} id={`${id}-menu`} role="group" aria-labelledby={`${id}-label`} style={compact && compactMenuPosition ? compactMenuPosition : undefined}>
    <div className="platform-multi-select-menu-header"><strong>Platformlar</strong><button type="button" onClick={() => onChange(null)} disabled={allSelected}>Tümünü seç</button></div>
    {options.map(option => <label className="platform-multi-select-option" key={option.value}>
      <input type="checkbox" checked={allSelected || Boolean(selectedCodes?.includes(option.value))} onChange={event => toggleOption(option.value, event.target.checked)} />
      <span>{option.label}</span>
    </label>)}
  </div>

  return <div className={`platform-multi-select-field${compact ? ' is-compact' : ''}`}>
    <span className="platform-multi-select-label" id={`${id}-label`}>{label}</span>
    <div className="platform-multi-select" ref={root}>
      <button ref={trigger} type="button" className={`platform-multi-select-trigger${compact ? ' is-compact' : ''}${open ? ' is-open' : ''}${!allSelected ? ' has-selection' : ''}`} aria-label={compact ? `${label}: ${triggerLabel}` : undefined} aria-labelledby={!compact ? `${id}-label ${id}-value` : undefined} title={compact ? `${label}: ${triggerLabel}` : undefined} aria-expanded={open} aria-controls={open ? `${id}-menu` : undefined} onClick={toggleMenu} disabled={!options.length}>
        {compact ? <><svg className="order-filter-funnel" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16l-6 7.2V18l-4 1v-6.8L4 5z" /></svg>{!allSelected && <span className="platform-multi-select-selection-count" aria-hidden="true">{selectedCodes?.length ?? 0}</span>}</> : <><span id={`${id}-value`}>{triggerLabel}</span><UiIcon name="chevronDown" /></>}
      </button>
    </div>
    {compact ? menuContent && createPortal(menuContent, document.body) : menuContent}
  </div>
}
