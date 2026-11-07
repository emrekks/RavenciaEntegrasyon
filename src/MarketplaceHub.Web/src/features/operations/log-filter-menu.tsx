import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { UiIcon } from '../../shared/components'
import { useDismissable } from '../../shared/hooks/useDismissable'

export type FilterMenuOption = { value: string; label: string; mark?: ReactNode }
export type FilterMenuGroup = { label?: string; options: FilterMenuOption[] }

// One filter of the Loglar views' Filtrele panel: a 36px button naming what is chosen ("Tüm Sonuçlar", the one value, or
// "2 Sonuç") and a menu of checkboxes under it. A group's heading picks or drops its whole group.
export function FilterMenu({ name, allLabel, unit, groups, selected, onChange, emptyText, extra }: { name: string; allLabel: string; unit: string; groups: FilterMenuGroup[]; selected: string[]; onChange: (values: string[]) => void; emptyText?: string; extra?: { label: string; checked: boolean; onChange: (checked: boolean) => void } }) {
  const [open, setOpen] = useState(false)
  const wrap = useRef<HTMLDivElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  useDismissable(open, [wrap], () => setOpen(false))
  // The menu opens in the browser's top layer, pinned under its button (above it when there is no room below) and kept
  // inside the window, so no panel, card, sticky header or the sidebar can clip or cover it. It closes on page scroll.
  useLayoutEffect(() => {
    const pop = menu.current
    const anchor = wrap.current
    if (!open || !pop || !anchor) return
    const place = () => {
      const rect = anchor.getBoundingClientRect()
      // Measured at the trigger's width first: a fixed menu's own min-width of 100% is the window's, not the trigger's.
      Object.assign(pop.style, { position: 'fixed', margin: '0', left: '0px', top: '0px', right: 'auto', bottom: 'auto', minWidth: `${rect.width}px`, maxWidth: `${Math.max(rect.width, Math.min(300, window.innerWidth - 16))}px` })
      const height = pop.offsetHeight
      const width = pop.offsetWidth
      const below = rect.bottom + 6
      const top = below + height > window.innerHeight - 8 && rect.top - 6 - height > 8 ? rect.top - 6 - height : below
      Object.assign(pop.style, { position: 'fixed', margin: '0', right: 'auto', bottom: 'auto', top: `${top}px`, left: `${Math.max(8, Math.min(rect.left, window.innerWidth - width - 8))}px`, minWidth: `${rect.width}px`, maxHeight: `${Math.max(160, Math.min(360, window.innerHeight - top - 12))}px`, overflowY: 'auto', zIndex: '1000' })
    }
    if (typeof pop.showPopover === 'function') {
      pop.setAttribute('popover', 'manual')
      pop.showPopover()
    }
    place()
  }, [open, selected.length])
  useEffect(() => {
    if (!open) return
    const close = (event: Event) => { if (!(event.target instanceof Node && menu.current?.contains(event.target))) setOpen(false) }
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => { window.removeEventListener('scroll', close, true); window.removeEventListener('resize', close) }
  }, [open])
  const options = groups.flatMap(group => group.options)
  const single = selected.length === 1 ? options.find(option => option.value === selected[0]) : undefined
  const summary = selected.length === 0 ? allLabel : single ? single.label : `${selected.length} ${unit}`
  const toggle = (value: string) => onChange(selected.includes(value) ? selected.filter(item => item !== value) : [...selected, value])
  const toggleGroup = (group: FilterMenuGroup) => {
    const values = group.options.map(option => option.value)
    onChange(values.every(value => selected.includes(value)) ? selected.filter(value => !values.includes(value)) : [...new Set([...selected, ...values])])
  }
  return <div className="psl-menu-wrap" ref={wrap}>
    <button type="button" className={`psl-control psl-menu-toggle${selected.length ? ' is-set' : ''}`} aria-label={`${name}: ${summary}`} aria-haspopup="true" aria-expanded={open} onClick={() => setOpen(current => !current)}>
      {single?.mark}<span className="psl-menu-summary">{summary}</span><UiIcon name="chevronDown" size={14} />
    </button>
    {open && <div ref={menu} className="psl-menu" role="group" aria-label={name}>
      {options.length === 0 && emptyText && <p className="psl-menu-empty">{emptyText}</p>}
      {groups.map((group, index) => {
        const picked = group.options.filter(option => selected.includes(option.value)).length
        return <div className="psl-menu-group" key={group.label ?? index}>
          {group.label && <label className="psl-menu-heading">
            <input type="checkbox" checked={picked === group.options.length} ref={input => { if (input) input.indeterminate = picked > 0 && picked < group.options.length }} onChange={() => toggleGroup(group)} />{group.label}
          </label>}
          {group.options.map(option => <label key={option.value} className="psl-menu-option">
            <input type="checkbox" checked={selected.includes(option.value)} onChange={() => toggle(option.value)} />{option.mark}{option.label}
          </label>)}
        </div>
      })}
      {extra && <div className="psl-menu-group psl-menu-extra">
        <label className="psl-menu-option"><input type="checkbox" checked={extra.checked} onChange={event => extra.onChange(event.target.checked)} />{extra.label}</label>
      </div>}
    </div>}
  </div>
}
