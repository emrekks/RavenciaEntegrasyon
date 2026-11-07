import { useState, type ReactNode } from 'react'
import { RrAnchor, RrIcon, RrLogo } from '../features/marketplace/returns-ui'
import { comparePlatformCodes, knownPlatformName } from './platform-names'

// The one platform filter of the panel (the İadeler "İade Platformu" menu): a 36px rr-trigger that opens an rr-pop
// under it, left edges aligned, with a checkbox, the platform's logo, its name and (where the page knows them) its
// count on every row, and "Tümünü Kaldır" / "Tümünü Seç" in the head. Every platform ticked is "every platform".
//
// Value: [] for every platform, a list of codes for some of them, and [PLATFORM_NONE] for none (only where the page
// can show "no platform": allowNone). Without allowNone, "Tümünü Kaldır" clears the ticks inside the open menu only, so
// one platform can be picked next; closing the menu with nothing ticked leaves every platform chosen.

export const PLATFORM_NONE = '__NONE__'
export type PlatformFilterOption = { value: string; label?: string }

/** The options in the panel's platform order, named from the platform dictionary. */
export function platformFilterOptions(options: ReadonlyArray<PlatformFilterOption>): Array<{ value: string; label: string }> {
  return [...options].sort((a, b) => comparePlatformCodes(a.value, b.value)).map(option => ({ value: option.value, label: knownPlatformName(option.value) ?? option.label ?? option.value }))
}

/** The trigger's text for a value. */
export function platformFilterSummary(options: ReadonlyArray<PlatformFilterOption>, selected: readonly string[] | null | undefined): string {
  const named = platformFilterOptions(options)
  const picked = (selected ?? []).filter(code => code !== PLATFORM_NONE && named.some(option => option.value === code))
  if (selected?.includes(PLATFORM_NONE)) return 'Platform Seçilmedi'
  if (picked.length === 0 || picked.length === named.length) return 'Tüm Platformlar'
  if (picked.length === 1) return named.find(option => option.value === picked[0])?.label ?? picked[0]
  return `${picked.length} Platform Seçili`
}

function OptionCount({ value }: { value: number | undefined }) {
  if (value === undefined) return <small className="rr-opt-count is-loading" aria-hidden="true" />
  return <small className={`rr-opt-count${value === 0 ? ' is-zero' : ''}`}>{value.toLocaleString('tr-TR')}</small>
}

function countOf(counts: Record<string, number> | null | undefined, code: string) {
  if (!counts) return undefined
  const key = Object.keys(counts).find(item => item.toUpperCase() === code.toUpperCase())
  return key === undefined ? 0 : counts[key]
}

type PopProps = {
  options: ReadonlyArray<PlatformFilterOption>
  selected: readonly string[] | null | undefined
  onChange: (value: string[]) => void
  title?: ReactNode
  /** Per-platform counts: undefined shows none, null shows them loading. */
  counts?: Record<string, number> | null
  allowNone?: boolean
  width?: number
}

/** The menu alone, for a trigger of the page's own (a table head's funnel). */
export function PlatformFilterPop({ options, selected, onChange, title = 'Platform', counts, allowNone = false, width = 260 }: PopProps) {
  const named = platformFilterOptions(options)
  const codes = named.map(option => option.value)
  // Without allowNone, "none" lives only while the menu is open (see the note at the top).
  const [cleared, setCleared] = useState(false)
  const none = cleared || Boolean(selected?.includes(PLATFORM_NONE))
  const picked = (selected ?? []).filter(code => codes.includes(code))
  const all = !none && (picked.length === 0 || picked.length === codes.length)
  const checked = (code: string) => !none && (all || picked.includes(code))
  const pickNone = () => { if (allowNone) onChange([PLATFORM_NONE]); else setCleared(true) }
  const toggle = (code: string, on: boolean) => {
    const current = none ? [] : all ? codes : picked
    const next = on ? codes.filter(item => item === code || current.includes(item)) : current.filter(item => item !== code)
    setCleared(false)
    if (next.length === 0) pickNone()
    else onChange(next.length === codes.length ? [] : next)
  }
  return <div className="rr-pop pf-pop" role="dialog" aria-label={typeof title === 'string' ? title : 'Platform'} style={{ minWidth: width }}>
    <div className="rr-ph">{title}{codes.length > 0 && <button type="button" className="rr-btn rr-link" onClick={() => { if (all) pickNone(); else { setCleared(false); onChange([]) } }}>{all ? 'Tümünü Kaldır' : 'Tümünü Seç'}</button>}</div>
    {named.length === 0 && <p className="pf-empty">Bağlı pazaryeri yok.</p>}
    {named.map(option => <label key={option.value} className="rr-opt"><input type="checkbox" className="rr-cb" checked={checked(option.value)} onChange={event => toggle(option.value, event.target.checked)} /><RrLogo code={option.value} /><span className="rr-grow">{option.label}</span>{counts !== undefined && <OptionCount value={countOf(counts, option.value)} />}</label>)}
  </div>
}

type PlatformFilterProps = PopProps & {
  label?: string
  /** The label is read by assistive technology only (the field sits in a row that names it otherwise). */
  hideLabel?: boolean
  className?: string
  disabled?: boolean
}

/** The field: label, trigger and menu. */
export function PlatformFilter({ label = 'Platform', hideLabel = false, className, disabled, title, ...pop }: PlatformFilterProps) {
  const summary = platformFilterSummary(pop.options, pop.selected)
  const filtered = summary !== 'Tüm Platformlar'
  return <div className={['rr-layer', 'pf-field', className].filter(Boolean).join(' ')}>
    <div className="rr-fld">
      <small className={hideLabel ? 'pf-sr-only' : undefined}>{label}</small>
      <RrAnchor className="pf-anchor" trigger={(open, toggle) => <button type="button" className={`rr-trigger pf-trigger${open ? ' rr-on' : ''}${filtered ? ' pf-active' : ''}`} aria-haspopup="dialog" aria-expanded={open} aria-label={`${label}: ${summary}`} disabled={disabled ?? pop.options.length === 0} onClick={toggle}><span>{summary}</span><RrIcon name="chev" /></button>}>{() => <PlatformFilterPop title={title ?? label} {...pop} />}</RrAnchor>
    </div>
  </div>
}
