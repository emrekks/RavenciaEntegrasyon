import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { focusOnMount } from '../../../shared/focus'
import { matchingKey } from '../../../shared/value-matching'
import './value-picker.css'

// One marketplace value: its id, its name and the folded name the search compares (matchingKey, worked out once).
export type PickerOption = { value: string; label: string; key: string }

const shownLimit = 200

export function pickerOptions(items: readonly { externalId: string; name: string }[]): PickerOption[] {
  return items.map(item => ({ value: item.externalId, label: item.name, key: matchingKey(item.name) }))
}

// The search hits, most fitting first: the value itself ("Sarı"), then values starting with it ("Sarı - Beyaz"), then
// values with it as a word ("Açık Mavi - Sarı"), then the rest that only contain it ("01S-Sarı"); A–Z inside each.
// Plain A–Z left the exact value behind hundreds of codes that contain it (26 Oct 2026, Hepsiburada Renk).
export function rankPickerMatches(options: readonly PickerOption[], folded: string) {
  const rank = (key: string) => key === folded ? 0 : key.startsWith(folded) ? 1 : ` ${key}`.includes(` ${folded}`) ? 2 : 3
  return options.flatMap((option, index) => option.key.includes(folded) ? [{ option, index, rank: rank(option.key) }] : [])
    .sort((left, right) => left.rank - right.rank || left.index - right.index)
    .map(entry => entry.option)
}

// A value cell of the mapping table. Closed it is only a button, so a table of hundreds of rows over a list of
// thousands of marketplace values (Hepsiburada Renk: 17,254) stays light; open it searches the list (Turkish letters
// and punctuation folded) and shows the first matches in the list's A–Z order.
export function ValuePicker({ value, options, label, onChange }: { value: string; options: readonly PickerOption[]; label: string; onChange: (value: string) => void }) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const [box, setBox] = useState<{ left: number; top: number; width: number; up: boolean; maxHeight: number } | null>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  const selected = value ? options.find(option => option.value === value) : undefined
  const folded = matchingKey(query)
  const matches = folded ? rankPickerMatches(options, folded) : options
  const shown = matches.slice(0, shownLimit)

  function place() {
    const rect = trigger.current?.getBoundingClientRect()
    if (!rect) return
    // The list opens where it fits whole (up to 360 px); otherwise toward the larger space, cut to that space so its
    // bottom edge never falls below the window (it did when 320-360 px were left below).
    const below = window.innerHeight - rect.bottom - 12
    const above = rect.top - 12
    const up = below < 360 && above > below
    const maxHeight = Math.max(160, Math.min(360, up ? above : below))
    setBox({ left: rect.left, top: up ? rect.top - 4 : rect.bottom + 4, width: Math.max(rect.width, 280), up, maxHeight })
  }
  useLayoutEffect(() => { if (open) place() }, [open])
  useEffect(() => {
    if (!open) return
    const outside = (event: PointerEvent) => { if (!menu.current?.contains(event.target as Node) && !trigger.current?.contains(event.target as Node)) setOpen(false) }
    const scrolled = (event: Event) => { if (!menu.current?.contains(event.target as Node)) setOpen(false) }
    // Escape closes only the list: caught first (capture) so the mapping window around it stays open.
    const escape = (event: globalThis.KeyboardEvent) => { if (event.key !== 'Escape') return; event.preventDefault(); event.stopImmediatePropagation(); setOpen(false); trigger.current?.focus() }
    document.addEventListener('keydown', escape, true)
    document.addEventListener('pointerdown', outside, true)
    window.addEventListener('scroll', scrolled, true)
    window.addEventListener('resize', place)
    return () => { document.removeEventListener('keydown', escape, true); document.removeEventListener('pointerdown', outside, true); window.removeEventListener('scroll', scrolled, true); window.removeEventListener('resize', place) }
  }, [open])
  useEffect(() => { menu.current?.querySelector<HTMLElement>('[data-active="true"]')?.scrollIntoView({ block: 'nearest' }) }, [active, open])

  function openMenu() { setQuery(''); setActive(Math.max(0, options.findIndex(option => option.value === value))); setOpen(true) }
  function choose(next: string) { onChange(next); setOpen(false); trigger.current?.focus() }
  function keyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'ArrowDown') { event.preventDefault(); setActive(index => Math.min(index + 1, shown.length - 1)) }
    else if (event.key === 'ArrowUp') { event.preventDefault(); setActive(index => Math.max(index - 1, 0)) }
    else if (event.key === 'Enter') { event.preventDefault(); if (shown[active]) choose(shown[active].value) }
  }

  return <>
    <button ref={trigger} type="button" className={`vp-trigger${value ? '' : ' is-empty'}${open ? ' is-open' : ''}`} aria-label={label} aria-haspopup="listbox" aria-expanded={open} onClick={() => open ? setOpen(false) : openMenu()}>
      <span>{selected?.label ?? (value ? 'Listede olmayan değer' : 'Seçin…')}</span>
      <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4.5 6.5 3.5 3.5 3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
    </button>
    {open && box && createPortal(<div ref={menu} className={`vp-menu${box.up ? ' is-up' : ''}`} style={{ left: box.left, top: box.top, width: box.width, maxHeight: box.maxHeight }}>
      <input ref={focusOnMount} className="vp-search" type="search" value={query} placeholder={`${options.length.toLocaleString('tr-TR')} değer içinde ara`} aria-label={`${label}: ara`} onChange={event => { setQuery(event.target.value); setActive(0) }} onKeyDown={keyDown} />
      <div className="vp-list" role="listbox" aria-label={label}>
        {value && <button type="button" className="vp-clear" onClick={() => choose('')}>Seçimi Kaldır</button>}
        {shown.length ? shown.map((option, index) => <button type="button" role="option" key={option.value} aria-selected={option.value === value} data-active={index === active} className={index === active ? 'is-active' : undefined} onMouseEnter={() => setActive(index)} onClick={() => choose(option.value)}>{option.label}</button>)
          : <p className="vp-none">Aramaya uyan değer yok.</p>}
        {matches.length > shownLimit && <p className="vp-none">{(matches.length - shownLimit).toLocaleString('tr-TR')} değer daha var; daha fazla harf yazın.</p>}
      </div>
    </div>, document.body)}
  </>
}
