import { useRef, useState, type ReactNode } from 'react'
import { UiIcon } from '../../shared/components'
import { useDismissable } from '../../shared/hooks/useDismissable'

export type FilterMenuOption = { value: string; label: string; mark?: ReactNode }
export type FilterMenuGroup = { label?: string; options: FilterMenuOption[] }

// One filter of the Loglar views' Filtrele panel: a 36px button naming what is chosen ("Tüm Sonuçlar", the one value, or
// "2 Sonuç") and a menu of checkboxes under it. A group's heading picks or drops its whole group.
export function FilterMenu({ name, allLabel, unit, groups, selected, onChange, emptyText }: { name: string; allLabel: string; unit: string; groups: FilterMenuGroup[]; selected: string[]; onChange: (values: string[]) => void; emptyText?: string }) {
  const [open, setOpen] = useState(false)
  const wrap = useRef<HTMLDivElement>(null)
  useDismissable(open, [wrap], () => setOpen(false))
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
    {open && <div className="psl-menu" role="group" aria-label={name}>
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
    </div>}
  </div>
}
