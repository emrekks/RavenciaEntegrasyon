import { Fragment, type ReactNode } from 'react'
import { RrAnchor, RrColumnPop, RrFilterCard, RrIcon } from '../marketplace/returns-ui'
import './filter-fields.css'

// İşlem Takibi's filters in the panel's shared filter card (returns-ui: RrFilterCard, RrAnchor, rr-pop), the one the
// Siparişler, İadeler and Faturalar pages use: labelled 36px fields in one card with Temizle and Filtrele at its end; on
// phones the search stays in the page and a Filtre button opens the rest in a sheet from the bottom.

export type FilterMenuOption = { value: string; label: string; mark?: ReactNode; count?: number | null }
export type FilterMenuGroup = { label?: string; options: FilterMenuOption[] }

/** The trigger's text: every value ("Tüm Sonuçlar"), the one picked value, or how many are picked ("2 Sonuç"). */
export function filterMenuSummary(groups: FilterMenuGroup[], selected: readonly string[], allLabel: string, unit: string) {
  if (selected.length === 0) return allLabel
  const single = selected.length === 1 ? groups.flatMap(group => group.options).find(option => option.value === selected[0]) : undefined
  return single ? single.label : `${selected.length} ${unit}`
}

/** A group heading picks its whole group, or drops it when every value of it is already picked. */
export function toggleFilterGroup(selected: readonly string[], values: readonly string[]) {
  return values.every(value => selected.includes(value)) ? selected.filter(value => !values.includes(value)) : [...new Set([...selected, ...values])]
}

function OptionCount({ value }: { value: number | null | undefined }) {
  if (value === undefined) return null
  if (value === null) return <small className="rr-opt-count is-loading" aria-hidden="true" />
  return <small className={`rr-opt-count${value === 0 ? ' is-zero' : ''}`}>{value.toLocaleString('tr-TR')}</small>
}

// A multiple choice: the İadeler platform list (a checkbox, the logo, the name and its count per row, Tümünü Kaldır in
// the head). A group heading ("Sorunlu") picks or drops its whole group; extra is one more switch under the list.
export function FilterMenu({ name, allLabel, unit, groups, selected, onChange, emptyText, extra, width = 260 }: { name: string; allLabel: string; unit: string; groups: FilterMenuGroup[]; selected: string[]; onChange: (values: string[]) => void; emptyText?: string; extra?: { label: string; checked: boolean; onChange: (checked: boolean) => void }; width?: number }) {
  const options = groups.flatMap(group => group.options)
  const summary = filterMenuSummary(groups, selected, allLabel, unit)
  const toggle = (value: string) => onChange(selected.includes(value) ? selected.filter(item => item !== value) : [...selected, value])
  return <div className="rr-fld"><small>{name}</small><RrAnchor trigger={(open, toggleOpen) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-label={`${name}: ${summary}`} aria-haspopup="dialog" aria-expanded={open} onClick={toggleOpen}><span>{summary}</span><RrIcon name="chev" /></button>}>{() =>
    <div className="rr-pop ops-pop" role="dialog" aria-label={name} style={{ minWidth: width }}>
      <div className="rr-ph">{name}{selected.length > 0 && <button type="button" className="rr-btn rr-link" onClick={() => onChange([])}>Tümünü Kaldır</button>}</div>
      {options.length === 0 && emptyText && <p className="ops-pop-empty">{emptyText}</p>}
      {groups.map((group, index) => {
        const values = group.options.map(option => option.value)
        const picked = values.filter(value => selected.includes(value)).length
        return <Fragment key={group.label ?? index}>
          {index > 0 && group.label && <div className="rr-sep" />}
          {group.label && <label className="rr-opt ops-opt-group"><input type="checkbox" className="rr-cb" checked={picked === values.length} ref={input => { if (input) input.indeterminate = picked > 0 && picked < values.length }} onChange={() => onChange(toggleFilterGroup(selected, values))} /><span className="rr-grow">{group.label}</span></label>}
          {group.options.map(option => <label key={option.value} className={`rr-opt${group.label ? ' ops-opt-member' : ''}`}><input type="checkbox" className="rr-cb" checked={selected.includes(option.value)} onChange={() => toggle(option.value)} />{option.mark}<span className="rr-grow">{option.label}</span><OptionCount value={option.count} /></label>)}
        </Fragment>
      })}
      {extra && <><div className="rr-sep" /><label className="rr-opt"><input type="checkbox" className="rr-cb" checked={extra.checked} onChange={event => extra.onChange(event.target.checked)} /><span className="rr-grow">{extra.label}</span></label></>}
    </div>}</RrAnchor></div>
}

// A single choice. With allValue it is a filter (Siparişler's Kargo Firması: the first option is every value, Filtreyi
// Temizle once one is picked); without it a plain pick like Siparişler's Sıralama. label overrides the trigger's text.
export function ChoiceField({ name, value, options, onChange, allValue, label, width = 240 }: { name: string; value: string; options: Array<{ value: string; label: string }>; onChange: (value: string) => void; allValue?: string; label?: string; width?: number }) {
  const current = options.find(option => option.value === value)
  const text = label ?? current?.label ?? options[0]?.label ?? ''
  return <div className="rr-fld"><small>{name}</small><RrAnchor trigger={(open, toggle) => <button type="button" className={`rr-trigger${open ? ' rr-on' : ''}`} aria-label={`${name}: ${text}`} aria-haspopup="dialog" aria-expanded={open} onClick={toggle}><span>{text}</span><RrIcon name="chev" /></button>}>{close => allValue !== undefined
    ? <RrColumnPop label={name} width={width} options={options.map(option => option.value === allValue ? { ...option, value: 'ALL' } : option)} value={value === allValue ? 'ALL' : value} onPick={next => { onChange(next === 'ALL' ? allValue : next); close() }} />
    : <div className="rr-pop" role="listbox" aria-label={name} style={{ minWidth: width }}>{options.map(option => { const on = option.value === value; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => { onChange(option.value); close() }}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}</div>}</RrAnchor></div>
}

// The search field, first in the card (and the one field left in the page on phones).
export function SearchField({ label, value, onChange, onEnter, placeholder, ariaLabel }: { label: string; value: string; onChange: (value: string) => void; onEnter?: () => void; placeholder: string; ariaLabel: string }) {
  return <label className="rr-fld rr-search"><small>{label}</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} /><input className="rr-in" style={{ paddingLeft: 34 }} aria-label={ariaLabel} value={value} placeholder={placeholder} onChange={event => onChange(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') onEnter?.() }} /></span></label>
}

// The card: its fields, then Temizle (with how many filters it clears) and Filtrele. active is the phone Filtre button's
// count (every filter but the search, which stays in view there). wide: five fields beside the search instead of four.
export function OpsFilterCard({ active, clearCount, onClear, onApply, wide = false, label, children }: { active: number; clearCount: number; onClear: () => void; onApply: () => void; wide?: boolean; label: string; children: ReactNode }) {
  return <div className="rr ops-filters" role="search" aria-label={label}><RrFilterCard active={active}><div className={`rr-filters ops-filter-grid${wide ? ' is-wide' : ''}`}>
    {children}
    <div className="rr-fbtns"><button type="button" className="rr-btn ops-clear" onClick={onClear} disabled={clearCount === 0}>Temizle{clearCount > 0 && <span className="rr-count" aria-label={`${clearCount} filtre`}>{clearCount}</span>}</button><button type="button" className="rr-btn rr-primary" onClick={onApply}>Filtrele</button></div>
  </div></RrFilterCard></div>
}
