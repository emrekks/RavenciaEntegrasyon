import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { RrAnchor, RrIcon } from '../features/marketplace/returns-ui'

// The panel's single-choice menu in place of a native <select>: an rr-trigger (36px, 14px/400) that opens an rr-pop
// list under it (above it when there is no room below; a bottom sheet on phones) with a radio dot on the picked row.
// The wrapper carries the rr- scope (rr-layer), so the rr- look works on any page. Keyboard: Enter, Space or an arrow opens it,
// arrows move between the rows, Enter picks, Escape closes and gives focus back to the trigger. With `name` a hidden
// input carries the value, so a form reads the same value it read from the select.

export type PanelSelectOption<T extends string = string> = { value: T; label: ReactNode; text?: string; disabled?: boolean }

type PanelSelectProps<T extends string> = {
  value: T
  options: ReadonlyArray<PanelSelectOption<T>>
  onChange: (value: T) => void
  /** The control's name for assistive technology (the old select's aria-label). */
  ariaLabel?: string
  /** Ids of the visible label, when one names the control (a <label htmlFor> also works: the trigger carries `id`). */
  ariaLabelledBy?: string
  id?: string
  name?: string
  disabled?: boolean
  className?: string
  placeholder?: string
  /** The list's smallest width; the trigger's width when larger. */
  width?: number
  /** A heading over the rows (as on the filter cards' menus). */
  title?: string
}

function optionText<T extends string>(option: PanelSelectOption<T> | undefined) {
  if (!option) return ''
  return option.text ?? (typeof option.label === 'string' || typeof option.label === 'number' ? String(option.label) : String(option.value))
}

export function PanelSelect<T extends string>({ value, options, onChange, ariaLabel, ariaLabelledBy, id, name, disabled = false, className, placeholder = 'Seçin', width = 180, title }: PanelSelectProps<T>) {
  const trigger = useRef<HTMLButtonElement>(null)
  const [triggerWidth, setTriggerWidth] = useState(0)
  const current = options.find(option => option.value === value)
  const text = current ? optionText(current) : placeholder
  return <div className={['rr-layer', 'pm-select', className].filter(Boolean).join(' ')}>
    <RrAnchor className="pm-anchor" trigger={(open, toggle) => {
      const openList = () => { setTriggerWidth(trigger.current?.offsetWidth ?? 0); toggle() }
      return <button ref={trigger} id={id} type="button" className={`rr-trigger pm-trigger${open ? ' rr-on' : ''}`} disabled={disabled}
        aria-haspopup="listbox" aria-expanded={open} aria-label={ariaLabel ? `${ariaLabel}: ${text}` : undefined} aria-labelledby={!ariaLabel && ariaLabelledBy ? `${ariaLabelledBy} ${id ?? ''}`.trim() : undefined}
        onClick={openList}
        onKeyDown={event => { if (!open && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) { event.preventDefault(); openList() } }}>
        <span>{current ? current.label : <span className="rr-muted">{placeholder}</span>}</span><RrIcon name="chev" />
      </button>
    }}>{close => <PanelSelectList title={title} ariaLabel={ariaLabel ?? title} options={options} value={value} width={Math.max(width, triggerWidth)} onPick={next => { close(); trigger.current?.focus(); if (next !== value) onChange(next) }} />}</RrAnchor>
    {name !== undefined && <input type="hidden" name={name} value={value} />}
  </div>
}

/** The list itself: one radio row per option, the picked one first in focus. Also used under other triggers. */
export function PanelSelectList<T extends string>({ title, ariaLabel, options, value, width, onPick }: { title?: string; ariaLabel?: string; options: ReadonlyArray<PanelSelectOption<T>>; value: T; width?: number; onPick: (value: T) => void }) {
  const list = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const rows = list.current?.querySelectorAll<HTMLButtonElement>('.rr-opt:not(:disabled)')
    const picked = list.current?.querySelector<HTMLButtonElement>('.rr-opt.rr-on:not(:disabled)')
    ;(picked ?? rows?.[0])?.focus({ preventScroll: true })
  }, [])
  // Typing jumps to the first row that starts with the letters typed (as a native select does).
  const typed = useRef({ text: '', at: 0 })
  function move(event: KeyboardEvent<HTMLDivElement>) {
    const rows = [...(list.current?.querySelectorAll<HTMLButtonElement>('.rr-opt:not(:disabled)') ?? [])]
    if (!rows.length) return
    if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey && event.key !== ' ') {
      const now = Date.now()
      typed.current = { text: (now - typed.current.at < 700 ? typed.current.text : '') + event.key.toLocaleLowerCase('tr-TR'), at: now }
      const hit = rows.find(row => (row.textContent ?? '').trim().toLocaleLowerCase('tr-TR').startsWith(typed.current.text))
      if (hit) { event.preventDefault(); hit.focus() }
      return
    }
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
    event.preventDefault()
    const at = rows.indexOf(document.activeElement as HTMLButtonElement)
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? rows.length - 1 : event.key === 'ArrowDown' ? Math.min(rows.length - 1, at + 1) : Math.max(0, at - 1)
    rows[next].focus()
  }
  return <div ref={list} className="rr-pop pm-pop" role="listbox" tabIndex={-1} aria-label={ariaLabel} style={{ minWidth: width }} onKeyDown={move}>
    {title && <div className="rr-ph">{title}</div>}
    {options.map(option => { const on = option.value === value; const head = option.value.startsWith('__group:'); return <button key={option.value} type="button" role="option" aria-selected={on} disabled={option.disabled} className={`rr-opt${on ? ' rr-on' : ''}${head ? ' rr-opt-head' : ''}`} onClick={() => onPick(option.value)}>{!head && <span className="rr-radio" aria-hidden="true" />}<span className="rr-grow">{option.label}</span></button> })}
  </div>
}
