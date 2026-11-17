import { moveListboxFocus } from '../../../shared/listbox-keys'
import { useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import { UiIcon } from '../../../shared/components'
import { placePublishPopover } from '../publish-popover-placement'
import { focusOnMount } from '../../../shared/focus'
import { useDismissable } from '../../../shared/hooks/useDismissable'
import { VariantMediaGroup } from './editor-types'
import { touchScreen } from './editor-helpers'

export function VariantFilterDropdown({
  group,
  selectedValueIds,
  onToggle,
  onClear
}: {
  group: VariantMediaGroup
  selectedValueIds: string[]
  onToggle: (valueId: string) => void
  onClear: () => void
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const rootRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const [menuStyle, setMenuStyle] = useState<CSSProperties | undefined>()
  const selected = group.values.filter(value => selectedValueIds.includes(value.id))
  const normalizedSearch = search.trim().toLocaleLowerCase('tr-TR')
  const filteredValues = group.values.filter(value => !normalizedSearch || value.value.toLocaleLowerCase('tr-TR').includes(normalizedSearch))
  const summary = selected.length === 0 ? 'Değer Seçin' : selected.length === 1 ? selected[0].value : `${selected[0].value} + ${selected.length - 1} değer`

  useDismissable(open, [rootRef], () => setOpen(false))

  // The menu is fixed to the screen, not to the dialog body, so the body's overflow cannot clip it; it opens upward when
  // there is no room below and is cut to the room left (the option list scrolls).
  useLayoutEffect(() => {
    if (!open) return
    const place = () => {
      const trigger = triggerRef.current
      if (!trigger) return
      const rect = trigger.getBoundingClientRect()
      const { placement, maxHeight } = placePublishPopover(trigger, 360)
      // As wide as its bar, never under 200px; a list too wide for the room right of the bar ends at the bar's right edge.
      const width = Math.max(rect.width, 200)
      const left = rect.left + width > window.innerWidth - 12 ? Math.max(12, rect.right - width) : rect.left
      setMenuStyle({ position: 'fixed', left, right: 'auto', width, ...(placement === 'top' ? { top: 'auto', bottom: window.innerHeight - rect.top + 8 } : { top: rect.bottom + 8, bottom: 'auto' }), ...(maxHeight != null ? { maxHeight } : {}) })
    }
    place()
    window.addEventListener('resize', place); window.addEventListener('scroll', place, true)
    return () => { window.removeEventListener('resize', place); window.removeEventListener('scroll', place, true) }
  }, [open])

  return <div className="variant-filter-field" ref={rootRef}>
    <button ref={triggerRef} type="button" className={`variant-filter-select-trigger ${selected.length ? 'active' : ''}`} aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen(current => !current)}>
      <span><small>{selected.length ? `${selected.length} değer seçildi` : group.name}</small><strong>{summary}</strong></span>
      <UiIcon name="chevronDown" />
    </button>
    {open && <div className="variant-filter-dropdown-menu" style={menuStyle} role="listbox" aria-label={`${group.name} filtre değerleri`} tabIndex={-1} onKeyDown={moveListboxFocus}>
      <div className="variant-filter-dropdown-tools">
        <input ref={touchScreen ? undefined : focusOnMount} value={search} onChange={event => setSearch(event.target.value)} placeholder="Değer ara..." aria-label={`${group.name} değerlerinde ara`} />
        <button type="button" className="variant-filter-clear-selection" onClick={onClear} disabled={selected.length === 0} tabIndex={selected.length === 0 ? -1 : undefined} aria-hidden={selected.length === 0 ? true : undefined} aria-label="Seçimi Temizle" title="Seçimi Temizle"><UiIcon name="close" /></button>
      </div>
      <div className="variant-filter-dropdown-options">
        {filteredValues.length ? filteredValues.map(value => {
          const isSelected = selectedValueIds.includes(value.id)
           return <button type="button" role="option" aria-selected={isSelected} className={isSelected ? 'active' : ''} key={value.id} onClick={() => onToggle(value.id)}><span>{value.value}</span><i aria-hidden="true">{isSelected ? <UiIcon name="check" /> : null}</i></button>
        }) : <span className="variant-filter-dropdown-empty">Aramaya uygun değer yok.</span>}
      </div>
    </div>}
  </div>
}
