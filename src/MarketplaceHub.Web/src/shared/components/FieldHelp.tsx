import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './UiIcon'
import { isTouchOnly } from '../touch-tips'
import './field-help.css'

// A field's explanation behind a small question mark next to its label: shown while the pointer is on the mark or it
// has keyboard focus, and toggled by a tap (a tap elsewhere or Esc closes it). The text stays in the page (hidden) so
// the field can name it with aria-describedby={fieldHelpId}.
export function FieldHelp({ id, label, children }: { id?: string; label: string; children: ReactNode }) {
  return <HelpMark id={id} idPrefix="field-help" ariaLabel={`${label}: açıklama`} className="field-help" tipClassName="field-help-tip" iconSize={15} tipWidth={280} edge={12} prefer="above">{children}</HelpMark>
}

type HelpMarkProps = {
  id?: string
  idPrefix: string
  ariaLabel: string
  className: string
  tipClassName: string
  iconSize: number
  // The widest the card gets, and the least room kept between it and the window's edge.
  tipWidth: number
  edge: number
  // Centred over the mark and above it while there is room (a field), or under it starting at the mark (a title).
  prefer: 'above' | 'below'
  children: ReactNode
}

// The question mark and its card, shared by FieldHelp and TitleHint. The card is drawn on the page body with a fixed
// position, so a panel that clips its content cannot cut it, and it is kept inside the window on a phone.
export function HelpMark({ id, idPrefix, ariaLabel, className, tipClassName, iconSize, tipWidth, edge, prefer, children }: HelpMarkProps) {
  const generated = useId()
  const tipId = id ?? `${idPrefix}-${generated}`
  const buttonRef = useRef<HTMLButtonElement>(null)
  const tipRef = useRef<HTMLSpanElement>(null)
  // Open while hovered or focused; a click or tap pins it open (so a link in it can be followed) until a second click,
  // Esc or a press elsewhere. A touch screen ((hover: none)) opens it on a tap only: a pointer that reports itself as a
  // mouse there (some phones and stylus pens do) or the focus a tap leaves behind does not open it.
  const [hovered, setHovered] = useState(false)
  const [focused, setFocused] = useState(false)
  const [pinned, setPinned] = useState(false)
  const open = hovered || focused || pinned
  const setOpen = (value: boolean) => { if (!value) { setHovered(false); setFocused(false); setPinned(false) } }
  const [place, setPlace] = useState<{ left: number; top: number; above: boolean } | null>(null)

  useLayoutEffect(() => {
    if (!open || !buttonRef.current) return
    const mark = buttonRef.current.getBoundingClientRect()
    const width = Math.min(tipWidth, window.innerWidth - edge * 2)
    const start = prefer === 'above' ? mark.left + mark.width / 2 - width / 2 : mark.left - 8
    const left = Math.max(edge, Math.min(start, window.innerWidth - width - edge))
    const above = prefer === 'above' ? mark.top > 140 : mark.bottom > window.innerHeight - 160 && mark.top > 160
    setPlace({ left, top: above ? mark.top - 8 : mark.bottom + 8, above })
  }, [open, tipWidth, edge, prefer])

  useEffect(() => {
    if (!open) return undefined
    const close = () => setOpen(false)
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); close() } }
    const onPointer = (event: PointerEvent) => { const target = event.target as Node; if (!buttonRef.current?.contains(target) && !tipRef.current?.contains(target)) close() }
    document.addEventListener('keydown', onKey, true)
    document.addEventListener('pointerdown', onPointer, true)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      document.removeEventListener('pointerdown', onPointer, true)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [open])

  return <>
    <button ref={buttonRef} type="button" className={`${className}${open ? ' is-open' : ''}`} aria-label={ariaLabel} aria-describedby={tipId} aria-expanded={open}
      onPointerEnter={event => { if (event.pointerType === 'mouse' && !isTouchOnly()) setHovered(true) }}
      onPointerLeave={event => { if (event.pointerType === 'mouse') setHovered(false) }}
      onFocus={event => { if (event.currentTarget.matches(':focus-visible') && !isTouchOnly()) setFocused(true) }}
      onBlur={() => setFocused(false)}
      onClick={event => { event.preventDefault(); event.stopPropagation(); setPinned(value => !value) }}>
      <UiIcon name="help" size={iconSize} />
    </button>
    {createPortal(<span ref={tipRef} id={tipId} role="tooltip" className={`${tipClassName}${place?.above ? ' is-above' : ''}`} hidden={!open || !place} style={place ? { left: place.left, top: place.top } : undefined}>{children}</span>, document.body)}
  </>
}

// The text of a field's label with its help mark beside it, as one line above the control.
export function FieldLabel({ text, help, helpId }: { text: ReactNode; help?: ReactNode; helpId?: string }) {
  return <span className="field-label">{text}{help ? <FieldHelp id={helpId} label={typeof text === 'string' ? text : 'Alan'}>{help}</FieldHelp> : null}</span>
}
